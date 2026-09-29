namespace Caravel.Queues;

/// <summary>Identity comes from the persisted envelope, never from properties in a job payload.</summary>
public sealed record JobContext(Guid JobId, string Queue, string TenantId, int Attempt);

public interface IJobHandler<TJob> where TJob : notnull
{
    Task HandleAsync(TJob job, JobContext context, CancellationToken cancellationToken);
}

/// <summary>The application must authorize the queue and tenant before enqueueing or replaying.</summary>
public sealed record QueueDispatchOptions(string Queue, string TenantId, string IdempotencyKey,
    DateTimeOffset? NotBefore = null);

public sealed record EnqueueResult(Guid JobId, bool AlreadyEnqueued);

/// <summary>An existing idempotency key was reused with different job content.</summary>
public sealed class QueueIdempotencyConflictException()
    : InvalidOperationException("The idempotency key is already associated with a different job.") { }

public enum QueueJobState { Pending, Leased, Completed, DeadLetter }
public enum QueueFailure { HandlerFailed, InvalidPayload, UnknownJobType, AttemptsExhausted, LeaseExpired }

public sealed record QueueJobStatus(Guid JobId, string Queue, string TenantId, QueueJobState State,
    int Attempts, int MaxAttempts, DateTimeOffset AvailableAt, QueueFailure? LastFailure, int ReplayCount);

/// <summary>A capability held by a worker. Acknowledgement requires its current, unexpired lease token.</summary>
public sealed class QueueLease
{
    internal QueueLease(QueueJob job, DateTimeOffset startedAt)
    {
        StartedAt = startedAt;
        Context = new(job.Id, job.Queue, job.TenantId, job.Attempts);
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(job.LeaseExpiresAt!.Value);
        Token = job.LeaseToken!.Value;
        JobType = job.JobType;
        Payload = job.Payload;
        MaxAttempts = job.MaxAttempts;
    }

    private QueueLease(QueueLease previous, DateTimeOffset expiry)
    {
        Context = previous.Context;
        ExpiresAt = expiry;
        StartedAt = previous.StartedAt;
        Token = previous.Token;
        JobType = previous.JobType;
        Payload = previous.Payload;
        MaxAttempts = previous.MaxAttempts;
    }

    internal QueueLease WithExpiry(DateTimeOffset expiry) => new(this, expiry);
    internal DateTimeOffset StartedAt { get; }
    public JobContext Context { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal Guid Token { get; }
    internal string JobType { get; }
    internal string Payload { get; }
    internal int MaxAttempts { get; }
}

/// <summary>Trusted infrastructure API, not an authorization boundary or a transactional outbox.</summary>
public interface IDatabaseQueue
{
    Task<EnqueueResult> EnqueueAsync<TJob>(TJob job, QueueDispatchOptions options,
        CancellationToken cancellationToken = default) where TJob : notnull;
    Task<QueueJobStatus?> GetStatusAsync(Guid jobId, string queue, string tenantId,
        CancellationToken cancellationToken = default);
    Task<QueueLease?> TryClaimAsync(string queue, CancellationToken cancellationToken = default);
    /// <summary>Extend a current unexpired lease, bounded by MaxLeaseLifetime from its original claim.</summary>
    Task<QueueLease?> RenewAsync(QueueLease lease, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(QueueLease lease, CancellationToken cancellationToken = default);
    Task<bool> FailAsync(QueueLease lease, QueueFailure failure, CancellationToken cancellationToken = default);
    Task<bool> ReplayAsync(Guid jobId, string queue, string tenantId, CancellationToken cancellationToken = default);
}
