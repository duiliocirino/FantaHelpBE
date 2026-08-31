namespace Fantahelp.API.Services;

/// <summary>
/// Lightweight projection of <see cref="Player"/> for the suggestion scoring pipeline.
/// Separates <see cref="MarketValue"/> (ML-predicted expected auction price) from
/// <see cref="AcquisitionCost"/> (actual or simulated price paid).
///
/// For available players: MarketValue == AcquisitionCost.
/// For current/favorite players: AcquisitionCost reflects auction price or expected price.
/// For forced (auctioned) players: AcquisitionCost is the user-provided bid price.
/// </summary>
public record ScoringPlayer(
    int Id,
    string Name,
    string Squad,
    string Role,
    /// <summary>
    /// Consensus robustness 1-5 (higher = more robust); null = unknown.
    /// Used by the scoring engine's integrity tilt (see ScoringEngine.ReliableValue).
    /// </summary>
    int? Integrity,
    string? Mate,
    int Regularness,
    /// <summary>
    /// League-adjusted expected performance (goal-bonus value uplift baked in when the
    /// league flag is on). Used for the starter sum, bench ratios and rankings.
    /// </summary>
    double ExpectedPerformance,
    /// <summary>
    /// Raw ML expected performance, before the goal-bonus adjustment. Used where the
    /// league rule itself references the base score — the back-4 defense bonus threshold
    /// is evaluated on the average WITHOUT the goal bonus.
    /// </summary>
    double BaseExpectedPerformance,
    double ExpectedStd,
    int MarketValue,
    int AcquisitionCost
);
