/// <summary>
/// User personal-preference weights for the suggestion engine (all 0-10).
/// Optional on the request: absent/null falls back to the defaults below, which
/// reproduce the legacy 0.6/0.3/0.1 block calibration.
///
/// Total = (StarterWeight·S + BenchWeight·B + StrategyWeight·T) / 10
/// The sub-knobs (SquadDiversity, MateWeight, ReliabilityWeight) scale terms inside T.
/// </summary>
public class StrategyWeights
{
    /// <summary>Weight of the starters block (match-day performance). Default 6 (= legacy 0.6).</summary>
    public int StarterWeight { get; set; } = 6;

    /// <summary>Weight of the bench block (rotation depth, structurally 0-4). Default 3 (= legacy 0.3).</summary>
    public int BenchWeight { get; set; } = 3;

    /// <summary>Weight of the strategy block (preferences + reliability). Default 1 (= legacy 0.1).</summary>
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
    /// Reliability (regularness) preference strength. 5 = neutral scaling (legacy behavior);
    /// 0 disables the regularness terms. Default 5.
    /// </summary>
    public int ReliabilityWeight { get; set; } = 5;

    /// <summary>Deterministic signature used in cache keys (weights change the score).</summary>
    public string Signature => $"{StarterWeight}|{BenchWeight}|{StrategyWeight}|{SquadDiversity}|{MateWeight}|{ReliabilityWeight}";
}
