using Caravel.Clarion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Clarion.Tests;

public sealed class PersistenceRegressionTests
{
    [Fact]
    public async Task SoftDeleteDiscardsUnsavedComplexValuesAndPreservesNestedConcurrencyToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddClarion<ComplexContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await using var otherScope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var other = otherScope.ServiceProvider.GetRequiredService<IClarion>();
        await db.Context.Database.EnsureCreatedAsync();
        var model = new ComplexModel { Detail = new Detail { Label = "Stored label" } };
        db.Models<ComplexModel>().Add(model);
        await db.SaveChangesAsync();
        var stale = await other.Models<ComplexModel>().SingleAsync();

        model.Detail.Label = "Unsaved complex change";
        model.Detail.Nested.Text = "Unsaved nested change";
        model.Detail.Nested.Version = "two";
        db.Models<ComplexModel>().Remove(model);
        await db.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        var deleted = await db.Models<ComplexModel>().WithTrashed().SingleAsync();
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal("Stored label", deleted.Detail.Label);
        Assert.Equal("Stored nested value", deleted.Detail.Nested.Text);
        Assert.Equal("two", deleted.Detail.Nested.Version);
        stale.Detail.Nested.Text = "Stale change";
        stale.Detail.Nested.Version = "three";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task SoftDeletePreservesExplicitConcurrencyTokenChangeAndRejectsStaleWriter()
    {
        await using var store = await Store.CreateAsync();
        await using var first = store.Host.Services.CreateAsyncScope();
        await using var second = store.Host.Services.CreateAsyncScope();
        var winner = first.ServiceProvider.GetRequiredService<IClarion>();
        var other = second.ServiceProvider.GetRequiredService<IClarion>();
        var person = new Person { Name = "Stored name", Version = "one" };
        winner.Models<Person>().Add(person);
        await winner.SaveChangesAsync();
        var stale = await other.Models<Person>().SingleAsync();

        person.Name = "Discard this unsaved name";
        person.Version = "two";
        winner.Models<Person>().Remove(person);
        await winner.SaveChangesAsync();
        winner.Context.ChangeTracker.Clear();
        var deleted = await winner.Models<Person>().WithTrashed().SingleAsync();
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal("Stored name", deleted.Name);
        Assert.Equal("two", deleted.Version);

        stale.Name = "Stale change";
        stale.Version = "three";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task RestoreTracksOriginalConcurrencyValuesEvenWhenQueriesDefaultToNoTracking()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddClarion<RestoreContext>(options => options.UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        await db.Context.Database.EnsureCreatedAsync();
        var deletedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        db.Models<Restorable>().AddRange(
            new Restorable { Id = 1, TenantId = "local", DeletedAt = deletedAt },
            new Restorable { Id = 2, TenantId = "other", DeletedAt = deletedAt });
        await db.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        Assert.Null(await db.RestoreAsync<Restorable>([2]));
        var restored = await db.RestoreAsync<Restorable>([1]);
        Assert.NotNull(restored);
        await db.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        Assert.Null((await db.Models<Restorable>().SingleAsync()).DeletedAt);
        Assert.NotNull((await db.Models<Restorable>().IgnoreQueryFilters().SingleAsync(row => row.Id == 2)).DeletedAt);
        Assert.Equal(QueryTrackingBehavior.NoTracking, db.Context.ChangeTracker.QueryTrackingBehavior);
    }

    public sealed class Restorable : ISoftDeletable
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "local";
        public DateTimeOffset? DeletedAt { get; set; }
    }

    public sealed class RestoreContext(DbContextOptions<RestoreContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Restorable>().Property(row => row.DeletedAt).IsConcurrencyToken();
            builder.Entity<Restorable>().HasQueryFilter("Tenant", row => row.TenantId == "local");
            builder.ApplyClarionConventions();
        }
    }

    public sealed class ComplexModel : ISoftDeletable
    {
        public int Id { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Detail Detail { get; set; } = new();
    }

    public sealed class Detail
    {
        public string Label { get; set; } = "";
        public NestedDetail Nested { get; set; } = new();
    }

    public sealed class NestedDetail
    {
        public string Text { get; set; } = "Stored nested value";
        public string Version { get; set; } = "one";
    }

    public sealed class ComplexContext(DbContextOptions<ComplexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ComplexModel>().ComplexProperty(model => model.Detail)
                .ComplexProperty(detail => detail.Nested).Property(nested => nested.Version).IsConcurrencyToken();
            builder.ApplyClarionConventions();
        }
    }
}
