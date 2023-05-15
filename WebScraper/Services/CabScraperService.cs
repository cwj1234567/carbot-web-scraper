using Cliver;
using Dapper;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using System.Data;
using WebScraper.Database;
using WebScraper.Helpers;
using WebScraper.Models;
using WebScraper.Services.Interfaces;
using static WebScraper.Helpers.WaitUntilElementExistsHelper;

namespace WebScraper.Services
{
    public class CabScraperService : IScraperService
    {
        private readonly ILogger<CabScraperService> _logger;
        private readonly PgConnectionFactory _connFactory;
        private readonly IWebDriver _webDriver;

        private const string BaseSearchUrl = "https://carsandbids.com/search?page={0}&q={1}";
        private const int MaxAuctionElements = 30;

        public CabScraperService(ILogger<CabScraperService> logger, PgConnectionFactory connFactory,
            WebDriverHelper webDriverHelper)
        {
            _logger = logger;
            _connFactory = connFactory;
            _webDriver = webDriverHelper.Driver;
        }

        private Task<List<string>> GetAuctionLinksAsync(string searchString)
        {
            _logger.LogInformation("Getting auction links (searchString = {searchString})", searchString);

            var links = new List<string>();
            var pageNum = 1;

            while (true)
            {
                _webDriver.Url = string.Format(BaseSearchUrl, pageNum, searchString);

                var auctionElements = _webDriver.FindElements(By.ClassName("auction-item"));

                if (auctionElements.Count < MaxAuctionElements) break;

                links.AddRange(auctionElements.Select(GetLinkFromElement));

                pageNum++;
            }

            return Task.FromResult(links);
        }

        private string GetLinkFromElement(IWebElement element)
        {
            var titleElement = element.FindElement(By.ClassName("auction-title"));
            var linkElement = titleElement.FindElement(By.LinkText(titleElement.Text));
            return linkElement.GetAttribute("href");
        }

        private CabAuctionItem ProcessAuction(string url)
        {
            _webDriver.Url = url;

            var auctionTitle = WaitUntilElementExists(_webDriver, By.ClassName("auction-title")).Text;
            var year = ParseYear(auctionTitle);

            var endTimeText = WaitUntilElementExists(_webDriver, By.ClassName("end-time")).Text.ToLowerInvariant();
            var ended = GetAuctionEndedStatus(endTimeText);

            var endIcon = WaitUntilElementExists(_webDriver, By.ClassName("end-icon"));
            var parsedDateTime = ParseDateTime(endIcon.Text);

            var facts = GetQuickFacts();

            var price = ParsePrice(WaitUntilElementExists(_webDriver, By.ClassName("bid-value")).Text);

            return new CabAuctionItem()
            {
                Year = year,
                Make = facts[0].Text,
                Model = GetModel(facts[1]),
                Mileage = GetMileage(facts[2]),
                Vin = facts[3].Text,
                TitleStatus = facts[4].Text,
                Location = facts[5].Text,
                Seller = facts[6].Text,
                Engine = facts[7].Text,
                Drivetrain = facts[8].Text,
                Transmission = facts[9].Text,
                BodyStyle = facts[10].Text,
                ExteriorColor = facts[11].Text,
                InteriorColor = facts[12].Text,
                SellerType = facts[13].Text,
                EndDate = parsedDateTime.DateTime,
                Price = price,
                Ended = ended
            };
        }

        private int ParseYear(string auctionTitle)
        {
            if (!int.TryParse(auctionTitle.Substring(0, 4), out int year))
                throw new ListingIssueException("Could not parse year from auction title");

            return year;
        }

        private bool GetAuctionEndedStatus(string endTimeText)
        {
            if (endTimeText.Contains("ended"))
                return true;
            if (endTimeText.Contains("ending"))
                return false;

            throw new ListingIssueException("Could not parse auction end time");
        }

        private DateTimeRoutines.ParsedDateTime ParseDateTime(string dateText)
        {
            if (!dateText.TryParseDateOrTime(DateTimeRoutines.DateTimeFormat.USA_DATE,
                    out DateTimeRoutines.ParsedDateTime parsedDateTime))
                throw new ListingIssueException("Could not parse end time");

            return parsedDateTime;
        }

        private List<IWebElement> GetQuickFacts()
        {
            var quickFacts = WaitUntilElementExists(_webDriver, By.ClassName("quick-facts"));
            return quickFacts.FindElements(By.TagName("dd")).ToList();
        }

        private string GetModel(IWebElement fact)
        {
            try
            {
                return fact.FindElements(By.XPath(".//*"))[0].Text;
            }
            catch (Exception e)
            {
                throw new ListingIssueException("Could not parse model");
            }
        }

        private int? GetMileage(IWebElement fact)
        {
            if (!int.TryParse(fact.Text.Replace(",", ""), out var mileage))
            {
                _logger.LogWarning("Could not parse mileage (url = {url})", _webDriver.Url);
                return null;
            }

            return mileage;
        }

        private decimal ParsePrice(string value)
        {
            if (!Decimal.TryParse(value.Replace("$", "").Replace(",", ""), out decimal price))
                throw new ListingIssueException("Could not parse price");

            return price;
        }

        public async Task RunTaskAsync()
        {
            _logger.LogInformation("Scraping carsandbids.com");

            using var dbConnection = _connFactory.CreateConnection();
            var searchStrings =
                dbConnection.Query<SearchStrings>(
                    "select search_string_id, search_string from carsandbids.search_strings");

            await ProcessSearchStringsAsync(searchStrings, dbConnection);

            var linksToProcess =
                dbConnection.Query<string>("select auction_url from carsandbids.auctions where ended is not true;");

            _logger.LogInformation("Processing {linksToProcessCount} links", linksToProcess.Count());
            await ProcessAuctionLinksAsync(linksToProcess, dbConnection);

            _webDriver.Quit();
            _webDriver.Dispose();

            _logger.LogInformation("Scraping carsandbids.com complete");
        }

        private async Task ProcessSearchStringsAsync(IEnumerable<SearchStrings> searchStrings,
            IDbConnection dbConnection)
        {
            foreach (var s in searchStrings)
            {
                var searchStringLinks = await GetAuctionLinksAsync(s.SearchString);

                foreach (var link in searchStringLinks)
                {
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
            }
        }

        private async Task ProcessAuctionLinksAsync(IEnumerable<string> linksToProcess, IDbConnection dbConnection)
        {
            foreach (var link in linksToProcess)
            {
                try
                {
                    var auction = ProcessAuction(link);

                    await UpdateAuctionDataAsync(auction, dbConnection, link);
                }
                catch (ListingIssueException e)
                {
                    await SetAuctionErrorAsync(dbConnection, link, e.Message);
                }
            }
        }

        private async Task SetAuctionErrorAsync(IDbConnection dbConnection, string link, string errorMessage)
        {
            await dbConnection.ExecuteAsync(
                "update carsandbids.auctions set error_message = @ErrorMessage where auction_url = @AuctionUrl",
                new { AuctionUrl = link, ErrorMessage = errorMessage });
        }

        private async Task UpdateAuctionDataAsync(CabAuctionItem auction, IDbConnection dbConnection, string link)
        {
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
    }
}
