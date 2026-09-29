using Caravel.IdentitySample;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace Caravel.Identity.Tests;

internal sealed class IdentityTestDatabase : IAsyncDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-identity-");
    private bool created;
    public string Provider { get; } = Environment.GetEnvironmentVariable("CARAVEL_IDENTITY_TEST_PROVIDER") ?? "sqlite";
    public string DatabasePath => Path.Combine(directory.FullName, "synthetic.db");
    public string ConfigurationValue { get; }
    public string Connection { get; }

    public IdentityTestDatabase()
    {
        var name = "caravel_identity_" + Guid.NewGuid().ToString("N");
        switch (Provider)
        {
            case "sqlite":
                ConfigurationValue = DatabasePath;
                Connection = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
                break;
            case "sqlserver":
                var sql = new SqlConnectionStringBuilder(RequiredVariable("CARAVEL_TEST_SQLSERVER"));
                if (!string.IsNullOrWhiteSpace(sql.AttachDBFilename))
                    throw new InvalidOperationException("Provider tests cannot attach database files.");
                RequireLoopback(sql.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0]);
                sql.InitialCatalog = name;
                sql.Pooling = false;
                sql.ConnectTimeout = 5;
                ConfigurationValue = Connection = sql.ConnectionString;
                break;
            case "postgres":
                var postgres = new NpgsqlConnectionStringBuilder(RequiredVariable("CARAVEL_TEST_POSTGRES"));
                RequireLoopback(postgres.Host);
                postgres.Database = name;
                postgres.Pooling = false;
                postgres.Timeout = 5;
                ConfigurationValue = Connection = postgres.ConnectionString;
                break;
            default:
                throw new InvalidOperationException("CARAVEL_IDENTITY_TEST_PROVIDER must be sqlite, sqlserver or postgres.");
        }
    }

    public void Configure(DbContextOptionsBuilder options) => SampleDatabase.Configure(options, Provider, Connection);

    public async Task<bool> ExistsAsync()
    {
        await using var db = Context();
        return await db.GetService<IRelationalDatabaseCreator>().ExistsAsync();
    }

    public async Task CreateAsync()
    {
        Assert.False(created, "This fixture database has already been initialized.");
        await using var db = Context();
        var creator = db.GetService<IRelationalDatabaseCreator>();
        Assert.False(await creator.ExistsAsync(), "Refusing to reuse an existing database.");
        await creator.CreateAsync();
        created = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (created)
        {
            await using var db = Context();
            await db.GetService<IRelationalDatabaseCreator>().DeleteAsync();
        }
        SqliteConnection.ClearAllPools();
        directory.Delete(recursive: true);
    }

    private IdentityContext Context()
    {
        var options = new DbContextOptionsBuilder<IdentityContext>();
        Configure(options);
        return new IdentityContext(options.Options);
    }

    private static string RequiredVariable(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the selected disposable provider test run.");

    private static void RequireLoopback(string? host)
    {
        if (host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Identity tests create/drop owned disposable databases and require a loopback server.");
    }
}
