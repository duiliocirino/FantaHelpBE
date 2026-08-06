/// <summary>
/// Describes a player the frontend wants to simulate adding to the team
/// for a "potential score" comparison against the base score.
/// </summary>
public class AuctionedPlayerInfo
{
    public int PlayerId { get; set; }
    public int AcquisitionPrice { get; set; }
}
