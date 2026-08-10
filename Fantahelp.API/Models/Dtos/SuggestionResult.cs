public class SuggestionResult
{
    public required List<PlayerReadDto> SuggestedPlayers { get; set; }
    public int TotalExpectedPrice { get; set; }
    public int TotalExpectedPriceStd { get; set; }
    public required Score Score { get; set; }

    /// <summary>
    /// Potential optimal score when an auctioned player is forced into the team.
    /// Null when no auctioned player is provided or the acquisition price exceeds budget.
    /// </summary>
    public PotentialSuggestionResult? PotentialScore { get; set; }

    /// <summary>
    /// Optimal score when the auctioned player is excluded from the market entirely (Plan B).
    /// Allows the frontend to compare: buy this player vs. live without them.
    /// Null when no auctioned player is provided.
    /// </summary>
    public WithoutPlayerSuggestionResult? WithoutPlayerScore { get; set; }
}

/// <summary>
/// Represents the optimal suggestion when a specific player is forced into the roster
/// at a given acquisition price. Returned alongside the base score in a single call.
/// </summary>
public class PotentialSuggestionResult
{
    public required List<PlayerReadDto> SuggestedPlayers { get; set; }
    public int TotalExpectedPrice { get; set; }
    public int TotalExpectedPriceStd { get; set; }
    public required Score Score { get; set; }
}

/// <summary>
/// Represents the optimal suggestion when the auctioned player is excluded from the market
/// entirely (another team bought them). Returned alongside the base and potential scores.
/// </summary>
public class WithoutPlayerSuggestionResult
{
    public required List<PlayerReadDto> SuggestedPlayers { get; set; }
    public int TotalExpectedPrice { get; set; }
    public int TotalExpectedPriceStd { get; set; }
    public required Score Score { get; set; }
}