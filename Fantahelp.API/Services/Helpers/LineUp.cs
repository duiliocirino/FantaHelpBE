
public class LineUp
{
    public int Keepers { get; set; } = 1;
    public int Defenders { get; set; }
    public int Midfielders { get; set; }
    public int Attackers { get; set; }
    public static string ToString(LineUp lineUp)
    {
        return $"{lineUp.Defenders}-{lineUp.Midfielders}-{lineUp.Attackers}";
    }
}