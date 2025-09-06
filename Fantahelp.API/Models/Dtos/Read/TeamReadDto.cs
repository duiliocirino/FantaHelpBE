public class TeamReadDto
{
    public required int Id { get; set; }
    public required int LeagueId { get; set; }
    public required string Name { get; set; }
    public required int RemainingBudget { get; set; }
    public required List<TeamPlayerReadDto> Players { get; set; }
}