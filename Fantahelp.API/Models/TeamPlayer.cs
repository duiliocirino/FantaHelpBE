public class TeamPlayer
{
    public int TeamId { get; set; }
    public required Team Team { get; set; }

    public int PlayerId { get; set; }
    public required Player Player { get; set; }

    public int AuctionPrice { get; set; }
}