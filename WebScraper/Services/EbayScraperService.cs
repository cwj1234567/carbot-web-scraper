using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Cliver;
using Dapper;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using WebScraper.Database;
using WebScraper.Helpers;
using WebScraper.Models.Ebay;
using WebScraper.Services.Interfaces;
using static WebScraper.Helpers.WaitUntilElementExistsHelper;

namespace WebScraper.Services;

public class EbayScraperService : IScraperService
{
    private readonly ILogger<EbayScraperService> _logger;
    private readonly PgConnectionFactory _connFactory;
    private readonly IWebDriver _webDriver;

    public EbayScraperService(ILogger<EbayScraperService> logger, PgConnectionFactory connFactory,
        WebDriverHelper webDriverHelper)
    {
        _logger = logger;
        _connFactory = connFactory;
        _webDriver = webDriverHelper.Driver;
    }

    private async Task UpdateAuctionLinks(SearchConfig config)
    {
        var make = config.MakeEncoded ?? WebUtility.UrlEncode(config.Make);
        var model = config.ModelEncoded ?? WebUtility.UrlEncode(config.Model);
        var years = config.Years;

        var listingsUrl =
            $"https://www.ebay.com/sch/Cars-Trucks/6001/i.html?_dcat=6001&_fsrp=1&_vxp=mtr&_dmpt=US_Cars_Trucks&Transmission=-1&Make={make}&_ipg=240&LH_Sold=1&_sofindtype=21&_sacat=6001&Model={model}&_sop=12&fisc=c6001&_sadis=200&Model%2520Year={years}&LH_All=1&LH_PrefLoc=1";

        if (!string.IsNullOrEmpty(config.BodyType))
            listingsUrl =
                $"https://www.ebay.com/sch/Cars-Trucks/6001/i.html?_dcat=6001&_fsrp=1&_vxp=mtr&_dmpt=US_Cars_Trucks&Transmission=-1&Body%2520Type={config.BodyType}&Make={make}&_ipg=240&LH_Sold=1&_sofindtype=21&_sacat=6001&Model={model}&_sop=12&fisc=c6001&_sadis=200&Model%2520Year={years}&LH_All=1&LH_PrefLoc=1";

        try
        {
            _logger.LogInformation("Searching for links (Url = {listingsUrl})", listingsUrl);
            _webDriver.Url = listingsUrl;

            WaitUntilElementExists(_webDriver, By.ClassName("srp-results"));

            var links = _webDriver.FindElements(By.ClassName("s-item__link"));

            var hrefList = links.Select(link => link.GetAttribute("href")).ToList();
            var linkCount = hrefList.Count();
            foreach (var href in hrefList)
            {
                using var connection = _connFactory.CreateConnection();
                await connection.ExecuteAsync(
                    "insert into ebaymotors.links(auction_id, auction_url,is_processed,search_config_id,found_at) " +
                    "values (@AuctionId, @Url, @IsProcessed,@searchConfigId, now()) " +
                    "on conflict do nothing;",
                    new
                    {
                        AuctionId = Guid.NewGuid(),
                        Url = RemoveUrlTracker(href),
                        IsProcessed = false,
                        config.SearchConfigId
                    });
            }
        }
        catch (OpenQA.Selenium.WebDriverException e)
        {
            _logger.LogError("OpenQA.Selenium.WebDriverException: {Message}", e.Message);
        }
    }

    public async Task RunTaskAsync()
    {
        _logger.LogInformation("Starting Ebay Scraper");

        _logger.LogInformation("Processing unprocessed links from previous runs");

        await ProcessDbLinks();

        using var connection = _connFactory.CreateConnection();

        _logger.LogInformation("Retrieving search configs");
        var configs = await connection.QueryAsync<SearchConfig>(
            "SELECT search_config_id, vehicle_id, make, model, years, body_type, make_encoded, model_encoded FROM ebaymotors.searchconfig;");


        _logger.LogInformation("Searching for new auction links");

        foreach (var config in configs)
            await UpdateAuctionLinks(config);

        _logger.LogInformation("Finished searching for new auction links");

        await ProcessDbLinks();

        _webDriver.Quit();
        _webDriver.Dispose();
    }

