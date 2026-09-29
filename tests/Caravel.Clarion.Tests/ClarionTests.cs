using System.Text.Json;
using Caravel.Clarion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Caravel.Clarion.Tests;

public sealed class ClarionTests
{
    [Fact]
    public async Task GenericHostUsesScopedSessionsAndNativeQueries()
    {
        await using var store = await Store.CreateAsync();
        await using var first = store.Host.Services.CreateAsyncScope();
        await using var second = store.Host.Services.CreateAsyncScope();
        var db = first.ServiceProvider.GetRequiredService<IClarion>();
        Assert.Same(db, first.ServiceProvider.GetRequiredService<IClarion>());
        Assert.Same(db.Context, first.ServiceProvider.GetRequiredService<AppDatabase>());
        Assert.NotSame(db.Context, second.ServiceProvider.GetRequiredService<IClarion>().Context);
        new ModelFactory<Person>(index => new Person { Name = $"User {index}" }).AddTo(db, 3);
        await db.SaveChangesAsync();
        var names = await db.Models<Person>().AsNoTracking().OrderBy(row => row.Name).Select(row => row.Name).GetAsync();
        Assert.Equal(new[] { "User 0", "User 1", "User 2" }, names);
        Assert.Throws<InvalidOperationException>(() => store.Host.Services.GetRequiredService<IClarion>());
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddClarion<AppDatabase>(options => options.UseSqlite("Data Source=:memory:"))
            .AddClarion<AppDatabase>(options => options.UseSqlite("Data Source=:memory:")));
    }

    [Fact]
    public async Task TimestampsPreserveAddedStateAndCreatedAtOnUpdate()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var person = new Person { Name = "Initial" };
        db.Models<Person>().Add(person);
        await db.SaveChangesAsync();
        Assert.True(person.Id > 0);
        Assert.Equal(store.Clock.GetUtcNow(), person.CreatedAt);
        Assert.Equal(person.CreatedAt, person.UpdatedAt);
        var created = person.CreatedAt;
        store.Clock.Now = store.Clock.Now.AddHours(2);
        person.Name = "Updated";
        person.CreatedAt = DateTimeOffset.MinValue;
        db.Context.SaveChanges();
        db.Context.ChangeTracker.Clear();
        var saved = await db.Models<Person>().SingleAsync();
        Assert.Equal("Updated", saved.Name);
        Assert.Equal(created, saved.CreatedAt);
        Assert.Equal(store.Clock.Now, saved.UpdatedAt);
    }

    [Fact]
    public async Task SoftDeleteAndRestorePreserveTenantFilterAndOnlyChangeDeletionColumns()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var visible = new Person { Name = "Keep stored name" };
        var hidden = new Person { Name = "Other tenant", TenantId = "other", DeletedAt = store.Clock.Now };
        db.Models<Person>().AddRange(visible, hidden);
        await db.SaveChangesAsync();
        visible.Name = "Unsaved mutation";
        db.Models<Person>().Remove(visible);
        await db.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Models<Person>().GetAsync());
        var trashed = Assert.Single(await db.Models<Person>().WithTrashed().GetAsync());
        Assert.Equal("Keep stored name", trashed.Name);
        Assert.NotNull(trashed.DeletedAt);
        Assert.Null(await db.RestoreAsync<Person>([hidden.Id]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.RestoreAsync<Person>(["invalid-key-type"]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.RestoreAsync<Person>([]));
        Assert.NotNull(await db.RestoreAsync<Person>([visible.Id]));
        await db.SaveChangesAsync();
        Assert.Single(await db.Models<Person>().GetAsync());
        Assert.Equal(2, await db.Models<Person>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task TrackedCascadeCannotPhysicallyDeleteChildrenOfSoftDeletedParent()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var parent = new Person { HardChildren = [new HardChild()] };
        db.Models<Person>().Add(parent);
        await db.SaveChangesAsync();
        db.Models<Person>().Remove(parent);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("cascade", error.Message);
        db.Context.ChangeTracker.Clear();
        Assert.Equal(1, await db.Models<HardChild>().CountAsync());
        Assert.Equal(1, await db.Models<Person>().CountAsync());
        // With no tracked children, the parent becomes UPDATE; the database never receives a DELETE cascade.
        parent = await db.Models<Person>().SingleAsync();
        db.Models<Person>().Remove(parent);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Models<HardChild>().CountAsync());
        Assert.Empty(await db.Models<Person>().GetAsync());
    }

    [Fact]
    public async Task SoftCascadeKeepsRowsAndRestorationDoesNotImplicitlyRestoreChildren()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var parent = new Person { SoftChildren = [new SoftChild()] };
        db.Models<Person>().Add(parent);
        await db.SaveChangesAsync();
        db.Models<Person>().Remove(parent);
        await db.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Models<SoftChild>().GetAsync());
        Assert.Single(await db.Models<SoftChild>().WithTrashed().GetAsync());
        await db.RestoreAsync<Person>([parent.Id]);
        await db.SaveChangesAsync();
        Assert.Empty(await db.Models<SoftChild>().GetAsync());
    }

    [Fact]
    public async Task ClientSetNullRelationshipFixupsAreRejectedBeforeWriting()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        var parent = new Person { OptionalChildren = [new OptionalChild()] };
        db.Models<Person>().Add(parent);
        await db.SaveChangesAsync();
        var parentId = parent.Id;
        db.Models<Person>().Remove(parent);
        Assert.Null(parent.OptionalChildren.Single().PersonId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("relationship", error.Message);
        db.Context.ChangeTracker.Clear();
        Assert.Equal(parentId, (await db.Models<OptionalChild>().SingleAsync()).PersonId);
        Assert.NotNull(await db.Models<Person>().SingleOrDefaultAsync());
    }

    [Fact]
    public async Task NativeTransactionsConcurrencyAndCancellationRemainAvailable()
    {
        await using var store = await Store.CreateAsync();
        await using var scope = store.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IClarion>();
        await using (var transaction = await db.Context.Database.BeginTransactionAsync())
        {
            db.Models<Person>().Add(new Person { Name = "Rollback" });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Models<Person>().GetAsync());
        var person = new Person { Version = "one" };
        db.Models<Person>().Add(person);
        await db.SaveChangesAsync();
        await using var second = store.Host.Services.CreateAsyncScope();
        var other = second.ServiceProvider.GetRequiredService<IClarion>();
        var stale = await other.Models<Person>().SingleAsync();
        person.Version = "two";
        await db.SaveChangesAsync();
        stale.Name = "Stale write";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        other.Context.ChangeTracker.Clear();
        stale = await other.Models<Person>().SingleAsync();
        person.Version = "three";
        await db.SaveChangesAsync();
        other.Models<Person>().Remove(stale);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => other.SaveChangesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Models<Person>().GetAsync(new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task FactoriesAndSeedersAreExplicitOrderedScopedAndHaveCompletionReceipts()
    {
        await using var store = await Store.CreateAsync(services => services.AddClarionSeeder<FirstSeeder>().AddClarionSeeder<SecondSeeder>());
        var factory = new ModelFactory<Person>(index => new Person { Name = index.ToString() });
        Assert.Empty(factory.Make(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Make(-1));
        await using var initial = store.Host.Services.CreateAsyncScope();
        Assert.Empty(await initial.ServiceProvider.GetRequiredService<IClarion>().Models<Person>().GetAsync());
        var directory = Directory.CreateTempSubdirectory("clarion-seed-");
        try
        {
            var path = Path.Combine(directory.FullName, "receipt.json");
            Assert.True(await store.Host.RunCaravelSeedersAsync(["--caravel-db-seed", path]));
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(receipt.RootElement.GetProperty("completed").GetBoolean());
            Assert.Equal(new[] { "first", "second" }, await initial.ServiceProvider.GetRequiredService<IClarion>().Models<Person>().OrderBy(row => row.Id).Select(row => row.Name).GetAsync());
            await Assert.ThrowsAsync<IOException>(() => store.Host.RunCaravelSeedersAsync(["--caravel-db-seed", path]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Host.Services.SeedClarionAsync("missing"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Host.Services.SeedClarionAsync(cancellationToken: new CancellationToken(true)));
            Assert.False(await store.Host.RunCaravelSeedersAsync([]));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void SqlServerTranslationPreservesTenantPredicateWhenIncludingSoftDeletedRows()
    {
        using var context = new AppDatabase(new DbContextOptionsBuilder<AppDatabase>().UseSqlServer("Server=localhost;Database=TranslationOnly;Integrated Security=true;TrustServerCertificate=true").Options, new Tenant());
        var ordinary = context.Set<Person>().ToQueryString();
        var trashed = context.Set<Person>().WithTrashed().ToQueryString();
        Assert.Contains("[DeletedAt] IS NULL", ordinary);
        Assert.Contains("[TenantId] =", trashed);
        Assert.DoesNotContain("[DeletedAt] IS NULL", trashed);
    }

    [Fact]
    public async Task FailedSeederCannotProduceSuccessfulCompletionReceipt()
    {
        await using var store = await Store.CreateAsync(services => services.AddClarionSeeder<FailingSeeder>());
        var directory = Directory.CreateTempSubdirectory("clarion-failed-seed-");
        var path = Path.Combine(directory.FullName, "receipt.json");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Host.RunCaravelSeedersAsync(["--caravel-db-seed", path]));
            Assert.Empty(await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<ArgumentException>(() => store.Host.RunCaravelSeedersAsync(["--caravel-db-seed"]));
            await Assert.ThrowsAsync<ArgumentException>(() => store.Host.RunCaravelSeedersAsync(["--caravel-db-seed", path, "--seeder"]));
        }
        finally { directory.Delete(recursive: true); }
    }

    public sealed class FailingSeeder : IClarionSeeder
    {
        public Task SeedAsync(IClarion database, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("Synthetic failure."));
    }

    public sealed class FirstSeeder : IClarionSeeder
    {
        public async Task SeedAsync(IClarion database, CancellationToken cancellationToken)
        {
            new ModelFactory<Person>(_ => new Person { Name = "first" }).AddTo(database);
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class SecondSeeder : IClarionSeeder
    {
        public async Task SeedAsync(IClarion database, CancellationToken cancellationToken)
        {
            Assert.Equal("first", (await database.Models<Person>().SingleAsync(cancellationToken)).Name);
            database.Models<Person>().Add(new Person { Name = "second" });
            await database.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed class Tenant { public string Id { get; set; } = "local"; }
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

[ClarionModel]
public sealed class Person : ITimestamped, ISoftDeletable
{
    public int Id { get; set; }
    public string Name { get; set; } = "Person";
    public string TenantId { get; set; } = "local";
    public string Version { get; set; } = "one";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public List<HardChild> HardChildren { get; set; } = [];
    public List<SoftChild> SoftChildren { get; set; } = [];
    public List<OptionalChild> OptionalChildren { get; set; } = [];
}

public sealed class OptionalChild
{
    public int Id { get; set; }
    public int? PersonId { get; set; }
}

public sealed class HardChild
{
    public int Id { get; set; }
    public int PersonId { get; set; }
}

public sealed class SoftChild : ISoftDeletable
{
    public int Id { get; set; }
    public int PersonId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AppDatabase(DbContextOptions<AppDatabase> options, Tenant tenant) : DbContext(options)
{
    public string TenantId => tenant.Id;
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddClarionModels(typeof(Person).Assembly);
        modelBuilder.Entity<Person>().HasQueryFilter("Tenant", model => model.TenantId == TenantId);
        modelBuilder.Entity<Person>().Property(model => model.Version).IsConcurrencyToken();
        modelBuilder.Entity<Person>().HasMany(model => model.HardChildren).WithOne().HasForeignKey(model => model.PersonId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Person>().HasMany(model => model.SoftChildren).WithOne().HasForeignKey(model => model.PersonId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Person>().HasMany(model => model.OptionalChildren).WithOne().HasForeignKey(model => model.PersonId).OnDelete(DeleteBehavior.ClientSetNull);
        modelBuilder.ApplyClarionConventions();
    }
}

internal sealed class Store(IHost host, SqliteConnection connection, ManualClock clock) : IAsyncDisposable
{
    public IHost Host => host;
    public ManualClock Clock => clock;
    public static async Task<Store> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new ManualClock();
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; })
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddScoped<Tenant>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddClarion<AppDatabase>(options => options.UseSqlite(connection));
                configure?.Invoke(services);
            });
        var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IClarion>().Context.Database.EnsureCreatedAsync();
        return new Store(host, connection, clock);
    }
    public async ValueTask DisposeAsync()
    {
        if (host is IAsyncDisposable disposable) await disposable.DisposeAsync(); else host.Dispose();
        await connection.DisposeAsync();
    }
}
