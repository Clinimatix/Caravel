using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Caravel.Queues;

/// <summary>A durable dispatch intent stored in the application's transaction. Treat it as infrastructure data.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Queue { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string DeduplicationKey { get; set; } = "";
    public string JobType { get; set; } = "";
    public string Payload { get; set; } = "";
    public long CreatedAt { get; set; }
    public long AvailableAt { get; set; }
    public int MaxAttempts { get; set; }
    public long? DispatchedAt { get; set; }
    public Guid? QueueJobId { get; set; }

    internal QueueJob ToJob() => new()
    {
        Id = Id, Queue = Queue, TenantId = TenantId, IdempotencyKey = IdempotencyKey,
        DeduplicationKey = DeduplicationKey, JobType = JobType, Payload = Payload,
        CreatedAt = CreatedAt, AvailableAt = AvailableAt, MaxAttempts = MaxAttempts
    };
}

public sealed record OutboxStageResult(Guid OutboxId, bool AlreadyStaged);

/// <summary>Stages jobs in the scoped application context; never saves or commits on the caller's behalf.</summary>
public sealed class QueueOutbox<TContext> where TContext : DbContext
{
    private readonly TContext database;
    private readonly DatabaseQueue queue;
    internal QueueOutbox(TContext database, DatabaseQueue queue) => (this.database, this.queue) = (database, queue);

    public async Task<OutboxStageResult> StageAsync<TJob>(TJob job, QueueDispatchOptions dispatch,
        CancellationToken cancellationToken = default) where TJob : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = queue.Prepare(job, dispatch);
        var set = database.Set<OutboxMessage>();
        var existing = set.Local.FirstOrDefault(x => x.DeduplicationKey == prepared.DeduplicationKey)
            ?? await set.AsNoTracking().SingleOrDefaultAsync(x => x.DeduplicationKey == prepared.DeduplicationKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            DatabaseQueue.Duplicate(existing.ToJob(), prepared);
            return new(existing.Id, true);
        }
        set.Add(new OutboxMessage
        {
            Id = prepared.Id, Queue = prepared.Queue, TenantId = prepared.TenantId,
            IdempotencyKey = prepared.IdempotencyKey, DeduplicationKey = prepared.DeduplicationKey,
            JobType = prepared.JobType, Payload = prepared.Payload, CreatedAt = prepared.CreatedAt,
            AvailableAt = prepared.AvailableAt, MaxAttempts = prepared.MaxAttempts
        });
        return new(prepared.Id, false);
    }
}

/// <summary>Forwards committed intents to the database queue. Queue deduplication makes concurrent relays and crashes safe.</summary>
public sealed class OutboxRelay<TContext> where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> factory;
    private readonly DatabaseQueue queue;
    private readonly TimeProvider clock;
    internal OutboxRelay(IDbContextFactory<TContext> factory, DatabaseQueue queue, TimeProvider clock)
        => (this.factory, this.queue, this.clock) = (factory, queue, clock);

    /// <summary>Returns the number of receipts marked dispatched. Infrastructure errors propagate for host supervision.</summary>
    public async Task<int> RunOnceAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await database.Set<OutboxMessage>().AsNoTracking().Where(x => x.DispatchedAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var message in pending)
        {
            var accepted = await queue.EnqueuePreparedAsync(message.ToJob(), cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            // If this update fails after enqueue, the same persisted key resolves to the same job on retry.
            count += await database.Set<OutboxMessage>().Where(x => x.Id == message.Id && x.DispatchedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DispatchedAt, now)
                    .SetProperty(x => x.QueueJobId, accepted.JobId), cancellationToken).ConfigureAwait(false);
        }
        return count;
    }
}

public static class OutboxExtensions
{
    /// <summary>Include this in the application model, then generate and apply its migration deliberately.</summary>
    public static ModelBuilder AddCaravelOutbox(this ModelBuilder builder)
    {
        var row = builder.Entity<OutboxMessage>();
        row.ToTable("CaravelOutboxMessages");
        row.HasKey(x => x.Id);
        row.Property(x => x.Queue).HasMaxLength(64).IsRequired();
        row.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        row.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        row.Property(x => x.JobType).HasMaxLength(128).IsRequired();
        row.Property(x => x.DeduplicationKey).HasMaxLength(64).IsUnicode(false).IsRequired();
        row.Property(x => x.Payload).IsRequired();
        row.HasIndex(x => x.DeduplicationKey).IsUnique();
        row.HasIndex(x => new { x.DispatchedAt, x.CreatedAt });
        return builder;
    }

    /// <summary>Requires AddCaravelDatabaseQueue and an application-configured IDbContextFactory for TContext.</summary>
    public static IServiceCollection AddCaravelOutbox<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.AddScoped(provider => new QueueOutbox<TContext>(provider.GetRequiredService<TContext>(),
            provider.GetRequiredService<DatabaseQueue>()));
        services.AddSingleton(provider => new OutboxRelay<TContext>(provider.GetRequiredService<IDbContextFactory<TContext>>(),
            provider.GetRequiredService<DatabaseQueue>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Opt into a bounded sequential relay; multiple hosts can relay the same application outbox.</summary>
    public static IServiceCollection AddCaravelOutboxRelay<TContext>(this IServiceCollection services,
        TimeSpan? pollInterval = null, int batchSize = 100) where TContext : DbContext
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        services.AddSingleton<IHostedService>(provider => new OutboxHostedService<TContext>(
            provider.GetRequiredService<OutboxRelay<TContext>>(), interval, batchSize, provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}

internal sealed class OutboxHostedService<TContext>(OutboxRelay<TContext> relay, TimeSpan interval, int batchSize,
    TimeProvider clock) : BackgroundService where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = await relay.RunOnceAsync(batchSize, stoppingToken).ConfigureAwait(false);
            if (dispatched < batchSize) await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
