namespace WebScraper.Models.Ebay;

public class SearchConfig
{
    public int SearchConfigId { get; set; }
    public int VehicleId { get; set; }
    public string Make { get; set; }
    public string Model { get; set; }
    public string Years { get; set; }
    public string BodyType { get; set; }
    public string? MakeEncoded { get; set; }
    
    public string? ModelEncoded { get; set; }

}