using System.Text.Json;
using Caravel.Queues;
using Caravel.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length != 1 || args[0] is not ("--enqueue-demo" or "--work-once" or "--work" or "--queue-status" or "--report" or "--check-model"))
{
    Console.Error.WriteLine("Use --enqueue-demo, --work-once, --work, --queue-status, --report or --check-model. Set CARAVEL_WORKER_DIRECTORY to an existing absolute scratch directory.");
    return 2;
}

var settings = SampleConfiguration.Load();
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<QueueDbContext>(settings.ConfigureQueue);
builder.Services.AddDbContext<ResultContext>(settings.ConfigureResults);
builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = settings.LeaseDuration);
builder.Services.AddQueueJob<RecordQuantity, RecordQuantityHandler>("synthetic.quantity.v1");
if (args[0] == "--work")
{
    builder.Logging.AddSimpleConsole();
    builder.Services.AddCaravelQueueWorker(SampleConfiguration.QueueName);
}
using var host = builder.Build();
if (args[0] == "--work")
{
    var worker = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
    await host.RunAsync();
    // StopHost returns normally after a background failure; preserve that failure in the sample's exit code.
    return worker.ExecuteTask?.IsFaulted == true ? 1 : 0;
}
if (args[0] == "--queue-status")
{
    // Native console lifetime supplies Ctrl+C cancellation; this command registers no worker.
    await host.StartAsync();
    var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
    try
    {
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync(stopping);
        database.Database.SetConnectionString(new SqliteConnectionStringBuilder(database.Database.GetConnectionString())
        { Mode = SqliteOpenMode.ReadOnly }.ToString());
        var observedAt = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var now = observedAt.ToUnixTimeMilliseconds();
        // One server-side aggregate; payloads, idempotency keys and lease tokens are never materialized.
        var counts = await database.Jobs.AsNoTracking()
            .Where(job => job.Queue == SampleConfiguration.QueueName && job.TenantId == SampleConfiguration.TenantId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                ready = group.LongCount(job => job.State == QueueJobState.Pending && job.AvailableAt <= now),
                delayed = group.LongCount(job => job.State == QueueJobState.Pending && job.AvailableAt > now),
                activeLeases = group.LongCount(job => job.State == QueueJobState.Leased && job.LeaseExpiresAt > now),
                expiredLeases = group.LongCount(job => job.State == QueueJobState.Leased && job.LeaseExpiresAt <= now),
                deadLetter = group.LongCount(job => job.State == QueueJobState.DeadLetter),
                completed = group.LongCount(job => job.State == QueueJobState.Completed)
            }).SingleOrDefaultAsync(stopping);
        stopping.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            observedAt,
            ready = counts?.ready ?? 0, delayed = counts?.delayed ?? 0,
            activeLeases = counts?.activeLeases ?? 0, expiredLeases = counts?.expiredLeases ?? 0,
            deadLetter = counts?.deadLetter ?? 0, completed = counts?.completed ?? 0
        }));
        return 0;
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 130; }
    finally { await host.StopAsync(); }
}
await using var scope = host.Services.CreateAsyncScope();
var queue = scope.ServiceProvider.GetRequiredService<IDatabaseQueue>();
var results = scope.ServiceProvider.GetRequiredService<ResultContext>();

switch (args[0])
{
    case "--enqueue-demo":
        var accepted = new List<EnqueueResult>();
        for (var quantity = 1; quantity <= 3; quantity++)
            accepted.Add(await queue.EnqueueAsync(new RecordQuantity(quantity),
                new(SampleConfiguration.QueueName, SampleConfiguration.TenantId, $"demo-{quantity}")));
        Console.WriteLine(JsonSerializer.Serialize(accepted));
        break;
    case "--work-once":
        var claimed = await host.Services.GetRequiredService<QueueWorker>().RunOnceAsync(SampleConfiguration.QueueName);
        Console.WriteLine(JsonSerializer.Serialize(new { claimed }));
        break;
    case "--report":
        var report = await results.ProcessedQuantities.AsNoTracking().Where(x => x.TenantId == SampleConfiguration.TenantId)
            .GroupBy(x => x.TenantId).Select(group => new { events = group.Count(), total = group.Sum(x => x.Quantity) })
            .SingleOrDefaultAsync();
        Console.WriteLine(JsonSerializer.Serialize(report ?? new { events = 0, total = 0 }));
        break;
    case "--check-model":
        await using (var database = await host.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
        {
            if (database.Database.HasPendingModelChanges() || results.Database.HasPendingModelChanges())
                throw new InvalidOperationException("A sample model has changes without a migration.");
            if ((await database.Database.GetPendingMigrationsAsync()).Any() || (await results.Database.GetPendingMigrationsAsync()).Any())
                throw new InvalidOperationException("Apply both sample contexts' migrations explicitly first.");
        }
        Console.WriteLine("{\"pendingModelChanges\":false,\"pendingMigrations\":false}");
        break;
}
return 0;
