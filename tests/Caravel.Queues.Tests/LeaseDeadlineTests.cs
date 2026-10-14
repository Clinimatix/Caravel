using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class LeaseDeadlineTests
{
    [Theory]
    [InlineData("cancel", 1)]
    [InlineData("cancel", 2)]
    [InlineData("return", 1)]
    [InlineData("return", 2)]
    [InlineData("throw", 1)]
    [InlineData("throw", 2)]
    public async Task EarlyDeadlineCancellationLeavesLeaseForExpiryWithoutRetryBackoff(
        string cancellationBehavior, int maxAttempts)
    {
        await using var fixture = await Fixture.Create(maxAttempts);
        var accepted = await fixture.Queue.EnqueueAsync(new WaitingJob(cancellationBehavior),
            new("deadline", "tenant", "one"));
        using var stop = new CancellationTokenSource();
        var run = fixture.Services.GetRequiredService<QueueWorker>().RunOnceAsync("deadline", stop.Token);
        try
        {
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await fixture.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>()
                .CreateDbContextAsync();
            var original = await db.Jobs.AsNoTracking().SingleAsync();
            var expiry = DateTimeOffset.FromUnixTimeMilliseconds(original.LeaseExpiresAt!.Value);

            // Timer delivery and UTC have different precision. Reproduce cancellation while the
            // stored lease is still owned, without depending on real scheduling or sleeping.
            fixture.Clock.SetUtcNow(expiry.AddTicks(-5_000));
            fixture.Clock.FireDeadline();
            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));

            var interrupted = await db.Jobs.AsNoTracking().SingleAsync();
            Assert.Equal(QueueJobState.Leased, interrupted.State);
            Assert.Null(interrupted.LastFailure);
            Assert.Equal(original.LeaseToken, interrupted.LeaseToken);
            Assert.Equal(original.LeaseExpiresAt, interrupted.LeaseExpiresAt);
            Assert.Equal(original.AvailableAt, interrupted.AvailableAt);
            Assert.Equal(1, interrupted.Attempts);
            Assert.Null(await fixture.Queue.TryClaimAsync("deadline"));

            fixture.Clock.SetUtcNow(expiry);
            var recovered = await fixture.Queue.TryClaimAsync("deadline");
            if (maxAttempts > 1)
            {
                Assert.NotNull(recovered);
                Assert.Equal(accepted.JobId, recovered.Context.JobId);
                Assert.Equal(2, recovered.Context.Attempt);
                Assert.True(await fixture.Queue.CompleteAsync(recovered));
            }
            else
            {
                Assert.Null(recovered);
                var exhausted = (await fixture.Queue.GetStatusAsync(accepted.JobId, "deadline", "tenant"))!;
                Assert.Equal(QueueJobState.DeadLetter, exhausted.State);
                Assert.Equal(QueueFailure.AttemptsExhausted, exhausted.LastFailure);
            }
        }
        finally
        {
            await stop.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    public sealed record WaitingJob(string CancellationBehavior);

    public sealed class Handler(TaskCompletionSource started) : IJobHandler<WaitingJob>
    {
        public async Task HandleAsync(WaitingJob job, JobContext context, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (job.CancellationBehavior == "return") return;
                if (job.CancellationBehavior == "throw") throw new InvalidOperationException("Synthetic cancellation failure.");
                throw;
            }
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        private ManualTimer? deadline;
        public override DateTimeOffset GetUtcNow() => now;
        public void SetUtcNow(DateTimeOffset value) => now = value;
        public void FireDeadline() => (deadline ?? throw new InvalidOperationException("No deadline timer.")).Fire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(deadline);
            Assert.True(dueTime > TimeSpan.Zero);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            return deadline = new ManualTimer(callback, state);
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-deadline-");
        public DeadlineClock Clock { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ServiceProvider Services { get; private set; } = null!;
        public IDatabaseQueue Queue => Services.GetRequiredService<IDatabaseQueue>();

        public static async Task<Fixture> Create(int maxAttempts)
        {
            var fixture = new Fixture();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(fixture.Clock);
            services.AddSingleton(fixture.Started);
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(fixture.directory.FullName, "queue.db")};Pooling=False"));
            services.AddCaravelDatabaseQueue(options =>
            {
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.MaxAttempts = maxAttempts;
            }).AddQueueJob<WaitingJob, Handler>("deadline.wait.v1");
            fixture.Services = services.BuildServiceProvider(new ServiceProviderOptions
                { ValidateScopes = true, ValidateOnBuild = true });
            await using var db = await fixture.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>()
                .CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync(); // Disposable synthetic state only.
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            directory.Delete(true);
        }
    }
}
