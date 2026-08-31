public class SuggestionRequest
{
    public required int TeamId { get; set; }
    public int NumTeams { get; set; } = 1;
    public List<int> FavoritePlayerIds { get; set; } = [];
    public required LineUp LineUp { get; set; }
    public int CreditsDistribution { get; set; } = 1; // 0: no spread (greedy), 1: low spread (activate computation), 5: nicely spread

    /// <summary>
    /// Per-role budget allocation percentages. When null, defaults to MaxPercInterval (P:0.1, D:0.3, C:0.6, A:0.6).
    /// </summary>
    public BudgetAllocation? BudgetAllocation { get; set; }

    /// <summary>
    /// Optional personal-preference weights for the scoring engine (0-10 knobs).
    /// Absent/null = defaults, which reproduce the legacy 0.6/0.3/0.1 block calibration.
    /// </summary>
    public StrategyWeights? Weights { get; set; }

    /// <summary>
    /// Optional player to simulate adding to the team for a "potential score"
    /// comparison. When provided, the suggestion engine should compute a second
    /// score that includes this player at the given acquisition price.
    /// </summary>
    public AuctionedPlayerInfo? AuctionedPlayer { get; set; }
    // ... other future options
}