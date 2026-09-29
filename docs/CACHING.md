# Caching

Caravel apps can use .NET's caching services directly. Start with `IDistributedCache` when you want the same application code to work with an in-memory or shared database cache. There is no separate Caravel cache package or facade yet.

## Start in memory

```csharp
builder.Services.AddDistributedMemoryCache();
```

This stores values in the current process. It doesn't share them between app instances, and restarting loses them.

Inject `IDistributedCache` from `Microsoft.Extensions.Caching.Distributed`:

```csharp
await cache.SetStringAsync("public:catalog:v1", serializedCatalog,
    new DistributedCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    }, cancellationToken);

var cached = await cache.GetStringAsync("public:catalog:v1", cancellationToken);
await cache.RemoveAsync("public:catalog:v1", cancellationToken);
```

For a plain Generic Host, add `Microsoft.Extensions.Caching.Memory`; ASP.NET Core already supplies it through the shared framework.

## Share values through SQL Server

Add `Microsoft.Extensions.Caching.SqlServer` to the application, aligned with the framework's .NET LTS version, then replace the memory registration:

```csharp
builder.Services.AddDistributedSqlServerCache(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("Cache")
        ?? throw new InvalidOperationException("Set the Cache connection string.");
    options.SchemaName = "dbo";
    options.TableName = "CacheEntries";
});
```

Create the backing table with Microsoft's `sql-cache` tool as part of your deployment. Caravel doesn't create it or reuse Clarion tables. Follow the [distributed caching guide](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/distributed?view=aspnetcore-10.0) for provider setup and tooling.

## Keep the cache replaceable

Treat a miss as normal and rebuild the value from its authoritative source. A cache write isn't durable acceptance of an event or job. Decide how a cache outage affects each endpoint rather than hiding every infrastructure error.

For per-user data, include the tenant or account and a data version in the cache key, and use an unambiguous separator. Never cache one user's filtered results under a key another user could hit. Don't cache passwords or credentials just because entries expire eventually.

Set an expiration, keep values small, and remove affected entries after you save changes. `IDistributedCache` doesn't make read-compute-write atomic, so two requests can fill the same entry at once. A Caravel `RememberAsync` helper, tags and locking are ideas for the [roadmap](ROADMAP.md).
