using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class OutboxTests
{
    [Fact]
    public async Task ApplicationSaveCommitsIntentAndRelayDeliversRegisteredJobWithItsEnvelope()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-outbox-");
        try
        {
            var registrations = new ServiceCollection();
            registrations.AddDbContextFactory<ApplicationDatabase>(options => options.UseSqlite(
                $"Data Source={Path.Combine(directory.FullName, "app.db")};Pooling=False"));
            registrations.AddDbContextFactory<QueueDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(directory.FullName, "queue.db")};Pooling=False"));
            registrations.AddCaravelDatabaseQueue();
            registrations.AddCaravelOutbox<ApplicationDatabase>();
            registrations.AddQueueJob<ReportJob, ReportHandler>("report.v1");
            await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            await using var app = await services.GetRequiredService<IDbContextFactory<ApplicationDatabase>>().CreateDbContextAsync();
            await using var queueDb = await services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
            Assert.True(await app.Database.EnsureCreatedAsync());
            Assert.True(await queueDb.Database.EnsureCreatedAsync());
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDatabase>();
                var outbox = scope.ServiceProvider.GetRequiredService<QueueOutbox<ApplicationDatabase>>();
                db.Reports.Add(new Report { Id = Guid.NewGuid(), Quantity = 7 });
                await outbox.StageAsync(new ReportJob(7), new("reports", "trusted-tenant", "request-one"));
                Assert.Empty(await app.Set<OutboxMessage>().ToListAsync());
                Assert.Empty(await queueDb.Jobs.ToListAsync());
                await db.SaveChangesAsync();
            }
            Assert.Single(await app.Reports.ToListAsync());
            var relay = services.GetRequiredService<OutboxRelay<ApplicationDatabase>>();
            Assert.Equal(1, await relay.RunOnceAsync());
            Assert.Equal(0, await relay.RunOnceAsync());
            var receipt = await app.Set<OutboxMessage>().SingleAsync();
            Assert.NotNull(receipt.DispatchedAt);
            Assert.NotNull(receipt.QueueJobId);
            Assert.True(await services.GetRequiredService<QueueWorker>().RunOnceAsync("reports"));
            var completed = await services.GetRequiredService<IDatabaseQueue>().GetStatusAsync(
                receipt.QueueJobId.Value, "reports", "trusted-tenant");
            Assert.Equal(QueueJobState.Completed, completed!.State);
        }
        finally { directory.Delete(true); }
    }

    public sealed class ApplicationDatabase(DbContextOptions<ApplicationDatabase> options) : DbContext(options)
    {
        public DbSet<Report> Reports => Set<Report>();
        protected override void OnModelCreating(ModelBuilder builder) => builder.AddCaravelOutbox();
    }
    public sealed class Report { public Guid Id { get; set; } public int Quantity { get; set; } }
    public sealed record ReportJob(int Quantity);
    public sealed class ReportHandler : IJobHandler<ReportJob>
    {
        public Task HandleAsync(ReportJob job, JobContext context, CancellationToken cancellationToken)
        {
            Assert.Equal(7, job.Quantity);
            Assert.Equal("trusted-tenant", context.TenantId);
            return Task.CompletedTask;
        }
    }
}
