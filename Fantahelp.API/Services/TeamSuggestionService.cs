using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fantahelp.API.Services
{
    public class TeamSuggestionService : ITeamSuggestionService
    {
        private readonly FantahelpContext _context;
        private readonly ILeagueService _leagueService;
        private readonly ILogger<TeamSuggestionService> _logger;

        public TeamSuggestionService(FantahelpContext context, ILeagueService leagueService, ILogger<TeamSuggestionService> logger)
        {
            _context = context;
            _leagueService = leagueService;
            _logger = logger;
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

            var availablePlayersResult = await _leagueService.GetAllAvailablePlayersAsync(team.LeagueId);
            if (!availablePlayersResult.Success && availablePlayersResult.ErrorMessage != null)
                return ServiceResult<List<SuggestionResult>>.FailureResult(availablePlayersResult.ErrorMessage);

            var currentPlayers = BuildCurrentPlayers(team, suggestionRequest.FavoritePlayerIds, playerLookup);
            var availablePlayers = (availablePlayersResult.Data ?? []).ToList();

            // --- 1. BASE SUGGESTION TASK ---
            _logger.LogInformation("Starting base team suggestion.");
            var baseTask = ComputeSingleSuggestionAsync(currentPlayers, availablePlayers, team, playerLookup, suggestionRequest);

            // --- 2. POTENTIAL SUGGESTION TASK (if auctioned player is provided) ---
            Task<SuggestionResult?> potentialTask = Task.FromResult<SuggestionResult?>(null);

            if (suggestionRequest.AuctionedPlayer != null)
            {
                if (playerLookup.TryGetValue(suggestionRequest.AuctionedPlayer.PlayerId, out var forcedPlayer))
                {
                    if (suggestionRequest.AuctionedPlayer.AcquisitionPrice <= team.League.InitialBudget)
                    {
                        _logger.LogInformation("Starting potential team suggestion for forced player {Name} (Id={Id}) at price {Price}.",
                            forcedPlayer.Name, forcedPlayer.Id, suggestionRequest.AuctionedPlayer.AcquisitionPrice);

                        var forcedScoringPlayer = new ScoringPlayer(
                            Id: forcedPlayer.Id, Name: forcedPlayer.Name, Squad: forcedPlayer.Squad,
                            Role: forcedPlayer.Role, Mate: forcedPlayer.Mate, Regularness: forcedPlayer.Regularness,
                            ExpectedPerformance: forcedPlayer.ExpectedPerformance, ExpectedStd: forcedPlayer.ExpectedStd,
                            MarketValue: (int)forcedPlayer.ExpectedPrice,
                            AcquisitionCost: suggestionRequest.AuctionedPlayer.AcquisitionPrice
                        );

                        var potentialCurrentPlayers = new List<ScoringPlayer>(currentPlayers) { forcedScoringPlayer };
                        var potentialAvailablePlayers = availablePlayers.Where(p => p.Id != forcedPlayer.Id).ToList();

                        potentialTask = ComputeSingleSuggestionAsync(
                            potentialCurrentPlayers, potentialAvailablePlayers, team, playerLookup, suggestionRequest, forcedScoringPlayer)
                            .ContinueWith(t => (SuggestionResult?)t.Result);
                    }
                    else
                    {
                        _logger.LogWarning("[Potential] Acquisition price {Price} exceeds initial budget {Budget}.",
                            suggestionRequest.AuctionedPlayer.AcquisitionPrice, team.League.InitialBudget);
                    }
                }
                else
                {
                    _logger.LogWarning("[Potential] Player {PlayerId} not found.", suggestionRequest.AuctionedPlayer.PlayerId);
                }
            }

            // Execute base and potential computations concurrently
            await Task.WhenAll((Task)baseTask, (Task)potentialTask);

            var baseResult = await baseTask;
            var potentialResult = await potentialTask;

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

            return ServiceResult<List<SuggestionResult>>.SuccessResult([baseResult]);
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
            ScoringPlayer? forcedPlayer = null)
        {
            var budgetSpentPerRole = ComputeBudgetSpentPerRole(team, forcedPlayer);
            var playersToBuy = ComputePlayersToBuy(currentPlayers);
            var availablePlayersByRole = BuildAvailableByRole(availablePlayers);

            var baseCapsPerRole = ComputeMaxBudgetsPerRole(team.League.InitialBudget, budgetSpentPerRole);

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
                    forcedPlayer: forcedPlayer)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["D"],
                    slots:      playersToBuy["D"],
                    maxBudget:  dpMaxPerRole["D"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    forcedPlayer: forcedPlayer)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["C"],
                    slots:      playersToBuy["C"],
                    maxBudget:  dpMaxPerRole["C"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    forcedPlayer: forcedPlayer)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["A"],
                    slots:      playersToBuy["A"],
                    maxBudget:  dpMaxPerRole["A"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League,
                    forcedPlayer: forcedPlayer))
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
                capsPerRole: capsPerRoleAdjusted
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
            Dictionary<string, int> capsPerRole)
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
                playerLookup: playerLookup
            );

            if (results.Count > 0)
                return results[0];

            return new SuggestionResult
            {
                SuggestedPlayers = [],
                Score = new Score()
            };
        }

        private static List<ScoringPlayer> BuildCurrentPlayers(Team team, List<int> favoritePlayerIds, Dictionary<int, Player> playerLookup)
        {
            var currentPlayers = team.Players
                .Select(tp => new ScoringPlayer(
                    Id: tp.Player.Id,
                    Name: tp.Player.Name,
                    Squad: tp.Player.Squad,
                    Role: tp.Player.Role,
                    Mate: tp.Player.Mate,
                    Regularness: tp.Player.Regularness,
                    ExpectedPerformance: tp.Player.ExpectedPerformance,
                    ExpectedStd: 0,
                    MarketValue: (int)tp.Player.ExpectedPrice,
                    AcquisitionCost: tp.AuctionPrice
                ))
                .ToList();

            foreach (var playerId in favoritePlayerIds)
            {
                var fp = playerLookup[playerId];
                currentPlayers.Add(new ScoringPlayer(
                    Id: fp.Id, Name: fp.Name, Squad: fp.Squad, Role: fp.Role,
                    Mate: fp.Mate, Regularness: fp.Regularness,
                    ExpectedPerformance: fp.ExpectedPerformance, ExpectedStd: fp.ExpectedStd,
                    MarketValue: (int)fp.ExpectedPrice, AcquisitionCost: (int)fp.ExpectedPrice
                ));
            }

            return currentPlayers;
        }

        private static Dictionary<string, List<ScoringPlayer>> BuildAvailableByRole(List<Player> availablePlayers)
        {
            var scoringPlayers = availablePlayers.Select(p => new ScoringPlayer(
                Id: p.Id, Name: p.Name, Squad: p.Squad, Role: p.Role,
                Mate: p.Mate, Regularness: p.Regularness,
                ExpectedPerformance: p.ExpectedPerformance, ExpectedStd: p.ExpectedStd,
                MarketValue: (int)p.ExpectedPrice, AcquisitionCost: (int)p.ExpectedPrice
            )).ToList();

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

        private static Dictionary<string, int> ComputeMaxBudgetsPerRole(int initialBudget, Dictionary<string, int> budgetSpentPerRole)
        {
            var result = MaxPercInterval.ToDictionary(
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
            ScoringPlayer? forcedPlayer = null)
        {
            RoleValueTable roleValueTable = new() { };
            var playerById = players.ToDictionary(p => p.Id);

            string role = players.Count > 0 ? players[0].Role : string.Empty;
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

                        var evalPlayers = applyForcedPlayer
                            ? new List<ScoringPlayer>(candidatePlayers) { forcedPlayer! }
                            : candidatePlayers;

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

        private List<SuggestionResult> BacktrackToGetTeams(FinalCombinationResult finalCombination, Dictionary<int, Player> playerLookup, int numTeams)
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

                var teamPlayersStd = (int)Math.Sqrt(teamPlayers.Sum(p => Math.Pow(p.ExpectedStd, 2)));
                suggestionResults.Add(new SuggestionResult
                {
                    SuggestedPlayers = PlayerMapper.ToReadDtos(teamPlayers),
                    TotalExpectedPrice = proposedTeam.Price,
                    TotalExpectedPriceStd = teamPlayersStd,
                    Score = proposedTeam.PlayerSelection.Score,
                });
            }

            return suggestionResults;
        }
    }
}