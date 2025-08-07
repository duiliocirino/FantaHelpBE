public class RoleValueTable
{
    public Dictionary<(int NumPlayers, int Budget), PlayerSelectionResult> Table { get; set; } = [];

    public PlayerSelectionResult? GetPlayerSelection(int numPlayers, int budget)
    {
        Table.TryGetValue((numPlayers, budget), out var playerIds);
        return playerIds;
    }

    public void SetPlayerSelection(int numPlayers, int budget, Score score, List<int> playerIds)
    {
        Table[(numPlayers, budget)] = new PlayerSelectionResult
        {
            Score = score,
            PlayerIds = playerIds
        };
    }
}