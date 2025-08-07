public class FinalCombinationResult
{
    public Dictionary<(int RoleNum, int Budget), PlayerSelectionResult> Table { get; set; } = [];

    public PlayerSelectionResult? GetPlayerSelection(int roleNum, int budget)
    {
        Table.TryGetValue((roleNum, budget), out var playerIds);
        return playerIds;
    }

    public void SetPlayerSelection(int roleNum, int budget, Score score, List<int> playerIds)
    {
        Table[(roleNum, budget)] = new PlayerSelectionResult
        {
            Score = score,
            PlayerIds = playerIds
        };
    }
}