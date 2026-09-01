namespace Fantahelp.API.Services
{
    /// <summary>
    /// Stateless scoring engine for the suggestion pipeline.
    ///
    /// Total = (StarterWeight·S + BenchWeight·B + StrategyWeight·T) / 10
    ///
    /// Player value: V = ExpectedPerformance × (regularness/100)^α × integrity tilt,
    /// with α = ReliabilityWeight/10 (0 = pure quality-when-playing, 1 = full expected
    /// contribution) and tilt = 1 + 0.03·(ReliabilityWeight/10)·(integrity−3), null → 1.
    /// expmf is the quality WHEN PLAYING (start probability not included) and the market
    /// price already reflects availability, so V is the expected contribution a player
    /// actually delivers per fixture — this is what both blocks score.
    ///
    /// S (starters): sum of V of the starters, plus the back-4 defense bonus for 4+DEF
    ///   lineups (threshold on the base performance, without the goal-bonus adjustment).
    /// B (bench): BenchRotationFactor × sum of V of the bench players. The rotation
    ///   factor (< 1) reflects that a bench player only plays a fraction of fixtures for
    ///   the team; it also keeps the S/B weight ratio a real line-vs-bench trade-off
    ///   (without it, line and bench points are worth the same and the engine always
    ///   buys the top-V players, making BenchWeight degenerate).
    /// T (strategy): personal preferences — mates, graduated same-club penalty,
    ///   credit spread.
    ///
    /// Weights come from SuggestionRequest.Weights (optional; block defaults 6/3/1
    /// reproduce the legacy 0.6/0.3/0.1 block calibration, reliability defaults to 10).
    ///
    /// League goal bonus (League.GoalBonusPerRole) is NOT a score term: it is a market
    /// adjustment applied up-front via EffectiveExpectedPerformance / EffectiveMarketPrice /
    /// EffectivePriceStd, which the suggestion pipeline bakes into every ScoringPlayer.
    /// That way both the value side (ranking) and the price side (budget decisions) reflect
    /// the league rule.
    /// </summary>
    public static class ScoringEngine
    {
        private static readonly List<string> Roles = ["P", "D", "C", "A"];

        // --- Goal-bonus league rule ---
        // Bonus points awarded per goal by role. 3 (attackers) is the base and is already
        // included in expmf, so only the excess over 3 has any effect.
        private static readonly Dictionary<string, int> GoalBonusPerRole = new()
        {
            { "P", 6 },
            { "D", 5 },
            { "C", 4 },
            { "A", 3 }
        };
        // Share of a player's expected points that come from goals, by role.
        // Used to estimate expected goals: goals ≈ goalPerc × expmf / 10 (a goal is 10 pts).
        private static readonly Dictionary<string, double> GoalPercPerRole = new()
        {
            { "P", 0.0 },
            { "D", 0.1 },
            { "C", 0.3 },
            { "A", 0.6 }
        };
        // Expected market price uplift by role in a goal-bonus league: the market pays more
        // for bonus-eligible positions (empirical: D +10%, C +5%). Applied to players being
        // bought; owned players keep their paid auction price.
        private static readonly Dictionary<string, double> PriceUpPerRole = new()
        {
            { "P", 0.0 },
            { "D", 0.10 },
            { "C", 0.05 },
            { "A", 0.0 }
        };

        public static Score CalculateScore(List<ScoringPlayer> players, SuggestionRequest suggestionRequest)
            => CalculateScore(players, suggestionRequest, null);

        internal static Score CalculateScore(
            List<ScoringPlayer> players,
            SuggestionRequest suggestionRequest,
            ScoringContext? scoringContext)
        {
            var weights = suggestionRequest.Weights ?? new StrategyWeights();

            if (scoringContext != null)
                return CalculateScoreFast(players, suggestionRequest, scoringContext, weights);

            // Starters/bench split per role by reliable value V (consistent with the
            // objective the DP optimizes).
            var playersByRole = players
                .GroupBy(p => p.Role)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p =>
                    scoringContext?.GetReliableValue(p) ?? ReliableValue(p, weights)).ToList());

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

            double startingScore = ComputeStarterContribution(starters, suggestionRequest.LineUp, weights, scoringContext);
            double subsScore = ComputeSubContribution(subs, weights, scoringContext);
            double strategyScore = ComputeStrategiesScore(starters, subs, suggestionRequest, weights);

            return new Score
            {
                StarterScore = startingScore,
                BenchScore = subsScore,
                PenaltyScore = strategyScore,
                TotalScore = (weights.StarterWeight * startingScore
                            + weights.BenchWeight * subsScore
                            + weights.StrategyWeight * strategyScore) / 10.0
            };
        }

            private static Score CalculateScoreFast(
                List<ScoringPlayer> players,
                SuggestionRequest suggestionRequest,
                ScoringContext scoringContext,
                StrategyWeights weights)
            {
                var playersByRole = new List<ScoringPlayer>[4]
                {
                    [], [], [], []
                };

                foreach (var player in players)
                {
                    var roleIndex = player.Role switch
                    {
                        "P" => 0,
                        "D" => 1,
                        "C" => 2,
                        "A" => 3,
                        _ => -1
                    };

                    if (roleIndex >= 0)
                        playersByRole[roleIndex].Add(player);
                }

                foreach (var rolePlayers in playersByRole)
                    StableSortByReliableValue(rolePlayers, scoringContext);

                var starters = new List<ScoringPlayer>(players.Count);
                var subs = new List<ScoringPlayer>(players.Count);
                var starterCounts = new[]
                {
                    1,
                    suggestionRequest.LineUp.Defenders,
                    suggestionRequest.LineUp.Midfielders,
                    suggestionRequest.LineUp.Attackers
                };

                for (int roleIndex = 0; roleIndex < playersByRole.Length; roleIndex++)
                {
                    var rolePlayers = playersByRole[roleIndex];
                    if (rolePlayers.Count == 0)
                        continue;

                    int starterCount = starterCounts[roleIndex];
                    if (starterCount > 0 && rolePlayers.Count >= starterCount)
                    {
                        for (int playerIndex = 0; playerIndex < starterCount; playerIndex++)
                            starters.Add(rolePlayers[playerIndex]);
                        for (int playerIndex = starterCount; playerIndex < rolePlayers.Count; playerIndex++)
                            subs.Add(rolePlayers[playerIndex]);
                    }
                    else
                    {
                        starters.AddRange(rolePlayers);
                    }
                }

                double startingScore = ComputeStarterContributionFast(
                    starters, suggestionRequest.LineUp, scoringContext);
                double subsScore = ComputeSubContributionFast(subs, scoringContext);
                double strategyScore = ComputeStrategiesScoreFast(
                    starters, subs, suggestionRequest, weights);

                return new Score
                {
                    StarterScore = startingScore,
                    BenchScore = subsScore,
                    PenaltyScore = strategyScore,
                    TotalScore = (weights.StarterWeight * startingScore
                                + weights.BenchWeight * subsScore
                                + weights.StrategyWeight * strategyScore) / 10.0
                };
            }

            private static void StableSortByReliableValue(
                List<ScoringPlayer> players,
                ScoringContext scoringContext)
            {
                for (int index = 1; index < players.Count; index++)
                {
                    var candidate = players[index];
                    var candidateValue = scoringContext.GetReliableValue(candidate);
                    int previousIndex = index - 1;

                    while (previousIndex >= 0
                        && scoringContext.GetReliableValue(players[previousIndex]) < candidateValue)
                    {
                        players[previousIndex + 1] = players[previousIndex];
                        previousIndex--;
                    }

                    players[previousIndex + 1] = candidate;
                }
            }

            private static double ComputeStarterContributionFast(
                List<ScoringPlayer> players,
                LineUp lineUp,
                ScoringContext scoringContext)
            {
                int bonusDefense = 0;

                if (lineUp.Defenders >= 4)
                {
                    ScoringPlayer? keeper = null;
                    ScoringPlayer? firstDefender = null;
                    ScoringPlayer? secondDefender = null;
                    ScoringPlayer? thirdDefender = null;

                    foreach (var player in players)
                    {
                        if (player.Role == "P")
                        {
                            if (keeper == null || player.BaseExpectedPerformance > keeper.BaseExpectedPerformance)
                                keeper = player;
                            continue;
                        }

                        if (player.Role != "D")
                            continue;

                        if (firstDefender == null || player.BaseExpectedPerformance > firstDefender.BaseExpectedPerformance)
                        {
                            thirdDefender = secondDefender;
                            secondDefender = firstDefender;
                            firstDefender = player;
                        }
                        else if (secondDefender == null || player.BaseExpectedPerformance > secondDefender.BaseExpectedPerformance)
                        {
                            thirdDefender = secondDefender;
                            secondDefender = player;
                        }
                        else if (thirdDefender == null || player.BaseExpectedPerformance > thirdDefender.BaseExpectedPerformance)
                        {
                            thirdDefender = player;
                        }
                    }

                    if (keeper != null && firstDefender != null && secondDefender != null && thirdDefender != null)
                    {
                        // Same addition order as the reference scorer (keeper + LINQ Sum of the
                        // three defenders) so the average is bit-identical: a 1-ULP difference
                        // could flip the back-4 k boundary (e.g. an average of exactly 6.25).
                        double defendersSum = firstDefender.BaseExpectedPerformance
                            + secondDefender.BaseExpectedPerformance
                            + thirdDefender.BaseExpectedPerformance;
                        double averageScore = (keeper.BaseExpectedPerformance + defendersSum) / 4.0;
                        if (averageScore >= 6)
                            bonusDefense = (int)Math.Floor((averageScore - 6) / 0.25) + 1;
                    }
                }

                double score = 0;
                foreach (var player in players)
                    score += scoringContext.GetReliableValue(player);
                return score + bonusDefense;
            }

            private static double ComputeSubContributionFast(
                List<ScoringPlayer> subs,
                ScoringContext scoringContext)
            {
                double score = 0;
                foreach (var player in subs)
                    score += scoringContext.GetReliableValue(player);
                return BenchRotationFactor * score;
            }

            private static double ComputeStrategiesScoreFast(
                List<ScoringPlayer> starters,
                List<ScoringPlayer> subs,
                SuggestionRequest suggestionRequest,
                StrategyWeights weights)
            {
                double startersSpreadCreditsScore = 0;
                if (suggestionRequest.CreditsDistribution != 0 && starters.Count + subs.Count > 0)
                {
                    foreach (var role in Roles)
                    {
                        int roleCount = 0;
                        double roleCostSum = 0;
                        foreach (var player in starters)
                        {
                            if (player.Role != role)
                                continue;
                            roleCount++;
                            roleCostSum += player.AcquisitionCost;
                        }

                        if (roleCount == 0)
                            continue;

                        double averageCost = roleCostSum / roleCount;
                        double squaredDifferenceSum = 0;
                        foreach (var player in starters)
                        {
                            if (player.Role != role)
                                continue;
                            squaredDifferenceSum += Math.Pow(player.AcquisitionCost - averageCost, 2);
                        }

                        startersSpreadCreditsScore += -0.005 * Math.Sqrt(squaredDifferenceSum / roleCount);
                    }
                }

                var subsNames = new HashSet<string>(subs.Select(player => player.Name));
                int mateCount = 0;
                if (starters.Count > 0)
                {
                    foreach (var starter in starters)
                    {
                        if (!string.IsNullOrEmpty(starter.Mate) && subsNames.Contains(starter.Mate))
                            mateCount++;
                    }
                }

                var roleSquadCounts = new Dictionary<(string Role, string Squad), int>();
                var squadCounts = new Dictionary<string, int>();
                foreach (var player in starters.Concat(subs))
                {
                    if (player.Role == "P")
                        continue;

                    var roleSquadKey = (player.Role, player.Squad);
                    roleSquadCounts[roleSquadKey] = roleSquadCounts.TryGetValue(roleSquadKey, out var roleCount)
                        ? roleCount + 1
                        : 1;
                    squadCounts[player.Squad] = squadCounts.TryGetValue(player.Squad, out var squadCount)
                        ? squadCount + 1
                        : 1;
                }

                int sameClubInRole = 0;
                foreach (var count in roleSquadCounts.Values)
                    sameClubInRole += Math.Max(0, count - 1);

                int sameClubTeamWide = 0;
                foreach (var count in squadCounts.Values)
                    sameClubTeamWide += Math.Max(0, count - 3);

                double sameTeamScore = -weights.SquadDiversity / 5.0
                    * (sameClubInRole + 2 * sameClubTeamWide);
                double strategyScore = weights.MateWeight * mateCount
                    + sameTeamScore
                    + suggestionRequest.CreditsDistribution * startersSpreadCreditsScore;

                return strategyScore;
            }

        private static double ComputeStarterContribution(
            List<ScoringPlayer> players,
            LineUp lineUp,
            StrategyWeights weights,
            ScoringContext? scoringContext)
        {
            int bonusDefense = 0;

            // Back-4 defense bonus: only for 4+DEF lineups (e.g. a 3-4-3 never earns it).
            // League rule: when the keeper + top 3 defenders average 6+ in a fixture, the
            // team gets k extra points, k = 1 for 6-6.25, 2 for 6.25-6.5, 3 for 6.5-6.75, ...
            // The threshold is evaluated on the BASE expected performance (without the
            // goal-bonus adjustment) — the bonus is a defensive-stability rule, independent
            // of the per-player goal value handled by EffectiveExpectedPerformance.
            if (lineUp.Defenders >= 4)
            {
                var gk = players.Where(p => p.Role == "P").OrderByDescending(p => p.BaseExpectedPerformance).FirstOrDefault();
                var topDefenders = players.Where(p => p.Role == "D")
                                    .OrderByDescending(p => p.BaseExpectedPerformance)
                                    .Take(3)
                                    .ToList();
                var selected = new List<ScoringPlayer>();
                if (gk != null)
                    selected.Add(gk);
                selected.AddRange(topDefenders);

                if (selected.Count == 4)
                {
                    double avgScore = selected.Average(p => p.BaseExpectedPerformance);
                    if (avgScore >= 6)
                    {
                        // Bonus: 1 for each 0.25 above 6 (floor to nearest category)
                        bonusDefense = (int)Math.Floor((avgScore - 6) / 0.25) + 1;
                    }
                }
            }

            double score = players.Sum(p => scoringContext?.GetReliableValue(p) ?? ReliableValue(p, weights)) + bonusDefense;
            return score;
        }

        /// <summary>
        /// Share of a bench player's reliable value that the team actually receives:
        /// a bench player only plays a fraction of fixtures (rotation + injury cover).
        /// Calibrated to 0.5 ("plays about half the fixtures"). Single tunable constant;
        /// expose as a league/user setting if real-world calibration suggests.
        /// </summary>
        private const double BenchRotationFactor = 0.5;

        private static double ComputeSubContribution(
            List<ScoringPlayer> subs,
            StrategyWeights weights,
            ScoringContext? scoringContext)
        {
            // Bench = rotation factor × sum of the reliable value of the bench players:
            // absolute rotation depth in expected points, correct direction under budget
            // pressure (unlike the old bench/starter ratio, which measured the pool's
            // talent curve and rose when the line got cheaper).
            return BenchRotationFactor * subs.Sum(p => scoringContext?.GetReliableValue(p) ?? ReliableValue(p, weights));
        }

        private static double ComputeStrategiesScore(List<ScoringPlayer> starters, List<ScoringPlayer> subs,
            SuggestionRequest suggestionRequest, StrategyWeights weights)
        {
            double startersSpreadCreditsScore = 0;
            double mateScore = 0;
            double sameTeamScore = 0;

            var allPlayers = starters.Concat(subs).ToList();

            /*                          --- Spread Credits ---
            This is done in order to make the variance of the expected performance be lower.
            The impact of this score will try to spread the credits over more players instead
            of centralising the credits on fewer instances, based on the intensity wanted.
            Costs are the league-adjusted acquisition costs (goal-bonus price uplift included).
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
            // Regularness is NOT a T term: it is part of every player's reliable value V
            // (see ReliableValue), where it has real decision weight in both S and B.
            //                            --- Team Bonuses ---
            {
                // MATES: points per starter whose mate sits on the bench (MateWeight).
                var subsNames = new HashSet<string>(subs.Select(p => p.Name));
                mateScore = weights.MateWeight * (starters.Count > 0
                    ? starters.Count(s => !string.IsNullOrEmpty(s.Mate) && subsNames.Contains(s.Mate))
                    : 0);

                // SAME SQUAD PLAYERS — graduated penalty (no dead zone):
                //   each same-club player beyond the 1st in a role costs d,
                //   each player beyond the 3rd club-wide (non-GK) costs 2d,
                //   with d = SquadDiversity/5 (default 2 -> d = 0.4).
                var nonP = allPlayers.Where(p => p.Role != "P").ToList();
                double sameClubInRole = nonP
                    .GroupBy(p => (p.Role, p.Squad))
                    .Sum(g => Math.Max(0, g.Count() - 1));
                double sameClubTeamWide = nonP
                    .GroupBy(p => p.Squad)
                    .Sum(g => Math.Max(0, g.Count() - 3));
                sameTeamScore = -weights.SquadDiversity / 5.0 * (sameClubInRole + 2 * sameClubTeamWide);
            }
            // Final Sum (the goal bonus and reliability are not score terms: they are
            // applied to each player's value before scoring).
            double strategyScore = mateScore + sameTeamScore
                + suggestionRequest.CreditsDistribution * startersSpreadCreditsScore;

            return strategyScore;
        }

        /// <summary>
        /// Reliable value of a player: expected contribution per fixture.
        /// V = ExpectedPerformance × (regularness/100)^α × integrity tilt, with
        /// α = ReliabilityWeight/10 and tilt = 1 + 0.03·α·(integrity−3) (null → 1).
        /// expmf is the quality when playing (start probability not included, by ML
        /// contract) and the market price already reflects availability, so the
        /// regularness factor converts quality into expected contribution without
        /// double-counting the price side. α=0 recovers pure quality-when-playing;
        /// α=1 (ReliabilityWeight 10) is the economically consistent value.
        /// </summary>
        internal static double ReliableValue(ScoringPlayer p, StrategyWeights weights)
        {
            double alpha = weights.ReliabilityWeight / 10.0;
            double value = p.ExpectedPerformance * Math.Pow(p.Regularness / 100.0, alpha);
            if (p.Integrity.HasValue)
                value *= 1.0 + 0.03 * alpha * (p.Integrity.Value - 3);
            return value;
        }

        /// <summary>
        /// Effective expected performance under the league's goal-bonus rule: expmf plus the
        /// expected net bonus points — goalPerc × expmf / 10 (expected goals) times the
        /// excess of the role bonus over the base 3 (already in expmf). Net factors:
        /// D +2%, C +3%, A/P unchanged.
        /// </summary>
        public static double EffectiveExpectedPerformance(double expmf, string role, League league)
            => league.GoalBonusPerRole
                ? expmf * (1 + GoalPercPerRole[role] * (GoalBonusPerRole[role] - 3) / 10.0)
                : expmf;

        /// <summary>
        /// Effective market price in a goal-bonus league (position uplift: D +10%, C +5%).
        /// Applies to players being bought; owned players keep their paid auction price.
        /// </summary>
        public static int EffectiveMarketPrice(int price, string role, League league)
            => league.GoalBonusPerRole
                ? (int)Math.Round(price * (1 + PriceUpPerRole[role]))
                : price;

        /// <summary>
        /// Effective price standard deviation, scaled with the price so team-level
        /// aggregations (sum of squares) stay consistent with the adjusted prices.
        /// </summary>
        public static double EffectivePriceStd(double std, string role, League league)
            => league.GoalBonusPerRole
                ? std * (1 + PriceUpPerRole[role])
                : std;
    }
}
