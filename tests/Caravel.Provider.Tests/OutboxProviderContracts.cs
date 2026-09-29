using System.Data.Common;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class OutboxProviderContracts
{
    [Fact]
    public Task Sqlite_outbox_contract() => Exercise("sqlite");
    [ProviderFact("CARAVEL_TEST_SQLSERVER")]
    public Task SqlServer_outbox_contract() => Exercise("sqlserver");
    [ProviderFact("CARAVEL_TEST_POSTGRES")]
    public Task Postgres_outbox_contract() => Exercise("postgres");

    private static async Task Exercise(string provider)
    {
        var appName = "caravel_outbox_test_" + Guid.NewGuid().ToString("N");
        var queueName = "caravel_outbox_queue_" + Guid.NewGuid().ToString("N");
        var outage = new ReceiptOutage();
        var queueOutage = new QueueOutage();
        ServiceProvider CreateServices()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<ApplicationDatabase>(options =>
            {
                ProviderTestDatabase.Configure(options, provider, appName);
                options.AddInterceptors(outage);
            });
            services.AddDbContextFactory<QueueDbContext>(options =>
            {
                ProviderTestDatabase.Configure(options, provider, queueName);
                options.AddInterceptors(queueOutage);
            });
            services.AddCaravelDatabaseQueue();
            services.AddQueueJob<SampleJob, SampleHandler>("outbox.sample.v1");
            services.AddCaravelOutbox<ApplicationDatabase>();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        await using var original = CreateServices();
        var appFactory = original.GetRequiredService<IDbContextFactory<ApplicationDatabase>>();
        var queueFactory = original.GetRequiredService<IDbContextFactory<QueueDbContext>>();
        await using var app = await appFactory.CreateDbContextAsync();
        await using var queueDb = await queueFactory.CreateDbContextAsync();
        var appCreated = false;
        var queueCreated = false;
        try
        {
            appCreated = await app.Database.EnsureCreatedAsync();
            Assert.True(appCreated, "Refusing to reuse an existing application database.");
            queueCreated = await queueDb.Database.EnsureCreatedAsync();
            Assert.True(queueCreated, "Refusing to reuse an existing queue database.");
            await using (var scope = original.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
                var outbox = scope.ServiceProvider.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.Results.Add(new Result { Id = Guid.NewGuid(), Quantity = 3 });
                await outbox.StageAsync(new SampleJob(3), new("reports", "tenant-a", "rolled-back"));
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }
            Assert.Empty(await app.Results.AsNoTracking().ToListAsync());
            Assert.Empty(await app.Set<OutboxMessage>().AsNoTracking().ToListAsync());
            Guid intent;
            await using (var scope = original.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
                var outbox = scope.ServiceProvider.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
                db.Results.Add(new Result { Id = Guid.NewGuid(), Quantity = 5 });
                var staged = await outbox.StageAsync(new SampleJob(5), new("reports", "tenant-a", "accepted"));
                intent = staged.OutboxId;
                var duplicate = await outbox.StageAsync(new SampleJob(5), new("reports", "tenant-a", "accepted"));
                Assert.Equal(intent, duplicate.OutboxId);
                Assert.True(duplicate.AlreadyStaged);
                await Assert.ThrowsAsync<QueueIdempotencyConflictException>(() =>
                    outbox.StageAsync(new SampleJob(9), new("reports", "tenant-a", "accepted")));
                await db.SaveChangesAsync();
            }
            Assert.Single(await app.Results.AsNoTracking().ToListAsync());
            Assert.Empty(await queueDb.Jobs.AsNoTracking().ToListAsync());
            queueOutage.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => original.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync());
            queueOutage.Fail = false;
            Assert.Empty(await queueDb.Jobs.AsNoTracking().ToListAsync());
            Assert.Null((await app.Set<OutboxMessage>().AsNoTracking().SingleAsync()).DispatchedAt);
            outage.FailReceipt = true;
            await Assert.ThrowsAsync<IOException>(() => original.GetRequiredService<OutboxRelay<ApplicationDatabase>>().RunOnceAsync());
            var delivered = Assert.Single(await queueDb.Jobs.AsNoTracking().ToListAsync());
            Assert.Equal(intent, delivered.Id);
            Assert.Null((await app.Set<OutboxMessage>().AsNoTracking().SingleAsync()).DispatchedAt);
            outage.FailReceipt = false;
            // A fresh service graph resumes after an enqueue succeeded but the app receipt was not saved.
            await using var restarted = CreateServices();
            var relay = restarted.GetRequiredService<OutboxRelay<ApplicationDatabase>>();
            Assert.Equal(1, await relay.RunOnceAsync());
            Assert.Equal(0, await relay.RunOnceAsync());
            Assert.Single(await queueDb.Jobs.AsNoTracking().ToListAsync());
            var receipt = await app.Set<OutboxMessage>().AsNoTracking().SingleAsync();
            Assert.NotNull(receipt.DispatchedAt);
            Assert.Equal(delivered.Id, receipt.QueueJobId);
            // Both callers stage before either saves. The losing unique-key race must roll back its business row too.
            var stagedTogether = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrivals = 0;
            var winners = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var scope = restarted.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
                await scope.ServiceProvider.GetRequiredService<QueueOutbox<ApplicationDatabase>>()
                    .StageAsync(new SampleJob(5), new("reports", "tenant-a", "raced"));
                db.Results.Add(new Result { Id = Guid.NewGuid(), Quantity = 7 });
                if (Interlocked.Increment(ref arrivals) == 2) stagedTogether.TrySetResult();
                await stagedTogether.Task.WaitAsync(TimeSpan.FromSeconds(15));
                try { await db.SaveChangesAsync(); return true; }
                catch (DbUpdateException) { return false; }
            }));
            Assert.Single(winners, won => won);
            Assert.Equal(2, await app.Results.CountAsync());
            Assert.Equal(2, await app.Set<OutboxMessage>().CountAsync());
            await using (var scope = restarted.CreateAsyncScope())
            {
                var outbox = scope.ServiceProvider.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
                Assert.True((await outbox.StageAsync(new SampleJob(5), new("reports", "tenant-a", "accepted"))).AlreadyStaged);
                await outbox.StageAsync(new SampleJob(5), new("reports", "tenant-b", "accepted"));
                await outbox.StageAsync(new SampleJob(5), new("other", "tenant-a", "accepted"));
                await scope.ServiceProvider.GetRequiredService<ApplicationDatabase>().SaveChangesAsync();
            }
            var marked = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => relay.RunOnceAsync()));
            Assert.Equal(3, marked.Sum());
            Assert.Equal(4, await queueDb.Jobs.CountAsync());
            Assert.Equal(4, await app.Set<OutboxMessage>().CountAsync(x => x.DispatchedAt != null));
            var queue = restarted.GetRequiredService<IDatabaseQueue>();
            Assert.Null(await queue.GetStatusAsync(delivered.Id, "reports", "tenant-b"));
            Assert.Null(await queue.GetStatusAsync(delivered.Id, "reports", "TENANT-A"));
            Assert.True(await restarted.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
            Assert.True(await restarted.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
            Assert.True(await restarted.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
            Assert.True(await restarted.GetRequiredService<QueueWorker>().RunOnceAsync("other"));
            Assert.Equal(4, await queueDb.Jobs.CountAsync(x => x.State == QueueJobState.Completed));
        }
        finally
        {
            outage.FailReceipt = false;
            queueOutage.Fail = false;
            if (queueCreated) await queueDb.Database.EnsureDeletedAsync();
            if (appCreated) await app.Database.EnsureDeletedAsync();
        }
    }

    public sealed class ApplicationDatabase(DbContextOptions<ApplicationDatabase> options) : DbContext(options)
    {
        public DbSet<Result> Results => Set<Result>();
        protected override void OnModelCreating(ModelBuilder builder) => builder.AddCaravelOutbox();
    }
    public sealed class Result { public Guid Id { get; set; } public int Quantity { get; set; } }
    public sealed record SampleJob(int Quantity);
    public sealed class SampleHandler : IJobHandler<SampleJob>
    {
        public Task HandleAsync(SampleJob job, JobContext context, CancellationToken cancellationToken)
        {
            Assert.Equal(5, job.Quantity);
            Assert.Contains(context.TenantId, new[] { "tenant-a", "tenant-b" });
            return Task.CompletedTask;
        }
    }
    private sealed class QueueOutage : DbConnectionInterceptor
    {
        public bool Fail;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic queue outage.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ReceiptOutage : DbCommandInterceptor
    {
        public bool FailReceipt;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailReceipt && command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("CaravelOutboxMessages", StringComparison.Ordinal))
                throw new IOException("Synthetic dispatch receipt outage.");
            return ValueTask.FromResult(result);
        }
    }
}
