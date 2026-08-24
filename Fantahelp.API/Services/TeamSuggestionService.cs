using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Fantahelp.API.Services
{
    public class TeamSuggestionService : ITeamSuggestionService
    {
        private readonly FantahelpContext _context;
        private readonly ILeagueService _leagueService;
        private readonly ITeamPrecomputer _precomputer;
        private readonly ILogger<TeamSuggestionService> _logger;
        private readonly IMemoryCache _cache;

        private const int CacheCapacity = 25;

        public TeamSuggestionService(FantahelpContext context, ILeagueService leagueService, ITeamPrecomputer precomputer, ILogger<TeamSuggestionService> logger, IMemoryCache cache)
        {
            _context = context;
            _leagueService = leagueService;
            _precomputer = precomputer;
            _logger = logger;
            _cache = cache;
        }

        private static readonly List<string> Roles = ["P", "D", "C", "A"];
        private static readonly Dictionary<string, int> PlayersPerRole = new()
        {
            { "P", 3 },
            { "D", 8 },
            { "C", 8 },
            { "A", 6 },
        };
        private static readonly Dictionary<string, double> MaxPercInterval = new()
        {
            { "P", 0.1 },
            { "D", 0.3 },
            { "C", 0.6 },
            { "A", 0.6 }
        };

        public async Task<ServiceResult<List<SuggestionResult>>> GetOptimalTeamSuggestionAsync(SuggestionRequest suggestionRequest)
        {
            // --- DATA PREPARATION ---
            Dictionary<int, Player> playerLookup = _context.Players.ToDictionary(p => p.Id, p => p);
            _logger.LogDebug("PlayerLookup count: {Count}", playerLookup.Count);

            Team? team = await _context.Teams
                .Include(t => t.League)
                .Include(t => t.Players)
                .ThenInclude(tp => tp.Player)
                .FirstOrDefaultAsync(t => t.Id == suggestionRequest.TeamId);

            if (team == null)
                return ServiceResult<List<SuggestionResult>>.FailureResult("No Team was found with the given teamId.");

            // --- PRECOMPUTE FAST PATH ---
            // Base-state requests (no auctioned player) return the precomputed result when
            // the team state (roster + params + player-data version) is unchanged.
            string? resultCacheKey = null;
            if (suggestionRequest.AuctionedPlayer == null)
            {
                resultCacheKey = BuildResultCacheKey(team, suggestionRequest);
                var cached = _precomputer.GetCachedResult(resultCacheKey);
                if (cached != null)
                {
                    _logger.LogInformation("Returning precomputed optimal team for team {TeamId}.", team.Id);
                    return ServiceResult<List<SuggestionResult>>.SuccessResult([cached]);
                }
            }

            // --- PRICE FORMAT RESOLUTION ---
            // A league's format is (total credits, starters). Scoring uses the format-specific
            // expected prices when ML data exists (exact match, else closest format); when no
            // per-format data exists at all, the engine falls back to the legacy
            // Player.ExpectedPrice/ExpectedStd columns.
            var lineup = suggestionRequest.LineUp;
            var totalStarters = lineup.Keepers + lineup.Defenders + lineup.Midfielders + lineup.Attackers;
            var priceFormat = await ResolvePriceFormatAsync(totalStarters, team.League.InitialBudget);
            var priceLookup = await LoadPriceLookupAsync(priceFormat);
            _logger.LogDebug("Price format resolved: {Format} (requested {Starters} starters, {Credits} credits).",
                priceFormat?.ToString() ?? "legacy (Player columns)", totalStarters, team.League.InitialBudget);

            var availablePlayersResult = await _leagueService.GetAllAvailablePlayersAsync(team.LeagueId);
            if (!availablePlayersResult.Success && availablePlayersResult.ErrorMessage != null)
                return ServiceResult<List<SuggestionResult>>.FailureResult(availablePlayersResult.ErrorMessage);

            var currentPlayers = BuildCurrentPlayers(team, suggestionRequest.FavoritePlayerIds, playerLookup, priceLookup);
            var availablePlayers = (availablePlayersResult.Data ?? []).ToList();

            // --- 1. BASE SUGGESTION TASK ---
            _logger.LogInformation("Starting base team suggestion.");
            var baseTask = ComputeSingleSuggestionAsync(currentPlayers, availablePlayers, team, playerLookup, suggestionRequest, priceLookup, priceFormat);

            // --- 2. POTENTIAL & WITHOUT PLAYER SUGGESTION TASKS (if auctioned player is provided) ---
            Task<SuggestionResult?> potentialTask = Task.FromResult<SuggestionResult?>(null);
            Task<SuggestionResult?> withoutTask = Task.FromResult<SuggestionResult?>(null);

            if (suggestionRequest.AuctionedPlayer != null)
            {
                if (playerLookup.TryGetValue(suggestionRequest.AuctionedPlayer.PlayerId, out var forcedPlayer))
                {
                    // Pool with auctioned player excluded (shared by both potential and without paths)
                    var excludedAvailablePlayers = availablePlayers.Where(p => p.Id != forcedPlayer.Id).ToList();

                    // --- POTENTIAL PATH: forced player at acquisition price ---
                    if (suggestionRequest.AuctionedPlayer.AcquisitionPrice <= team.League.InitialBudget)
                    {
                        _logger.LogInformation("Starting potential team suggestion for forced player {Name} (Id={Id}) at price {Price}.",
                            forcedPlayer.Name, forcedPlayer.Id, suggestionRequest.AuctionedPlayer.AcquisitionPrice);

                        var forcedMarket = ResolveMarketPrice(priceLookup, forcedPlayer);
                        var forcedScoringPlayer = new ScoringPlayer(
                            Id: forcedPlayer.Id, Name: forcedPlayer.Name, Squad: forcedPlayer.Squad,
                            Role: forcedPlayer.Role, Mate: forcedPlayer.Mate, Regularness: forcedPlayer.Regularness,
                            ExpectedPerformance: forcedPlayer.ExpectedPerformance, ExpectedStd: forcedMarket.Std,
                            MarketValue: forcedMarket.Price,
                            AcquisitionCost: suggestionRequest.AuctionedPlayer.AcquisitionPrice
                        );

                        var potentialCurrentPlayers = new List<ScoringPlayer>(currentPlayers) { forcedScoringPlayer };

                        potentialTask = ComputeSingleSuggestionAsync(
                            potentialCurrentPlayers, excludedAvailablePlayers, team, playerLookup, suggestionRequest, priceLookup, priceFormat, forcedScoringPlayer)
                            .ContinueWith(t => (SuggestionResult?)t.Result);
                    }
                    else
                    {
                        _logger.LogWarning("[Potential] Acquisition price {Price} exceeds initial budget {Budget}.",
                            suggestionRequest.AuctionedPlayer.AcquisitionPrice, team.League.InitialBudget);
                    }

                    // --- WITHOUT PLAYER PATH: player excluded from market entirely (Plan B) ---
                    _logger.LogInformation("Starting without-player team suggestion (excluding {Name} from market).",
                        forcedPlayer.Name);

                    withoutTask = ComputeSingleSuggestionAsync(
                        currentPlayers, excludedAvailablePlayers, team, playerLookup, suggestionRequest, priceLookup, priceFormat, forcedPlayer: null)
                        .ContinueWith(t => (SuggestionResult?)t.Result);
                }
                else
                {
                    _logger.LogWarning("[Auctioned] Player {PlayerId} not found.", suggestionRequest.AuctionedPlayer.PlayerId);
                }
            }

            // Execute all computations concurrently
            await Task.WhenAll((Task)baseTask, (Task)potentialTask, (Task)withoutTask);

            var baseResult = await baseTask;
            var potentialResult = await potentialTask;
            var withoutResult = await withoutTask;

            // --- PRECOMPUTE STORE ---
            // Only base-state runs are cached; auctioned runs attach Potential/Without scores
            // in-place, which must never leak into a cached base result.
            if (suggestionRequest.AuctionedPlayer == null && resultCacheKey != null)
                _precomputer.StoreResult(team.Id, resultCacheKey, suggestionRequest, baseResult);

            if (potentialResult != null)
            {
                baseResult.PotentialScore = new PotentialSuggestionResult
                {
                    SuggestedPlayers = potentialResult.SuggestedPlayers,
                    TotalExpectedPrice = potentialResult.TotalExpectedPrice,
                    TotalExpectedPriceStd = potentialResult.TotalExpectedPriceStd,
                    Score = potentialResult.Score
                };
            }

            if (withoutResult != null)
            {
                baseResult.WithoutPlayerScore = new WithoutPlayerSuggestionResult
                {
                    SuggestedPlayers = withoutResult.SuggestedPlayers,
                    TotalExpectedPrice = withoutResult.TotalExpectedPrice,
                    TotalExpectedPriceStd = withoutResult.TotalExpectedPriceStd,
                    Score = withoutResult.Score
                };
            }

            return ServiceResult<List<SuggestionResult>>.SuccessResult([baseResult]);
        }

        /// <summary>
        /// Cache key for a precomputed base-state result: player-data version, team, league,
        /// roster (sorted player ids) and every request param that influences the result.
        /// Roster and version in the key make a stale hit impossible: a mutation or an import
        /// always produces a different key, even when player ids are reused across seasons.
        /// </summary>
        private string BuildResultCacheKey(Team team, SuggestionRequest request)
        {
            var roster = string.Join(",", team.Players.Select(tp => tp.PlayerId).OrderBy(id => id));
            var favorites = string.Join(",", request.FavoritePlayerIds.OrderBy(id => id));
            var lineup = request.LineUp;
            var allocation = request.BudgetAllocation is { } a
                ? $"{a.Goalkeepers}|{a.Defenders}|{a.Midfielders}|{a.Attackers}"
                : "default";

            var payload = string.Join("|",
                _precomputer.DataVersion,
                team.Id,
                team.LeagueId,
                roster,
                $"{lineup.Keepers}.{lineup.Defenders}.{lineup.Midfielders}.{lineup.Attackers}",
                request.CreditsDistribution,
                request.NumTeams,
                favorites,
                allocation);

            return $"optimal:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))[..16]).ToLowerInvariant()}";
        }

        /// <summary>
        /// Unified calculation pipeline for team suggestions.
        /// Reused identically for both base and potential runs.
        /// </summary>
        private async Task<SuggestionResult> ComputeSingleSuggestionAsync(
            List<ScoringPlayer> currentPlayers,
            List<Player> availablePlayers,
            Team team,
            Dictionary<int, Player> playerLookup,
            SuggestionRequest suggestionRequest,
            Dictionary<int, (int Price, double Std)>? priceLookup,
            (int Credits, int Starters)? priceFormat,
            ScoringPlayer? forcedPlayer = null)
        {
            var budgetSpentPerRole = ComputeBudgetSpentPerRole(team, forcedPlayer);
            var playersToBuy = ComputePlayersToBuy(currentPlayers);
            var availablePlayersByRole = BuildAvailableByRole(availablePlayers, priceLookup);

            var baseCapsPerRole = ComputeMaxBudgetsPerRole(team.League.InitialBudget, budgetSpentPerRole, suggestionRequest);

            // Saved credits from already-purchased team players and forced player discounts
            int savedFromCurrent = 0;
            foreach (var cp in currentPlayers)
            {
                savedFromCurrent += Math.Max(0, cp.MarketValue - cp.AcquisitionCost);
            }

            var dpMaxPerRole = baseCapsPerRole.ToDictionary(
                kvp => kvp.Key, kvp => kvp.Value + savedFromCurrent);

            _logger.LogDebug("Starting Stage 1 DP tasks.");
            var stage1Tasks = new[]
            {
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["P"],
                    slots:      playersToBuy["P"],
                    maxBudget:  dpMaxPerRole["P"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["D"],
                    slots:      playersToBuy["D"],
                    maxBudget:  dpMaxPerRole["D"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["C"],
                    slots:      playersToBuy["C"],
                    maxBudget:  dpMaxPerRole["C"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["A"],
                    slots:      playersToBuy["A"],
                    maxBudget:  dpMaxPerRole["A"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat))
            };

            var results = await Task.WhenAll(stage1Tasks);
            var roleValueTables = new[] { results[0], results[1], results[2], results[3] };

            var scoreableLookup = new Dictionary<int, ScoringPlayer>(currentPlayers.ToDictionary(p => p.Id));
            foreach (var rolePlayers in availablePlayersByRole.Values)
                foreach (var p in rolePlayers)
                    scoreableLookup.TryAdd(p.Id, p);

            var capsPerRoleAdjusted = dpMaxPerRole.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            _logger.LogDebug("Starting Stage 2+3 Combination.");
            return CombineAndBacktrack(
                roleValueTables: roleValueTables,
                currentPlayers: currentPlayers,
                playersToBuy: playersToBuy,
                totalBudget: team.League.InitialBudget,
                numTeams: suggestionRequest.NumTeams,
                suggestionRequest: suggestionRequest,
                playerLookup: playerLookup,
                scoreableLookup: scoreableLookup,
                league: team.League,
                capsPerRole: capsPerRoleAdjusted,
                priceLookup: priceLookup
            );
        }

        private SuggestionResult CombineAndBacktrack(
            RoleValueTable[] roleValueTables,
            List<ScoringPlayer> currentPlayers,
            Dictionary<string, int> playersToBuy,
            int totalBudget,
            int numTeams,
            SuggestionRequest suggestionRequest,
            Dictionary<int, Player> playerLookup,
            Dictionary<int, ScoringPlayer> scoreableLookup,
            League league,
            Dictionary<string, int> capsPerRole,
            Dictionary<int, (int Price, double Std)>? priceLookup)
        {
            var finalCombination = CombineRoleResults(
                roleValueTables: roleValueTables,
                scoreableLookup: scoreableLookup,
                currentPlayers: currentPlayers,
                playersToBuy: playersToBuy,
                totalBudget: totalBudget,
                suggestionRequest: suggestionRequest,
                league: league,
                capsPerRole: capsPerRole
            );

            var results = BacktrackToGetTeams(
                finalCombination: finalCombination,
                numTeams: numTeams,
                playerLookup: playerLookup,
                priceLookup: priceLookup
            );

            if (results.Count > 0)
                return results[0];

            return new SuggestionResult
            {
                SuggestedPlayers = [],
                Score = new Score()
            };
        }

        private static List<ScoringPlayer> BuildCurrentPlayers(Team team, List<int> favoritePlayerIds, Dictionary<int, Player> playerLookup, Dictionary<int, (int Price, double Std)>? priceLookup)
        {
            var currentPlayers = team.Players
                .Select(tp =>
                {
                    var market = ResolveMarketPrice(priceLookup, tp.Player);
                    return new ScoringPlayer(
                        Id: tp.Player.Id,
                        Name: tp.Player.Name,
                        Squad: tp.Player.Squad,
                        Role: tp.Player.Role,
                        Mate: tp.Player.Mate,
                        Regularness: tp.Player.Regularness,
                        ExpectedPerformance: tp.Player.ExpectedPerformance,
                        ExpectedStd: 0,
                        MarketValue: market.Price,
                        AcquisitionCost: tp.AuctionPrice
                    );
                })
                .ToList();

            foreach (var playerId in favoritePlayerIds)
            {
                var fp = playerLookup[playerId];
                var market = ResolveMarketPrice(priceLookup, fp);
                currentPlayers.Add(new ScoringPlayer(
                    Id: fp.Id, Name: fp.Name, Squad: fp.Squad, Role: fp.Role,
                    Mate: fp.Mate, Regularness: fp.Regularness,
                    ExpectedPerformance: fp.ExpectedPerformance, ExpectedStd: market.Std,
                    MarketValue: market.Price, AcquisitionCost: market.Price
                ));
            }

            return currentPlayers;
        }

        private static Dictionary<string, List<ScoringPlayer>> BuildAvailableByRole(List<Player> availablePlayers, Dictionary<int, (int Price, double Std)>? priceLookup)
        {
            var scoringPlayers = availablePlayers.Select(p =>
            {
                var market = ResolveMarketPrice(priceLookup, p);
                return new ScoringPlayer(
                    Id: p.Id, Name: p.Name, Squad: p.Squad, Role: p.Role,
                    Mate: p.Mate, Regularness: p.Regularness,
                    ExpectedPerformance: p.ExpectedPerformance, ExpectedStd: market.Std,
                    MarketValue: market.Price, AcquisitionCost: market.Price
                );
            }).ToList();

            var result = scoringPlayers
                .GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var role in Roles)
                if (!result.ContainsKey(role))
                    result[role] = [];
            return result;
        }

        private static Dictionary<string, int> ComputeBudgetSpentPerRole(Team team, ScoringPlayer? forcedPlayer = null)
        {
            var result = team.Players
                .GroupBy(tp => tp.Player.Role)
                .ToDictionary(g => g.Key, g => g.Sum(tp => tp.AuctionPrice));

            if (forcedPlayer != null)
            {
                if (!result.ContainsKey(forcedPlayer.Role))
                    result[forcedPlayer.Role] = 0;
                result[forcedPlayer.Role] += forcedPlayer.AcquisitionCost;
            }

            foreach (var role in Roles)
                if (!result.ContainsKey(role))
                    result[role] = 0;
            return result;
        }

        private static Dictionary<string, int> ComputePlayersToBuy(List<ScoringPlayer> currentPlayers)
        {
            var currentByRole = currentPlayers.GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.Count());
            var result = PlayersPerRole.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value - (currentByRole.TryGetValue(kvp.Key, out var count) ? count : 0)
            );
            return result;
        }

        private static Dictionary<string, int> ComputeMaxBudgetsPerRole(int initialBudget, Dictionary<string, int> budgetSpentPerRole, SuggestionRequest suggestionRequest)
        {
            var allocation = suggestionRequest.BudgetAllocation;
            var perc = allocation != null
                ? new Dictionary<string, double>
                {
                    { "P", allocation.Goalkeepers },
                    { "D", allocation.Defenders },
                    { "C", allocation.Midfielders },
                    { "A", allocation.Attackers }
                }
                : MaxPercInterval;

            var result = perc.ToDictionary(
                kvp => kvp.Key,
                kvp => (int)(kvp.Value * initialBudget - budgetSpentPerRole[kvp.Key])
            );
            return result;
        }

        private RoleValueTable PrecomputeRoleValues(
            List<ScoringPlayer> players,
            int slots,
            int maxBudget,
            SuggestionRequest suggestionRequest,
            League league,
            List<ScoringPlayer> currentPlayers,
            ScoringPlayer? forcedPlayer = null,
            (int Credits, int Starters)? priceFormat = null)
        {
            string role = players.Count > 0 ? players[0].Role : string.Empty;
            var currentRoleMates = currentPlayers.Where(p => p.Role == role).ToList();

            // --- CACHE LOOKUP ---
            var cacheKey = BuildDpCacheKey(
                role: role,
                rolePlayerIds: players.Select(p => p.Id).ToList(),
                slots: slots,
                maxBudget: maxBudget,
                forcedPlayerId: forcedPlayer?.Id,
                currentRoleMateIds: currentRoleMates.Select(p => p.Id).ToList(),
                lineup: suggestionRequest.LineUp,
                creditsDistribution: suggestionRequest.CreditsDistribution,
                budgetAllocation: suggestionRequest.BudgetAllocation,
                priceFormat: priceFormat
            );

            if (_cache.TryGetValue(cacheKey, out RoleValueTable? cached))
            {
                _logger.LogDebug("[Cache hit] Role={Role} Slots={Slots} Budget={Budget}",
                    cacheKey.Split(':')[1], slots, maxBudget);
                return cached!;
            }

            _logger.LogDebug("[Cache miss] Role={Role} Slots={Slots} Budget={Budget}",
                cacheKey.Split(':')[1], slots, maxBudget);

            // --- DP COMPUTATION ---
            RoleValueTable roleValueTable = new() { };
            var playerById = players.ToDictionary(p => p.Id);

            bool applyForcedPlayer = forcedPlayer != null && role == forcedPlayer.Role;

            for (int k = 1; k <= slots; k++)
            {
                for (int b = 1; b <= maxBudget; b++)
                {
                    foreach (var player in players)
                    {
                        if (player.AcquisitionCost > b)
                            continue;

                        var prevSelection = roleValueTable.GetPlayerSelection(k - 1, b - player.AcquisitionCost);
                        List<ScoringPlayer> candidatePlayers;
                        if (prevSelection != null)
                            candidatePlayers = prevSelection.PlayerIds
                                .Select(id => playerById[id])
                                .ToList();
                        else if (k == 1 && b >= player.AcquisitionCost)
                            candidatePlayers = new List<ScoringPlayer>();
                        else
                            continue;

                        if (candidatePlayers.Any(p => p.Id == player.Id))
                            continue;

                        candidatePlayers.Add(player);

                        // Score against the full role unit: candidates + current mates + forced player
                        var evalPlayers = new List<ScoringPlayer>(candidatePlayers);
                        evalPlayers.AddRange(currentRoleMates);
                        if (applyForcedPlayer)
                            evalPlayers.Add(forcedPlayer!);

                        var score = ScoringEngine.CalculateScore(evalPlayers, suggestionRequest, league);

                        var currentSelection = roleValueTable.GetPlayerSelection(k, b);
                        if (currentSelection == null || score.TotalScore > currentSelection.Score.TotalScore)
                        {
                            roleValueTable.SetPlayerSelection(
                                k, b,
                                score,
                                candidatePlayers.Select(p => p.Id).ToList());
                        }
                    }
                }
            }

            for (int k = 1; k <= slots; k++)
            {
                PlayerSelectionResult? best = null;
                for (int b = 0; b <= maxBudget; b++)
                {
                    var current = roleValueTable.GetPlayerSelection(k, b);
                    if (current != null && (best == null || current.Score.TotalScore > best.Score.TotalScore))
                        best = current;
                    if (best != null)
                        roleValueTable.SetPlayerSelection(k, b, best.Score, best.PlayerIds);
                }
            }

            // --- CACHE INSERT ---
            _cache.Set(cacheKey, roleValueTable, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(10),
                Size = 1,
            });

            return roleValueTable;
        }

        private FinalCombinationResult CombineRoleResults(
            RoleValueTable[] roleValueTables,
            Dictionary<int, ScoringPlayer> scoreableLookup,
            List<ScoringPlayer> currentPlayers,
            Dictionary<string, int> playersToBuy,
            int totalBudget,
            SuggestionRequest suggestionRequest,
            League league,
            Dictionary<string, int> capsPerRole)
        {
            FinalCombinationResult finalTable = new FinalCombinationResult {};

            for (int t = 0; t < Roles.Count; t++)
            {
                int slots = playersToBuy[Roles[t]];
                string role = Roles[t];
                int roleCap = capsPerRole[role];

                Dictionary<int, PlayerSelectionResult> currTable = roleValueTables[t].Table
                    .Where(kvp => kvp.Key.NumPlayers == slots && kvp.Key.Budget <= roleCap)
                    .ToDictionary(kvp => kvp.Key.Budget, kvp => kvp.Value);

                if (currTable.Count == 0)
                    continue;

                if (finalTable.Table.Count == 0)
                {
                    int currentPlayersTotalPrice = currentPlayers.Sum(p => p.AcquisitionCost);
                    foreach (var kvp in currTable)
                    {
                        var allIds = currentPlayers.Select(p => p.Id)
                            .Concat(kvp.Value.PlayerIds)
                            .ToList();

                        finalTable.SetPlayerSelection(
                            roleNum: t,
                            budget: kvp.Key + currentPlayersTotalPrice,
                            score: kvp.Value.Score,
                            playerIds: allIds);
                    }
                    continue;
                }

                var prevSelections = finalTable.Table
                    .Where(e => e.Key.RoleNum == t - 1)
                    .ToDictionary(e => e.Key.Budget, e => e.Value);

                if (prevSelections.Count == 0)
                    continue;

                foreach (var curr in currTable)
                {
                    foreach (var prev in prevSelections)
                    {
                        int newBudget = prev.Key + curr.Key;
                        if (newBudget > totalBudget)
                            continue;

                        var newIds = prev.Value.PlayerIds
                                        .Concat(curr.Value.PlayerIds)
                                        .ToList();
                        List<ScoringPlayer> selectedPlayers = newIds
                            .Where(scoreableLookup.ContainsKey)
                            .Select(id => scoreableLookup[id])
                            .ToList();

                        Score newScore = ScoringEngine.CalculateScore(selectedPlayers, suggestionRequest, league);
                        Score lastScore = finalTable.GetPlayerSelection(t, newBudget)?.Score ?? new Score { TotalScore = double.MinValue };

                        if (newScore.TotalScore > lastScore.TotalScore)
                            finalTable.SetPlayerSelection(
                                roleNum: t,
                                budget: newBudget,
                                score: newScore,
                                playerIds: newIds);
                    }
                }
            }

            return finalTable;
        }

        private List<SuggestionResult> BacktrackToGetTeams(FinalCombinationResult finalCombination, Dictionary<int, Player> playerLookup, int numTeams, Dictionary<int, (int Price, double Std)>? priceLookup)
        {
            List<SuggestionResult> suggestionResults = [];

            var proposedTeams = finalCombination.Table
                .Where(kvp => kvp.Key.RoleNum == Roles.Count - 1)
                .OrderByDescending(kvp => kvp.Value.Score.TotalScore)
                .Take(numTeams)
                .Select(kvp => new SuggestedTeam
                {
                    Price = kvp.Key.Budget,
                    PlayerSelection = kvp.Value
                })
                .ToList();

            foreach (var proposedTeam in proposedTeams)
            {
                var teamPlayers = proposedTeam.PlayerSelection.PlayerIds
                    .Where(playerLookup.ContainsKey)
                    .Select(id => playerLookup[id])
                    .ToList();

                // Team-level price std from the same format the engine priced with
                // (legacy Player columns as fallback).
                var teamPlayersStd = (int)Math.Sqrt(teamPlayers.Sum(p =>
                {
                    var std = priceLookup != null && priceLookup.TryGetValue(p.Id, out var pp) ? pp.Std : p.ExpectedStd;
                    return Math.Pow(std, 2);
                }));

                // Keep the displayed per-player prices consistent with the format used for scoring
                // (the legacy Player columns carry the reference format only).
                var playerDtos = PlayerMapper.ToReadDtos(teamPlayers);
                foreach (var dto in playerDtos)
                {
                    if (priceLookup != null && priceLookup.TryGetValue(dto.Id, out var pp))
                    {
                        dto.ExpectedPrice = pp.Price;
                        dto.ExpectedStd = pp.Std;
                    }
                }

                suggestionResults.Add(new SuggestionResult
                {
                    SuggestedPlayers = playerDtos,
                    TotalExpectedPrice = proposedTeam.Price,
                    TotalExpectedPriceStd = teamPlayersStd,
                    Score = proposedTeam.PlayerSelection.Score,
                });
            }

            return suggestionResults;
        }

        /// <summary>
        /// Builds a deterministic cache key for a DP table entry.
        /// Includes all factors that affect DP scoring: role, player pool, slots, budget,
        /// forced player context, current role-mates, lineup configuration, credits distribution and budget allocation.
        /// </summary>
        private static string BuildDpCacheKey(
            string role,
            IReadOnlyList<int> rolePlayerIds,
            int slots,
            int maxBudget,
            int? forcedPlayerId,
            IReadOnlyList<int> currentRoleMateIds,
            LineUp lineup,
            int creditsDistribution,
            BudgetAllocation? budgetAllocation,
            (int Credits, int Starters)? priceFormat)
        {
            // Deterministic hash from sorted player IDs
            var hash = rolePlayerIds.OrderBy(id => id).Aggregate(0L, (h, id) => h ^ (id.GetHashCode() * 31L));
            var matesHash = currentRoleMateIds.OrderBy(id => id).Aggregate(0L, (h, id) => h ^ (id.GetHashCode() * 31L));
            var lineupKey = $"{lineup.Defenders}-{lineup.Midfielders}-{lineup.Attackers}";
            var forcedKey = forcedPlayerId ?? -1;
            var allocKey = budgetAllocation != null
                ? $"{budgetAllocation.Goalkeepers:F2}-{budgetAllocation.Defenders:F2}-{budgetAllocation.Midfielders:F2}-{budgetAllocation.Attackers:F2}"
                : "default";
            // The DP prices players with format-specific expected prices, so the format
            // must be part of the key (two leagues can share lineup + per-role budgets
            // while using different price formats).
            var formatKey = priceFormat == null ? "legacy" : $"{priceFormat.Value.Credits}_{priceFormat.Value.Starters}";
            return $"dp:{role}:{hash}:{slots}:{maxBudget}:{forcedKey}:{matesHash}:{lineupKey}:{creditsDistribution}:{allocKey}:{formatKey}";
        }

        /// <summary>
        /// Resolves the player price format for a request: the exact (credits, starters)
        /// combination when ML data exists for it, otherwise the closest available format
        /// (by credits distance, then starters distance). Returns null when no per-format
        /// price data exists at all (e.g. a legacy single-format import); callers then fall
        /// back to Player.ExpectedPrice/ExpectedStd.
        /// </summary>
        private async Task<(int Credits, int Starters)?> ResolvePriceFormatAsync(int starters, int credits)
        {
            var rows = await _context.PlayerPrices
                .Select(pp => new { pp.Credits, pp.Starters })
                .Distinct()
                .ToListAsync();
            var formats = rows.GroupBy(r => (r.Credits, r.Starters)).Select(g => g.Key).ToList();
            if (formats.Count == 0)
                return null;
            return formats
                .OrderBy(f => Math.Abs(f.Credits - credits))
                .ThenBy(f => Math.Abs(f.Starters - starters))
                .ThenBy(f => f.Credits) // deterministic tie-break
                .First();
        }

        /// <summary>
        /// Loads per-player (expected price, std) for the resolved format.
        /// Returns null when the format is null (no per-format data).
        /// </summary>
        private async Task<Dictionary<int, (int Price, double Std)>?> LoadPriceLookupAsync((int Credits, int Starters)? priceFormat)
        {
            if (priceFormat == null)
                return null;
            var rows = await _context.PlayerPrices
                .Where(pp => pp.Credits == priceFormat.Value.Credits && pp.Starters == priceFormat.Value.Starters)
                .Select(pp => new { pp.PlayerId, pp.Price, pp.Std })
                .ToListAsync();
            return rows.ToDictionary(r => r.PlayerId, r => (r.Price, r.Std));
        }

        /// <summary>
        /// Resolves a player's market value (expected price, std): the format-specific
        /// PlayerPrice row when available, otherwise the legacy Player columns.
        /// </summary>
        private static (int Price, double Std) ResolveMarketPrice(Dictionary<int, (int Price, double Std)>? priceLookup, Player player)
        {
            return priceLookup != null && priceLookup.TryGetValue(player.Id, out var pp)
                ? (pp.Price, pp.Std)
                : ((int)player.ExpectedPrice, player.ExpectedStd);
        }
    }
}