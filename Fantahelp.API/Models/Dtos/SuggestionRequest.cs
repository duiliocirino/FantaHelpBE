public class SuggestionRequest
{
    public required int TeamId { get; set; }
    public int NumTeams { get; set; } = 1;
    public List<int> FavoritePlayerIds { get; set; } = [];
    public required LineUp LineUp { get; set; }
    public int CreditsDistribution { get; set; } = 1; // 0: no spread (greedy), 1: low spread (activate computation), 5: nicely spread
    // ... other future options
}