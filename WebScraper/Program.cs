using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using WebScraper.Database;
using WebScraper.Services;
using WebScraper.Helpers;
using Microsoft.Extensions.Logging;
using System;

var builder = new ConfigurationBuilder();

    
 

    // Add configuration from appsettings.json
    builder.AddJsonFile("appsettings.json",
        optional: false,
        reloadOnChange: true);

    // configure logging
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Build()) // connect serilog to our configuration folder
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .CreateLogger();

    Log.Logger.Information("Start");

    // map the c# pascal case model property names to the pgsql snake case column names
    Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    
    

    // build host and configure our DI services
    var host = Host.CreateDefaultBuilder()
        .ConfigureServices((_, services) =>
        {
            // Add custom SSH logger
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(provider => provider.GetService<ILoggerFactory>().CreateLogger("SshLogger"));

            // Update the PgConnectionFactory registration
            services.AddTransient<PgConnectionFactory>();

            services.AddSingleton<SshTunnel>();

            services.AddSingleton<WebDriverHelper>(); // selenium webdriver
            services.AddSingleton<CabScraperService>();
            services.AddSingleton<BatScraperService>();
            services.AddSingleton<EbayScraperService>();
            services.AddSingleton<Temp>();
        })

        .UseSerilog()
        .Build();

    // now the fun starts
    try
    {
        
        await host.Services.GetService<CabScraperService>().Test();
        
        
        Log.Logger.Information("Waiting ~30s for chrome driver to start");
        await Task.Delay(30000);


        // run whatever service the environment variable is set to
        var serviceToRun = Environment.GetEnvironmentVariable("SCRAPER_SERVICE");

        switch (serviceToRun)
        {
            case "cab":
                await host.Services.GetService<CabScraperService>().Scrape();
                break;
            case "bat":
                await host.Services.GetService<BatScraperService>().Scrape();
                break;
            case "ebay":
                await host.Services.GetService<EbayScraperService>().Scrape();
                break;
            default:
                await host.Services.GetService<EbayScraperService>().Scrape();
                break;
        }

        // http request to web api to reset the cache
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", builder.Build().GetSection("CarbotApiKey").Value);
        var response = await client.DeleteAsync("https://api.carbot.lol/cache");

        if (response.IsSuccessStatusCode)
            Log.Logger.Information("Cache cleared");
        else
            Log.Logger.Error("Cache clear failed");
    }
    catch (Exception e)
    {
        Log.Logger.Fatal(e, "Error");
    }

Log.Logger.Information("Stop");

