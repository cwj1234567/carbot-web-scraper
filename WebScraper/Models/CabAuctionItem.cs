namespace WebScraper.Models;

public class CabAuctionItem
{
    public string Make { get; set; }
    public string Model { get; set; }
    public int Year { get; set; }
    public int? Mileage { get; set; }
    public string Vin { get; set; }
    public string TitleStatus { get; set; }
    public string Location { get; set; }
    public string Seller { get; set; }
    public string Engine { get; set; }
    public string Drivetrain { get; set; }
    public string Transmission { get; set; }
    public string BodyStyle { get; set; }
    public string ExteriorColor { get; set; }
    public string InteriorColor { get; set; }
    public string SellerType { get; set; }
    public DateTime EndDate { get; set; }
    public decimal Price { get; set; }
    public DateTime UpdatedAt => DateTime.Now;
    public bool? Ended { get; set; }
}
