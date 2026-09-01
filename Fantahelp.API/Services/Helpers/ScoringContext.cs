namespace Fantahelp.API.Services;

internal sealed class ScoringContext
{
    private readonly Dictionary<int, double> _reliableValues = new();
    private readonly StrategyWeights _weights;

    public ScoringContext(IEnumerable<ScoringPlayer> players, StrategyWeights weights)
    {
        _weights = weights;
        foreach (var player in players)
            _reliableValues.TryAdd(player.Id, ScoringEngine.ReliableValue(player, weights));
    }

    public double GetReliableValue(ScoringPlayer player)
        => _reliableValues.TryGetValue(player.Id, out var value)
            ? value
            : ScoringEngine.ReliableValue(player, _weights);
}
