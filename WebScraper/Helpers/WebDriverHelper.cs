using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;

namespace WebScraper.Helpers;

public class WebDriverHelper
{
    private readonly ILogger<WebDriverHelper> _logger;

    public WebDriverHelper(ILogger<WebDriverHelper> _logger)
    {
        Driver = new RemoteWebDriver(new Uri("http://localhost:4444"), new ChromeOptions());
        _logger.LogInformation("Webdriver connected");
    }

    public IWebDriver Driver { get; }
}
