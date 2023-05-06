using Microsoft.Extensions.Logging;

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
