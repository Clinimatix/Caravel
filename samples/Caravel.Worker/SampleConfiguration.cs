using Caravel.Queues;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Worker;

public sealed record SampleConfiguration(string Directory, TimeSpan LeaseDuration)
{
    public const string QueueName = "synthetic-quantities";
    public const string TenantId = "synthetic";

    public static SampleConfiguration Load()
    {
        var directory = Environment.GetEnvironmentVariable("CARAVEL_WORKER_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !System.IO.Directory.Exists(directory))
            throw new InvalidOperationException("Set CARAVEL_WORKER_DIRECTORY to an existing absolute scratch directory for synthetic queue.db and results.db files.");
        var leaseSetting = Environment.GetEnvironmentVariable("CARAVEL_WORKER_LEASE_SECONDS");
        var leaseSeconds = 60;
        if (leaseSetting is not null && (!int.TryParse(leaseSetting, out leaseSeconds) || leaseSeconds is < 1 or > 3600))
            throw new InvalidOperationException("CARAVEL_WORKER_LEASE_SECONDS must be an integer from 1 to 3600.");
        return new(Path.GetFullPath(directory), TimeSpan.FromSeconds(leaseSeconds));
    }

    public void ConfigureQueue(DbContextOptionsBuilder options) => options.UseSqlite(Connection("queue.db"),
        sqlite => sqlite.MigrationsAssembly(typeof(SampleConfiguration).Assembly.FullName!));
    public void ConfigureResults(DbContextOptionsBuilder options) => options.UseSqlite(Connection("results.db"),
        sqlite => sqlite.MigrationsAssembly(typeof(SampleConfiguration).Assembly.FullName!));

    private string Connection(string file) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Directory, file), Pooling = false, DefaultTimeout = 15
    }.ToString();
}
