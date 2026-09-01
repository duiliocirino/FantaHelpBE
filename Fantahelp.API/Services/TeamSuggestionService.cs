using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fantahelp.API.Services.Helpers;
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
        private readonly IOptimalScenarioCache _scenarioCache;
        private readonly ILogger<TeamSuggestionService> _logger;
        private readonly IMemoryCache _cache;

        private static readonly ConcurrentDictionary<string, Lazy<RoleValueTable>> InflightDpComputations = new();

        public TeamSuggestionService(
            FantahelpContext context,
            ILeagueService leagueService,
            ITeamPrecomputer precomputer,
            IOptimalScenarioCache scenarioCache,
            ILogger<TeamSuggestionService> logger,
            IMemoryCache cache)
        {
            _context = context;
            _leagueService = leagueService;
            _precomputer = precomputer;
            _scenarioCache = scenarioCache;
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
            string resultCacheKey = BuildResultCacheKey(team, suggestionRequest);
            var cachedBaseResult = _precomputer.GetCachedResult(resultCacheKey);
            if (suggestionRequest.AuctionedPlayer == null && cachedBaseResult != null)
            {
                _logger.LogInformation("Returning precomputed optimal team for team {TeamId}.", team.Id);
                return ServiceResult<List<SuggestionResult>>.SuccessResult([cachedBaseResult]);
            }

            // --- PRICE FORMAT RESOLUTION ---
            // A league's format is (total credits, starters). Scoring uses the format-specific
            // expected prices when ML data exists (exact match, else closest format); when no
            // per-format data exists at all, the engine falls back to the legacy
            // Player.ExpectedPrice/ExpectedStd columns.
            var lineup = suggestionRequest.LineUp;
            var totalStarters = lineup.Keepers + lineup.Defenders + lineup.Midfielders + lineup.Attackers;
            var priceFormat = await PriceFormatResolver.ResolveAsync(_context, totalStarters, team.League.InitialBudget);
            var priceLookup = await PriceFormatResolver.LoadLookupAsync(_context, priceFormat);
            _logger.LogDebug("Price format resolved: {Format} (requested {Starters} starters, {Credits} credits).",
                priceFormat?.ToString() ?? "legacy (Player columns)", totalStarters, team.League.InitialBudget);

            var availablePlayersResult = await _leagueService.GetAllAvailablePlayersAsync(team.LeagueId);
            if (!availablePlayersResult.Success && availablePlayersResult.ErrorMessage != null)
                return ServiceResult<List<SuggestionResult>>.FailureResult(availablePlayersResult.ErrorMessage);

            var currentPlayers = BuildCurrentPlayers(team, suggestionRequest.FavoritePlayerIds, playerLookup, priceLookup, team.League);
            var availablePlayers = (availablePlayersResult.Data ?? []).ToList();

            // --- 1. BASE SUGGESTION TASK ---
            Task<SuggestionResult> baseTask;
            if (cachedBaseResult != null)
            {
                _logger.LogDebug("Reusing precomputed base team for team {TeamId}.", team.Id);
                baseTask = Task.FromResult(cachedBaseResult);
            }
            else
            {
                _logger.LogInformation("Starting base team suggestion.");
                baseTask = _scenarioCache.GetOrCreateAsync(resultCacheKey, async () =>
                {
                    var result = await ComputeSingleSuggestionAsync(
                        currentPlayers, availablePlayers, team, playerLookup, suggestionRequest, priceLookup, priceFormat);
                    _precomputer.StoreResult(team.Id, resultCacheKey, suggestionRequest, result);
                    return result;
                });
            }

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
                            Role: forcedPlayer.Role, Integrity: forcedPlayer.Integrity, Mate: forcedPlayer.Mate, Regularness: forcedPlayer.Regularness,
                            // League goal bonus: effective value + market price; the bid price stays the acquisition cost.
                            ExpectedPerformance: ScoringEngine.EffectiveExpectedPerformance(forcedPlayer.ExpectedPerformance, forcedPlayer.Role, team.League),
                            BaseExpectedPerformance: forcedPlayer.ExpectedPerformance, ExpectedStd: forcedMarket.Std,
                            MarketValue: ScoringEngine.EffectiveMarketPrice(forcedMarket.Price, forcedPlayer.Role, team.League),
                            AcquisitionCost: suggestionRequest.AuctionedPlayer.AcquisitionPrice
                        );

                        var potentialCurrentPlayers = new List<ScoringPlayer>(currentPlayers) { forcedScoringPlayer };

                        potentialTask = GetCachedScenarioAsync(
                            BuildScenarioCacheKey(resultCacheKey, "potential", forcedPlayer.Id,
                                suggestionRequest.AuctionedPlayer.AcquisitionPrice),
                            () => ComputeSingleSuggestionAsync(
                                potentialCurrentPlayers, excludedAvailablePlayers, team, playerLookup,
                                suggestionRequest, priceLookup, priceFormat, forcedScoringPlayer));
                    }
                    else
                    {
                        _logger.LogWarning("[Potential] Acquisition price {Price} exceeds initial budget {Budget}.",
                            suggestionRequest.AuctionedPlayer.AcquisitionPrice, team.League.InitialBudget);
                    }

                    // --- WITHOUT PLAYER PATH: player excluded from market entirely (Plan B) ---
                    _logger.LogInformation("Starting without-player team suggestion (excluding {Name} from market).",
                        forcedPlayer.Name);

                    withoutTask = GetCachedScenarioAsync(
                        BuildScenarioCacheKey(resultCacheKey, "without", forcedPlayer.Id),
                        () => ComputeSingleSuggestionAsync(
                            currentPlayers, excludedAvailablePlayers, team, playerLookup,
                            suggestionRequest, priceLookup, priceFormat, forcedPlayer: null));
                }
                else
                {
                    _logger.LogWarning("[Auctioned] Player {PlayerId} not found.", suggestionRequest.AuctionedPlayer.PlayerId);
                }
            }

            // Execute all computations concurrently
            await Task.WhenAll((Task)baseTask, (Task)potentialTask, (Task)withoutTask);

            var baseResult = CloneSuggestionResult(await baseTask);
            var potentialResult = await potentialTask;
            var withoutResult = await withoutTask;

            // --- PRECOMPUTE STORE ---
            // Base results are cached independently; auctioned enrichment is applied only
            // to a defensive copy so it cannot leak into the cached base result.
            if (suggestionRequest.AuctionedPlayer == null)
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
            var roster = string.Join(",", team.Players.Select(tp => $"{tp.PlayerId}:{tp.AuctionPrice}"));
            var favorites = string.Join(",", request.FavoritePlayerIds);
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
                allocation,
                (request.Weights ?? new StrategyWeights()).Signature,
                team.League.GoalBonusPerRole);

            return $"optimal:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))[..16]).ToLowerInvariant()}";
        }

        private static string BuildScenarioCacheKey(
            string baseResultCacheKey,
            string scenario,
            int playerId,
            int? acquisitionPrice = null)
        {
            var priceKey = acquisitionPrice?.ToString() ?? "none";
            return $"{scenario}:{baseResultCacheKey}:{playerId}:{priceKey}";
        }

        private async Task<SuggestionResult?> GetCachedScenarioAsync(
            string cacheKey,
            Func<Task<SuggestionResult>> factory)
            => await _scenarioCache.GetOrCreateAsync(cacheKey, factory);

        private static SuggestionResult CloneSuggestionResult(SuggestionResult source)
        {
            return new SuggestionResult
            {
                SuggestedPlayers = source.SuggestedPlayers.ToList(),
                TotalExpectedPrice = source.TotalExpectedPrice,
                TotalExpectedPriceStd = source.TotalExpectedPriceStd,
                Score = new Score
                {
                    StarterScore = source.Score.StarterScore,
                    BenchScore = source.Score.BenchScore,
                    PenaltyScore = source.Score.PenaltyScore,
                    TotalScore = source.Score.TotalScore
                }
            };
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
            var availablePlayersByRole = BuildAvailableByRole(availablePlayers, priceLookup, team.League);
            var scoringContext = new ScoringContext(
                currentPlayers.Concat(availablePlayersByRole.Values.SelectMany(players => players)),
                suggestionRequest.Weights ?? new StrategyWeights());

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
                    priceFormat: priceFormat,
                    scoringContext: scoringContext)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["D"],
                    slots:      playersToBuy["D"],
                    maxBudget:  dpMaxPerRole["D"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat,
                    scoringContext: scoringContext)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["C"],
                    slots:      playersToBuy["C"],
                    maxBudget:  dpMaxPerRole["C"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat,
                    scoringContext: scoringContext)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["A"],
                    slots:      playersToBuy["A"],
                    maxBudget:  dpMaxPerRole["A"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    currentPlayers: currentPlayers,
                    forcedPlayer: forcedPlayer,
                    priceFormat: priceFormat,
                    scoringContext: scoringContext))
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
                priceLookup: priceLookup,
                scoringContext: scoringContext
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
            Dictionary<int, (int Price, double Std)>? priceLookup,
            ScoringContext scoringContext)
        {
            var finalCombination = CombineRoleResults(
                roleValueTables: roleValueTables,
                scoreableLookup: scoreableLookup,
                currentPlayers: currentPlayers,
                playersToBuy: playersToBuy,
                totalBudget: totalBudget,
                suggestionRequest: suggestionRequest,
                league: league,
                capsPerRole: capsPerRole,
                scoringContext: scoringContext
            );

            var results = BacktrackToGetTeams(
                finalCombination: finalCombination,
                playerLookup: playerLookup,
                numTeams: numTeams,
                priceLookup: priceLookup,
                league: league
            );

            if (results.Count > 0)
                return results[0];

            return new SuggestionResult
            {
                SuggestedPlayers = [],
                Score = new Score()
            };
        }

        private static List<ScoringPlayer> BuildCurrentPlayers(Team team, List<int> favoritePlayerIds, Dictionary<int, Player> playerLookup, Dictionary<int, (int Price, double Std)>? priceLookup, League league)
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
                        Integrity: tp.Player.Integrity,
                        Mate: tp.Player.Mate,
                        Regularness: tp.Player.Regularness,
                        // League goal bonus: effective value and market value are adjusted;
                        // the paid auction price is the acquisition cost (never scaled).
                        ExpectedPerformance: ScoringEngine.EffectiveExpectedPerformance(tp.Player.ExpectedPerformance, tp.Player.Role, league),
                        BaseExpectedPerformance: tp.Player.ExpectedPerformance,
                        ExpectedStd: 0,
                        MarketValue: ScoringEngine.EffectiveMarketPrice(market.Price, tp.Player.Role, league),
                        AcquisitionCost: tp.AuctionPrice
                    );
                })
                .ToList();

            foreach (var playerId in favoritePlayerIds)
            {
                var fp = playerLookup[playerId];
                var market = ResolveMarketPrice(priceLookup, fp);
                var marketPrice = ScoringEngine.EffectiveMarketPrice(market.Price, fp.Role, league);
                currentPlayers.Add(new ScoringPlayer(
                    Id: fp.Id, Name: fp.Name, Squad: fp.Squad, Role: fp.Role,
                    Integrity: fp.Integrity, Mate: fp.Mate, Regularness: fp.Regularness,
                    ExpectedPerformance: ScoringEngine.EffectiveExpectedPerformance(fp.ExpectedPerformance, fp.Role, league),
                    BaseExpectedPerformance: fp.ExpectedPerformance, ExpectedStd: market.Std,
                    MarketValue: marketPrice, AcquisitionCost: marketPrice
                ));
            }

            return currentPlayers;
        }

        private static Dictionary<string, List<ScoringPlayer>> BuildAvailableByRole(List<Player> availablePlayers, Dictionary<int, (int Price, double Std)>? priceLookup, League league)
        {
            var scoringPlayers = availablePlayers.Select(p =>
            {
                var market = ResolveMarketPrice(priceLookup, p);
                var marketPrice = ScoringEngine.EffectiveMarketPrice(market.Price, p.Role, league);
                return new ScoringPlayer(
                    Id: p.Id, Name: p.Name, Squad: p.Squad, Role: p.Role,
                    Integrity: p.Integrity, Mate: p.Mate, Regularness: p.Regularness,
                    ExpectedPerformance: ScoringEngine.EffectiveExpectedPerformance(p.ExpectedPerformance, p.Role, league),
                    BaseExpectedPerformance: p.ExpectedPerformance, ExpectedStd: market.Std,
                    MarketValue: marketPrice, AcquisitionCost: marketPrice
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
            ScoringPlayer? forcedPlayer,
            (int Credits, int Starters)? priceFormat,
            ScoringContext scoringContext)
        {
            string role = players.Count > 0 ? players[0].Role : string.Empty;
            var currentRoleMates = currentPlayers.Where(p => p.Role == role).ToList();
            bool applyForcedPlayer = forcedPlayer != null && role == forcedPlayer.Role;

            // --- CACHE LOOKUP ---
            var cacheKey = BuildDpCacheKey(
                role: role,
                rolePlayerIds: players.Select(p => p.Id).ToList(),
                slots: slots,
                maxBudget: maxBudget,
                forcedPlayerId: applyForcedPlayer ? forcedPlayer!.Id : null,
                currentRoleMates: currentRoleMates.Select(p => (p.Id, p.AcquisitionCost)).ToList(),
                forcedPlayerAcquisitionCost: applyForcedPlayer ? forcedPlayer!.AcquisitionCost : null,
                lineup: suggestionRequest.LineUp,
                creditsDistribution: suggestionRequest.CreditsDistribution,
                budgetAllocation: suggestionRequest.BudgetAllocation,
                priceFormat: priceFormat,
                weights: suggestionRequest.Weights,
                goalBonus: league.GoalBonusPerRole,
                dataVersion: _precomputer.DataVersion
            );

            if (_cache.TryGetValue(cacheKey, out RoleValueTable? cached))
            {
                _logger.LogDebug("[Cache hit] Role={Role} Slots={Slots} Budget={Budget}",
                    cacheKey.Split(':')[1], slots, maxBudget);
                return cached!;
            }

            _logger.LogDebug("[Cache miss] Role={Role} Slots={Slots} Budget={Budget}",
                cacheKey.Split(':')[1], slots, maxBudget);

            var computation = InflightDpComputations.GetOrAdd(
                cacheKey,
                _ => new Lazy<RoleValueTable>(
                    () => ComputeRoleValues(
                        cacheKey,
                        role,
                        players,
                        slots,
                        maxBudget,
                        suggestionRequest,
                        currentRoleMates,
                        forcedPlayer,
                        applyForcedPlayer,
                        scoringContext),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return computation.Value;
            }
            finally
            {
                if (InflightDpComputations.TryGetValue(cacheKey, out var current)
                    && ReferenceEquals(current, computation))
                {
                    InflightDpComputations.TryRemove(cacheKey, out _);
                }
            }
        }

        private RoleValueTable ComputeRoleValues(
            string cacheKey,
            string role,
            List<ScoringPlayer> players,
            int slots,
            int maxBudget,
            SuggestionRequest suggestionRequest,
            List<ScoringPlayer> currentRoleMates,
            ScoringPlayer? forcedPlayer,
            bool applyForcedPlayer,
            ScoringContext scoringContext)
        {
            // --- DP COMPUTATION ---
            RoleValueTable roleValueTable = new() { };
            var playerById = players.ToDictionary(p => p.Id);
            // Transition memo: key is (previous selection, added player). The list part is
            // deliberately compared by REFERENCE (List<int> has no value equality): the
            // dense table shares one PlayerSelectionResult object across the budget cells
            // that hold the same selection (gap fill copies the reference), so identical
            // selections are the same object and dedup is exact. Different objects always
            // mean different transitions (miss = recompute, never a wrong result).
            var transitionCache = new Dictionary<(List<int>? PreviousPlayerIds, int PlayerId), PlayerSelectionResult?>();
            int transitionEvaluationCount = 0;
            int transitionReuseCount = 0;

            for (int k = 1; k <= slots; k++)
            {
                for (int b = 1; b <= maxBudget; b++)
                {
                    foreach (var player in players)
                    {
                        if (player.AcquisitionCost > b)
                            continue;

                        var prevSelection = roleValueTable.GetPlayerSelection(k - 1, b - player.AcquisitionCost);
                        if (prevSelection == null && k != 1)
                            continue;

                        var transitionKey = (prevSelection?.PlayerIds, player.Id);
                        if (!transitionCache.TryGetValue(transitionKey, out var candidateSelection))
                        {
                            transitionEvaluationCount++;
                            if (prevSelection?.PlayerIds.Contains(player.Id) == true)
                            {
                                transitionCache[transitionKey] = null;
                                continue;
                            }

                            var candidatePlayerIds = prevSelection != null
                                ? new List<int>(prevSelection.PlayerIds)
                                : new List<int>();
                            candidatePlayerIds.Add(player.Id);

                            // Score against the full role unit: candidates + current mates + forced player
                            var evalPlayers = candidatePlayerIds
                                .Select(id => playerById[id])
                                .ToList();
                            evalPlayers.AddRange(currentRoleMates);
                            if (applyForcedPlayer)
                                evalPlayers.Add(forcedPlayer!);

                            candidateSelection = new PlayerSelectionResult
                            {
                                Score = ScoringEngine.CalculateScore(evalPlayers, suggestionRequest, scoringContext),
                                PlayerIds = candidatePlayerIds
                            };
                            transitionCache[transitionKey] = candidateSelection;
                        }
                        else
                        {
                            transitionReuseCount++;
                        }

                        if (candidateSelection == null)
                            continue;

                        var currentSelection = roleValueTable.GetPlayerSelection(k, b);
                        if (currentSelection == null || candidateSelection.Score.TotalScore > currentSelection.Score.TotalScore)
                        {
                            roleValueTable.SetPlayerSelection(
                                k, b,
                                candidateSelection.Score,
                                candidateSelection.PlayerIds);
                        }
                    }
                }
            }

            _logger.LogDebug(
                "[Stage 1 dedup] Role={Role} UniqueTransitions={UniqueTransitions} ReusedTransitions={ReusedTransitions}",
                role,
                transitionEvaluationCount,
                transitionReuseCount);

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
            Dictionary<string, int> capsPerRole,
            ScoringContext scoringContext)
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

                var currEntries = AssignSelectionIds(currTable);
                var prevEntries = AssignSelectionIds(prevSelections);
                var evaluationCache = new Dictionary<(int PreviousSelectionId, int CurrentSelectionId), PlayerSelectionResult>();
                int feasiblePairCount = 0;

                foreach (var curr in currEntries)
                {
                    foreach (var prev in prevEntries)
                    {
                        int newBudget = prev.Budget + curr.Budget;
                        if (newBudget > totalBudget)
                            continue;

                        feasiblePairCount++;
                        var evaluationKey = (prev.SelectionId, curr.SelectionId);
                        if (!evaluationCache.TryGetValue(evaluationKey, out var evaluation))
                        {
                            var newIds = prev.Selection.PlayerIds
                                .Concat(curr.Selection.PlayerIds)
                                .ToList();
                            List<ScoringPlayer> selectedPlayers = newIds
                                .Where(scoreableLookup.ContainsKey)
                                .Select(id => scoreableLookup[id])
                                .ToList();

                            evaluation = new PlayerSelectionResult
                            {
                                Score = ScoringEngine.CalculateScore(selectedPlayers, suggestionRequest, scoringContext),
                                PlayerIds = newIds
                            };
                            evaluationCache[evaluationKey] = evaluation;
                        }

                        Score lastScore = finalTable.GetPlayerSelection(t, newBudget)?.Score ?? new Score { TotalScore = double.MinValue };

                        if (evaluation.Score.TotalScore > lastScore.TotalScore)
                            finalTable.SetPlayerSelection(
                                roleNum: t,
                                budget: newBudget,
                                score: evaluation.Score,
                                playerIds: evaluation.PlayerIds);
                    }
                }

                _logger.LogDebug(
                    "[Combination dedup] Role={Role} FeasiblePairs={FeasiblePairs} UniqueEvaluations={UniqueEvaluations}",
                    role,
                    feasiblePairCount,
                    evaluationCache.Count);
            }

            return finalTable;
        }

        private static List<(int Budget, PlayerSelectionResult Selection, int SelectionId)> AssignSelectionIds(
            Dictionary<int, PlayerSelectionResult> selections)
        {
            var selectionIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var entries = new List<(int Budget, PlayerSelectionResult Selection, int SelectionId)>(selections.Count);

            foreach (var entry in selections)
            {
                var selectionKey = string.Join(',', entry.Value.PlayerIds);
                if (!selectionIds.TryGetValue(selectionKey, out int selectionId))
                {
                    selectionId = selectionIds.Count;
                    selectionIds[selectionKey] = selectionId;
                }

                entries.Add((entry.Key, entry.Value, selectionId));
            }

            return entries;
        }

        private List<SuggestionResult> BacktrackToGetTeams(FinalCombinationResult finalCombination, Dictionary<int, Player> playerLookup, int numTeams, Dictionary<int, (int Price, double Std)>? priceLookup, League league)
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
                // (legacy Player columns as fallback), adjusted for the league goal bonus.
                var teamPlayersStd = (int)Math.Sqrt(teamPlayers.Sum(p =>
                {
                    var std = priceLookup != null && priceLookup.TryGetValue(p.Id, out var pp) ? pp.Std : p.ExpectedStd;
                    return Math.Pow(ScoringEngine.EffectivePriceStd(std, p.Role, league), 2);
                }));

                // Keep the displayed per-player prices consistent with the format used for scoring
                // (the legacy Player columns carry the reference format only) and with the
                // league goal-bonus price uplift applied during scoring.
                var playerDtos = PlayerMapper.ToReadDtos(teamPlayers);
                foreach (var dto in playerDtos)
                {
                    var (rawPrice, rawStd) = priceLookup != null && priceLookup.TryGetValue(dto.Id, out var pp)
                        ? (pp.Price, pp.Std)
                        : (dto.ExpectedPrice, dto.ExpectedStd);
                    dto.ExpectedPrice = ScoringEngine.EffectiveMarketPrice(rawPrice, dto.Role, league);
                    dto.ExpectedStd = ScoringEngine.EffectivePriceStd(rawStd, dto.Role, league);
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
        /// forced player context, current role-mates, lineup configuration, credits distribution,
        /// budget allocation, price format, personal-preference weights and the league
        /// goal-bonus rule (it changes both values and prices).
        /// </summary>
        private static string BuildDpCacheKey(
            string role,
            IReadOnlyList<int> rolePlayerIds,
            int slots,
            int maxBudget,
            int? forcedPlayerId,
            IReadOnlyList<(int Id, int AcquisitionCost)> currentRoleMates,
            int? forcedPlayerAcquisitionCost,
            LineUp lineup,
            int creditsDistribution,
            BudgetAllocation? budgetAllocation,
            (int Credits, int Starters)? priceFormat,
            StrategyWeights? weights,
            bool goalBonus,
            long dataVersion)
        {
            // Deterministic hash from sorted player IDs
            var hash = rolePlayerIds.OrderBy(id => id).Aggregate(0L, (h, id) => h ^ (id.GetHashCode() * 31L));
            var lineupKey = $"{lineup.Defenders}-{lineup.Midfielders}-{lineup.Attackers}";
            var forcedKey = forcedPlayerId.HasValue
                ? $"{forcedPlayerId.Value}:{forcedPlayerAcquisitionCost ?? 0}"
                : "none";
            var matesKey = string.Join(',', currentRoleMates.Select(mate => $"{mate.Id}:{mate.AcquisitionCost}"));
            var allocKey = budgetAllocation != null
                ? $"{budgetAllocation.Goalkeepers:F2}-{budgetAllocation.Defenders:F2}-{budgetAllocation.Midfielders:F2}-{budgetAllocation.Attackers:F2}"
                : "default";
            // The DP prices players with format-specific expected prices, so the format
            // must be part of the key (two leagues can share lineup + per-role budgets
            // while using different price formats).
            var formatKey = priceFormat == null ? "legacy" : $"{priceFormat.Value.Credits}_{priceFormat.Value.Starters}";
            var weightsKey = (weights ?? new StrategyWeights()).Signature;
            var bonusKey = goalBonus ? "gb" : "nobb";
            return $"dp:{role}:v{dataVersion}:{hash}:{slots}:{maxBudget}:{forcedKey}:{matesKey}:{lineupKey}:{creditsDistribution}:{allocKey}:{formatKey}:{weightsKey}:{bonusKey}";
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