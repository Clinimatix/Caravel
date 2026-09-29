using Caravel.Queues;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Scheduling.Tests;

public sealed class SchedulingTests
{
    [Fact]
    public async Task CompetingSchedulersDeduplicateTheCurrentUtcSlotAndEnqueueTheNextOne()
    {
        await using var store = await Store.Create();
        await using var first = store.Provider();
        await using var second = store.Provider();
        var outcomes = await Task.WhenAll(Task.Run(() => first.GetRequiredService<QueueScheduler>().RunOnceAsync()),
            Task.Run(() => second.GetRequiredService<QueueScheduler>().RunOnceAsync()));
        Assert.Equal(1, outcomes.Sum());
        Assert.Equal(0, await first.GetRequiredService<QueueScheduler>().RunOnceAsync());
        Assert.Single(await store.Rows());
        store.Clock.Now = store.Clock.Now.AddMinutes(1);
        Assert.Equal(1, await second.GetRequiredService<QueueScheduler>().RunOnceAsync());
        Assert.Equal(2, (await store.Rows()).Count);
    }

    [Fact]
    public async Task RestartSkipsDowntimeAndPayloadSnapshotDoesNotFollowCallerMutations()
    {
        await using var store = await Store.Create();
        var payload = new Report { Name = "original" };
        await using (var first = store.Provider(payload))
        {
            payload.Name = "changed after registration";
            Assert.Equal(1, await first.GetRequiredService<QueueScheduler>().RunOnceAsync());
        }
        store.Clock.Now = store.Clock.Now.AddHours(5);
        await using var restarted = store.Provider();
        Assert.Equal(1, await restarted.GetRequiredService<QueueScheduler>().RunOnceAsync());
        var rows = await store.Rows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Contains("original", row.Payload));
        Assert.All(rows, row => Assert.Equal("tenant-a", row.TenantId));
    }

    [Fact]
    public async Task ChangedPayloadForAnExistingScheduleSlotFailsInsteadOfSilentlyReusingTheJob()
    {
        await using var store = await Store.Create();
        await using var original = store.Provider();
        await original.GetRequiredService<QueueScheduler>().RunOnceAsync();
        await using var changed = store.Provider(new Report { Name = "different" });
        await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() => changed.GetRequiredService<QueueScheduler>().RunOnceAsync());
        Assert.Single(await store.Rows());
    }

    [Fact]
    public async Task CancellationDoesNotCreateJobsAndUtcAlignmentUsesFloorBeforeTheEpoch()
    {
        await using var store = await Store.Create();
        await using var services = store.Provider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.GetRequiredService<QueueScheduler>().RunOnceAsync(cancelled.Token));
        Assert.Empty(await store.Rows());
        store.Clock.Now = DateTimeOffset.UnixEpoch.AddMilliseconds(-1);
        await services.GetRequiredService<QueueScheduler>().RunOnceAsync();
        Assert.EndsWith(":-1", Assert.Single(await store.Rows()).IdempotencyKey);
        store.Clock.Now = DateTimeOffset.UnixEpoch;
        await services.GetRequiredService<QueueScheduler>().RunOnceAsync();
        Assert.Contains(await store.Rows(), row => row.IdempotencyKey.EndsWith(":0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterScheduleConflictPreservesEarlierEnqueueAndCorrectedRetryDeduplicatesTheTick()
    {
        await using var store = await Store.Create();
        await using (var original = store.ProviderForSchedules(("second.v1", "accepted")))
            Assert.Equal(1, await original.GetRequiredService<QueueScheduler>().RunOnceAsync());
        await using (var conflicting = store.ProviderForSchedules(("first.v1", "first"), ("second.v1", "conflict")))
            await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() => conflicting.GetRequiredService<QueueScheduler>().RunOnceAsync());

        var retained = await store.Rows();
        Assert.Equal(2, retained.Count);
        Assert.All(retained, row => Assert.Equal(QueueJobState.Pending, row.State));
        Assert.Contains("first", Assert.Single(retained, row => row.IdempotencyKey.Contains(":first.v1:")).Payload);
        Assert.Contains("accepted", Assert.Single(retained, row => row.IdempotencyKey.Contains(":second.v1:")).Payload);
        await using var corrected = store.ProviderForSchedules(("first.v1", "first"), ("second.v1", "accepted"));
        Assert.Equal(0, await corrected.GetRequiredService<QueueScheduler>().RunOnceAsync());
        Assert.Equal(retained.Select(row => row.Id).Order(), (await store.Rows()).Select(row => row.Id).Order());
        store.Clock.Now = store.Clock.Now.AddMinutes(1);
        Assert.Equal(2, await corrected.GetRequiredService<QueueScheduler>().RunOnceAsync());
        Assert.Equal(4, (await store.Rows()).Count);
    }

    [Fact]
    public async Task NativeHostStopsWhenAScheduleConflictsWithPersistedWork()
    {
        await using var store = await Store.Create();
        await using (var original = store.Provider())
            Assert.Equal(1, await original.GetRequiredService<QueueScheduler>().RunOnceAsync());
        using var host = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders()).ConfigureServices(services =>
        {
            store.Configure(services, new Report { Name = "conflict" });
            services.AddCaravelScheduler(TimeSpan.FromMilliseconds(100));
        }).Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var service = Assert.Single(host.Services.GetServices<IHostedService>().OfType<BackgroundService>());
            Assert.True(service.ExecuteTask!.IsFaulted);
            Assert.IsType<QueueIdempotencyConflictException>(service.ExecuteTask.Exception!.GetBaseException());
            Assert.Contains("original", Assert.Single(await store.Rows()).Payload);
        }
        finally { await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public void InvalidConfigurationIsRejectedAndBackgroundPollingIsOptional()
    {
        var services = new ServiceCollection();
        var job = new Report { Name = "original" };
        Assert.Throws<ArgumentException>(() => services.AddCaravelSchedule("Bad name", TimeSpan.FromSeconds(1), job, "reports", "tenant-a"));
        Assert.Throws<ArgumentException>(() => services.AddCaravelSchedule("valid", TimeSpan.FromSeconds(1), job, "../reports", "tenant-a"));
        Assert.Throws<ArgumentException>(() => services.AddCaravelSchedule("valid", TimeSpan.FromSeconds(1), job, "reports", "\n"));
        foreach (var interval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(999), TimeSpan.FromDays(366), TimeSpan.FromSeconds(1).Add(TimeSpan.FromTicks(1)) })
            Assert.Throws<ArgumentOutOfRangeException>(() => services.AddCaravelSchedule("valid", interval, job, "reports", "tenant-a"));
        Assert.Throws<ArgumentException>(() => services.AddCaravelSchedule("large", TimeSpan.FromSeconds(1),
            new Report { Name = new string('x', 1024 * 1024 + 1) }, "reports", "tenant-a"));
        services.AddCaravelSchedule("reports.v1", TimeSpan.FromMinutes(1), job, "reports", "tenant-a");
        Assert.Throws<InvalidOperationException>(() => services.AddCaravelSchedule("reports.v1", TimeSpan.FromMinutes(2), job, "reports", "tenant-a"));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IHostedService));
        services.AddCaravelScheduler();
        services.AddCaravelScheduler();
        Assert.Single(services, service => service.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task OptionalNativeHostedServiceSchedulesAndStopsCleanly()
    {
        await using var store = await Store.Create();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            store.Configure(services);
            services.AddCaravelScheduler(TimeSpan.FromMilliseconds(100));
        }).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(deadline.Token);
        try
        {
            while ((await store.Rows()).Count == 0) await Task.Delay(20, deadline.Token);
            Assert.Single(await store.Rows());
        }
        finally { await host.StopAsync(CancellationToken.None); }
    }

    public sealed class Report { public string Name { get; set; } = ""; }
    public sealed class ReportHandler : IJobHandler<Report>
    {
        public Task HandleAsync(Report job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 10, 0, 15, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("caravel-schedule-");
        private DbContextOptions<QueueDbContext> Options => new DbContextOptionsBuilder<QueueDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.FullName, "queue.db"), Pooling = false }.ToString()).Options;
        public Clock Clock { get; } = new();
        public static async Task<Store> Create()
        {
            var store = new Store();
            await using var db = new QueueDbContext(store.Options);
            await db.Database.EnsureCreatedAsync();
            return store;
        }
        public ServiceProvider Provider(Report? payload = null)
        {
            var services = new ServiceCollection();
            Configure(services, payload);
            return services.BuildServiceProvider();
        }
        public ServiceProvider ProviderForSchedules(params (string Name, string Payload)[] schedules)
        {
            var services = new ServiceCollection();
            Configure(services, includeDefaultSchedule: false);
            foreach (var schedule in schedules)
                services.AddCaravelSchedule(schedule.Name, TimeSpan.FromMinutes(1),
                    new Report { Name = schedule.Payload }, "reports", "tenant-a");
            return services.BuildServiceProvider();
        }
        public void Configure(IServiceCollection services, Report? payload = null, bool includeDefaultSchedule = true)
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.FullName, "queue.db"), Pooling = false }.ToString()));
            services.AddCaravelDatabaseQueue();
            services.AddQueueJob<Report, ReportHandler>("report.generate.v1");
            if (includeDefaultSchedule)
                services.AddCaravelSchedule("reports.v1", TimeSpan.FromMinutes(1), payload ?? new Report { Name = "original" }, "reports", "tenant-a");
        }
        public async Task<List<QueueJob>> Rows()
        {
            await using var db = new QueueDbContext(Options);
            return await db.Jobs.AsNoTracking().ToListAsync();
        }
        public ValueTask DisposeAsync() { directory.Delete(recursive: true); return ValueTask.CompletedTask; }
    }
}
