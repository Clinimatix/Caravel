using System.Data.Common;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class LeaseRenewalTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExpiredClaimBeforeStartupDoesNotInvokeHandlerAndRemainsRecoverable()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(3));
        fixture.Observer.ExpireClaim = true;
        var accepted = await fixture.Enqueue();
        Assert.True(await fixture.Start().WaitAsync(Bound));
        Assert.False(fixture.Signals.Started.Task.IsCompleted);
        Assert.False(fixture.Signals.Cancelled.Task.IsCompleted);
        Assert.Equal(0, fixture.Clock.TimerCount);
        var row = await fixture.Row();
        await fixture.AssertRecoverable(accepted.JobId, row);
    }

    [Fact]
    public async Task WorkerRenewsBeyondOriginalExpiryAndCompletesWithoutAnotherClaim()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(10));
        var accepted = await fixture.Enqueue();
        var run = fixture.Start();
        await fixture.Ready();
        var original = await fixture.Row();
        await fixture.RenewAt(1);
        await fixture.RenewAt(2);
        await fixture.RenewAt(2.6);
        var renewed = await fixture.Row();
        Assert.True(fixture.Clock.GetUtcNow().ToUnixTimeMilliseconds() > original.LeaseExpiresAt);
        Assert.True(renewed.LeaseExpiresAt > original.LeaseExpiresAt);
        Assert.Equal(original.LeaseToken, renewed.LeaseToken);
        Assert.Equal(original.AvailableAt, renewed.AvailableAt);
        Assert.Equal(QueueJobState.Leased, renewed.State);
        Assert.Null(renewed.LastFailure);
        Assert.False(run.IsCompleted);
        Assert.False(fixture.Signals.Cancelled.Task.IsCompleted);
        Assert.Null(await fixture.Queue.TryClaimAsync("renew"));
        fixture.Signals.Finish.TrySetResult();
        Assert.True(await run.WaitAsync(Bound));
        var status = (await fixture.Queue.GetStatusAsync(accepted.JobId, "renew", "tenant"))!;
        Assert.Equal(QueueJobState.Completed, status.State);
        Assert.Equal(1, status.Attempts);
        Assert.Equal(1, fixture.Observer.Completions);
        Assert.Equal(0, fixture.Observer.Failures);
    }

    [Fact]
    public async Task WorkerCancelsAtTotalLifetimeAndLeavesTheLeaseRecoverable()
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(3));
        var accepted = await fixture.Enqueue();
        var run = fixture.Start();
        await fixture.Ready();
        await fixture.RenewAt(1);
        await fixture.RenewAt(2);
        var capped = await fixture.Row();
        Assert.Equal(fixture.Clock.Epoch.AddSeconds(3).ToUnixTimeMilliseconds(), capped.LeaseExpiresAt);
        Assert.False(run.IsCompleted);
        Assert.False(fixture.Signals.Cancelled.Task.IsCompleted);
        Assert.Null(await fixture.Queue.TryClaimAsync("renew"));
        fixture.Clock.AdvanceTo(fixture.Clock.Epoch.AddSeconds(3));
        Assert.True(await run.WaitAsync(Bound));
        Assert.True(fixture.Signals.Cancelled.Task.IsCompleted);
        await fixture.AssertRecoverable(accepted.JobId, capped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOwnershipOrRenewalOutageCancelsHandlerAndDoesNotAcknowledge(bool outage)
    {
        await using var fixture = await Fixture.Create(TimeSpan.FromSeconds(10));
        var accepted = await fixture.Enqueue();
        var run = fixture.Start();
        await fixture.Ready();
        var retained = await fixture.Row();
        if (outage) fixture.Outage.Fail = true;
        else
        {
            await using var db = await fixture.Database();
            retained.LeaseToken = Guid.NewGuid();
            await db.Jobs.Where(x => x.Id == accepted.JobId).ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LeaseToken, retained.LeaseToken));
        }
        // Only the renewal timer is due: cancellation cannot be attributed to natural expiry.
        fixture.Clock.AdvanceTo(fixture.Clock.Epoch.AddSeconds(1));
        if (outage) await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Bound));
        else Assert.True(await run.WaitAsync(Bound));
        Assert.True(fixture.Signals.Cancelled.Task.IsCompleted);
        fixture.Outage.Fail = false;
        Assert.Null(await fixture.Queue.TryClaimAsync("renew"));
        await fixture.AssertRecoverable(accepted.JobId, retained);
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

    // Observe worker transitions without replacing the relational queue's lease semantics.
    private sealed class ObservedQueue(IDatabaseQueue inner, ControlledClock clock) : IDatabaseQueue
    {
        public bool ExpireClaim { get; set; }
        public int Completions { get; private set; }
        public int Failures { get; private set; }
        public async Task<QueueLease?> TryClaimAsync(string queue, CancellationToken cancellationToken = default)
        {
            var lease = await inner.TryClaimAsync(queue, cancellationToken);
            if (ExpireClaim && lease is not null) clock.AdvanceTo(lease.ExpiresAt);
            return lease;
        }
        public Task<EnqueueResult> EnqueueAsync<T>(T job, QueueDispatchOptions options, CancellationToken cancellationToken = default) where T : notnull
            => inner.EnqueueAsync(job, options, cancellationToken);
        public Task<QueueJobStatus?> GetStatusAsync(Guid id, string queue, string tenant, CancellationToken cancellationToken = default)
            => inner.GetStatusAsync(id, queue, tenant, cancellationToken);
        public Task<QueueLease?> RenewAsync(QueueLease lease, CancellationToken cancellationToken = default)
            => inner.RenewAsync(lease, cancellationToken);
        public Task<bool> CompleteAsync(QueueLease lease, CancellationToken cancellationToken = default)
        { Completions++; return inner.CompleteAsync(lease, cancellationToken); }
        public Task<bool> FailAsync(QueueLease lease, QueueFailure failure, CancellationToken cancellationToken = default)
        { Failures++; return inner.FailAsync(lease, failure, cancellationToken); }
        public Task<bool> ReplayAsync(Guid id, string queue, string tenant, CancellationToken cancellationToken = default)
            => inner.ReplayAsync(id, queue, tenant, cancellationToken);
    }

    // One-shot timers model both the deadline and renewal delays. Rearming a renewal delay
    // proves that the previous database renewal and deadline update have both completed.
    private sealed class ControlledClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private readonly Channel<bool> renewals = Channel.CreateUnbounded<bool>();
        public DateTimeOffset Epoch { get; } = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset? now;
        public int TimerCount { get { lock (gate) return timers.Count; } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now ?? Epoch; }
        public Task NextRenewal() => renewals.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (gate)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                timers.Add(timer);
                if (dueTime == TimeSpan.FromMilliseconds(250)) renewals.Writer.TryWrite(true);
                return timer;
            }
        }
        public void AdvanceTo(DateTimeOffset value)
        {
            List<ManualTimer> due;
            lock (gate)
            {
                Assert.True(value >= (now ?? Epoch));
                now = value;
                due = timers.Where(x => !x.Disposed && x.Due <= value).ToList();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ControlledClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due = DateTimeOffset.MaxValue;
            public bool Disposed;
            public void Fire()
            {
                lock (owner.gate) { if (Disposed) return; }
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-renewal-");
        private readonly CancellationTokenSource stop = new();
        private Task<bool>? run;
        public ControlledClock Clock { get; } = new();
        public ServiceProvider Services { get; private set; } = null!;
        public Signals Signals { get; } = new();
        public ConnectionOutage Outage { get; } = new();
        public ObservedQueue Observer { get; private set; } = null!;
        public IDatabaseQueue Queue { get; private set; } = null!;
        public Task<EnqueueResult> Enqueue() => Queue.EnqueueAsync(new WaitingJob(), new("renew", "tenant", "one"));
        public Task<bool> Start() => run = Services.GetRequiredService<QueueWorker>().RunOnceAsync("renew", stop.Token);
        public async Task Ready()
        {
            await Signals.Started.Task.WaitAsync(Bound);
            await Clock.NextRenewal();
        }
        public async Task RenewAt(double seconds)
        {
            Clock.AdvanceTo(Clock.Epoch.AddSeconds(seconds));
            await Clock.NextRenewal();
        }
        public Task<QueueDbContext> Database() => Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        public async Task<QueueJob> Row()
        {
            await using var db = await Database();
            return await db.Jobs.AsNoTracking().SingleAsync();
        }
        public async Task AssertRecoverable(Guid id, QueueJob before)
        {
            var row = await Row();
            Assert.Equal(QueueJobState.Leased, row.State);
            Assert.Null(row.LastFailure);
            Assert.Equal(before.LeaseToken, row.LeaseToken);
            Assert.Equal(before.LeaseExpiresAt, row.LeaseExpiresAt);
            Assert.Equal(before.AvailableAt, row.AvailableAt);
            Assert.Equal(1, row.Attempts);
            Assert.Equal(0, Observer.Completions);
            Assert.Equal(0, Observer.Failures);
            Clock.AdvanceTo(DateTimeOffset.FromUnixTimeMilliseconds(row.LeaseExpiresAt!.Value));
            var recovered = await Queue.TryClaimAsync("renew");
            Assert.NotNull(recovered);
            Assert.Equal(id, recovered.Context.JobId);
            Assert.Equal(2, recovered.Context.Attempt);
            Assert.True(await Queue.CompleteAsync(recovered));
        }
        public static async Task<Fixture> Create(TimeSpan lifetime)
        {
            var fixture = new Fixture();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(fixture.Clock);
            services.AddSingleton(fixture.Signals);
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(fixture.directory.FullName, "queue.db")};Pooling=False").AddInterceptors(fixture.Outage));
            services.AddCaravelDatabaseQueue(options =>
            {
                options.LeaseDuration = TimeSpan.FromSeconds(2);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(250);
                options.MaxLeaseLifetime = lifetime;
            });
            var queueFactory = services.Single(x => x.ServiceType == typeof(IDatabaseQueue)).ImplementationFactory!;
            services.AddSingleton<IDatabaseQueue>(provider =>
            {
                fixture.Queue = (IDatabaseQueue)queueFactory(provider);
                return fixture.Observer = new ObservedQueue(fixture.Queue, fixture.Clock);
            });
            services.AddQueueJob<WaitingJob, Handler>("wait.v1");
            fixture.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            _ = fixture.Services.GetRequiredService<IDatabaseQueue>();
            await using var db = await fixture.Database();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Outage.Fail = false;
            await stop.CancelAsync();
            Signals.Finish.TrySetResult();
            if (run is not null)
            {
                try { await run.WaitAsync(Bound); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) { } // The outage test asserts this result before cleanup.
            }
            stop.Dispose();
            await Services.DisposeAsync();
            directory.Delete(true);
        }
    }
}
