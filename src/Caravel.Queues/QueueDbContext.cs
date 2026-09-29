using Microsoft.EntityFrameworkCore;

namespace Caravel.Queues;

/// <summary>Configure a relational provider and manage migrations explicitly; this package never creates a schema.</summary>
public class QueueDbContext(DbContextOptions<QueueDbContext> options) : DbContext(options)
{
    public DbSet<QueueJob> Jobs => Set<QueueJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<QueueJob>();
        job.ToTable("CaravelQueueJobs");
        job.HasKey(x => x.Id);
        job.Property(x => x.Queue).HasMaxLength(64).IsRequired();
        job.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        job.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        job.Property(x => x.JobType).HasMaxLength(128).IsRequired();
        job.Property(x => x.DeduplicationKey).HasMaxLength(64).IsUnicode(false).IsRequired();
        job.Property(x => x.Payload).IsRequired();
        job.HasIndex(x => x.DeduplicationKey).IsUnique();
        job.HasIndex(x => new { x.Queue, x.State, x.AvailableAt });
    }
}

/// <summary>Persisted infrastructure state. UTC times use Unix milliseconds for portable relational ordering.</summary>
public sealed class QueueJob
{
    public Guid Id { get; set; }
    public string Queue { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string DeduplicationKey { get; set; } = "";
    public string JobType { get; set; } = "";
    public string Payload { get; set; } = "";
    public QueueJobState State { get; set; }
    public long CreatedAt { get; set; }
    public long AvailableAt { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public Guid? LeaseToken { get; set; }
    public long? LeaseExpiresAt { get; set; }
    public QueueFailure? LastFailure { get; set; }
    public int ReplayCount { get; set; }
}
