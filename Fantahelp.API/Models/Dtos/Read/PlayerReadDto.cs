public class PlayerReadDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Squad { get; set; }
    public required string Role { get; set; }
    public int Price { get; set; }
    public double Rating { get; set; }
    public int Regularness { get; set; }
    public int FVM { get; set; }
    public double ExpectedPerformance { get; set; }
    public double ExpectedStd { get; set; }
    public int ExpectedPrice { get; set; }
}