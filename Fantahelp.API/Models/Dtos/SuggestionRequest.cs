public class SuggestionRequest
{
    public required int TeamId { get; set; }
    public int NumTeams { get; set; } = 1;
    public List<int> FavoritePlayerIds { get; set; } = new(); // Your idea!
    public required LineUp LineUp { get; set; }
    // ... other options we discuss below
}