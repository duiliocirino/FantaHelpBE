public class PlayerReadDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Squad { get; set; }
    public required string Role { get; set; }
    public List<string> Role_M { get; set; } = new();
    public int Price { get; set; }
    public int Age { get; set; }
    public double Rating { get; set; }
    public string? Mate { get; set; }
    public int Regularness { get; set; }
    /// <summary>Injury-proneness consensus 1-5 (higher = more robust). Null when unknown.</summary>
    public int? Integrity { get; set; }
    public int FVM { get; set; }
    public double ExpectedPerformance { get; set; }
    public double ExpectedStd { get; set; }
    public int ExpectedPrice { get; set; }
}