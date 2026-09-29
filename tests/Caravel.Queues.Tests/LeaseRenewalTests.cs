using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class LeaseRenewalTests
{
    [Fact]
    public async Task WorkerRenewsBeyondOriginalExpiryAndCompletesWithoutAnotherClaim()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(10));
        var accepted = await fixture.Queue.EnqueueAsync(new WaitingJob(), new("renew", "tenant", "one"));
        var run = fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("renew");
        await fixture.Signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        Assert.False(fixture.Signals.Cancelled.Task.IsCompleted);
        Assert.Null(await fixture.Queue.TryClaimAsync("renew"));
        fixture.Signals.Finish.TrySetResult();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var status = (await fixture.Queue.GetStatusAsync(accepted.JobId, "renew", "tenant"))!;
        Assert.Equal(QueueJobState.Completed, status.State);
        Assert.Equal(1, status.Attempts);
    }

    [Fact]
    public async Task WorkerCancelsAtTotalLifetimeAndLeavesTheLeaseRecoverable()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(3));
        var accepted = await fixture.Queue.EnqueueAsync(new WaitingJob(), new("renew", "tenant", "one"));
        Assert.True(await fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("renew").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(fixture.Signals.Cancelled.Task.IsCompleted);
        var status = (await fixture.Queue.GetStatusAsync(accepted.JobId, "renew", "tenant"))!;
        Assert.Equal(QueueJobState.Leased, status.State);
        Assert.Equal(2, (await fixture.Queue.TryClaimAsync("renew"))!.Context.Attempt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOwnershipOrRenewalOutageCancelsHandlerAndDoesNotAcknowledge(bool outage)
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(10));
        var accepted = await fixture.Queue.EnqueueAsync(new WaitingJob(), new("renew", "tenant", "one"));
        var run = fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("renew");
        await fixture.Signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (outage) fixture.Outage.Fail = true;
        else
        {
            await using var db = await fixture.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
            await db.Jobs.Where(x => x.Id == accepted.JobId).ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LeaseToken, Guid.NewGuid()));
        }
        if (outage) await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        else Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(fixture.Signals.Cancelled.Task.IsCompleted);
        fixture.Outage.Fail = false;
        var status = (await fixture.Queue.GetStatusAsync(accepted.JobId, "renew", "tenant"))!;
        Assert.Equal(QueueJobState.Leased, status.State);
        Assert.Null(status.LastFailure);
    }

    public sealed record WaitingJob;
    public sealed class Signals
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Handler(Signals signals) : IJobHandler<WaitingJob>
    {
        public async Task HandleAsync(WaitingJob job, JobContext context, CancellationToken cancellationToken)
        {
            signals.Started.TrySetResult();
            try { await signals.Finish.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                signals.Cancelled.TrySetResult();
                throw;
            }
        }
    }
    public sealed class ConnectionOutage : DbConnectionInterceptor
    {
        public volatile bool Fail;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic renewal connection outage.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-renewal-");
        public ServiceProvider Services { get; private set; } = null!;
        public Signals Signals { get; } = new();
        public ConnectionOutage Outage { get; } = new();
        public IDatabaseQueue Queue => Services.GetRequiredService<IDatabaseQueue>();
        public static async Task<Fixture> Create(TimeSpan lifetime)
        {
            var fixture = new Fixture();
            var services = new ServiceCollection();
            services.AddSingleton(fixture.Signals);
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(fixture.directory.FullName, "queue.db")};Pooling=False").AddInterceptors(fixture.Outage));
            services.AddCaravelDatabaseQueue(options =>
            {
                options.LeaseDuration = TimeSpan.FromSeconds(2);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(250);
                options.MaxLeaseLifetime = lifetime;
            });
            services.AddQueueJob<WaitingJob, Handler>("wait.v1");
            fixture.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var db = await fixture.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Signals.Finish.TrySetResult();
            await Services.DisposeAsync();
            directory.Delete(true);
        }
    }
}
