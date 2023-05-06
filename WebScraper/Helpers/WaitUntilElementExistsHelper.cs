using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;

namespace WebScraper.Helpers;

public static class WaitUntilElementExistsHelper
{
    public static IWebElement WaitUntilElementExists(IWebDriver driver, By elementLocator, int timeout = 10)
    {
        try
        {
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(timeout));
            return wait.Until(ExpectedConditions.ElementExists(elementLocator));
        }
        catch (NoSuchElementException)
        {
            throw new ListingIssueException("Element with locator: '" + elementLocator +
                                            "' was not found in current context page.");
        }
    }
}
