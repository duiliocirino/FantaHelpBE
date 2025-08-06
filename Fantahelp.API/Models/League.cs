public class League
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public int InitialBudget { get; set; } = 800;

    public bool GoalBonusPerRole { get; set; } = true;

    // --- Navigation Properties ---
    public ICollection<Team> Teams { get; set; } = new List<Team>();
}
