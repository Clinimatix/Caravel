# Clarion

Clarion is Caravel's data layer. It gives you a friendly way to query and save plain C# models, plus timestamps, soft deletes, factories, and seeders, all built on EF Core 10. Everything EF Core can do is still available when you need it.

Clarion is the optional `Clinimatix.Caravel.Clarion` package. It works in any .NET app, including console apps and background services, not just Caravel web apps. It never creates databases or runs migrations on its own.

## Getting started

Add the EF Core provider for your database (SQL Server, PostgreSQL, or SQLite) to your app, then register Clarion with it. Clarion doesn't bundle any provider. MariaDB isn't supported yet; see [database providers](DATABASE-PROVIDERS.md).

```csharp
using Caravel.Clarion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddClarion<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("App")));
using var host = builder.Build();

await using var scope = host.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<IClarion>();
var users = await db.Models<User>()
    .Where(user => user.Active)
    .OrderBy(user => user.Name)
    .GetAsync();
```

Each DI scope (for a web app, each request) gets its own `IClarion` session, which is disposed when the scope ends. A few things to know:

- Register your context once, through `AddClarion`. For additional databases, register those contexts the usual EF Core way.
- If you need other services while configuring the context, use the overload `AddClarion<TContext>((services, options) => ...)`.
- Sessions aren't thread-safe. Give each concurrent job its own scope.

## Models

Models are plain C# classes. If your context already declares `DbSet<T>` properties or maps entities with `modelBuilder.Entity<T>()`, they work as-is. You can also mark classes with `[ClarionModel]` and have Clarion find them in the assemblies you name:

```csharp
[ClarionModel]
public sealed class User : ITimestamped, ISoftDeletable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddClarionModels(typeof(User).Assembly);
        modelBuilder.Entity<User>().Property(user => user.Name).HasMaxLength(100);
        // Finish model discovery and application mappings before applying conventions.
        modelBuilder.ApplyClarionConventions();
    }
}
```

Clarion only looks in the assemblies you pass, and only picks up concrete (non-abstract) classes with the attribute. All the usual EF Core configuration (relationships, conversions, JSON columns, and so on) still works.

Soft deletes have a few limits: they don't work on owned or keyless entities, and with inheritance the base entity must implement `ISoftDeletable`. The timestamp and soft-delete properties must be regular mapped properties with the names and types the interfaces declare.

**A note on dates.** The examples use UTC `DateTimeOffset`. Databases handle it differently: SQL Server stores it natively, PostgreSQL requires UTC offsets, and SQLite can't sort every `DateTimeOffset` expression. Add EF Core value conversions if your database needs them, and test against the database you'll actually use. Clarion doesn't choose column types for you.

## Querying and saving

`db.Models<T>()` returns EF Core's `DbSet<T>`, so every LINQ query works. `GetAsync()` is shorthand for `ToListAsync()` and returns a read-only list. `db.Context` is the underlying `DbContext`, and `db.SaveChangesAsync()` saves through it.

For `Include`, `AsNoTracking`, projections, raw SQL, bulk updates, and everything else, use EF Core directly. Clarion doesn't replace any of it.

```csharp
db.Models<User>().Add(new User { Name = "Synthetic user", Active = true });
await db.SaveChangesAsync(cancellationToken);

await using var transaction = await db.Context.Database.BeginTransactionAsync(cancellationToken);
// Make and save the application's changes.
await transaction.CommitAsync(cancellationToken);
```

Clarion never retries saves automatically. For retries, follow your EF Core provider's guidance on execution strategies. For optimistic concurrency, configure concurrency tokens as usual and handle `DbUpdateConcurrencyException` in your code.

Database rollback does not rewind EF's change tracker. After a failed save, review or reload the tracked state before retrying; after rolling back a transaction containing successful saves, use a fresh scope or clear and reload the context. Timestamps and soft-delete state are prepared before the write and can remain in memory when a write fails.

## Timestamps and soft deletes

Implement `ITimestamped` and Clarion fills in the timestamps whenever you save:

- **New records:** `CreatedAt` is set to the current UTC time unless you've already set it (handy for importing history). `UpdatedAt` is set to now.
- **Updates:** `UpdatedAt` is set to now, and the original `CreatedAt` is kept.
- **Testing:** the time comes from the `TimeProvider` registered in DI, so you can register a fake clock in tests.

