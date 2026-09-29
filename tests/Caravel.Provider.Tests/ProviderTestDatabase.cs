using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Caravel.Provider.Tests;

internal static class ProviderTestDatabase
{
    public static void Configure(DbContextOptionsBuilder options, string provider, string database)
    {
        switch (provider)
        {
            case "sqlite":
                options.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), database + ".db")}");
                break;
            case "sqlserver":
                var sql = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CARAVEL_TEST_SQLSERVER")
                    ?? "Server=localhost;Integrated Security=true;TrustServerCertificate=true");
                RequireLoopback(sql.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0]);
                sql.InitialCatalog = database;
                options.UseSqlServer(sql.ConnectionString);
                break;
            case "postgres":
                var postgres = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CARAVEL_TEST_POSTGRES")
                    ?? "Host=localhost;Username=postgres");
                RequireLoopback(postgres.Host);
                postgres.Database = database;
                options.UseNpgsql(postgres.ConnectionString);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private static void RequireLoopback(string? host)
    {
        if (host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Provider tests create/drop disposable databases and accept only loopback database servers.");
    }
}
