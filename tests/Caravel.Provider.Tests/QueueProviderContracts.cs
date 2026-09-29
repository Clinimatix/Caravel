using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class QueueProviderContracts
{
    [Fact]
    public Task Sqlite_queue_contract() => Exercise("sqlite");

    [ProviderFact("CARAVEL_TEST_SQLSERVER")]
    public Task SqlServer_queue_contract() => Exercise("sqlserver");

    [ProviderFact("CARAVEL_TEST_POSTGRES")]
    public Task Postgres_queue_contract() => Exercise("postgres");

    private static async Task Exercise(string provider)
    {
        var databaseName = "caravel_queue_test_" + Guid.NewGuid().ToString("N");
        var clock = new QueueClock();
        var registrations = new ServiceCollection();
        registrations.AddDbContextFactory<QueueDbContext>(options => ProviderTestDatabase.Configure(options, provider, databaseName));
        registrations.AddSingleton<TimeProvider>(clock);
        registrations.AddCaravelDatabaseQueue(options =>
        {
            options.LeaseDuration = TimeSpan.FromSeconds(10);
            options.RetryDelay = TimeSpan.FromSeconds(1);
            options.MaxRetryDelay = TimeSpan.FromSeconds(1);
        });
        registrations.AddQueueJob<SampleJob, SampleHandler>("sample.v1");
        await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = services.GetRequiredService<IDbContextFactory<QueueDbContext>>();
        await using var database = await factory.CreateDbContextAsync();
        var created = false;
        try
        {
            created = await database.Database.EnsureCreatedAsync();
            Assert.True(created, "Refusing to reuse an existing database.");
            var queue = services.GetRequiredService<IDatabaseQueue>();
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                queue.EnqueueAsync(new SampleJob(5), new("reports", "tenant-a", "first"))));
            var accepted = Assert.Single(attempts, result => !result.AlreadyEnqueued);
            Assert.Single(attempts.Select(result => result.JobId).Distinct());
            await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() =>
                queue.EnqueueAsync(new SampleJob(6), new("reports", "tenant-a", "first")));
            Assert.Null(await queue.GetStatusAsync(accepted.JobId, "reports", "tenant-b"));
            Assert.Null(await queue.GetStatusAsync(accepted.JobId, "reports", "TENANT-A"));
            Assert.Null(await queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a "));

            var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => queue.TryClaimAsync("reports")));
            var stale = Assert.Single(claims, lease => lease is not null)!;
            clock.Advance(TimeSpan.FromSeconds(6));
            var renewed = Assert.IsType<QueueLease>(await queue.RenewAsync(stale));
            Assert.True(renewed.ExpiresAt > stale.ExpiresAt);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Null(await queue.TryClaimAsync("reports"));
            clock.Advance(TimeSpan.FromSeconds(6));
            Assert.Null(await queue.RenewAsync(renewed));
            var recovered = Assert.IsType<QueueLease>(await queue.TryClaimAsync("reports"));
            Assert.Equal(2, recovered.Context.Attempt);
            Assert.Null(await queue.RenewAsync(stale));
            Assert.False(await queue.CompleteAsync(stale));
            Assert.False(await queue.FailAsync(stale, QueueFailure.HandlerFailed));
            Assert.True(await queue.FailAsync(recovered, QueueFailure.HandlerFailed));
            Assert.Null(await queue.TryClaimAsync("reports"));
            clock.Advance(TimeSpan.FromSeconds(2));
            var lastAttempt = Assert.IsType<QueueLease>(await queue.TryClaimAsync("reports"));
            Assert.Equal(3, lastAttempt.Context.Attempt);
            Assert.True(await queue.FailAsync(lastAttempt, QueueFailure.HandlerFailed));
            Assert.Equal(QueueJobState.DeadLetter, (await queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!.State);
            Assert.False(await queue.ReplayAsync(accepted.JobId, "reports", "TENANT-A"));
            Assert.False(await queue.ReplayAsync(accepted.JobId, "reports", "tenant-a "));
            Assert.True(await queue.ReplayAsync(accepted.JobId, "reports", "tenant-a"));
            Assert.False(await queue.ReplayAsync(accepted.JobId, "reports", "tenant-a"));
            Assert.True(await services.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
            var completed = (await queue.GetStatusAsync(accepted.JobId, "reports", "tenant-a"))!;
            Assert.Equal(QueueJobState.Completed, completed.State);
            Assert.Equal(1, completed.ReplayCount);
            Assert.True((await queue.EnqueueAsync(new SampleJob(5), new("reports", "tenant-a", "first"))).AlreadyEnqueued);
            Assert.Null(await queue.TryClaimAsync("reports"));
        }
        finally
        {
            if (created) await database.Database.EnsureDeletedAsync();
        }
    }

    private sealed class QueueClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

    public sealed record SampleJob(int Quantity);
    public sealed class SampleHandler : IJobHandler<SampleJob>
    {
        public Task HandleAsync(SampleJob job, JobContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(5, job.Quantity);
            Assert.Equal("tenant-a", context.TenantId);
            return Task.CompletedTask;
        }
    }
}
