using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Caravel.Queues;

internal sealed class DatabaseQueue(IDbContextFactory<QueueDbContext> factory, JobRegistry registry,
    DatabaseQueueOptions options, TimeProvider clock) : IDatabaseQueue
{
    public async Task<EnqueueResult> EnqueueAsync<TJob>(TJob job, QueueDispatchOptions dispatch,
        CancellationToken cancellationToken = default) where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(dispatch);
        QueueValidation.Name(dispatch.Queue, 64, nameof(dispatch.Queue));
        QueueValidation.Text(dispatch.TenantId, 128, nameof(dispatch.TenantId));
        QueueValidation.Text(dispatch.IdempotencyKey, 128, nameof(dispatch.IdempotencyKey));
        cancellationToken.ThrowIfCancellationRequested();
        var registration = registry.For<TJob>();
        using var payload = new BoundedPayloadStream(options.MaxPayloadBytes);
        JsonSerializer.Serialize(payload, job, JobRegistry.Json);
        var serialized = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var row = new QueueJob
        {
            Id = Guid.NewGuid(), Queue = dispatch.Queue, TenantId = dispatch.TenantId,
            IdempotencyKey = dispatch.IdempotencyKey, JobType = registration.Name, Payload = serialized,
            DeduplicationKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new[] { dispatch.Queue, dispatch.TenantId, dispatch.IdempotencyKey }))),
            CreatedAt = now, AvailableAt = dispatch.NotBefore is { } due ? DueMillisecond(due) : now,
            MaxAttempts = options.MaxAttempts
        };
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.DeduplicationKey == row.DeduplicationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return Duplicate(existing, row);
        database.Jobs.Add(row);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            QueueDiagnostics.Add(QueueDiagnostics.Accepted, row.Queue);
            return new(row.Id, false);
        }
        catch (DbUpdateException)
        {
            // The unique key resolves concurrent enqueue attempts; unrelated database errors still propagate.
            existing = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.DeduplicationKey == row.DeduplicationKey, cancellationToken).ConfigureAwait(false);
            if (existing is null) throw;
            return Duplicate(existing, row);
        }
    }

    public async Task<QueueJobStatus?> GetStatusAsync(Guid jobId, string queue, string tenantId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(queue, tenantId);
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.Queue == queue && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        // Do not let a provider's case-insensitive collation broaden an application's tenant identity.
        return job is null || !string.Equals(job.TenantId, tenantId, StringComparison.Ordinal) ? null :
            new(job.Id, job.Queue, job.TenantId, job.State, job.Attempts, job.MaxAttempts,
                DateTimeOffset.FromUnixTimeMilliseconds(job.AvailableAt), job.LastFailure, job.ReplayCount);
    }

    public async Task<QueueLease?> TryClaimAsync(string queue, CancellationToken cancellationToken = default)
    {
        QueueValidation.Name(queue, 64, nameof(queue));
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var exhausted = await database.Jobs.Where(x => x.Queue == queue && x.State == QueueJobState.Leased
                && x.LeaseExpiresAt <= now && x.Attempts >= x.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, QueueJobState.DeadLetter)
                .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (long?)null)
                .SetProperty(x => x.LastFailure, QueueFailure.AttemptsExhausted), cancellationToken).ConfigureAwait(false);
        QueueDiagnostics.Add(QueueDiagnostics.DeadLettered, queue, exhausted, QueueFailure.AttemptsExhausted);

        // Bound contention work per poll. A losing worker retries on its next poll instead of spinning indefinitely.
        for (var contender = 0; contender < 8; contender++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var job = await database.Jobs.AsNoTracking().Where(x => x.Queue == queue && x.Attempts < x.MaxAttempts
                    && ((x.State == QueueJobState.Pending && x.AvailableAt <= now)
                        || (x.State == QueueJobState.Leased && x.LeaseExpiresAt <= now)))
                .OrderBy(x => x.AvailableAt).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (job is null) return null;
            var token = Guid.NewGuid();
            var expiry = checked(now + (long)options.LeaseDuration.TotalMilliseconds);
            var updated = await database.Jobs.Where(x => x.Id == job.Id && x.State == job.State
                    && x.LeaseToken == job.LeaseToken && x.Attempts == job.Attempts
                    && ((x.State == QueueJobState.Pending && x.AvailableAt <= now)
                        || (x.State == QueueJobState.Leased && x.LeaseExpiresAt <= now)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, QueueJobState.Leased)
                    .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseExpiresAt, expiry)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken).ConfigureAwait(false);
            if (updated == 0) continue;
            job.State = QueueJobState.Leased;
            job.LeaseToken = token;
            job.LeaseExpiresAt = expiry;
            job.Attempts++;
            QueueDiagnostics.Add(QueueDiagnostics.Claimed, queue);
            return new(job);
        }
        return null;
    }

    public async Task<bool> CompleteAsync(QueueLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var completed = await Owned(database, lease, now).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.State, QueueJobState.Completed).SetProperty(x => x.LeaseToken, (Guid?)null)
            .SetProperty(x => x.LeaseExpiresAt, (long?)null).SetProperty(x => x.LastFailure, (QueueFailure?)null),
            cancellationToken).ConfigureAwait(false) == 1;
        QueueDiagnostics.Add(completed ? QueueDiagnostics.Completed : QueueDiagnostics.LeaseLost, lease.Context.Queue);
        return completed;
    }

    public async Task<bool> FailAsync(QueueLease lease, QueueFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instant = clock.GetUtcNow();
        var now = instant.ToUnixTimeMilliseconds();
        var multiplier = 1L << Math.Min(lease.Context.Attempt - 1, 30);
        var delayTicks = options.RetryDelay.Ticks > options.MaxRetryDelay.Ticks / multiplier
            ? options.MaxRetryDelay.Ticks : options.RetryDelay.Ticks * multiplier;
        // Round only the final instant so neither a fractional clock nor delay makes a retry early.
        var available = DueMillisecond(instant.AddTicks(delayTicks));
        var recorded = await Owned(database, lease, now).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.State, x => x.Attempts >= x.MaxAttempts ? QueueJobState.DeadLetter : QueueJobState.Pending)
            .SetProperty(x => x.AvailableAt, available).SetProperty(x => x.LeaseToken, (Guid?)null)
            .SetProperty(x => x.LeaseExpiresAt, (long?)null).SetProperty(x => x.LastFailure, failure),
            cancellationToken).ConfigureAwait(false) == 1;
        if (recorded)
        {
            QueueDiagnostics.Add(QueueDiagnostics.Failed, lease.Context.Queue, failure: failure);
            QueueDiagnostics.Add(lease.Context.Attempt >= lease.MaxAttempts ? QueueDiagnostics.DeadLettered : QueueDiagnostics.Retried,
                lease.Context.Queue, failure: failure);
        }
        else QueueDiagnostics.Add(QueueDiagnostics.LeaseLost, lease.Context.Queue);
        return recorded;
    }

    public async Task<bool> ReplayAsync(Guid jobId, string queue, string tenantId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(queue, tenantId);
        await using var database = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await database.Jobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.Queue == queue && x.TenantId == tenantId && x.State == QueueJobState.DeadLetter,
            cancellationToken).ConfigureAwait(false);
        if (row is null || !string.Equals(row.TenantId, tenantId, StringComparison.Ordinal)) return false;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var replayed = await database.Jobs.Where(x => x.Id == jobId && x.State == QueueJobState.DeadLetter && x.ReplayCount == row.ReplayCount)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, QueueJobState.Pending)
                .SetProperty(x => x.Attempts, 0).SetProperty(x => x.AvailableAt, now)
                .SetProperty(x => x.LastFailure, (QueueFailure?)null).SetProperty(x => x.ReplayCount, x => x.ReplayCount + 1),
                cancellationToken).ConfigureAwait(false) == 1;
        if (replayed) QueueDiagnostics.Add(QueueDiagnostics.Replayed, queue);
        return replayed;
    }

    private static IQueryable<QueueJob> Owned(QueueDbContext database, QueueLease lease, long now) =>
        database.Jobs.Where(x => x.Id == lease.Context.JobId && x.State == QueueJobState.Leased
            && x.LeaseToken == lease.Token && x.LeaseExpiresAt > now);

    private static EnqueueResult Duplicate(QueueJob existing, QueueJob requested)
    {
        if (existing.Queue != requested.Queue || existing.TenantId != requested.TenantId
            || existing.IdempotencyKey != requested.IdempotencyKey || existing.JobType != requested.JobType
            || existing.Payload != requested.Payload)
            throw new QueueIdempotencyConflictException();
        QueueDiagnostics.Add(QueueDiagnostics.Deduplicated, requested.Queue);
        return new(existing.Id, true);
    }

    private static void ValidateIdentity(string queue, string tenantId)
    {
        QueueValidation.Name(queue, 64, nameof(queue));
        QueueValidation.Text(tenantId, 128, nameof(tenantId));
    }

    private static long DueMillisecond(DateTimeOffset due)
    {
        var floor = due.ToUnixTimeMilliseconds();
        if (due.UtcTicks % TimeSpan.TicksPerMillisecond == 0) return floor;
        if (floor == DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            throw new ArgumentOutOfRangeException(nameof(QueueDispatchOptions.NotBefore),
                "The due time must round up to a representable UTC millisecond.");
        return floor + 1;
    }

    private sealed class BoundedPayloadStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (Length + count > limit) throw new ArgumentException("The serialized queue payload exceeds the configured byte limit.");
        }
    }
}
