using System.Text.RegularExpressions;
using Caravel.Provider.Tests;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Worker;

public sealed record SampleConfiguration(string Directory, string Provider, string Database)
{
    public const string QueueName = "synthetic-quantities";
    public const string TenantId = "synthetic";
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(30);

    public static SampleConfiguration Load()
    {
        var provider = Environment.GetEnvironmentVariable("CARAVEL_CONTAINER_PROVIDER");
        var database = Environment.GetEnvironmentVariable("CARAVEL_CONTAINER_DATABASE") ?? "";
        if (provider is not ("sqlserver" or "postgres") ||
            !Regex.IsMatch(database, "^caravel_container_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Expected a disposable container provider and unique database name.");
        if (!System.IO.Directory.Exists("/data")) throw new InvalidOperationException("Missing scratch volume.");
        return new("/data", provider, database);
    }

    public void ConfigureQueue(DbContextOptionsBuilder options) => ProviderTestDatabase.Configure(options, Provider, Database);
    public void ConfigureResults(DbContextOptionsBuilder options) => ProviderTestDatabase.Configure(options, Provider, Database + "_results");
}
