public class TeamCreateDto
{
    public required string Name { get; set; }
    public int InitialBudget { get; set; }
    public required int OwnerId { get; set; }
    public int LeagueId { get; set; }
}