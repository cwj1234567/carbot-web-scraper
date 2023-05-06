using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;

namespace WebScraper.Helpers;

public class WebDriverHelper
{
    private IWebDriver _driver;
    private readonly ILogger<WebDriverHelper> _logger;

    public WebDriverHelper(ILogger<WebDriverHelper> _logger)
    {
        _driver = new RemoteWebDriver(new Uri($"http://localhost:4444"), new ChromeOptions());
        _logger.LogInformation("Webdriver connected");
    }

    public IWebDriver Driver => _driver;
}
