using System.ComponentModel.DataAnnotations;

public class Team
{
    [Key]
    public int Id { get; set; }

    [Required]
    public required string Name { get; set; }

    [Required]
    public required string OwnerName { get; set; }

    public int RemainingBudget { get; set; }

    // --- Navigation Properties ---
    public int OwnerId { get; set; }
    public required User Owner { get; set; } // Navigation property

    public int LeagueId { get; set; }
    public required League League { get; set; } // Navigation property

    public ICollection<TeamPlayer> Players { get; set; } = new List<TeamPlayer>();
}