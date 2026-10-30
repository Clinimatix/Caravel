using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class HostedWorkerProviderContracts
{
    [Fact]
    public Task Sqlite_hosted_shutdown_contract() => Exercise("sqlite");

    [ProviderFact("CARAVEL_TEST_SQLSERVER")]
    public Task SqlServer_hosted_shutdown_contract() => Exercise("sqlserver");

    [ProviderFact("CARAVEL_TEST_POSTGRES")]
    public Task Postgres_hosted_shutdown_contract() => Exercise("postgres");

    private static async Task Exercise(string provider)
    {
        // Both a throwing and a normally returning cancelled handler must leave its lease intact.
        foreach (var returnAfterCancellation in new[] { false, true })
        {
            var name = "caravel_shutdown_" + Guid.NewGuid().ToString("N");
            var clock = new QueueClock();
            var signals = new HandlerSignals(returnAfterCancellation);
            using var first = CreateHost(provider, name, clock, signals);
            var factory = first.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>();
            await using var database = await factory.CreateDbContextAsync();
            await using var results = await first.Services.GetRequiredService<IDbContextFactory<ResultDatabase>>().CreateDbContextAsync();
            var allocated = false;
            var resultsAllocated = false;
            Exception? failure = null;
            try
            {
                Assert.False(await database.GetService<IRelationalDatabaseCreator>().ExistsAsync(),
                    "Refusing to reuse an existing queue database.");
                allocated = true;
                Assert.True(await database.Database.EnsureCreatedAsync());
                Assert.False(await results.GetService<IRelationalDatabaseCreator>().ExistsAsync());
                resultsAllocated = true;
                Assert.True(await results.Database.EnsureCreatedAsync());
                var queue = first.Services.GetRequiredService<IDatabaseQueue>();
                var accepted = await queue.EnqueueAsync(new WaitingJob(), new("shutdown", "tenant-a", "one"));
                var availableAt = (await queue.GetStatusAsync(accepted.JobId, "shutdown", "tenant-a"))!.AvailableAt;
                await first.StartAsync();
                await signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await signals.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var worker = Assert.Single(first.Services.GetServices<IHostedService>().OfType<BackgroundService>());
                Assert.True(worker.ExecuteTask!.IsCompleted);
                Assert.False(worker.ExecuteTask.IsFaulted);
                var interrupted = (await queue.GetStatusAsync(accepted.JobId, "shutdown", "tenant-a"))!;
                Assert.Equal(QueueJobState.Leased, interrupted.State);
                Assert.Equal(1, interrupted.Attempts);
                Assert.Null(interrupted.LastFailure);
                Assert.Equal(availableAt, interrupted.AvailableAt);
                Assert.Null(await queue.TryClaimAsync("shutdown"));
                Assert.Equal(accepted.JobId, Assert.Single(await results.Results.AsNoTracking().ToListAsync()).Id);

                // Recover through a fresh host and DI graph, never by acknowledging the old lease.
                clock.Advance(TimeSpan.FromMinutes(2));
                var recovery = new HandlerSignals(returnAfterCancellation);
                using var second = CreateHost(provider, name, clock, recovery);
                var recoveredQueue = second.Services.GetRequiredService<IDatabaseQueue>();
                Exception? recoveryFailure = null;
                try
                {
                    await second.StartAsync();
                    await recovery.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while ((await recoveredQueue.GetStatusAsync(accepted.JobId, "shutdown", "tenant-a", timeout.Token))!.State
                        != QueueJobState.Completed)
                        await Task.Delay(25, timeout.Token);
                }
                catch (Exception error) { recoveryFailure = error; throw; }
                finally { await Cleanup(recoveryFailure, () => second.StopAsync().WaitAsync(TimeSpan.FromSeconds(15))); }
                var completed = (await recoveredQueue.GetStatusAsync(accepted.JobId, "shutdown", "tenant-a"))!;
                Assert.Equal(QueueJobState.Completed, completed.State);
                Assert.Equal(2, completed.Attempts);
                Assert.Null(completed.LastFailure);
                Assert.Equal(1, recovery.Executions);
                Assert.Equal(accepted.JobId, Assert.Single(await results.Results.AsNoTracking().ToListAsync()).Id);
                Assert.Null(await recoveredQueue.TryClaimAsync("shutdown"));
                Assert.True((await recoveredQueue.EnqueueAsync(new WaitingJob(), new("shutdown", "tenant-a", "one"))).AlreadyEnqueued);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                await Cleanup(failure,
                    () => first.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)),
                    async () => { if (allocated) await database.Database.EnsureDeletedAsync(); },
                    async () => { if (resultsAllocated) await results.Database.EnsureDeletedAsync(); });
            }
        }
    }

    private static async Task Cleanup(Exception? originalFailure, params Func<Task>[] actions)
    {
        var failures = new List<Exception>();
        foreach (var action in actions)
        {
            try { await action(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 0) return;
        var cleanupFailure = new AggregateException("Synthetic worker cleanup failed.", failures);
        if (originalFailure is null) throw cleanupFailure;
        originalFailure.Data["WorkerCleanupFailure"] = cleanupFailure;
    }

    private static IHost CreateHost(string provider, string database, QueueClock clock, HandlerSignals signals)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(signals);
        builder.Services.AddDbContextFactory<QueueDbContext>(options => ProviderTestDatabase.Configure(options, provider, database));
        builder.Services.AddDbContextFactory<ResultDatabase>(options => ProviderTestDatabase.Configure(options, provider, database + "_results"));
        builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = TimeSpan.FromMinutes(1));
        builder.Services.AddQueueJob<WaitingJob, WaitingHandler>("shutdown.wait.v1");
        builder.Services.AddCaravelQueueWorker("shutdown", TimeSpan.FromMilliseconds(50));
        return builder.Build();
    }

    private sealed class QueueClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan interval) => Interlocked.Add(ref ticks, interval.Ticks);
    }

    public sealed record WaitingJob;
    public sealed class HandlerSignals(bool returnAfterCancellation)
    {
        public bool ReturnAfterCancellation => returnAfterCancellation;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions;
    }

    public sealed class ResultDatabase(DbContextOptions<ResultDatabase> options) : DbContext(options)
    {
        public DbSet<Result> Results => Set<Result>();
    }
    public sealed class Result { public Guid Id { get; set; } }

    public sealed class WaitingHandler(HandlerSignals signals, IDbContextFactory<ResultDatabase> factory) : IJobHandler<WaitingJob>
    {
        public async Task HandleAsync(WaitingJob job, JobContext context, CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", context.TenantId);
            await using var database = await factory.CreateDbContextAsync(cancellationToken);
            // The result's primary key is the application's durable idempotency boundary.
            if (!await database.Results.AnyAsync(x => x.Id == context.JobId, cancellationToken))
            {
                database.Results.Add(new Result { Id = context.JobId });
                await database.SaveChangesAsync(cancellationToken);
            }
            if (context.Attempt == 1)
            {
                signals.Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    signals.Cancelled.TrySetResult();
                    if (!signals.ReturnAfterCancellation) throw;
                }
            }
            else
            {
                Assert.Equal(2, context.Attempt);
                Interlocked.Increment(ref signals.Executions);
                signals.Recovered.TrySetResult();
            }
        }
    }
}
