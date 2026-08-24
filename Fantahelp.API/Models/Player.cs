using System.ComponentModel.DataAnnotations;

public class Player
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public required string Name { get; set; }

    [Required]
    [StringLength(50)]
    public required string Squad { get; set; }

    [Required]
    public required string Role { get; set; }

    [Required]
    public required List<string> Role_M { get; set; }

    [Required]
    public required int Price { get; set; }

    [Range(0, 100)]
    public int Age { get; set; }

    [Range(0.0, 5.0)]
    public double Rating { get; set; }

    public string? Mate { get; set; }

    public int Regularness { get; set; }

    /// <summary>
    /// Injury-proneness consensus (CSV column <c>integrity</c>): 1-5, higher = more robust.
    /// Nullable: ML leaves it empty when there is no consensus. Null means "unknown" --
    /// it is never defaulted to a middle value.
    /// </summary>
    [Range(1, 5)]
    public int? Integrity { get; set; }

    public int FVM { get; set; }

    // ML retrieved properties
    public double ExpectedPerformance { get; set; }
    public double ExpectedStd { get; set; }

    public int ExpectedPrice { get; set; }

    // --- Navigation Properties ---
    public ICollection<TeamPlayer> TeamPlayers { get; set; } = new List<TeamPlayer>();
    public ICollection<PlayerPrice> Prices { get; set; } = new List<PlayerPrice>();
}