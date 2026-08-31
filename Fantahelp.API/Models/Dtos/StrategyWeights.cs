/// <summary>
/// User personal-preference weights for the suggestion engine (all 0-10).
/// Optional on the request: absent/null falls back to the defaults below.
///
/// Total = (StarterWeight·S + BenchWeight·B + StrategyWeight·T) / 10
/// SquadDiversity and MateWeight scale terms inside T; ReliabilityWeight controls
/// the regularness/integrity discount applied to every player's value (S and B).
/// </summary>
public class StrategyWeights
{
    /// <summary>Weight of the starters block (match-day performance). Default 6 (= legacy 0.6).</summary>
    public int StarterWeight { get; set; } = 6;

    /// <summary>
    /// Weight of the bench block (rotation depth). The bench is scored as
    /// 0.5 × Σ reliable value (rotation factor: a bench player plays about half the
    /// fixtures), so the effective bench value per point is BenchWeight×0.5/10 vs
    /// StarterWeight/10 for the line — this ratio is the line-vs-bench trade-off.
    /// Default 3.
    /// </summary>
    public int BenchWeight { get; set; } = 3;

    /// <summary>Weight of the strategy block (mates, squad diversity, credit spread). Default 1 (= legacy 0.1).</summary>
    public int StrategyWeight { get; set; } = 1;

    /// <summary>
    /// Same-club aversion strength. Scales the graduated squad penalty
    /// (d = SquadDiversity/5: each extra same-club player in a role costs d,
    /// each player beyond the 3rd club-wide costs 2d). Default 2.
    /// </summary>
    public int SquadDiversity { get; set; } = 2;

    /// <summary>Points per starter whose mate sits on the bench. Default 1 (legacy behavior).</summary>
    public int MateWeight { get; set; } = 1;

    /// <summary>
    /// Reliability strength: how strongly a player's value is discounted by their
    /// starting probability — V = expmf × (regularness/100)^(w/10) × integrity tilt.
    /// 0 = pure quality-when-playing (overvalues irregular players); 10 = full expected
    /// contribution (economically consistent: the market prices availability). Default 10.
    /// </summary>
    public int ReliabilityWeight { get; set; } = 10;

    /// <summary>Deterministic signature used in cache keys (weights change the score).</summary>
    public string Signature => $"{StarterWeight}|{BenchWeight}|{StrategyWeight}|{SquadDiversity}|{MateWeight}|{ReliabilityWeight}";
}