    private async Task ProcessDbLinks()
    {
        var connection = _connFactory.CreateConnection();
        var unprocessedLinks = await connection.QueryAsync<AuctionLink>(
            "SELECT auction_id, auction_url, is_processed, search_config_id FROM ebaymotors.links  WHERE (processing_attempts IS NULL OR processing_attempts < 3) AND (is_processed IS NULL OR is_processed = false)");

        var auctionLinks = unprocessedLinks as AuctionLink[] ?? unprocessedLinks.ToArray();
        _logger.LogInformation("Processing {count} auction links", auctionLinks.Count());

        var i = 0;
        foreach (var link in auctionLinks)
        {
            i = i + 1;
            _logger.LogInformation("[{i}/{unprocessedLinks}] Processing link (auctionId = {AuctionId})", i,
                auctionLinks.Count(), link.AuctionId);

            try
            {
                await ProcessLink(link);
            }
            catch (OpenQA.Selenium.WebDriverException e)
            {
                _logger.LogError("OpenQA.Selenium.WebDriverException: {Message}", e.Message);

                if (e.Message.Contains("invalid session id") || e.Message.Contains("session deleted"))
                    break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing link (auctionId = {AuctionId})", link.AuctionId);
            }
        }
    }

    private async Task ProcessLink(AuctionLink link)
    {
        var errorMessage = string.Empty;

        try
        {
            NavigateToAuctionPage(link.AuctionUrl);
            await TakeScreenshot(link.AuctionId);
            var statusElementText = CheckAuctionStatus();
            var price = GetPrice();
            var parameterDict = GetParameterDictionary();
            var endTime = GetAuctionEndTime();
            await InsertData(parameterDict, price, link.AuctionId, endTime, statusElementText, link.SearchConfigId);
        }
        catch (ListingIssueException ex)
        {
            _logger.LogWarning(ex, "Error processing (link = {link}, error = {message})", link.AuctionUrl, ex.Message);
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing (link = {link}, error = {message})", link.AuctionUrl, ex.Message);
            errorMessage = ex.Message;
        }
        finally
        {
            await MarkLinkAsProcessed(link.AuctionId, errorMessage);
        }
    }


    private void NavigateToAuctionPage(string auctionUrl)
    {
        _webDriver.Url = auctionUrl + "&orig_cvip=true";
        WaitUntilElementExists(_webDriver, By.ClassName("ux-textspans"));
    }

    private string CheckAuctionStatus()
    {
        var statusElement = _webDriver.FindElement(By.CssSelector("div[class='vim d-statusmessage']"));
        var statusElementText = statusElement.Text;

        if (statusElementText.ToLower().Contains("relisted"))
            throw new ListingIssueException("Vehicle has been relisted");

        if (statusElementText.ToLower().Contains("because the item was sold"))
            throw new ListingIssueException("Vehicle has been sold");

        return statusElementText;
    }

    private string GetPrice()
    {
        var priceLabelElement = _webDriver.FindElement(By.CssSelector(".x-price-primary .ux-textspans"));
        var strikethrough = priceLabelElement.GetAttribute("class").Contains("ux-textspans--STRIKETHROUGH");

        if (strikethrough)
            throw new ListingIssueException("Could not confirm price");

        var priceElement = _webDriver.FindElement(By.CssSelector("div.vim-buybox-wrapper span[itemprop='price']"));
        return priceElement.GetAttribute("content");
    }

    private Dictionary<string, string> GetParameterDictionary()
    {
        var parameterDict = new Dictionary<string, string>();
        IList<IWebElement> elements =
            _webDriver.FindElements(By.CssSelector("div.vim.x-about-this-item div.ux-layout-section__row"));

        foreach (var element in elements)
        {
            var labelElements = element.FindElements(By.CssSelector("div.ux-labels-values__labels span.ux-textspans"));
            var valueElements = element.FindElements(By.CssSelector("div.ux-labels-values__values span.ux-textspans"));

            for (var i = 0; i < labelElements.Count; i++)
            {
                var label = labelElements[i].Text.Replace(":", "");
                var value = valueElements[i].Text;
                parameterDict.Add(label, value);
            }
        }

        return parameterDict;
    }

    private DateTime GetAuctionEndTime()
    {
        DateTimeRoutines.ParsedDateTime parsedDateTime;
        try
        {
            var dateElement =
                _webDriver.FindElement(By.CssSelector("div.vi-bboxrev-posabs.vi-bboxrev-dsplinline > span#bb_tlft"));

            if (!dateElement.Text.TryParseDateOrTime(DateTimeRoutines.DateTimeFormat.USA_DATE, out parsedDateTime))
                throw new ListingIssueException("Could not parse end time");
        }
        catch (NoSuchElementException)
        {
            try
            {
                var dateElementTimer =
                    _webDriver.FindElement(By.CssSelector("div.vim.x-timer-module > span.x-timer-module__timer"));

                if (!dateElementTimer.Text.TryParseDateOrTime(DateTimeRoutines.DateTimeFormat.USA_DATE,
                        out parsedDateTime))
                    throw new ListingIssueException("Could not parse end time");
            }
            catch (NoSuchElementException)
            {
                throw new ListingIssueException("Could not find end time element");
            }
        }

        return parsedDateTime.DateTime;
    }


