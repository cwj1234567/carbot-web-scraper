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

    public async Task Scrape()
    {
        _logger.LogInformation("Scraping bring-a-trailer.com");

        using var httpClient = new HttpClient();

        IEnumerable<int> keywordPageIds;
        using (var connection = _connFactory.CreateConnection())
        {
            keywordPageIds =
                connection.Query<int>("SELECT keyword_page_id FROM bringatrailer.keywordpages");
        }

        foreach (var keywordPageId in keywordPageIds)
        {
            var loopIt = true;

            var page = 1;
            while (loopIt)
            {
                var task = httpClient.GetFromJsonAsync<BatKeywordPage>(
                    $"https://bringatrailer.com/wp-json/bringatrailer/1.0/data/keyword-filter?bat_keyword_pages={keywordPageId}&sort=td&page={page}&results=items");
                var result = task.Result;

                bool? ended = null;
                decimal bidValue = 0;

                var newItems = GetNonExistentAuctionItems(result.items);

                foreach (var item in newItems)
                {
                    
                    DateTime endDate;
                    string[] splitUrl;
                    try
                    {
                        splitUrl = (item.url.Replace("https://bringatrailer.com/listing/", "")).Replace("/", "")
                            .Split("-");


                        if (!item.subtitle.TryParseDateOrTime(DateTimeRoutines.DateTimeFormat.USA_DATE,
                                out DateTimeRoutines.ParsedDateTime parsedDateTime))
                            _logger.LogWarning("Could not parse date {date} (url = {url})", item.subtitle);

                        endDate = parsedDateTime.DateTime;


                        var splitSubtitle = item.subtitle.Split(" ");

                        ended = splitSubtitle[0] switch
                        {
                            "Sold" => true,
                            "Bid" => false,
                            _ => ended
                        };

                        try
                        {
                            bidValue = decimal.Parse(splitSubtitle[2].Replace("$", "").Replace(",", ""));
                        }
                        catch (Exception)
                        {
                            _logger.LogWarning("Could not parse bid value (url = {url})", item.url);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e.Message);
                        continue;
                    }


                    Dictionary<string, string> vehicleInfo;
                    try
                    {
                        vehicleInfo = (await ValidateVehicleInfo(item.url));
                    }
                    catch (ArgumentException e)
                    {
                        _logger.LogWarning("Could not validate vehicle data (url = {url})", item.url);
                        continue;
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e.Message);
                        continue;
                    }

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
                            _logger.LogWarning("Could not parse year (url = {url})", item.url);
                            continue;
                        }
                    }

                    if (year == 0)
                        continue;

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
                                });
                        }
                    }
                    catch (System.IndexOutOfRangeException)
                    {
                        _logger.LogWarning("Could not parse url {url}", item.url);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e.Message);
                    }
                }

                if (result.page_current == result.page_maximum)
                    loopIt = false;

                page++;
            }
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