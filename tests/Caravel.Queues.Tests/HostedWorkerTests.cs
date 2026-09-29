using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class HostedWorkerTests
{
    [Fact]
    public async Task EmptyQueueStartsAndStopsThroughTheNativeHost()
    {
        await using var fixture = await HostedFixture.Create();
        await fixture.Host.StartAsync();
        Assert.True(fixture.Lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.False(fixture.Worker.ExecuteTask!.IsCompleted);
        await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Lifetime.ApplicationStopped.IsCancellationRequested);
        Assert.True(fixture.Worker.ExecuteTask.IsCompleted);
        Assert.False(fixture.Worker.ExecuteTask.IsFaulted);
        Assert.False(fixture.Signals.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task HostShutdownCancelsTheHandlerWithoutAcknowledgingItsRecoverableLease()
    {
        await using var fixture = await HostedFixture.Create();
        var accepted = await fixture.Queue.EnqueueAsync(new WaitingJob(), new("hosted", "tenant-a", "cancel"));
        await fixture.Host.StartAsync();
        await fixture.Signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Signals.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var interrupted = (await fixture.Queue.GetStatusAsync(accepted.JobId, "hosted", "tenant-a"))!;
        Assert.Equal(QueueJobState.Leased, interrupted.State);
        Assert.Equal(1, interrupted.Attempts);
        Assert.Null(interrupted.LastFailure);
        Assert.Null(await fixture.Queue.TryClaimAsync("hosted"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = Assert.IsType<QueueLease>(await fixture.Queue.TryClaimAsync("hosted"));
        Assert.Equal(accepted.JobId, recovered.Context.JobId);
        Assert.Equal(2, recovered.Context.Attempt);
        Assert.True(await fixture.Queue.CompleteAsync(recovered));
        Assert.Equal(QueueJobState.Completed,
            (await fixture.Queue.GetStatusAsync(accepted.JobId, "hosted", "tenant-a"))!.State);
    }

    [Fact]
    public async Task ClaimInfrastructureFailureFaultsTheWorkerAndStopsTheHost()
    {
        await using var fixture = await HostedFixture.Create(createSchema: false);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        await fixture.Host.StartAsync();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Worker.ExecuteTask!.IsFaulted);
        Assert.IsType<SqliteException>(fixture.Worker.ExecuteTask.Exception!.GetBaseException());
        Assert.True(fixture.Lifetime.ApplicationStopped.IsCancellationRequested);
        Assert.False(fixture.Signals.Started.Task.IsCompleted);
    }

    public sealed record WaitingJob;
    public sealed class HandlerSignals
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WaitingHandler(HandlerSignals signals) : IJobHandler<WaitingJob>
    {
        public async Task HandleAsync(WaitingJob job, JobContext context, CancellationToken cancellationToken)
        {
            signals.Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                signals.Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class HostedFixture(IHost host, string directory) : IAsyncDisposable
    {
        public IHost Host => host;
        public IDatabaseQueue Queue => host.Services.GetRequiredService<IDatabaseQueue>();
        public IHostApplicationLifetime Lifetime => host.Services.GetRequiredService<IHostApplicationLifetime>();
        public BackgroundService Worker => Assert.Single(host.Services.GetServices<IHostedService>().OfType<BackgroundService>());
        public HandlerSignals Signals => host.Services.GetRequiredService<HandlerSignals>();
        public DatabaseQueueTests.ManualClock Clock => (DatabaseQueueTests.ManualClock)host.Services.GetRequiredService<TimeProvider>();

        public static async Task<HostedFixture> Create(bool createSchema = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "caravel-hosted-queue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            builder.Services.AddSingleton<TimeProvider>(new DatabaseQueueTests.ManualClock());
            builder.Services.AddSingleton<HandlerSignals>();
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "queue.db"), Pooling = false
            }.ToString();
            builder.Services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddCaravelDatabaseQueue(options => options.LeaseDuration = TimeSpan.FromMinutes(1));
            builder.Services.AddQueueJob<WaitingJob, WaitingHandler>("waiting.v1");
            builder.Services.AddCaravelQueueWorker("hosted", TimeSpan.FromMilliseconds(100));
            var fixture = new HostedFixture(builder.Build(), directory);
            if (createSchema)
            {
                await using var database = await fixture.Host.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
                await database.Database.EnsureCreatedAsync(); // A new synthetic database, never sample/application startup.
            }
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
            else host.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
