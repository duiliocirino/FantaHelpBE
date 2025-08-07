using System.Linq;
using System.Reflection.Emit;
using System.Runtime.ConstrainedExecution;
using Microsoft.EntityFrameworkCore;

namespace Fantahelp.API.Services
{
    public class TeamSuggestionService : ITeamSuggestionService
    {
        private readonly FantahelpContext _context;
        private readonly ILeagueService _leagueService;

        // The DbContext is "injected" into the service via the constructor.
        public TeamSuggestionService(FantahelpContext context, ILeagueService leagueService)
        {
            _context = context;
            _leagueService = leagueService;
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

        public Dictionary<string, int> GoalBonusPerRole { get; set; } = new Dictionary<string, int>
        {
            { "P", 6 },
            { "D", 5 },
            { "C", 4 },
            { "A", 3 }
        };
        private static readonly Dictionary<string, double> GoalPercPerRole = new()
        {
            { "P", 0.0 },
            { "D", 0.1 },
            { "C", 0.3 },
            { "A", 0.6 }
        };
        private readonly double startMultiplier = 0.6;
        private readonly double subsMultiplier = 0.3;
        private readonly double strategyMultiplier = 0.1;

        public async Task<ServiceResult<List<SuggestionResult>>> GetOptimalTeamSuggestionAsync(SuggestionRequest suggestionRequest)
        {
            // --- DATA PREPARATION ---
            Dictionary<int, Player> playerLookup = _context.Players.ToDictionary(p => p.Id, p => p);
            Console.WriteLine($"PlayerLookup count: {playerLookup.Count}");

            Team? team = await _context.Teams
                .Include(t => t.League)      // Loads the related League entity
                .Include(t => t.Players)     // Loads the related Players collection
                .ThenInclude(tp => tp.Player) // Loads the Player entity for each TeamPlayer
                .FirstOrDefaultAsync(t => t.Id == suggestionRequest.TeamId);

            if (team == null)
                return ServiceResult<List<SuggestionResult>>.FailureResult("No Team was found with the given teamId.");

            var availablePlayersResult = await _leagueService.GetAllAvailablePlayersAsync(team.LeagueId);
            if (!availablePlayersResult.Success && availablePlayersResult.ErrorMessage != null)
                return ServiceResult<List<SuggestionResult>>.FailureResult(availablePlayersResult.ErrorMessage);

            var currentPlayers = team.Players
                .Select(tp => new Player
                {
                    Id = tp.Player.Id,
                    Name = tp.Player.Name,
                    Squad = tp.Player.Squad,
                    Role = tp.Player.Role,
                    Role_M = tp.Player.Role_M,
                    Price = tp.Player.Price,
                    Age = tp.Player.Age,
                    Rating = tp.Player.Rating,
                    Mate = tp.Player.Mate,
                    Regularness = tp.Player.Regularness,
                    FVM = tp.Player.FVM,
                    ExpectedPerformance = tp.Player.ExpectedPerformance,
                    ExpectedStd = 0,
                    ExpectedPrice = tp.AuctionPrice, // Set to AuctionPrice
                    TeamPlayers = tp.Player.TeamPlayers
                })
                .ToList();

            foreach (var playerId in suggestionRequest.FavoritePlayerIds)
                currentPlayers.Add(playerLookup[playerId]);

            Console.WriteLine($"CurrentPlayers count: {currentPlayers.Count}");
            foreach (var p in currentPlayers)
                Console.WriteLine($"CurrentPlayer: {p.Id}, {p.Name}, {p.Role}, AuctionPrice: {p.ExpectedPrice}");

            var currentPlayersByRole = currentPlayers
                .GroupBy(p => p.Role)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList()
                );
            // Ensure all roles are present, even if empty
            foreach (var role in Roles)
            {
                if (!currentPlayersByRole.ContainsKey(role))
                    currentPlayersByRole[role] = [];
                Console.WriteLine($"CurrentPlayersByRole[{role}]: {currentPlayersByRole[role].Count}");
            }

            var availablePlayers = availablePlayersResult.Data;
            var availablePlayersByRole = (availablePlayers ?? [])
                .GroupBy(p => p.Role)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList()
                );
            // Ensure all roles are present, even if empty
            foreach (var role in Roles)
            {
                if (!availablePlayersByRole.ContainsKey(role))
                    availablePlayersByRole[role] = [];
                Console.WriteLine($"AvailablePlayersByRole[{role}]: {availablePlayersByRole[role].Count}");
            }

