using Microsoft.Extensions.Logging;
using WebScraper.Helpers;

public class SshLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new SshLogger();
    }

    public void Dispose()
    {
    }
}
