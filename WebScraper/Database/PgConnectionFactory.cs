using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace WebScraper.Database;

public class PgConnectionFactory
{
    private static string _connectionString = string.Empty;

    public PgConnectionFactory(IConfiguration configuration, SshTunnel sshTunnel)
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            // Get the SSH tunnel singleton instance
            var forwardedPort = sshTunnel.ForwardedPort;

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = forwardedPort.BoundHost,
                Port = (int)forwardedPort.BoundPort,
                Database = configuration["DbDatabase"],
                Username = configuration["DbUser"],
                Password = configuration["DbPass"],
                TrustServerCertificate = true,
                Pooling = true
            };

            _connectionString = builder.ConnectionString;
        }
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}
