using System.Text.Json.Serialization;

/// <summary>
/// Describes a player the frontend wants to simulate adding to the team
/// for a "potential score" comparison against the base score.
/// Supports both "price" and "acquisitionPrice" JSON property names for FE compatibility.
/// </summary>
public class AuctionedPlayerInfo
{
    public int PlayerId { get; set; }

    private int _acquisitionPrice;

    /// <summary>
    /// Mapped from JSON field "price" (FE alias).
    /// </summary>
    [JsonPropertyName("price")]
    public int Price
    {
        get => _acquisitionPrice;
        set => _acquisitionPrice = value;
    }

    /// <summary>
    /// Mapped from JSON field "acquisitionPrice".
    /// </summary>
    [JsonPropertyName("acquisitionPrice")]
    public int AcquisitionPrice
    {
        get => _acquisitionPrice;
        set => _acquisitionPrice = value;
    }
}
