using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class AcknowledgementRecoveryTests
{
    [Fact]
    public async Task AcknowledgementOutageStopsHostAndRestartRecoversOneCommittedEffect()
    {
        await using var fixture = new RecoveryFixture();
        var activities = new ConcurrentQueue<Activity>();
        using var recoveredActivity = new SemaphoreSlim(0);
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Caravel.Queues",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (!Equals(activity.GetTagItem("messaging.destination.name"), fixture.QueueName)) return;
                activities.Enqueue(activity);
                if (Equals(activity.GetTagItem("caravel.queue.outcome"), "completed")) recoveredActivity.Release();
            }
        };
        ActivitySource.AddActivityListener(tracing);
        long completions = 0;
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Caravel.Queues" && instrument.Name == "caravel.queue.jobs.completed")
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "queue" && Equals(tag.Value, fixture.QueueName)) Interlocked.Add(ref completions, value);
        });
        metrics.Start();

        var first = fixture.CreateHost();
        await using (var db = await first.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE SyntheticReceipts (JobId TEXT PRIMARY KEY, Quantity INTEGER NOT NULL)");
        }
        var queue = first.Services.GetRequiredService<IDatabaseQueue>();
        var accepted = await queue.EnqueueAsync(new ReceiptJob(7), new(fixture.QueueName, "synthetic-tenant", "synthetic-key"));
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
            .Register(() => stopping.TrySetResult());
        await first.StartAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var worker = Assert.Single(first.Services.GetServices<IHostedService>().OfType<BackgroundService>());
        Assert.True(worker.ExecuteTask!.IsFaulted);
        Assert.IsType<IOException>(worker.ExecuteTask.Exception!.GetBaseException());
        Assert.True(fixture.Outage.BlockConnections);
        Assert.Equal(1, fixture.HandlerCalls);
        Assert.Equal((1L, 7L), await fixture.ReadReceipts());
        Assert.Equal(0, Interlocked.Read(ref completions));
        var failed = Assert.Single(activities);
        Assert.Equal("infrastructure_error", failed.GetTagItem("caravel.queue.outcome"));
        Assert.Equal("InfrastructureError", failed.GetTagItem("caravel.queue.failure"));
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Empty(failed.Events);
        Assert.DoesNotContain("synthetic-outage", string.Join(' ', failed.TagObjects.Select(tag => tag.Value)));

        // Restore queue access and create a new host/service graph against the same persisted database.
        fixture.Outage.BlockConnections = false;
        fixture.InterruptAfterCommit = false;
        var second = fixture.CreateHost();
        var recoveredQueue = second.Services.GetRequiredService<IDatabaseQueue>();
        var interrupted = (await recoveredQueue.GetStatusAsync(accepted.JobId, fixture.QueueName, "synthetic-tenant"))!;
        Assert.Equal(QueueJobState.Leased, interrupted.State);
        Assert.Equal(1, interrupted.Attempts);
        Assert.Null(interrupted.LastFailure);
        Assert.Null(await recoveredQueue.TryClaimAsync(fixture.QueueName));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await second.StartAsync();
        Assert.True(await recoveredActivity.WaitAsync(TimeSpan.FromSeconds(10)));
        await second.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var completed = (await recoveredQueue.GetStatusAsync(accepted.JobId, fixture.QueueName, "synthetic-tenant"))!;
        Assert.Equal(QueueJobState.Completed, completed.State);
        Assert.Equal(2, completed.Attempts);
        Assert.Equal(2, fixture.HandlerCalls);
        Assert.Equal((1L, 7L), await fixture.ReadReceipts());
        Assert.Equal(1, Interlocked.Read(ref completions));
        Assert.Equal(2, activities.Count);
        Assert.Equal("completed", activities.Last().GetTagItem("caravel.queue.outcome"));
        Assert.All(activities, activity => Assert.Equal(accepted.JobId.ToString(), activity.GetTagItem("messaging.message.id")));
    }

    public sealed record ReceiptJob(int Quantity);

    private sealed class ReceiptHandler(RecoveryFixture fixture) : IJobHandler<ReceiptJob>
    {
        public async Task HandleAsync(ReceiptJob job, JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref fixture.HandlerCalls);
            await using var connection = new SqliteConnection(fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO SyntheticReceipts (JobId, Quantity) VALUES ($id, $quantity) ON CONFLICT(JobId) DO NOTHING";
            command.Parameters.AddWithValue("$id", context.JobId.ToString());
            command.Parameters.AddWithValue("$quantity", job.Quantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
            // The durable effect is committed before the next queue connection (acknowledgement) fails.
            if (fixture.InterruptAfterCommit) fixture.Outage.BlockConnections = true;
        }
    }

    private sealed class QueueOutage : DbConnectionInterceptor
    {
        public volatile bool BlockConnections;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockConnections) throw new IOException("synthetic-outage-private-diagnostic");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-ack-recovery-");
        private readonly List<IHost> hosts = [];
        public string QueueName { get; } = "ack-recovery-" + Guid.NewGuid().ToString("N");
        public string ConnectionString => new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory.FullName, "queue.db"), Pooling = false }.ToString();
        public QueueOutage Outage { get; } = new();
        public DatabaseQueueTests.ManualClock Clock { get; } = new();
        public bool InterruptAfterCommit { get; set; } = true;
        public int HandlerCalls;

        public IHost CreateHost()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            builder.Services.AddSingleton(this);
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(ConnectionString).AddInterceptors(Outage));
            builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = TimeSpan.FromMinutes(1));
            builder.Services.AddQueueJob<ReceiptJob, ReceiptHandler>("receipt.v1");
            builder.Services.AddCaravelQueueWorker(QueueName, TimeSpan.FromMilliseconds(100));
            var host = builder.Build();
            hosts.Add(host);
            return host;
        }

        public async Task<(long Count, long Total)> ReadReceipts()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), SUM(Quantity) FROM SyntheticReceipts";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            foreach (var host in hosts)
            {
                try { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { failures.Add(error); }
                try
                {
                    if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
                    else host.Dispose();
                }
                catch (Exception error) { failures.Add(error); }
            }
            try { directory.Delete(recursive: true); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Recovery fixture cleanup failed.", failures);
        }
    }
}
