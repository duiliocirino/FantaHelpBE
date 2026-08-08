// CSV column names are lowercased by CsvParser (case-insensitive match).
// e.g., CSV header "age" maps to property "Age", "role_m" maps to "Role_M".
// Nullable fields handle missing ML data (players without stats).
public class PlayerCreateDto
{
    public required int Id { get; set; }
    public required string Role { get; set; }
    public required string Name { get; set; }
    public required string Squad { get; set; }
    public required int Price { get; set; }
    public float? Age { get; set; }
    public float? MyRating { get; set; }
    public string? Mate { get; set; }
    public int? Regularness { get; set; }
    public int FVM { get; set; }
    public float? ExpMf { get; set; }
    public int ExpPrice { get; set; }
    public int ExpStd { get; set; }
    // Semicolon-separated sub-positions from ML (e.g., "Dd;Ds;Dc").
    // Parsed into List<string> during import.
    public string? Role_M { get; set; }
}
