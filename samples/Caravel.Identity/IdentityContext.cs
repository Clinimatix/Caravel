using Caravel.Clarion;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

public sealed class IdentityContext(DbContextOptions<IdentityContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<CounterResult> CounterResults => Set<CounterResult>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Note>().Property(note => note.Text).HasMaxLength(500);
        builder.Entity<Note>().HasOne<IdentityUser>().WithMany().HasForeignKey(note => note.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Note>().HasIndex(note => new { note.OwnerId, note.Id });
        builder.Entity<CounterResult>().HasKey(result => result.JobId);
        builder.Entity<CounterResult>().HasOne<IdentityUser>().WithMany().HasForeignKey(result => result.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Entity<CounterResult>().HasIndex(result => new { result.OwnerId, result.RecordedAt, result.JobId });
        builder.AddWorkItems();
        builder.ApplyClarionConventions();
    }
}

public sealed class CounterResult
{
    public Guid JobId { get; set; }
    public string OwnerId { get; set; } = "";
    public int Quantity { get; set; }
    public long RecordedAt { get; set; }
}

public sealed class Note : ITimestamped
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
