public class LeagueGoalBonusUpdateDto
{
    /// <summary>Whether the league awards the per-role goal bonus (GK 6 / DEF 5 / MID 4 / ATT 3 per goal).</summary>
    public required bool GoalBonusPerRole { get; set; }
}
