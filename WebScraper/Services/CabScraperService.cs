using Cliver;
using Dapper;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using WebScraper.Database;
using WebScraper.Helpers;
using WebScraper.Models;
using WebScraper.Services.Interfaces;
using static WebScraper.Helpers.WaitUntilElementExistsHelper;

namespace WebScraper.Services;

public class CabScraperService : IScraperService
{
    private readonly ILogger<CabScraperService> _logger;
    private readonly PgConnectionFactory _connFactory;
    private readonly IWebDriver _webDriver;
    
    public CabScraperService(ILogger<CabScraperService> logger, PgConnectionFactory connFactory,
        WebDriverHelper webDriverHelper)
    {
        _logger = logger;
        _connFactory = connFactory;
        _webDriver = webDriverHelper.Driver;
    }

    private Task<List<string>> GetAuctionLinks(string searchString)
    {
        _logger.LogInformation("Getting auction links (searchString = {searchString})", searchString);

        var loopit = true;
        var links = new List<string>();
        var pagenum = 1;
        while (loopit)
        {
            _webDriver.Url = $"https://carsandbids.com/search?page={pagenum}&q={searchString}";
            var url = _webDriver.Url;

            var firstElement = WaitUntilElementExists(_webDriver, By.ClassName("auction-item"));
            var auctionElements = _webDriver.FindElements(By.ClassName("auction-item"));

            if (auctionElements.Count < 30) loopit = false;

            links.AddRange(from element in auctionElements
                select element.FindElement(By.ClassName("auction-title"))
                into titleElement
                select titleElement.FindElement(By.LinkText(titleElement.Text))
                into linkElement
                select linkElement.GetAttribute("href"));

            pagenum = pagenum + 1;
        }

        return Task.FromResult(links);
    }

    private CabAuctionItem ProcessAuction(string url)
    {
        _webDriver.Url = url;

        bool? ended = null;

        var auctionTitle = WaitUntilElementExists(_webDriver, By.ClassName("auction-title")).Text;

        int year;

        try
        {
            year = int.Parse(auctionTitle.Substring(0, 4));
        }
        catch (Exception e)
        {
            throw new ListingIssueException("Could not parse year from auction title");
        }


        var endTimeText = WaitUntilElementExists(_webDriver, By.ClassName("end-time")).Text;

        if (endTimeText.ToLowerInvariant().Contains("ended"))
            ended = true;
        else if (endTimeText.ToLowerInvariant().Contains("ending"))
            ended = false;
        else
            throw new ListingIssueException("Could not parse auction end time");


        var endIcon = WaitUntilElementExists(_webDriver, By.ClassName("end-icon"));

        if (!DateTimeRoutines.TryParseDateOrTime(endIcon.Text, DateTimeRoutines.DateTimeFormat.USA_DATE,
                out DateTimeRoutines.ParsedDateTime parsedDateTime))
            throw new ListingIssueException("Could not parse end time");


        var quickFacts = WaitUntilElementExists(_webDriver, By.ClassName("quick-facts"));
        var facts = quickFacts.FindElements(By.TagName("dd"));

        var make = facts[0].Text;

        string model;
        try
        {
            model = facts[1].FindElements(By.XPath(".//*"))[0].Text;
        }
        catch (Exception e)
        {
            throw new ListingIssueException("Could not parse model");
        }


        int? mileage = null;
        try
        {
            mileage = Int32.Parse(facts[2].Text.Replace(",", ""));
        }
        catch (Exception e)
        {
            _logger.LogWarning("Could not parse mileage (url = {url})", url);
        }

        var vin = facts[3].Text;
        var titleStatus = facts[4].Text;
        var location = facts[5].Text;
        var seller = facts[6].Text;
        var engine = facts[7].Text;
        var driveTrain = facts[8].Text;
        var transmission = facts[9].Text;
        var bodyStyle = facts[10].Text;
        var exteriorColor = facts[11].Text;
        var interiorColor = facts[12].Text;
        var sellerType = facts[13].Text;

        var value = WaitUntilElementExists(_webDriver, By.ClassName("bid-value")).Text;

        decimal price;
        try
        {
            price = Decimal.Parse(value.Replace("$", "").Replace(",", ""));
        }
        catch (Exception e)
        {
            throw new ListingIssueException("Could not parse price");
        }


        return new CabAuctionItem()
        {
            Year = year,
            Make = make,
            Model = model,
            Mileage = mileage,
            Vin = vin,
            TitleStatus = titleStatus,
            Location = location,
            Seller = seller,
            Engine = engine,
            Drivetrain = driveTrain,
            Transmission = transmission,
            BodyStyle = bodyStyle,
            ExteriorColor = exteriorColor,
            InteriorColor = interiorColor,
            SellerType = sellerType,
            EndDate = parsedDateTime.DateTime,
            Price = price,
            Ended = ended
        };
    }