    private async Task MarkLinkAsProcessed(Guid auctionId, string errorMessage)
    {
        _logger.LogInformation("Marking auction as processed (auctionId = {auctionId})", auctionId);
        using var connection = _connFactory.CreateConnection();
        if (string.IsNullOrEmpty(errorMessage))
        {
            await connection.ExecuteAsync(
                "update ebaymotors.links set is_processed = true, processed_at = now(), error_message = @errorMessage, processing_attempts = COALESCE(processing_attempts, 0) + 1 where auction_id = @AuctionId;",
                new { AuctionId = auctionId, errorMessage });
        }
        else
        {
            await connection.ExecuteAsync(
                "update ebaymotors.links set error_message = @errorMessage, processing_attempts = COALESCE(processing_attempts, 0) + 1 where auction_id = @AuctionId;",
                new { AuctionId = auctionId, errorMessage });
        }
    }


    private async Task InsertData(Dictionary<string, string> parameterDict, string price, Guid auctionId, DateTime date,
        string statusText, int searchConfigId)
    {
        _logger.LogInformation("Inserting data for auction (auctionId = {auctionId})", auctionId);
        using var connection = _connFactory.CreateConnection();


        var parameterJson = JsonSerializer.Serialize(parameterDict);
        var bidValue = decimal.Parse(price);
        var yearValue = int.Parse(parameterDict["Year"]);

        string? vin = null;
        if (parameterDict.ContainsKey("VIN (Vehicle Identification Number)"))
            vin = parameterDict["VIN (Vehicle Identification Number)"];

        string? mileage = null;
        if (parameterDict.ContainsKey("Mileage"))
            mileage = parameterDict["Mileage"];

        string? bodyType = null;
        if (parameterDict.ContainsKey("Body Type"))
            bodyType = parameterDict["Body Type"];

        var values = new
        {
            auctionId,
            BidValue = bidValue,
            EndDate = date,
            Year = yearValue,
            Make = parameterDict["Make"],
            Model = parameterDict["Model"],
            Vin = vin,
            Mileage = mileage,
            BodyType = bodyType,
            Parameters = parameterJson,
            StatusText = statusText,
            SearchConfigId = searchConfigId
        };

        await connection.ExecuteAsync(
            "insert into ebaymotors.auctions(auction_id, bid_value, end_date, year, make, model, vin, mileage, body_type, parameters, status_text, search_config_id) " +
            "values (@AuctionId, @BidValue, @EndDate, @Year, @Make, @Model, @Vin, @Mileage, @BodyType, cast(@Parameters as jsonb), @StatusText, @SearchConfigId) ",
            values);
    }

    private async Task TakeScreenshot(Guid auctionId)
    {
        _logger.LogInformation("Taking screenshot (auctionId = {auctionId})", auctionId);
        var screenshot = ((ITakesScreenshot)_webDriver).GetScreenshot();

        // Save the screenshot to a memory stream
        using var stream = new MemoryStream(screenshot.AsByteArray);
        // Set up the Amazon S3 client
        var client = new AmazonS3Client();

        // Set the bucket name and key for the screenshot
        var bucketName = "carbot-media-ebay";
        var key = $"{auctionId}.png";

        try
        {
            // Upload the screenshot to the S3 bucket
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = stream
            };
            await client.PutObjectAsync(request);
        }
        catch (AmazonS3Exception ex)
        {
            if (ex.ErrorCode == "ObjectAlreadyExists")
            {
                // object already exists -- do nothing
            }
            else
            {
                throw ex;
            }
        }
    }

    private string RemoveUrlTracker(string url)
    {
        var uriBuilder = new UriBuilder(url);

        var queryString = uriBuilder.Query;

        var queryParams = queryString.TrimStart('?')
            .Split('&')
            .ToDictionary(kvp => kvp.Split('=')[0], kvp => kvp.Split('=')[1]);

        queryParams.Remove("amdata");

        var newQueryString = string.Join("&", queryParams.Select(kvp => kvp.Key + "=" + kvp.Value));

        uriBuilder.Query = newQueryString;

        return uriBuilder.ToString();
    }
}
