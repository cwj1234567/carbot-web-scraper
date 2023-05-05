using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace WebScraper.Services;

public class PgsqlHelper
{
    private readonly string _connString;

    public PgsqlHelper(IConfiguration configuration)
    {
        _connString = configuration["PgsqlConnectionString"];
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connString);
    }
}