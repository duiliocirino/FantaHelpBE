public class PlayerReadDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string TeamName { get; set; }
    public required string Role { get; set; }
    public int InitialCost { get; set; }
    public double PredictedScore { get; set; }
}