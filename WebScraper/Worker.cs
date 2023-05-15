using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebScraper.Services.Interfaces;

namespace WebScraper;

public class Worker : IHostedService
{
    private readonly ILogger<Worker> _logger;
    private readonly IScraperService _service;
    
    public Worker(ILogger<Worker> logger, IScraperService service)
    {
        _logger = logger;
        _service = service;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
        await _service.RunTaskAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker stopping at: {time}", DateTimeOffset.Now);
        return Task.CompletedTask;
    }
}