            var bugdetSpentPerRole = team.Players
                .GroupBy(tp => tp.Player.Role)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(tp => tp.AuctionPrice)
                );
            // Ensure all roles are present, even if empty
            foreach (var role in Roles)
            {
                if (!bugdetSpentPerRole.ContainsKey(role))
                    bugdetSpentPerRole[role] = 0;
                Console.WriteLine($"BudgetSpentPerRole[{role}]: {bugdetSpentPerRole[role]}");
            }

            var maxBudgetsPerRole = MaxPercInterval
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (int)(kvp.Value * team.League.InitialBudget - bugdetSpentPerRole[kvp.Key])
                );
            foreach (var role in Roles)
                Console.WriteLine($"MaxBudgetPerRole[{role}]: {maxBudgetsPerRole[role]}");

            var playersToBuy = PlayersPerRole
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                            - (currentPlayersByRole.TryGetValue(kvp.Key, out var list)
                                ? list.Count
                                : 0)
                );
            foreach (var role in Roles)
                Console.WriteLine($"PlayersToBuy[{role}]: {playersToBuy[role]}");

            // --- STAGE 1: PRE-COMPUTATION PER ROLE ---

            Console.WriteLine("Stage 1: Pre-computation per role started.");

            var stage1Tasks = new[]
            {
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["P"],
                    slots:      playersToBuy["P"],
                    maxBudget:  maxBudgetsPerRole["P"],
                    lineUp:     suggestionRequest.LineUp,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["D"],
                    slots:      playersToBuy["D"],
                    maxBudget:  maxBudgetsPerRole["D"],
                    lineUp:     suggestionRequest.LineUp,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["C"],
                    slots:      playersToBuy["C"],
                    maxBudget:  maxBudgetsPerRole["C"],
                    lineUp:     suggestionRequest.LineUp,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["A"],
                    slots:      playersToBuy["A"],
                    maxBudget:  maxBudgetsPerRole["A"],
                    lineUp:     suggestionRequest.LineUp,
                    league:     team.League))
            };

            var results = await Task.WhenAll(stage1Tasks);

            var goalkeeperValues = results[0];
            Console.WriteLine("Goalkeeper values precomputed.");
            PrintTopExamples(goalkeeperValues, "Goalkeeper", playersToBuy["P"], playerLookup);

            var defenderValues = results[1];
            Console.WriteLine("Defender values precomputed.");
            PrintTopExamples(defenderValues, "Defender", playersToBuy["D"], playerLookup);

            var midfielderValues = results[2];
            Console.WriteLine("Midfielder values precomputed.");
            PrintTopExamples(midfielderValues, "Midfielder", playersToBuy["C"], playerLookup);

            var attackerValues = results[3];
            Console.WriteLine("Attacker values precomputed.");
            PrintTopExamples(attackerValues, "Attacker", playersToBuy["A"], playerLookup);

            // --- STAGE 2: FINAL COMBINATION ---
            Console.WriteLine("Stage 2: Final combination started.");
            var finalCombination = CombineRoleResults(
                roleValueTables:    [goalkeeperValues, defenderValues, midfielderValues, attackerValues],
                currentPlayers:     currentPlayers,
                playersToBuy:       playersToBuy,
                totalBudget:        team.RemainingBudget,
                lineUp:             suggestionRequest.LineUp,
                playerLookup:       playerLookup,
                league:             team.League
            );
            Console.WriteLine("Final combination completed.");

            // --- STAGE 3: BACKTRACK AND RETURN ---
            Console.WriteLine("Stage 3: Backtrack and return started.");
            var suggestedResults = BacktrackToGetTeams(
                finalCombination: finalCombination,
                numTeams: suggestionRequest.NumTeams,
                playerLookup: playerLookup);
            Console.WriteLine("Backtrack and return completed.");

            return ServiceResult<List<SuggestionResult>>.SuccessResult(suggestedResults);
        }

        // --- PRIVATE METHODS ---
        // Add this helper method to the TeamSuggestionService class:
        private void PrintTopExamples(RoleValueTable table, string roleName, int slots, Dictionary<int, Player> playerLookup)
        {
            var entries = table.Table.ToList();
            var topExamples = entries
                .Where(x => x.Key.NumPlayers == slots)
                .OrderByDescending(x => x.Value.Score.TotalScore)
                .Take(10)
                .ToList();

            Console.WriteLine($"--- {roleName} Top Score Entries ---");
            foreach (var example in topExamples)
            {
                var playerNames = example.Value.PlayerIds
                    .Select(id => playerLookup.ContainsKey(id) ? playerLookup[id].Name : $"Unknown({id})");
                Console.WriteLine($"NumPlayers: {example.Key.NumPlayers}, Budget: {example.Key.Budget}, Score: {example.Value.Score.TotalScore}");
                Console.WriteLine($"PlayerIds: {string.Join(",", example.Value.PlayerIds)}");
                Console.WriteLine($"PlayerNames: {string.Join(",", playerNames)}");
            }
            Console.WriteLine($"--- End {roleName} Top Score Examples ---");
        }
        private Score CalculateScore(List<Player> players, LineUp lineUp, League league)
        {
            var playersByRole = players
                .GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.ExpectedPrice).ToList());

            List<Player> starters = [];
            List<Player> subs = [];

            foreach (var role in Roles)
            {
                if (!playersByRole.TryGetValue(role, out var rolePlayers) || rolePlayers.Count == 0)
                    continue;

                int starterCount = role switch
                {
                    "P" => 1,
                    "D" => lineUp.Defenders,
                    "C" => lineUp.Midfielders,
                    "A" => lineUp.Attackers,
                    _ => 0
                };

                // Use AddRange only if there are enough players
                if (starterCount > 0 && rolePlayers.Count >= starterCount)
                {
                    starters.AddRange(rolePlayers.Take(starterCount));
                    subs.AddRange(rolePlayers.Skip(starterCount));
                }
                else
                {
                    starters.AddRange(rolePlayers);
                }
            }

            double startingScore = ComputeStarterContribution(starters, lineUp);
            double subsScore = ComputeSubContribution(subs, starters);
            double strategyScore = ComputeStrategiesScore(starters, subs, league);

            return new Score
            {
                StarterScore = startingScore,
                BenchScore = subsScore,
                PenaltyScore = strategyScore,
                TotalScore = startMultiplier * startingScore + subsMultiplier * subsScore + strategyMultiplier * strategyScore
            };
        }

        private double ComputeStarterContribution(List<Player> players, LineUp lineUp)
        {
            int bonusDefense = 0;

            if (lineUp.Defenders >= 4)
            {
                var gk = players.Where(p => p.Role == "P").OrderByDescending(p => p.ExpectedPerformance).FirstOrDefault();
                var topDefenders = players.Where(p => p.Role == "D")
                                    .OrderByDescending(p => p.ExpectedPerformance)
                                    .Take(3)
                                    .ToList();
                var selected = new List<Player>();
                if (gk != null)
                    selected.Add(gk);
                selected.AddRange(topDefenders);

                if (selected.Count == 4)
                {
                    double avgScore = selected.Average(p => p.ExpectedPerformance);
                    if (avgScore >= 6)
                    {
                        // Bonus: 1 for each 0.25 above 6 (floor to nearest category)
                        bonusDefense = (int)Math.Floor((avgScore - 6) / 0.25) + 1;
                    }
                }
            }

            double score = players.Sum(p => p.ExpectedPerformance) + bonusDefense;
            return score;
        }

        private double ComputeSubContribution(List<Player> subs, List<Player> starters)
        {
            double sumRatio = 0;

            foreach (var role in Roles)
            {
                var startersRole = starters.Where(p => p.Role == role).ToList();
                var subsRole = subs.Where(p => p.Role == role).ToList();

                double startersAvg = startersRole.Count > 0 ? startersRole.Average(p => p.ExpectedPerformance) : 0;
                double subsAvg = subsRole.Count > 0 ? subsRole.Average(p => p.ExpectedPerformance) : 0;

                // Avoid division by zero
                double ratio = (startersAvg > 0) ? (subsAvg / startersAvg) : 0;
                sumRatio += ratio;
            }

            return sumRatio;
        }

        private double ComputeStrategiesScore(List<Player> starters, List<Player> subs, League league)
        {
            double strategyScore = 0;
            double goalBonusPerRole = 0;
            double regularnessStartersScore = 0;
            double regularnessSubsScore = 0;
            double mateScore = 0;
            double sameTeamScore = 0;

            // --- Goal Bonuses Per Role ---
            {
                if (league.GoalBonusPerRole)
                    foreach (var role in Roles)
                    {
                        if (role != "P" && role != "A" && starters.Count > 0)
                        {
                            var startersForRole = starters.Where(p => p.Role == role).ToList();
                            if (startersForRole.Count > 0)
                                goalBonusPerRole += (GoalBonusPerRole[role] - 3) * GoalPercPerRole[role] * starters
                                    .Where(p => p.Role == role)
                                    .Average(p => p.ExpectedPerformance - 5);
                        }
                    }
            }
            // --- Regularness ---
            {
                if (starters.Count > 0)
                {
                    var regularnessStarters = starters.Average(p => p.Regularness);
                    regularnessStartersScore = regularnessStarters - 4;
                }

                if (subs.Count > 0)
                {
                    var regularnessSubs = subs.Average(p => p.Regularness);
                    regularnessSubsScore = regularnessSubs - 3;
                }
            }
            // --- Team Bonuses ---
            {
                // MATES
                var subsNames = new HashSet<string>(subs.Select(p => p.Name));
                mateScore = starters.Count > 0
                    ? starters.Count(s => !string.IsNullOrEmpty(s.Mate) && subsNames.Contains(s.Mate))
                    : 0;

                // SAME SQUAD PLAYERS
                var nonGkPlayers = subs.Concat(starters).Where(p => p.Role != "P").ToList();

                // More than 5 players of the same team
                sameTeamScore = nonGkPlayers
                    .GroupBy(p => p.Squad)
                    .Sum(g => g.Count() >= 4 ? -1 * (g.Count() - 3) : 0);

                // More than 2 players of the same team in the same Role
                foreach (var role in Roles.Where(r => r != "P"))
                {
                    if (nonGkPlayers.Where(p => p.Role == role)
                        .GroupBy(p => p.Squad)
                        .Any(g => g.Count() >= 2))
                    {
                        sameTeamScore -= 0.5;
                    }
                }
            }
            // Final Sum
            strategyScore = 2*regularnessStartersScore + regularnessSubsScore
                + mateScore + sameTeamScore + goalBonusPerRole;

            if (starters.Concat(subs).Count() == 25)
                Console.WriteLine($"regularnessStartersScore: {regularnessStartersScore}\nregularnessSubsScore: {regularnessSubsScore}\nmateScore: {mateScore}\nsameTeamScore: {sameTeamScore}\ngoalBonusPerRole: {goalBonusPerRole}");

            return strategyScore;
        }

        private RoleValueTable PrecomputeRoleValues(List<Player> players, int slots, int maxBudget, LineUp lineUp, League league)
        {
            RoleValueTable roleValueTable = new RoleValueTable {};

            // DP table: [numPlayers][budget] = best selection
            for (int k = 1; k <= slots; k++)
            {
                for (int b = 1; b <= maxBudget; b++)
                {
                    foreach (var player in players)
                    {
                        if (player.ExpectedPrice > b)
                            continue;

                        // Try all previous selections with k-1 players and budget b - player.ExpectedPrice
                        var prevSelection = roleValueTable.GetPlayerSelection(k - 1, b - (int)player.ExpectedPrice);
                        List<Player> candidatePlayers;
                        if (prevSelection != null)
                            candidatePlayers = prevSelection.PlayerIds
                                .Select(id => players.First(p => p.Id == id))
                                .ToList();
                        else if (k == 1 && b >= player.ExpectedPrice)
                            candidatePlayers = new List<Player>();
                        else
                            continue;

                        // Avoid duplicates
                        if (candidatePlayers.Any(p => p.Id == player.Id))
                            continue;

                        candidatePlayers.Add(player);
                        var score = CalculateScore(candidatePlayers, lineUp, league);

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
            return roleValueTable;
        }

        private FinalCombinationResult CombineRoleResults(
            RoleValueTable[] roleValueTables,
            Dictionary<int, Player> playerLookup,
            List<Player> currentPlayers,
            Dictionary<string, int> playersToBuy,
            int totalBudget,
            LineUp lineUp,
            League league)
        {
            FinalCombinationResult finalTable = new FinalCombinationResult {};

            for (int t = 0; t < Roles.Count; t++)
            {
                int slots = playersToBuy[Roles[t]];
                Dictionary<int, PlayerSelectionResult> currTable = roleValueTables[t].Table
                    .Where(kvp => kvp.Key.NumPlayers == slots)
                    .ToDictionary(kvp => kvp.Key.Budget, kvp => kvp.Value);

                // SKIP COMPLETE: check if we have anything at all for this role
                if (currTable.Count == 0)
                    continue;

                // BASE CASE: first non‐empty role seeds finalTable
                if (finalTable.Table.Count == 0)
                {
                    int currentPlayersTotalPrice = currentPlayers.Sum(p => p.ExpectedPrice);
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

                // RECURSIVE CASE: chain onto whatever is already in finalTable
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
                        List<Player> selectedPlayers = newIds
                            .Where(playerLookup.ContainsKey)
                            .Select(id => playerLookup[id])
                            .ToList();

                        Score newScore = CalculateScore(selectedPlayers, lineUp, league);
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
                // Calculate the Variance
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