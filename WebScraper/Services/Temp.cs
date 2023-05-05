using Dapper;
using WebScraper.Services.Interfaces;
using OpenQA.Selenium;
using OpenQA.Selenium.DevTools.V105.Fetch;
using WebScraper.Helpers;
using static WebScraper.Helpers.WaitUntilElementExistsHelper;

namespace WebScraper.Services;

public class Temp : IScraperService
{
    private readonly PgsqlHelper _pgsqlHelper;
    private readonly IWebDriver _webDriver;

    public Temp(PgsqlHelper pgsqlService, WebDriverHelper webDriverHelper)
    {
        _pgsqlHelper = pgsqlService;
        _webDriver = webDriverHelper.Driver;
    }

    public async Task Scrape()
    {
        _webDriver.Url = "https://bringatrailer.com/models/";

        WaitUntilElementExists(_webDriver, By.ClassName("post-title"));

        var elements = _webDriver.FindElements(By.ClassName("previous-listing-image-link"));

        var links = elements.Select(element => element.GetAttribute("href")).ToList();

        foreach (var link in links)
        {
            try
            {
                _webDriver.Url = link;
                WaitUntilElementExists(_webDriver, By.ClassName("hero-inner"));
                var element = _webDriver.FindElement(By.XPath("//span[@data-bind='text: formattedTotal']"));
                var value = element.Text;

                var count = int.TryParse(value, out var result) ? result : 0;

                using var connection = _pgsqlHelper.CreateConnection();
                await connection.ExecuteAsync(
                    @"INSERT INTO bringatrailer.research_sale_count(page_url, sale_count) VALUES (@link, @result) on conflict do nothing;",
                    new { link, result });
            }
            catch (Exception ex)
            {
                // lol
            }
        }

        var x = 0;
    }
}