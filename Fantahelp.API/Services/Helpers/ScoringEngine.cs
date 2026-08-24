namespace Fantahelp.API.Services
{
    /// <summary>
    /// Stateless scoring engine for the suggestion pipeline.
    /// Computes composite team scores from starters, bench, and strategy bonuses.
    /// </summary>
    public static class ScoringEngine
    {
        private static readonly List<string> Roles = ["P", "D", "C", "A"];

        private static readonly Dictionary<string, int> GoalBonusPerRole = new()
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

        private const double StartMultiplier = 0.6;
        private const double SubsMultiplier = 0.3;
        private const double StrategyMultiplier = 0.1;

        public static Score CalculateScore(List<ScoringPlayer> players, SuggestionRequest suggestionRequest, League league)
        {
            var playersByRole = players
                .GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.ExpectedPerformance).ToList());

            List<ScoringPlayer> starters = [];
            List<ScoringPlayer> subs = [];

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
                TotalScore = StartMultiplier * startingScore + SubsMultiplier * subsScore + StrategyMultiplier * strategyScore
            };
        }

        private static double ComputeStarterContribution(List<ScoringPlayer> players, LineUp lineUp)
        {
            int bonusDefense = 0;

            if (lineUp.Defenders >= 4)
            {
                var gk = players.Where(p => p.Role == "P").OrderByDescending(p => p.ExpectedPerformance).FirstOrDefault();
                var topDefenders = players.Where(p => p.Role == "D")
                                    .OrderByDescending(p => p.ExpectedPerformance)
                                    .Take(3)
                                    .ToList();
                var selected = new List<ScoringPlayer>();
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

        private static double ComputeSubContribution(List<ScoringPlayer> subs, List<ScoringPlayer> starters)
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

        private static double ComputeStrategiesScore(List<ScoringPlayer> starters, List<ScoringPlayer> subs,
            League league, SuggestionRequest suggestionRequest)
        {
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

                        var avgCost = rolePlayers.Average(p => p.AcquisitionCost);
                        startersSpreadCreditsScore += -0.005 * Math.Sqrt(
                            rolePlayers.Sum(p => Math.Pow(p.AcquisitionCost - avgCost, 2)) / rolePlayers.Count
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
            /*                          --- Regularness ---
            Percentage-native (26-27+ contract): regularness is the expected starting %
            (0-100, multiples of 5), not a 1-5 rating. Each group is scored against its
            own baseline: starters 80% (reliable starter), subs 60% (reliable sub).
            Slopes: 1 point per 4% of deviation for starters, 1 point per 20% for subs.
            (These preserve the magnitude of the legacy 1-5 formulas 5*(avg-4) / (avg-3)
            under the 1<->20% ... 5<->100% mapping, so the relative tuning of the other
            strategy terms is unchanged; the slopes are one-line knobs if re-tuning.)
            */
            {
                if (starters.Count > 0)
                {
                    var regularnessStarters = starters.Average(p => p.Regularness);
                    regularnessStartersScore = (regularnessStarters - 80) / 4;
                }

                if (subs.Count > 0)
                {
                    var regularnessSubs = subs.Average(p => p.Regularness);
                    regularnessSubsScore = (regularnessSubs - 60) / 20;
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
            double strategyScore = regularnessStartersScore + regularnessSubsScore
                + mateScore + sameTeamScore + goalBonusPerRole
                + suggestionRequest.CreditsDistribution * startersSpreadCreditsScore;

            return strategyScore;
        }
    }
}
