using Caravel.Clarion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Provider.Tests;

public sealed class ProviderFactAttribute : FactAttribute
{
    public ProviderFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = $"Set {variable} for disposable local database integration; SQL translation alone is not acceptance.";
    }
}

public sealed class ProviderContracts
{
    [Fact]
    public Task Sqlite_contract() => Exercise("sqlite");

    [ProviderFact("CARAVEL_TEST_SQLSERVER")]
    public Task SqlServer_contract() => Exercise("sqlserver");

    [ProviderFact("CARAVEL_TEST_POSTGRES")]
    public Task Postgres_contract() => Exercise("postgres");

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    public void Common_queries_translate_without_loading_the_database(string provider)
    {
        using var services = Services(provider, "translation_only");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var sql = db.Models<Activity>().AsNoTracking().Where(x => x.Quantity > 0)
            .GroupBy(x => x.Kind).Select(group => new { group.Key, Count = group.Count(), Total = group.Sum(x => x.Quantity) })
            .ToQueryString();
        Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", sql);
        Assert.Contains("DeletedAt", sql);
        var trashedSql = db.Models<Activity>().WithTrashed().ToQueryString();
        Assert.Contains("TenantId", trashedSql);
        Assert.DoesNotContain("DeletedAt\" IS NULL", trashedSql);
        Assert.DoesNotContain("DeletedAt] IS NULL", trashedSql);
    }

    private static async Task Exercise(string provider)
    {
        // Every run owns a new random database. Only loopback servers are accepted.
        var name = "caravel_test_" + Guid.NewGuid().ToString("N");
        await using var services = Services(provider, name);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var created = false;
        try
        {
            created = await db.Context.Database.EnsureCreatedAsync();
            Assert.True(created, "Refusing to reuse an existing database.");
            var activity = new Activity { EventKey = "event-1", Kind = "processed", Quantity = 2 };
            var otherTenant = new Activity { EventKey = "event-1", TenantId = "tenant-b", Kind = "processed", Quantity = 100 };
            db.Models<Activity>().AddRange(activity, otherTenant);
            await db.SaveChangesAsync();
            Assert.NotEqual(default, activity.CreatedAt);
            Assert.Equal(activity.CreatedAt, activity.UpdatedAt);
            db.Context.ChangeTracker.Clear();
            Assert.Single(await db.Models<Activity>().GetAsync());
            Assert.Equal(2, await db.Models<Activity>().SumAsync(x => x.Quantity));

            // Database uniqueness underpins a consumer's idempotency implementation.
            db.Models<Activity>().Add(new Activity { EventKey = "event-1", Kind = "processed" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.Context.ChangeTracker.Clear();

            var stored = await db.Models<Activity>().SingleAsync();
            db.Models<Activity>().Remove(stored);
            await db.SaveChangesAsync();
            db.Context.ChangeTracker.Clear();
            db.Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            Assert.Empty(await db.Models<Activity>().GetAsync());
            Assert.Single(await db.Models<Activity>().WithTrashed().ToListAsync());
            Assert.Null(await db.RestoreAsync<Activity>([otherTenant.Id]));
            Assert.NotNull(await db.RestoreAsync<Activity>([activity.Id]));
            await db.SaveChangesAsync();
            Assert.Equal(QueryTrackingBehavior.NoTracking, db.Context.ChangeTracker.QueryTrackingBehavior);
            db.Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;

            await using (var transaction = await db.Context.Database.BeginTransactionAsync())
            {
                db.Models<Activity>().Add(new Activity { EventKey = "rolled-back", Kind = "processed" });
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }
            db.Context.ChangeTracker.Clear();
            Assert.Single(await db.Models<Activity>().GetAsync());

            await using var secondScope = services.CreateAsyncScope();
            var second = secondScope.ServiceProvider.GetRequiredService<IClarion>();
            Assert.NotSame(db.Context, second.Context);
            var winner = await db.Models<Activity>().SingleAsync();
            var stale = await second.Models<Activity>().SingleAsync();
            winner.Quantity = 3;
            winner.Version = Guid.NewGuid();
            await db.SaveChangesAsync();
            stale.Quantity = 4;
            stale.Version = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

            // Soft deletion must preserve the application's new version as well as native concurrency checks.
            db.Context.ChangeTracker.Clear();
            second.Context.ChangeTracker.Clear();
            winner = await db.Models<Activity>().SingleAsync();
            stale = await second.Models<Activity>().SingleAsync();
            var deletedVersion = Guid.NewGuid();
            winner.Version = deletedVersion;
            winner.Quantity = 999; // Ordinary unsaved changes are discarded by soft deletion.
            db.Models<Activity>().Remove(winner);
            await db.SaveChangesAsync();
            db.Context.ChangeTracker.Clear();
            var deleted = await db.Models<Activity>().WithTrashed().SingleAsync();
            Assert.Equal(deletedVersion, deleted.Version);
            Assert.Equal(3, deleted.Quantity);
            Assert.NotNull(deleted.DeletedAt);
            stale.Quantity = 5;
            stale.Version = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        }
        finally
        {
            if (created) await db.Context.Database.EnsureDeletedAsync();
        }
    }

    private static ServiceProvider Services(string provider, string database)
    {
        var services = new ServiceCollection();
        services.AddClarion<ActivityContext>(options => ProviderTestDatabase.Configure(options, provider, database));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

public sealed class ActivityContext(DbContextOptions<ActivityContext> options) : DbContext(options)
{
    public string TenantId => "tenant-a";
    protected override void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<Activity>();
        entity.Property(x => x.TenantId).HasMaxLength(60);
        entity.Property(x => x.EventKey).HasMaxLength(80);
        entity.Property(x => x.Kind).HasMaxLength(40);
        entity.Property(x => x.Version).IsConcurrencyToken();
        entity.Property(x => x.DeletedAt).IsConcurrencyToken();
        entity.HasIndex(x => new { x.TenantId, x.EventKey }).IsUnique();
        entity.HasQueryFilter("Tenant", x => x.TenantId == TenantId);
        builder.ApplyClarionConventions();
    }
}

public sealed class Activity : ITimestamped, ISoftDeletable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = "tenant-a";
    public string EventKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Quantity { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
