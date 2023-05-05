namespace WebScraper.Models.Ebay;

public class AuctionLink
{
    public Guid AuctionId { get; set; }
    public string AuctionUrl { get; set; }
    
    public bool IsProcessed { get; set; }
    
    public int VehicleId { get; set; }
    
    public int ProcessingAttempts { get; set; }
    
    public int SearchConfigId { get; set; }
}