    public async Task Scrape()
    {
        _logger.LogInformation("Scraping carsandbids.com");
        using var dbConnection = _connFactory.CreateConnection();

        var searchStrings =
            dbConnection.Query<SearchStrings>(
                "select search_string_id, search_string from carsandbids.search_strings");

        foreach (var s in searchStrings)
        {
            var searchStringLinks = GetAuctionLinks(s.SearchString);

            foreach (var link in await searchStringLinks)
                await dbConnection.ExecuteAsync(
                    "insert into carsandbids.auctions(auction_id, search_string_id, auction_url) " +
                    "values (@AuctionId, @SearchStringId, @AuctionUrl) " +
                    "on conflict do nothing;",
                    new
                    {
                        AuctionId = Guid.NewGuid(),
                        SearchStringId = s.SearchStringId,
                        AuctionUrl = link
                    });
        }

        var linksToProcess =
            dbConnection.Query<string>("select auction_url from carsandbids.auctions where ended is not true;");

        _logger.LogInformation("Processing {linksToProcessCount} links", linksToProcess.Count());
        foreach (var link in linksToProcess)
        {
            
            CabAuctionItem auction;
            try
            {
                auction = ProcessAuction(link);
            }
            catch (ListingIssueException e)
            {
                await dbConnection.ExecuteAsync(
                    "update carsandbids.auctions set error_message = @ErrorMessage where auction_url = @AuctionUrl",
                    new { AuctionUrl = link, ErrorMessage = e.Message });
                continue;
            }

            await dbConnection.ExecuteAsync("update carsandbids.auctions set " +
                                            "make = @Make, " +
                                            "model = @Model, " +
                                            "year = @Year, " +
                                            "mileage = @Mileage, " +
                                            "vin = @Vin, " +
                                            "title_status = @TitleStatus, " +
                                            "location = @Location, " +
                                            "seller = @Seller, " +
                                            "engine = @Engine, " +
                                            "drivetrain = @Drivetrain, " +
                                            "transmission = @Transmission, " +
                                            "body_style = @BodyStyle, " +
                                            "exterior_color = @ExteriorColor, " +
                                            "interior_color = @InteriorColor, " +
                                            "seller_type = @SellerType, " +
                                            "end_date = @EndDate, " +
                                            "bid_value = @BidValue, " +
                                            "updated_at = @UpdatedAt, " +
                                            "ended = @Ended " +
                                            "where auction_url = @AuctionUrl;",
                new
                {
                    Make = auction.Make,
                    Model = auction.Model,
                    Year = auction.Year,
                    Mileage = auction.Mileage,
                    Vin = auction.Vin,
                    TitleStatus = auction.TitleStatus,
                    Location = auction.Location,
                    Seller = auction.Seller,
                    Engine = auction.Engine,
                    Drivetrain = auction.Drivetrain,
                    Transmission = auction.Transmission,
                    BodyStyle = auction.BodyStyle,
                    ExteriorColor = auction.ExteriorColor,
                    InteriorColor = auction.InteriorColor,
                    SellerType = auction.SellerType,
                    EndDate = auction.EndDate,
                    BidValue = auction.Price,
                    UpdatedAt = auction.UpdatedAt,
                    Ended = auction.Ended,
                    AuctionUrl = link
                });
        }

        _webDriver.Quit();
        _webDriver.Dispose();

        _logger.LogInformation("Scraping carsandbids.com complete");
    }
}