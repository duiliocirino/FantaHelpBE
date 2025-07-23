public class PlayerCreateDto
{
    public required int Id { get; set; }
    public required string Role { get; set; }
    public required string Name { get; set; }
    public required string Squad { get; set; }
    public required int Price { get; set; }
    public required float MyRating { get; set; }
    public required string Mate { get; set; }
    public required int Regularness { get; set; }
    public required int FVM { get; set; }
    public required float ExpMf { get; set; }
    public required int ExpPrice { get; set; }
    public required int ExpStd { get; set; }
}
