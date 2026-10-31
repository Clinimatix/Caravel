using System.Text.Json;
using Caravel.Queues;
using Caravel.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length != 1 || args[0] is not ("--init" or "--cleanup" or "--enqueue-demo" or "--work" or "--work-once" or "--report" or "--queue-status"))
    return 2;
var settings = SampleConfiguration.Load();
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddDbContextFactory<QueueDbContext>(settings.ConfigureQueue);
builder.Services.AddDbContext<ResultContext>(settings.ConfigureResults);
builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = settings.LeaseDuration);
builder.Services.AddQueueJob<RecordQuantity, RecordQuantityHandler>("synthetic.quantity.v1");
if (args[0] == "--work") builder.Services.AddCaravelQueueWorker(SampleConfiguration.QueueName, TimeSpan.FromMilliseconds(100));
using var host = builder.Build();
if (args[0] == "--work")
{
    var worker = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
    await host.RunAsync();
    return worker.ExecuteTask?.IsFaulted == true ? 1 : 0;
}
await using var scope = host.Services.CreateAsyncScope();
await using var database = await host.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
var results = scope.ServiceProvider.GetRequiredService<ResultContext>();
var queue = scope.ServiceProvider.GetRequiredService<IDatabaseQueue>();
switch (args[0])
{
    case "--init":
        await Allocate(database, "queue");
        await Allocate(results, "results");
        Console.WriteLine("{\"initialized\":true}");
        break;
    case "--cleanup":
        var errors = new List<Exception>();
        foreach (var (context, name) in new (DbContext, string)[] { (database, "queue"), (results, "results") })
        {
            try
            {
                var marker = Path.Combine(settings.Directory, "allocated-" + name);
                if (File.Exists(marker))
                {
                    await context.Database.EnsureDeletedAsync();
                    File.Delete(marker);
                }
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Disposable database cleanup failed.", errors);
        Console.WriteLine("{\"cleaned\":true}");
        break;
    case "--enqueue-demo":
        var accepted = new List<EnqueueResult>();
        for (var quantity = 1; quantity <= 3; quantity++)
            accepted.Add(await queue.EnqueueAsync(new RecordQuantity(quantity),
                new(SampleConfiguration.QueueName, SampleConfiguration.TenantId, $"demo-{quantity}")));
        Console.WriteLine(JsonSerializer.Serialize(accepted));
        break;
    case "--work-once":
        Console.WriteLine(JsonSerializer.Serialize(new { claimed = await host.Services.GetRequiredService<QueueWorker>().RunOnceAsync(SampleConfiguration.QueueName) }));
        break;
    case "--report":
        var report = await results.ProcessedQuantities.AsNoTracking().GroupBy(x => x.TenantId)
            .Select(group => new { events = group.Count(), total = group.Sum(x => x.Quantity) }).SingleOrDefaultAsync();
        Console.WriteLine(JsonSerializer.Serialize(report ?? new { events = 0, total = 0 }));
        break;
    case "--queue-status":
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var jobs = await database.Jobs.AsNoTracking().ToListAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ready = jobs.LongCount(j => j.State == QueueJobState.Pending && j.AvailableAt <= now),
            delayed = jobs.LongCount(j => j.State == QueueJobState.Pending && j.AvailableAt > now),
            activeLeases = jobs.LongCount(j => j.State == QueueJobState.Leased && j.LeaseExpiresAt > now),
            expiredLeases = jobs.LongCount(j => j.State == QueueJobState.Leased && j.LeaseExpiresAt <= now),
            attempts = jobs.Sum(j => j.Attempts),
            failures = jobs.LongCount(j => j.LastFailure != null),
            completed = jobs.LongCount(j => j.State == QueueJobState.Completed),
            deadLetter = jobs.LongCount(j => j.State == QueueJobState.DeadLetter)
        }));
        break;
}
return 0;

async Task Allocate(DbContext context, string name)
{
    if (await context.GetService<IRelationalDatabaseCreator>().ExistsAsync())
        throw new InvalidOperationException("Refusing to reuse a database.");
    // Marker precedes creation so partial creation still has an explicit cleanup owner.
    await File.WriteAllTextAsync(Path.Combine(settings.Directory, "allocated-" + name), "owned");
    if (!await context.Database.EnsureCreatedAsync()) throw new InvalidOperationException("Database allocation failed.");
}