Implement `ISoftDeletable` and removing a record sets `DeletedAt` instead of deleting the row. Other unsaved edits to that record are discarded rather than saved along with the delete, with one exception: if you manage your own concurrency token (a version number, say) and change it before removing the record, that change is kept, so stale writers are still detected. Database-generated tokens such as SQL Server `rowversion` work as they normally do in EF Core.

Soft-deleted records are hidden from queries by an EF Core named filter called `ClarionSoftDelete`. To see them or bring them back:

- `WithTrashed()` includes soft-deleted records. It turns off only the soft-delete filter; your other filters, such as tenant filters, stay on.
- `RestoreAsync<T>(keys)` finds a soft-deleted record (still respecting your other filters) and clears `DeletedAt`. It returns `null` if nothing matches. Call `SaveChangesAsync` afterward. Restoring works even if your context uses no-tracking queries by default.

```csharp
var trashed = await db.Models<User>().WithTrashed().GetAsync(cancellationToken);
var restored = await db.RestoreAsync<User>([userId], cancellationToken);
await db.SaveChangesAsync(cancellationToken);
```

Use `RestoreAsync` rather than `Find` or `IgnoreQueryFilters()`; those can bypass your tenant filters.

**Filters hide data; they don't protect it.** Query filters only affect what queries return. Your app still needs to check ownership and permissions on inserts, updates, raw SQL, and admin operations.

### Relationships and deletion

**In short:** Clarion won't combine a soft delete with a hard delete, or with changes to the deleted record's child relationships, in the same save. It throws instead, so nothing is lost silently. Save those changes separately.

The details:

- **Loaded children** that EF would delete along with the parent are soft-deleted too, if they're soft-deletable.
- **Children that aren't loaded** are left alone. Because the parent row is updated rather than deleted, the database's own cascade delete never fires. Decide whether children should stay visible on their own, and set up their filters and relationships to match.
- **Soft and hard deletes can't share a save.** If one save contains a soft delete and *any* hard delete, even an unrelated one, Clarion refuses the whole save before anything reaches the database. Save hard deletes separately.
- **Detaching or moving children can't share a save.** If deleting a parent would make EF clear loaded children's foreign keys, or you move children to another parent in the same save, Clarion refuses. Make those changes in a separate save.
- **Restoring a parent doesn't restore its children.**
- **A refused save leaves your changes in the context**, so you can inspect them or reload.

`ExecuteDelete`, `ExecuteUpdate`, raw SQL, and database triggers skip all of this and behave exactly as they do in plain EF Core, including permanently deleting rows. Use them deliberately.

## Factories and seeders

A factory builds test records from a function you supply. `Make` creates them in memory; `AddTo` adds them to the session. Neither one saves, and neither invents random personal data.

```csharp
var factory = new ModelFactory<User>(index => new User
{
    Name = $"Synthetic user {index + 1}", Active = true
});
var users = factory.AddTo(db, count: 5);
await db.SaveChangesAsync(cancellationToken);
```

A seeder fills a database with starting data. Register each one; they run in the order you register them, and each saves its own work:

```csharp
builder.Services.AddClarionSeeder<DemoSeeder>();

public sealed class DemoSeeder : IClarionSeeder
{
    public async Task SeedAsync(IClarion database, CancellationToken cancellationToken)
    {
        if (await database.Models<User>().AnyAsync(cancellationToken)) return;
        new ModelFactory<User>(index => new User { Name = $"Synthetic {index}" })
            .AddTo(database, 3);
        await database.SaveChangesAsync(cancellationToken);
    }
}
```

Run seeders from code with `await host.Services.SeedClarionAsync(cancellationToken: token)`, or run just one with `seeder: "DemoSeeder"`. To run them from the command line with `caravel db:seed`, add the hook described in [Bosun data commands](BOSUN-DATA.md#seeding).

Seeders can use constructor injection like any other scoped service. Keep in mind:

- **Make seeders safe to re-run.** The `AnyAsync` check above is fine for a demo, but it won't stop two seed processes that run at the same time.
- **There's no shared transaction.** If a later seeder fails, data saved by earlier seeders stays.
- Errors and cancellation are passed straight through to the caller.

## Migrations

Clarion uses standard EF Core migrations; it doesn't have its own migration system or run migrations automatically. [Bosun's data commands](BOSUN-DATA.md) wrap the EF Core tool and keep migrations, seeders, and factories under `Database/`.

For which databases are supported and how they differ, see [database providers](DATABASE-PROVIDERS.md).
