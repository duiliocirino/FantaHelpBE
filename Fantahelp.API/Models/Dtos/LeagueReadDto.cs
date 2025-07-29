public class LeagueReadDto
{
    public required int Id { get; set; }
    public required string Name { get; set; }
    public required int InitialBudget { get; set; }
    public required List<TeamReadDto> Teams { get; set; }
}