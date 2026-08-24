using System.ComponentModel.DataAnnotations;

/// <summary>
/// Format-specific expected auction price for a player.
/// A "format" is a league configuration defined by total credits and number of starters
/// (e.g. 800 credits / 8 starters = "800_8"). One row per player x format, sourced from
/// the per-format ML CSVs (<c>players_{credits}_{starters}.csv</c>) at season import.
/// </summary>
public class PlayerPrice
{
    // --- Composite key: one row per player x format ---

    public int PlayerId { get; set; }

    /// <summary>League total credits (e.g. 800, 1000).</summary>
    public int Credits { get; set; }

    /// <summary>Number of starters in the lineup (e.g. 8, 10).</summary>
    public int Starters { get; set; }

    /// <summary>Expected auction price in this format's market (CSV column <c>expprice</c>).</summary>
    public int Price { get; set; }

    /// <summary>Expected auction price std in this format's market (CSV column <c>expstd</c>).</summary>
    public double Std { get; set; }

    // --- Navigation Properties ---
    public Player Player { get; set; } = null!;
}
