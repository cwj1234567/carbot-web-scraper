using Dapper;
using Microsoft.Extensions.Logging;
using WebScraper.Models;
using WebScraper.Services.Interfaces;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Cliver;
using HtmlAgilityPack;
using WebScraper.Database;

namespace WebScraper.Services;

public class BatScraperService : IScraperService
{
    private readonly ILogger<BatScraperService> _logger;
    private readonly PgConnectionFactory _connFactory;

    public BatScraperService(ILogger<BatScraperService> logger, PgConnectionFactory connFactory)
    {
        _logger = logger;
        _connFactory = connFactory;
    }


    public async Task RunTaskAsync()
    {
        _logger.LogInformation("Scraping bring-a-trailer.com");

        using var httpClient = new HttpClient();
        IEnumerable<int> keywordPageIds;

        using (var connection = _connFactory.CreateConnection())
        {
            keywordPageIds = connection.Query<int>("SELECT keyword_page_id FROM bringatrailer.keywordpages");
        }

        foreach (var keywordPageId in keywordPageIds)
        {
            var page = 1;

            BatKeywordPage? result;
            do
            {
                var url =
                    $"https://bringatrailer.com/wp-json/bringatrailer/1.0/data/keyword-filter?bat_keyword_pages={keywordPageId}&sort=td&page={page}&results=items";
                result = await httpClient.GetFromJsonAsync<BatKeywordPage>(url).ConfigureAwait(false);

                var newItems = GetNonExistentAuctionItems(result?.items ?? throw new InvalidOperationException());

                foreach (var item in newItems)
                {
                    await ProcessItem(item, keywordPageId).ConfigureAwait(false);
                }

                page++;
            } while (result.page_current != result.page_maximum);
        }
    }


    private async Task ProcessItem(Item item, int keywordPageId)
    {
        try
        {
            var (splitUrl, endDate, ended, bidValue) = ProcessItemMetadata(item);
            var vehicleInfo = await ValidateAndGetVehicleInfo(item.url).ConfigureAwait(false);

            var year = ExtractYear(splitUrl, vehicleInfo);
            if (year == 0)
                return;

            await SaveAuction(item, splitUrl, endDate, ended, bidValue, year, keywordPageId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e.Message);
        }
    }

    private (string[] splitUrl, DateTime endDate, bool? ended, decimal bidValue) ProcessItemMetadata(Item item)
    {
        var splitUrl = (item.url.Replace("https://bringatrailer.com/listing/", "")).Replace("/", "").Split("-");
        var endDate = ParseEndDate(item.subtitle);
        var (ended, bidValue) = ParseSubtitle(item.subtitle);

        return (splitUrl, endDate, ended, bidValue);
    }

    private DateTime ParseEndDate(string subtitle)
    {
        if (!subtitle.TryParseDateOrTime(DateTimeRoutines.DateTimeFormat.USA_DATE,
                out DateTimeRoutines.ParsedDateTime parsedDateTime))
        {
            _logger.LogWarning("Could not parse date {date}", subtitle);
        }

        return parsedDateTime.DateTime;
    }

    private (bool? ended, decimal bidValue) ParseSubtitle(string subtitle)
    {
        var splitSubtitle = subtitle.Split(" ");

        var ended = splitSubtitle[0] switch
        {
            "Sold" => true,
            "Bid" => false,
            _ => (bool?)null
        };

        var bidValue = ParseBidValue(splitSubtitle[2]);

        return (ended, bidValue);
    }

    private decimal ParseBidValue(string bidText)
    {
        try
        {
            return decimal.Parse(bidText.Replace("$", "").Replace(",", ""));
        }
        catch (Exception)
        {
            _logger.LogWarning("Could not parse bid value {bidValue}", bidText);
            return 0;
        }
    }

    private async Task<Dictionary<string, string>> ValidateAndGetVehicleInfo(string url)
    {
        try
        {
            return await ValidateVehicleInfo(url).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            _logger.LogWarning("Could not validate vehicle data (url = {url})", url);
            throw;
        }
    }

    private int ExtractYear(string[] splitUrl, Dictionary<string, string> vehicleInfo)
    {
        var year = 0;

        try
        {
            year = int.Parse(splitUrl[0]);
        }
        catch (Exception)
        {
            try
            {
                year = int.Parse(vehicleInfo["Year"]);
            }
            catch (Exception)
            {
                _logger.LogWarning("Could not parse year from url");
            }
        }

        return year;
    }

    private async Task SaveAuction(Item item, string[] splitUrl, DateTime endDate, bool? ended, decimal bidValue,
        int year, int keywordPageId)
    {
        try
        {
            using (var connection = _connFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    "insert into bringatrailer.auctions(auction_id, auction_url, year, make, model, bid_value, end_date, ended, updated_at, keyword_page_id ) " +
                    "values (@AuctionId, @AuctionUrl, @Year, @Make, @Model, @BidValue, @EndDate, @Ended, @UpdatedAt, @KeywordPageId) " +
                    "on conflict do nothing;",
                    new
                    {
                        AuctionId = Guid.NewGuid(),
                        AuctionUrl = item.url,
                        Year = year,
                        Make = splitUrl[1],
                        Model = splitUrl[2],
                        BidValue = bidValue,
                        EndDate = endDate,
                        Ended = ended,
                        UpdatedAt = DateTime.Now,
                        KeywordPageId = keywordPageId
                    }).ConfigureAwait(false);
            }
        }
        catch (System.IndexOutOfRangeException)
        {
            _logger.LogWarning("Could not parse url {url}", item.url);
            throw;
        }
    }

    private async Task<Dictionary<string, string>> ValidateVehicleInfo(string url)
    {
        var httpClient = new HttpClient();
        var html = await httpClient.GetStringAsync(url);

        var vehicleInfo = new Dictionary<string, string>();

        // Load the HTML document
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        // Select all strong elements using a CSS selector
        var strongNodes = htmlDoc.DocumentNode.SelectNodes("//strong[@class='group-title-label']");
        if (strongNodes == null)
            return new Dictionary<string, string>();

        // Iterate through the strong elements
        foreach (var strongNode in strongNodes)
        {
            var field = strongNode.InnerText;
            // Navigate to the next element in the document
            var nextNode = strongNode.NextSibling;

            // Extract the text content of the element
            var value = nextNode.InnerText;
            vehicleInfo.Add(field, value);
        }


        var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//h1[@class='post-title listing-post-title']");
        if (titleNode == null) return vehicleInfo;
        // Extract the text content of the element
        var title = titleNode.InnerText;

        var match = Regex.Match(title, @"\b\d{4}\b");

        if (!match.Success) return vehicleInfo;
        // Extract the year from the match
        var year = match.Value;
        vehicleInfo.Add("Year", year);

        return vehicleInfo;
    }


    private List<Item> GetNonExistentAuctionItems(List<Item> items)
    {
        using var connection = _connFactory.CreateConnection();
        connection.Open();

        // Get the list of URLs from the items
        var urls = items.Select(i => i.url).ToList();

        // Create a parameterized query to prevent SQL injection attacks
        var query = "SELECT auction_url FROM bringatrailer.auctions WHERE auction_url = ANY(@urls)";

        // Execute the query and get the list of URLs that exist in the table
        var existingUrls = connection.Query<string>(query, new { urls });

        // Return the list of items with URLs that are not in the table
        return items.Where(i => !existingUrls.Contains(i.url)).ToList();
    }
}
