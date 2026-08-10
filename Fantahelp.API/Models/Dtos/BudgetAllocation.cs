/// <summary>
/// Per-role budget allocation percentages for the suggestion engine.
/// Each value represents the fraction of InitialBudget allocated as a spending cap for that role.
/// Defaults match the existing hardcoded MaxPercInterval values.
/// </summary>
public class BudgetAllocation
{
    public double Goalkeepers { get; set; } = 0.1;
    public double Defenders { get; set; } = 0.3;
    public double Midfielders { get; set; } = 0.6;
    public double Attackers { get; set; } = 0.6;
}
