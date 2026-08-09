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
            // --- DATA PREPARATION (shared) ---
            Dictionary<int, Player> playerLookup = _context.Players.ToDictionary(p => p.Id, p => p);
            Console.WriteLine($"PlayerLookup count: {playerLookup.Count}");

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
            Console.WriteLine($"CurrentPlayers count: {currentPlayers.Count}");
            foreach (var p in currentPlayers)
                Console.WriteLine($"CurrentPlayer: {p.Id}, {p.Name}, {p.Role}, AuctionPrice: {p.ExpectedPrice}");

            var availablePlayers = (availablePlayersResult.Data ?? []).ToList();
            var budgetSpentPerRole = ComputeBudgetSpentPerRole(team);
            var playersToBuy = ComputePlayersToBuy(currentPlayers);

            var availablePlayersByRole = BuildAvailableByRole(availablePlayers);

            // --- STAGE 1: PRE-COMPUTATION PER ROLE (shared) ---
            Console.WriteLine("Stage 1: Pre-computation per role started.");

            var maxBudgetsPerRole = ComputeMaxBudgetsPerRole(team.League.InitialBudget, budgetSpentPerRole);

            var stage1Tasks = new[]
            {
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["P"],
                    slots:      playersToBuy["P"],
                    maxBudget:  maxBudgetsPerRole["P"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["D"],
                    slots:      playersToBuy["D"],
                    maxBudget:  maxBudgetsPerRole["D"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["C"],
                    slots:      playersToBuy["C"],
                    maxBudget:  maxBudgetsPerRole["C"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League)),
                Task.Run(() => PrecomputeRoleValues(
                    players:    availablePlayersByRole["A"],
                    slots:      playersToBuy["A"],
                    maxBudget:  maxBudgetsPerRole["A"],
                    suggestionRequest: suggestionRequest,
                    league:     team.League))
            };

            var results = await Task.WhenAll(stage1Tasks);
            var roleValueTables = new[] { results[0], results[1], results[2], results[3] };

            // --- STAGE 2+3: BASE (always) ---
            Console.WriteLine("Stage 2+3: Base combination started.");
            var baseResult = CombineAndBacktrack(
                roleValueTables: roleValueTables,
                currentPlayers: currentPlayers,
                playersToBuy: playersToBuy,
                totalBudget: team.RemainingBudget,
                numTeams: suggestionRequest.NumTeams,
                suggestionRequest: suggestionRequest,
                playerLookup: playerLookup,
                league: team.League
            );

            // --- STAGE 2+3: POTENTIAL (only when auctioned player is provided) ---
            PotentialSuggestionResult? potentialResult = null;
            if (suggestionRequest.AuctionedPlayer != null)
            {
                potentialResult = ComputePotentialScore(
                    roleValueTables: roleValueTables,
                    currentPlayers: currentPlayers,
                    availablePlayers: availablePlayers,
                    playersToBuy: playersToBuy,
                    budgetSpentPerRole: budgetSpentPerRole,
                    remainingBudget: team.RemainingBudget,
                    auctionedPlayer: suggestionRequest.AuctionedPlayer,
                    playerLookup: playerLookup,
                    suggestionRequest: suggestionRequest,
                    league: team.League,
                    numTeams: suggestionRequest.NumTeams
                );
            }

            baseResult.PotentialScore = potentialResult;
            return ServiceResult<List<SuggestionResult>>.SuccessResult([baseResult]);
        }

        /// <summary>
        /// Computes the potential score by forcing an auctioned player into the roster.
        /// Reuses the same Stage 1 DP tables — only Stage 2+3 are re-run with adjusted inputs.
        /// Returns null if the player is not found or the acquisition price exceeds budget.
        /// </summary>
        private PotentialSuggestionResult? ComputePotentialScore(
            RoleValueTable[] roleValueTables,
            List<Player> currentPlayers,
            List<Player> availablePlayers,
            Dictionary<string, int> playersToBuy,
            Dictionary<string, int> budgetSpentPerRole,
            int remainingBudget,
            AuctionedPlayerInfo auctionedPlayer,
            Dictionary<int, Player> playerLookup,
            SuggestionRequest suggestionRequest,
            League league,
            int numTeams)
        {
            // Look up the player
            if (!playerLookup.TryGetValue(auctionedPlayer.PlayerId, out var forcedPlayer))
            {
                Console.WriteLine($"[Potential] Player {auctionedPlayer.PlayerId} not found.");
                return null;
            }

            // Check affordability
            if (auctionedPlayer.AcquisitionPrice > remainingBudget)
            {
                Console.WriteLine($"[Potential] Acquisition price {auctionedPlayer.AcquisitionPrice} exceeds remaining budget {remainingBudget}.");
                return null;
            }

            var forcedRole = forcedPlayer.Role;

            // Clone the forced player with the acquisition price
            var forcedPlayerClone = new Player
            {
                Id = forcedPlayer.Id,
                Name = forcedPlayer.Name,
                Squad = forcedPlayer.Squad,
                Role = forcedPlayer.Role,
                Role_M = forcedPlayer.Role_M,
                Price = forcedPlayer.Price,
                Age = forcedPlayer.Age,
                Rating = forcedPlayer.Rating,
                Mate = forcedPlayer.Mate,
                Regularness = forcedPlayer.Regularness,
                FVM = forcedPlayer.FVM,
                ExpectedPerformance = forcedPlayer.ExpectedPerformance,
                ExpectedStd = forcedPlayer.ExpectedStd,
                ExpectedPrice = auctionedPlayer.AcquisitionPrice,
                TeamPlayers = forcedPlayer.TeamPlayers
            };

            // Build modified inputs
            var potentialCurrentPlayers = new List<Player>(currentPlayers) { forcedPlayerClone };

            // Remove forced player from available pool (so DP won't pick him again)
            var potentialAvailableByRole = BuildAvailableByRole(
                availablePlayers.Where(p => p.Id != auctionedPlayer.PlayerId).ToList());

            // Recompute Stage 1 for the forced role with one fewer slot
            // (other roles' tables are reused unchanged)
            var adjustedPlayersToBuy = playersToBuy.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            adjustedPlayersToBuy[forcedRole] = Math.Max(0, adjustedPlayersToBuy[forcedRole] - 1);

            var adjustedBudgetSpent = budgetSpentPerRole.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            adjustedBudgetSpent[forcedRole] += auctionedPlayer.AcquisitionPrice;

            var adjustedMaxBudgets = MaxPercInterval
                .ToDictionary(kvp => kvp.Key, kvp => (int)(kvp.Value * league.InitialBudget - adjustedBudgetSpent[kvp.Key]));

            // Recompute Stage 1 only for the affected role
            Console.WriteLine($"[Potential] Recomputing Stage 1 for role {forcedRole} (slots {adjustedPlayersToBuy[forcedRole]}, budget {adjustedMaxBudgets[forcedRole]}).");
            var roleIndex = Roles.IndexOf(forcedRole);
            var potentialRoleTables = new RoleValueTable[4];
            roleValueTables.CopyTo(potentialRoleTables, 0);

            var recomputedTable = PrecomputeRoleValues(
                players: potentialAvailableByRole[forcedRole],
                slots: adjustedPlayersToBuy[forcedRole],
                maxBudget: adjustedMaxBudgets[forcedRole],
                suggestionRequest: suggestionRequest,
                league: league);
            potentialRoleTables[roleIndex] = recomputedTable;

            var adjustedRemainingBudget = remainingBudget - auctionedPlayer.AcquisitionPrice;

            Console.WriteLine("Stage 2+3: Potential combination started.");
            var finalCombination = CombineRoleResults(
                roleValueTables: potentialRoleTables,
                currentPlayers: potentialCurrentPlayers,
                playersToBuy: adjustedPlayersToBuy,
                totalBudget: adjustedRemainingBudget,
                suggestionRequest: suggestionRequest,
                playerLookup: playerLookup,
                league: league
            );

            var potentialResults = BacktrackToGetTeams(
                finalCombination: finalCombination,
                numTeams: numTeams,
                playerLookup: playerLookup
            );

            if (potentialResults.Count == 0)
                return null;

            return new PotentialSuggestionResult
            {
                SuggestedPlayers = potentialResults[0].SuggestedPlayers,
                TotalExpectedPrice = potentialResults[0].TotalExpectedPrice,
                TotalExpectedPriceStd = potentialResults[0].TotalExpectedPriceStd,
                Score = potentialResults[0].Score
            };
        }

        /// <summary>
        /// Runs Stage 2 (combine) + Stage 3 (backtrack) and returns a single SuggestionResult.
        /// </summary>
        private SuggestionResult CombineAndBacktrack(
            RoleValueTable[] roleValueTables,
            List<Player> currentPlayers,
            Dictionary<string, int> playersToBuy,
            int totalBudget,
            int numTeams,
            SuggestionRequest suggestionRequest,
            Dictionary<int, Player> playerLookup,
            League league)
        {
            var finalCombination = CombineRoleResults(
                roleValueTables: roleValueTables,
                currentPlayers: currentPlayers,
                playersToBuy: playersToBuy,
                totalBudget: totalBudget,
                suggestionRequest: suggestionRequest,
                playerLookup: playerLookup,
                league: league
            );

            var results = BacktrackToGetTeams(
                finalCombination: finalCombination,
                numTeams: numTeams,
                playerLookup: playerLookup
            );

            // Return the first (best) result; PotentialScore is attached by the caller
            if (results.Count > 0)
                return results[0];

            // Fallback: empty result
            return new SuggestionResult
            {
                SuggestedPlayers = [],
                Score = new Score()
            };
        }

        /// <summary>
        /// Builds the list of current players from the team's roster plus favorites.
        /// </summary>
        private static List<Player> BuildCurrentPlayers(Team team, List<int> favoritePlayerIds, Dictionary<int, Player> playerLookup)
        {
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
                    ExpectedPrice = tp.AuctionPrice,
                    TeamPlayers = tp.Player.TeamPlayers
                })
                .ToList();

            foreach (var playerId in favoritePlayerIds)
                currentPlayers.Add(playerLookup[playerId]);

            return currentPlayers;
        }

        /// <summary>
        /// Groups available players by role, ensuring all roles are present.
        /// </summary>
        private static Dictionary<string, List<Player>> BuildAvailableByRole(List<Player> availablePlayers)
        {
            var result = availablePlayers
                .GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var role in Roles)
                if (!result.ContainsKey(role))
                    result[role] = [];
            return result;
        }

        /// <summary>
        /// Computes budget already spent per role from the team's roster.
        /// </summary>
        private static Dictionary<string, int> ComputeBudgetSpentPerRole(Team team)
        {
            var result = team.Players
                .GroupBy(tp => tp.Player.Role)
                .ToDictionary(g => g.Key, g => g.Sum(tp => tp.AuctionPrice));
            foreach (var role in Roles)
                if (!result.ContainsKey(role))
                    result[role] = 0;
            return result;
        }

        /// <summary>
        /// Computes how many players still need to be bought per role.
        /// </summary>
        private static Dictionary<string, int> ComputePlayersToBuy(List<Player> currentPlayers)
        {
            var currentByRole = currentPlayers.GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.Count());
            var result = PlayersPerRole.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value - (currentByRole.TryGetValue(kvp.Key, out var count) ? count : 0)
            );
            foreach (var role in Roles)
                Console.WriteLine($"PlayersToBuy[{role}]: {result[role]}");
            return result;
        }

        /// <summary>
        /// Computes max budget per role based on league rules and already-spent amounts.
        /// </summary>
        private static Dictionary<string, int> ComputeMaxBudgetsPerRole(int initialBudget, Dictionary<string, int> budgetSpentPerRole)
        {
            var result = MaxPercInterval.ToDictionary(
                kvp => kvp.Key,
                kvp => (int)(kvp.Value * initialBudget - budgetSpentPerRole[kvp.Key])
            );
            foreach (var role in Roles)
                Console.WriteLine($"MaxBudgetPerRole[{role}]: {result[role]}");
            return result;
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
        private Score CalculateScore(List<Player> players, SuggestionRequest suggestionRequest, League league)
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
                    "D" => suggestionRequest.LineUp.Defenders,
                    "C" => suggestionRequest.LineUp.Midfielders,
                    "A" => suggestionRequest.LineUp.Attackers,
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

            double startingScore = ComputeStarterContribution(starters, suggestionRequest.LineUp);
            double subsScore = ComputeSubContribution(subs, starters);
            double strategyScore = ComputeStrategiesScore(starters, subs, league, suggestionRequest);

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

        private double ComputeStrategiesScore(List<Player> starters, List<Player> subs,
            League league, SuggestionRequest suggestionRequest)
        {
            double strategyScore = 0;
            double startersSpreadCreditsScore = 0;
            double goalBonusPerRole = 0;
            double regularnessStartersScore = 0;
            double regularnessSubsScore = 0;
            double mateScore = 0;
            double sameTeamScore = 0;

            var allPlayers = starters.Concat(subs).ToList();

            /*                          --- Spread Credits ---
            This is done in order to make the variance of the expected performance be lower.
            The impact of this score will try to spread the credits over more players instead
            of centralising the credits on fewer instances, based on the intensity wanted.
            */
            {
                if (suggestionRequest.CreditsDistribution != 0 && allPlayers.Count > 0)
                {
                    foreach (var role in Roles)
                    {
                        var rolePlayers = starters.Where(p => p.Role == role).ToList();
                        if (rolePlayers.Count == 0)
                            continue;

                        var avgPerf = rolePlayers.Average(p => p.ExpectedPrice);
                        startersSpreadCreditsScore += -0.005 * Math.Sqrt(
                            rolePlayers.Sum(p => Math.Pow(p.ExpectedPrice - avgPerf, 2)) / rolePlayers.Count
                        );
                    }
                }
            }

            //                       --- Goal Bonuses Per Role ---
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
            //                            --- Regularness ---
            {
                if (starters.Count > 0)
                {
                    var regularnessStarters = starters.Average(p => p.Regularness);
                    regularnessStartersScore = 5 * (regularnessStarters - 4);
                }

                if (subs.Count > 0)
                {
                    var regularnessSubs = subs.Average(p => p.Regularness);
                    regularnessSubsScore = regularnessSubs - 3;
                }
            }
            //                            --- Team Bonuses ---
            {
                // MATES
                var subsNames = new HashSet<string>(subs.Select(p => p.Name));
                mateScore = starters.Count > 0
                    ? starters.Count(s => !string.IsNullOrEmpty(s.Mate) && subsNames.Contains(s.Mate))
                    : 0;

                // SAME SQUAD PLAYERS

                // More than 5 players of the same team
                sameTeamScore = allPlayers
                    .Where(p => p.Role != "P")
                    .GroupBy(p => p.Squad)
                    .Sum(g => g.Count() >= 4 ? -1 * (g.Count() - 3) : 0);

                // More than 2 players of the same team in the same Role
                foreach (var role in Roles.Where(r => r != "P"))
                {
                    if (allPlayers.Where(p => p.Role == role)
                        .GroupBy(p => p.Squad)
                        .Any(g => g.Count() >= 2))
                    {
                        sameTeamScore -= 0.5;
                    }
                }
            }
            // Final Sum
            strategyScore = regularnessStartersScore + regularnessSubsScore
                + mateScore + sameTeamScore + goalBonusPerRole
                + suggestionRequest.CreditsDistribution * startersSpreadCreditsScore;

            return strategyScore;
        }

        private RoleValueTable PrecomputeRoleValues(List<Player> players, int slots, int maxBudget, SuggestionRequest suggestionRequest, League league)
        {
            RoleValueTable roleValueTable = new() { };

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
                        var score = CalculateScore(candidatePlayers, suggestionRequest, league);

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
            SuggestionRequest suggestionRequest,
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

                        Score newScore = CalculateScore(selectedPlayers, suggestionRequest, league);
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