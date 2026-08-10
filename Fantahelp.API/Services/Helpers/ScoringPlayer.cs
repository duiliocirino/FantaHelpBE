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
    string? Mate,
    int Regularness,
    double ExpectedPerformance,
    double ExpectedStd,
    int MarketValue,
    int AcquisitionCost
);
