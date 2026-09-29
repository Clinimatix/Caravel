using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed partial class DatabaseQueueTests
{
    [Theory]
    [InlineData("2026-09-28T00:00:00.0000001Z", "2026-09-28T00:00:00.001Z")]
    [InlineData("2026-09-28T00:00:00.001Z", "2026-09-28T00:00:00.001Z")]
    [InlineData("1969-12-31T23:59:59.9990001Z", "1970-01-01T00:00:00Z")]
    [InlineData("1969-12-31T23:59:59.999Z", "1969-12-31T23:59:59.999Z")]
    public async Task ExplicitDueTimesRoundUpToAMillisecondAndNeverBecomeClaimableEarly(string requested, string expected)
    {
        await using var store = await TestStore.Create();
        var due = DateTimeOffset.Parse(requested);
        var available = DateTimeOffset.Parse(expected);
        store.Clock.Advance(available.AddMilliseconds(-1) - store.Clock.GetUtcNow());
        var accepted = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one", due));
        Assert.Equal(available, (await store.Queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!.AvailableAt);
        store.Clock.Advance(available.AddTicks(-1) - store.Clock.GetUtcNow());
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(accepted.JobId, (await store.Queue.TryClaimAsync("reports"))!.Context.JobId);
    }

    [Fact]
    public async Task UnrepresentableCeilingIsRejectedBeforePersistenceButLastExactMillisecondIsAccepted()
    {
        await using var store = await TestStore.Create();
        var lastMillisecond = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        foreach (var due in new[] { lastMillisecond.AddTicks(1), DateTimeOffset.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.Queue.EnqueueAsync(
                new Message("one"), new("reports", "tenant-a", "outside", due)));
        await using (var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
            Assert.Empty(await db.Jobs.ToListAsync());
        var accepted = await store.Queue.EnqueueAsync(new Message("one"),
            new("reports", "tenant-a", "last", lastMillisecond));
        Assert.Equal(lastMillisecond, (await store.Queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!.AvailableAt);
    }

    [Fact]
    public async Task ImmediateEnqueueKeepsTheCurrentMillisecondWithoutRoundingIntoTheFuture()
    {
        await using var store = await TestStore.Create();
        store.Clock.Advance(TimeSpan.FromTicks(1));
        var accepted = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        var currentMillisecond = DateTimeOffset.FromUnixTimeMilliseconds(store.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal(currentMillisecond, (await store.Queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!.AvailableAt);
        Assert.Equal(accepted.JobId, (await store.Queue.TryClaimAsync("reports"))!.Context.JobId);
    }

    [Fact]
    public async Task ConcurrentEnqueuesDeduplicateDurablyWithinQueueAndTenant()
    {
        await using var store = await TestStore.Create();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "same-key")))));
        Assert.Single(submissions.Select(x => x.JobId).Distinct());
        Assert.Single(submissions, x => !x.AlreadyEnqueued);
        using var secondProcess = store.CreateProvider();
        var recovered = await secondProcess.GetRequiredService<IDatabaseQueue>()
            .EnqueueAsync(new Message("one"), new("reports", "tenant-a", "same-key"));
        Assert.Equal(submissions[0].JobId, recovered.JobId);
        Assert.True(recovered.AlreadyEnqueued);
        await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() =>
            store.Queue.EnqueueAsync(new Message("changed"), new("reports", "tenant-a", "same-key")));
        var differentTenant = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-b", "same-key"));
        var differentQueue = await store.Queue.EnqueueAsync(new Message("one"), new("other", "tenant-a", "same-key"));
        Assert.NotEqual(recovered.JobId, differentTenant.JobId);
        Assert.NotEqual(recovered.JobId, differentQueue.JobId);
        Assert.Null(await store.Queue.GetStatusAsync(recovered.JobId, "reports", "tenant-b"));
    }

    [Fact]
    public async Task CompetingWorkersCannotOwnTheSameUnexpiredLease()
    {
        await using var store = await TestStore.Create();
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        using var secondProcess = store.CreateProvider();
        var otherQueue = secondProcess.GetRequiredService<IDatabaseQueue>();
        var contenders = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? store.Queue : otherQueue).TryClaimAsync("reports"))));
        var lease = Assert.Single(contenders, x => x is not null)!;
        Assert.Equal(enqueued.JobId, lease.Context.JobId);
        Assert.Equal(1, lease.Context.Attempt);
        Assert.True(await otherQueue.CompleteAsync(lease));
        Assert.False(await store.Queue.CompleteAsync(lease));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        var duplicate = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        Assert.True(duplicate.AlreadyEnqueued);
        Assert.Equal(enqueued.JobId, duplicate.JobId);
    }

    [Fact]
    public async Task CrashedLeaseIsRecoveredAndStaleCompletionAndFailureAreFenced()
    {
        await using var store = await TestStore.Create();
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        var crashed = (await store.Queue.TryClaimAsync("reports"))!;
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await store.Queue.CompleteAsync(crashed));
        using var restarted = store.CreateProvider();
        var queue = restarted.GetRequiredService<IDatabaseQueue>();
        var recovered = (await queue.TryClaimAsync("reports"))!;
        Assert.Equal(enqueued.JobId, recovered.Context.JobId);
        Assert.Equal(2, recovered.Context.Attempt);
        Assert.False(await store.Queue.CompleteAsync(crashed));
        Assert.False(await store.Queue.FailAsync(crashed, QueueFailure.HandlerFailed));
        Assert.True(await queue.CompleteAsync(recovered));
        Assert.Equal(QueueJobState.Completed, (await queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!.State);
    }

    [Fact]
    public async Task DueTimesRetriesDeadLettersAndExplicitTenantScopedReplayArePersisted()
    {
        await using var store = await TestStore.Create();
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"),
            new("reports", "tenant-a", "one", store.Clock.GetUtcNow().AddHours(1)));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromHours(1));
        var first = (await store.Queue.TryClaimAsync("reports"))!;
        Assert.True(await store.Queue.FailAsync(first, QueueFailure.HandlerFailed));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = (await store.Queue.TryClaimAsync("reports"))!;
        Assert.Equal(2, second.Context.Attempt);
        Assert.True(await store.Queue.FailAsync(second, QueueFailure.HandlerFailed));
        var failed = (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!;
        Assert.Equal(QueueJobState.DeadLetter, failed.State);
        Assert.Equal(QueueFailure.HandlerFailed, failed.LastFailure);
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        Assert.False(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "tenant-b"));
        Assert.False(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "TENANT-A"));
        Assert.True(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "tenant-a"));
        Assert.False(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "tenant-a"));
        var replay = (await store.Queue.TryClaimAsync("reports"))!;
        Assert.Equal(1, replay.Context.Attempt);
        Assert.False(await store.Queue.CompleteAsync(second));
        Assert.Equal(1, (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!.ReplayCount);
    }

    [Fact]
    public async Task RepeatedCrashesExhaustAttemptsWithoutLosingThePersistedJob()
    {
        await using var store = await TestStore.Create();
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        Assert.NotNull(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        var job = (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!;
        Assert.Equal(QueueJobState.DeadLetter, job.State);
        Assert.Equal(QueueFailure.AttemptsExhausted, job.LastFailure);
    }

    [Fact]
    public async Task RetryDelayGrowsExponentiallyButNeverExceedsTheConfiguredCap()
    {
        await using var store = await TestStore.Create(options =>
        {
            options.MaxAttempts = 4;
            options.RetryDelay = TimeSpan.FromSeconds(2);
            options.MaxRetryDelay = TimeSpan.FromSeconds(3);
        });
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        foreach (var delay in new[] { 2, 3, 3 })
        {
            var lease = (await store.Queue.TryClaimAsync("reports"))!;
            Assert.True(await store.Queue.FailAsync(lease, QueueFailure.HandlerFailed));
            var status = (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!;
            Assert.Equal(store.Clock.GetUtcNow().AddSeconds(delay), status.AvailableAt);
            Assert.Null(await store.Queue.TryClaimAsync("reports"));
            store.Clock.Advance(TimeSpan.FromSeconds(delay));
        }
        Assert.Equal(4, (await store.Queue.TryClaimAsync("reports"))!.Context.Attempt);
    }

    [Theory]
    [InlineData(5000, 10000, 100000, 2, 3)]
    [InlineData(0, 15000, 100000, 2, 3)]
    [InlineData(5000, 15000, 100000, 2, 4)]
    [InlineData(0, 10000, 20000, 1, 2)]
    [InlineData(5000, 15000, 25000, 2, 3)]
    public async Task RetryDueTimesPreserveFractionalClockAndDelayUntilRoundingTheFinalInstant(
        long clockOffsetTicks, long retryTicks, long capTicks, int firstDueMilliseconds, int secondDueMilliseconds)
    {
        await using var store = await TestStore.Create(options =>
        {
            options.MaxAttempts = 3;
            options.RetryDelay = TimeSpan.FromTicks(retryTicks);
            options.MaxRetryDelay = TimeSpan.FromTicks(capTicks);
        });
        var accepted = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        var lease = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
        foreach (var dueMilliseconds in new[] { firstDueMilliseconds, secondDueMilliseconds })
        {
            var expectedDue = store.Clock.GetUtcNow().AddMilliseconds(dueMilliseconds);
            store.Clock.Advance(TimeSpan.FromTicks(clockOffsetTicks));
            Assert.True(await store.Queue.FailAsync(lease, QueueFailure.HandlerFailed));
            Assert.Equal(expectedDue, (await store.Queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!.AvailableAt);
            store.Clock.Advance(expectedDue.AddTicks(-1) - store.Clock.GetUtcNow());
            Assert.Null(await store.Queue.TryClaimAsync("reports"));
            store.Clock.Advance(TimeSpan.FromTicks(1));
            lease = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
            Assert.Equal(accepted.JobId, lease.Context.JobId);
        }
        Assert.Equal(3, lease.Context.Attempt);
    }

    [Fact]
    public async Task RestartPreservesDelayedOrderingAndTheFirstAcceptedDueTime()
    {
        await using var store = await TestStore.Create();
        var nearDue = store.Clock.GetUtcNow().AddSeconds(5);
        var farDue = nearDue.AddSeconds(5);
        var far = await store.Queue.EnqueueAsync(new Message("far"), new("reports", "tenant-a", "far", farDue));
        var firstNear = await store.Queue.EnqueueAsync(new Message("first-near"), new("reports", "tenant-a", "first-near", nearDue));
        store.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var secondNear = await store.Queue.EnqueueAsync(new Message("second-near"), new("reports", "tenant-a", "second-near", nearDue));

        await store.RestartAsync();
        var duplicate = await store.Queue.EnqueueAsync(new Message("far"),
            new("reports", "tenant-a", "far", store.Clock.GetUtcNow()));
        Assert.True(duplicate.AlreadyEnqueued);
        Assert.Equal(far.JobId, duplicate.JobId);
        Assert.Equal(farDue, (await store.Queue.GetStatusAsync(far.JobId, "reports", "tenant-a"))!.AvailableAt);

        store.Clock.Advance(nearDue - store.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromMilliseconds(1));
        foreach (var expected in new[] { firstNear.JobId, secondNear.JobId })
        {
            var lease = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
            Assert.Equal(expected, lease.Context.JobId);
            Assert.True(await store.Queue.CompleteAsync(lease));
        }
        await store.RestartAsync();
        store.Clock.Advance(farDue - store.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        store.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var last = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
        Assert.Equal(far.JobId, last.Context.JobId);
        Assert.True(await store.Queue.CompleteAsync(last));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
    }

    [Fact]
    public async Task RestartAtFinalAttemptExpiryRequiresExplicitReplayAndPreservesAcceptedAttemptBudget()
    {
        var maxAttempts = 1;
        await using var store = await TestStore.Create(options => options.MaxAttempts = maxAttempts);
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        var crashed = Assert.IsType<QueueLease>(await store.Queue.TryClaimAsync("reports"));
        maxAttempts = 5; // A deployment's changed defaults must not rewrite an accepted envelope.
        await store.RestartAsync();
        store.Clock.Advance(crashed.ExpiresAt - store.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        Assert.Null(await store.Queue.TryClaimAsync("reports"));
        Assert.Equal(QueueJobState.Leased, (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!.State);

        store.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(await store.Queue.CompleteAsync(crashed));
        Assert.False(await store.Queue.FailAsync(crashed, QueueFailure.HandlerFailed));
        var worker = store.Provider.GetRequiredService<QueueWorker>();
        Assert.False(await worker.RunOnceAsync("reports"));
        Assert.Empty(store.Observed.Executions);
        var dead = (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!;
        Assert.Equal(QueueJobState.DeadLetter, dead.State);
        Assert.Equal(QueueFailure.AttemptsExhausted, dead.LastFailure);
        Assert.Equal(1, dead.Attempts);
        Assert.Equal(1, dead.MaxAttempts);

        await store.RestartAsync();
        Assert.False(await store.Provider.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
        Assert.True(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "tenant-a"));
        Assert.True(await store.Provider.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
        var execution = Assert.Single(store.Observed.Executions);
        Assert.Equal(enqueued.JobId, execution.Context.JobId);
        Assert.Equal(1, execution.Context.Attempt);
        Assert.False(await store.Queue.CompleteAsync(crashed));
        var completed = (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!;
        Assert.Equal(QueueJobState.Completed, completed.State);
        Assert.Equal(1, completed.ReplayCount);
        Assert.Equal(1, completed.MaxAttempts);
    }

    [Fact]
    public async Task WorkerResolvesOneScopePerJobAndUsesEnvelopeTenantInsteadOfPayloadTenant()
    {
        await using var store = await TestStore.Create();
        await store.Queue.EnqueueAsync(new Message("one", "forged-tenant"), new("reports", "tenant-a", "one"));
        await store.Queue.EnqueueAsync(new Message("two", "forged-tenant"), new("reports", "tenant-b", "two"));
        var worker = store.Provider.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("reports"));
        Assert.True(await worker.RunOnceAsync("reports"));
        Assert.False(await worker.RunOnceAsync("reports"));
        Assert.Equal(new[] { "tenant-a", "tenant-b" }, store.Observed.Executions.Select(x => x.Context.TenantId).Order());
        Assert.Equal(2, store.Observed.Executions.Select(x => x.ScopeId).Distinct().Count());
        Assert.Equal(2, store.Observed.Disposals);
    }

    [Fact]
    public async Task WorkerFailureRetriesAndShutdownCancellationLeavesRecoverableLease()
    {
        await using var store = await TestStore.Create();
        var failing = await store.Queue.EnqueueAsync(new Message("fail"), new("reports", "tenant-a", "fail"));
        var worker = store.Provider.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("reports"));
        var failure = (await store.Queue.GetStatusAsync(failing.JobId, "reports", "tenant-a"))!;
        Assert.Equal(QueueJobState.Pending, failure.State);
        Assert.Equal(QueueFailure.HandlerFailed, failure.LastFailure);
        var cancelling = await store.Queue.EnqueueAsync(new Message("wait"), new("reports", "tenant-a", "wait"));
        using var stop = new CancellationTokenSource();
        var running = worker.RunOnceAsync("reports", stop.Token);
        await store.Observed.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(QueueJobState.Leased, (await store.Queue.GetStatusAsync(cancelling.JobId, "reports", "tenant-a"))!.State);
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        var recovered = await store.Queue.TryClaimAsync("reports");
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforePersistenceAndUnknownWireNamesNeverActivateTypes()
    {
        await using var store = await TestStore.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => store.Queue.EnqueueAsync(new Message(new string('x', 1024)), new("reports", "tenant-a", "large")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Queue.EnqueueAsync("unregistered", new("reports", "tenant-a", "unknown")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Queue.EnqueueAsync(new Message("one"), new("reports", "", "one")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "cancelled"), new CancellationToken(true)));
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        await using (var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal(1, await db.Jobs.CountAsync());
            await db.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobType, "System.Diagnostics.Process"));
        }
        await store.Provider.GetRequiredService<QueueWorker>().RunOnceAsync("reports");
        Assert.Empty(store.Observed.Executions);
        Assert.Equal(QueueFailure.UnknownJobType, (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!.LastFailure);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"value\":[]}")]
    public async Task MalformedPersistedJsonIsRetriedWithoutInvokingAHandler(string payload)
    {
        await using var store = await TestStore.Create();
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        await using (var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
            await db.Jobs.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Payload, payload));
        Assert.True(await store.Provider.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
        Assert.Empty(store.Observed.Executions);
        Assert.Equal(QueueFailure.InvalidPayload, (await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "tenant-a"))!.LastFailure);
    }

    [Fact]
    public async Task JsonExceptionFromAHandlerIsRetriedAndDeadLetteredAsAHandlerFailure()
    {
        await using var store = await TestStore.Create();
        var accepted = await store.Queue.EnqueueAsync(new Message("json-fail"), new("reports", "tenant-a", "upstream-json"));
        var worker = store.Provider.GetRequiredService<QueueWorker>();
        foreach (var expected in new[] { QueueJobState.Pending, QueueJobState.DeadLetter })
        {
            Assert.True(await worker.RunOnceAsync("reports"));
            var status = (await store.Queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!;
            Assert.Equal(expected, status.State);
            Assert.Equal(QueueFailure.HandlerFailed, status.LastFailure);
            store.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, store.Observed.Executions.Count);
        Assert.Equal(2, store.Observed.Disposals);
        Assert.False(await worker.RunOnceAsync("reports"));
    }

    [Fact]
    public async Task CaseInsensitiveDatabaseCollationCannotBroadenTenantStatusOrReplay()
    {
        await using var store = await TestStore.Create(options => options.MaxAttempts = 1,
            options => options.ReplaceService<IModelCustomizer, CaseInsensitiveTenantModelCustomizer>());
        var enqueued = await store.Queue.EnqueueAsync(new Message("one"), new("reports", "tenant-a", "one"));
        var lease = (await store.Queue.TryClaimAsync("reports"))!;
        Assert.True(await store.Queue.FailAsync(lease, QueueFailure.HandlerFailed));
        await using (var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync())
            Assert.True(await db.Jobs.AnyAsync(x => x.Id == enqueued.JobId && x.TenantId == "TENANT-A"));
        Assert.Null(await store.Queue.GetStatusAsync(enqueued.JobId, "reports", "TENANT-A"));
        Assert.False(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "TENANT-A"));
        Assert.True(await store.Queue.ReplayAsync(enqueued.JobId, "reports", "tenant-a"));
    }

    [Fact]
    public async Task NativeCountersReflectPersistedTransitionsAndExcludePrivateIdentifiers()
    {
        var queueName = "metrics-" + Guid.NewGuid().ToString("N");
        var measurements = new ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "Caravel.Queues") owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var captured = tags.ToArray();
            if (captured.Any(x => x.Key == "queue" && Equals(x.Value, queueName)))
                measurements.Enqueue((instrument.Name, value, captured));
        });
        listener.Start();
        await using var store = await TestStore.Create();
        var dispatch = new QueueDispatchOptions(queueName, "private-tenant-42", "private-key-72");
        var payload = new Message("private-payload-81");
        var accepted = await store.Queue.EnqueueAsync(payload, dispatch);
        await store.Queue.EnqueueAsync(payload, dispatch);
        await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() => store.Queue.EnqueueAsync(new Message("changed"), dispatch));
        var first = (await store.Queue.TryClaimAsync(queueName))!;
        Assert.True(await store.Queue.FailAsync(first, QueueFailure.HandlerFailed));
        store.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = (await store.Queue.TryClaimAsync(queueName))!;
        Assert.True(await store.Queue.FailAsync(second, QueueFailure.HandlerFailed));
        Assert.False(await store.Queue.ReplayAsync(accepted.JobId, queueName, "wrong-tenant"));
        Assert.True(await store.Queue.ReplayAsync(accepted.JobId, queueName, dispatch.TenantId));
        var replay = (await store.Queue.TryClaimAsync(queueName))!;
        Assert.True(await store.Queue.CompleteAsync(replay));
        Assert.False(await store.Queue.CompleteAsync(replay));
        Assert.False(await store.Queue.FailAsync(replay, QueueFailure.HandlerFailed));
        await store.Queue.EnqueueAsync(payload, dispatch with { IdempotencyKey = "crash" });
        Assert.NotNull(await store.Queue.TryClaimAsync(queueName));
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await store.Queue.TryClaimAsync(queueName));
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.Queue.TryClaimAsync(queueName));

        var counts = measurements.GroupBy(x => x.Name).ToDictionary(group => group.Key, group => group.Sum(x => x.Value));
        Assert.Equal(2, counts["caravel.queue.jobs.accepted"]);
        Assert.Equal(1, counts["caravel.queue.jobs.deduplicated"]);
        Assert.Equal(5, counts["caravel.queue.jobs.claimed"]);
        Assert.Equal(1, counts["caravel.queue.jobs.completed"]);
        Assert.Equal(2, counts["caravel.queue.jobs.failed"]);
        Assert.Equal(1, counts["caravel.queue.jobs.retried"]);
        Assert.Equal(2, counts["caravel.queue.jobs.deadlettered"]);
        Assert.Equal(1, counts["caravel.queue.jobs.replayed"]);
        Assert.Equal(2, counts["caravel.queue.jobs.lease_lost"]);
        Assert.All(measurements.SelectMany(x => x.Tags), tag =>
        {
            Assert.Contains(tag.Key, new[] { "queue", "failure" });
            Assert.Contains(tag.Value, new object[] { queueName, "HandlerFailed", "AttemptsExhausted" });
        });
    }

    [Fact]
    public async Task NativeConsumerActivitiesDistinguishCompletionFailureAndLostLeaseWithoutPayloads()
    {
        var prefix = "traces-" + Guid.NewGuid().ToString("N");
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Caravel.Queues",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("messaging.destination.name") is string queue && queue.StartsWith(prefix, StringComparison.Ordinal))
                    activities.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        await using var store = await TestStore.Create();
        var worker = store.Provider.GetRequiredService<QueueWorker>();
        var jobs = new Dictionary<string, EnqueueResult>();
        foreach (var behavior in new[] { "success", "fail", "expire", "unknown" })
        {
            var queueName = prefix + "-" + behavior;
            jobs[behavior] = await store.Queue.EnqueueAsync(new Message(behavior, "private-payload-tenant"),
                new(queueName, "private-envelope-tenant", "private-key"));
            if (behavior == "unknown")
            {
                await using var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
                await db.Jobs.Where(x => x.Id == jobs[behavior].JobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.JobType, "private-unknown-wire-name"));
            }
            Assert.True(await worker.RunOnceAsync(queueName));
        }
        Assert.Equal(4, activities.Count);
        foreach (var activity in activities)
        {
            Assert.Equal(ActivityKind.Consumer, activity.Kind);
            Assert.Equal("caravel.queue.process", activity.OperationName);
            Assert.Empty(activity.Events);
            Assert.True(string.IsNullOrEmpty(activity.StatusDescription));
            var queueName = (string)activity.GetTagItem("messaging.destination.name")!;
            var behavior = queueName[(prefix.Length + 1)..];
            Assert.Equal(jobs[behavior].JobId.ToString(), activity.GetTagItem("messaging.message.id"));
            if (behavior == "success")
            {
                Assert.Equal(ActivityStatusCode.Ok, activity.Status);
                Assert.Equal("completed", activity.GetTagItem("caravel.queue.outcome"));
            }
            else
            {
                Assert.Equal(ActivityStatusCode.Error, activity.Status);
                Assert.Equal(behavior == "expire" ? "lease_lost" : "failed", activity.GetTagItem("caravel.queue.outcome"));
                Assert.Equal(behavior switch { "fail" => "HandlerFailed", "expire" => "LeaseLost", _ => "UnknownJobType" },
                    activity.GetTagItem("caravel.queue.failure"));
            }
            Assert.Equal(behavior == "unknown" ? null : "test.message.v1", activity.GetTagItem("caravel.job.type"));
            Assert.All(activity.TagObjects, tag =>
            {
                Assert.DoesNotContain("private", tag.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Sensitive", tag.Value?.ToString() ?? "", StringComparison.Ordinal);
            });
        }
    }

    public sealed class CaseInsensitiveTenantModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<QueueJob>().Property(x => x.TenantId).UseCollation("NOCASE");
        }
    }

    public sealed record Message(string Value, string TenantId = "untrusted");
    public sealed class Observations
    {
        public List<(JobContext Context, Guid ScopeId)> Executions { get; } = [];
        public int Disposals;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class ScopeMarker(Observations observed) : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ValueTask DisposeAsync() { observed.Disposals++; return ValueTask.CompletedTask; }
    }
    public sealed class Handler(Observations observed, ScopeMarker scope, TimeProvider clock) : IJobHandler<Message>
    {
        public async Task HandleAsync(Message job, JobContext context, CancellationToken cancellationToken)
        {
            if (job.Value == "json-fail")
            {
                observed.Executions.Add((context, scope.Id));
                throw new JsonException("Sensitive upstream response must not be persisted as an error.");
            }
            if (job.Value == "fail") throw new InvalidOperationException("Sensitive payload must not be persisted as an error.");
            if (job.Value == "expire") ((ManualClock)clock).Advance(TimeSpan.FromMinutes(2));
            if (job.Value == "wait")
            {
                observed.Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            observed.Executions.Add((context, scope.Id));
        }
    }
    public sealed class ManualClock : TimeProvider
    {
        private long ticks = DateTimeOffset.Parse("2026-09-28T00:00:00Z").Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    private sealed class TestStore : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "caravel-queue-" + Guid.NewGuid().ToString("N"));
        public ManualClock Clock { get; } = new();
        public Observations Observed { get; } = new();
        public ServiceProvider Provider { get; private set; } = null!;
        private Action<DatabaseQueueOptions>? configure;
        private Action<DbContextOptionsBuilder>? configureDatabase;
        public IDatabaseQueue Queue => Provider.GetRequiredService<IDatabaseQueue>();
        public static async Task<TestStore> Create(Action<DatabaseQueueOptions>? configure = null,
            Action<DbContextOptionsBuilder>? configureDatabase = null)
        {
            var store = new TestStore { configure = configure, configureDatabase = configureDatabase };
            Directory.CreateDirectory(store.directory);
            store.Provider = store.CreateProvider();
            await using var db = await store.Provider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync(); // Only a new synthetic test database; never application startup.
            return store;
        }
        public ServiceProvider CreateProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(Observed);
            services.AddScoped<ScopeMarker>();
            services.AddDbContextFactory<QueueDbContext>(options =>
            {
                options.UseSqlite($"Data Source={Path.Combine(directory, "queue.db")};Pooling=False;Default Timeout=15");
                configureDatabase?.Invoke(options);
            });
            services.AddCaravelDatabaseQueue(options =>
            {
                options.LeaseDuration = TimeSpan.FromMinutes(1);
                options.MaxAttempts = 2;
                options.MaxPayloadBytes = 256;
                configure?.Invoke(options);
            }).AddQueueJob<Message, Handler>("test.message.v1");
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        public async Task RestartAsync()
        {
            await Provider.DisposeAsync();
            Provider = CreateProvider();
        }
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
