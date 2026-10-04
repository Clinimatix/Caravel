using System.ComponentModel.DataAnnotations;
using Caravel.Mail;
using Caravel.Queues;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

// These are application models, not a framework tenancy or workflow service.
public sealed class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class WorkspaceMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = "";
    public bool Active { get; set; }
    public bool CanComplete { get; set; }
}

public sealed class WorkItem
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Title { get; set; } = "";
    public int Revision { get; set; } = 1;
    public bool Completed { get; set; }
}

public enum NoticeState { Pending, Submitted, Suppressed }

public sealed class WorkItemReceipt
{
    public Guid Id { get; set; }
    public string ScopeKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public Guid ItemId { get; set; }
    public string ActorId { get; set; } = "";
    public long CreatedAt { get; set; }
    public NoticeState Notice { get; set; }
}

public sealed class WorkItemChange
{
    public Guid ReceiptId { get; set; }
    public string Comment { get; set; } = "";
    public int FromRevision { get; set; }
}

public sealed record CompleteWorkItem([property: Required, StringLength(160)] string Comment);
public sealed record WorkItemView(Guid Id, string Title, int Revision, bool Completed);
public sealed record WorkItemOutcome(Guid ReceiptId, WorkItemView Item, string Notice);
public sealed record WorkItemNotice(Guid ReceiptId);

public static class WorkItemModel
{
    public static void AddWorkItems(this ModelBuilder builder)
    {
        builder.Entity<Workspace>().Property(x => x.Name).HasMaxLength(100);
        var member = builder.Entity<WorkspaceMember>();
        member.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
        member.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        var item = builder.Entity<WorkItem>();
        item.Property(x => x.Title).HasMaxLength(160);
        item.Property(x => x.Revision).IsConcurrencyToken();
        item.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        item.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        var receipt = builder.Entity<WorkItemReceipt>();
        receipt.Property(x => x.ScopeKey).HasMaxLength(64).IsUnicode(false);
        receipt.Property(x => x.Fingerprint).HasMaxLength(64).IsUnicode(false);
        receipt.HasIndex(x => x.ScopeKey).IsUnique();
        receipt.HasIndex(x => new { x.WorkspaceId, x.ItemId });
        receipt.HasOne<WorkItem>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ItemId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        var change = builder.Entity<WorkItemChange>();
        change.HasKey(x => x.ReceiptId);
        change.Property(x => x.Comment).HasMaxLength(160);
        change.HasOne<WorkItemReceipt>().WithOne().HasForeignKey<WorkItemChange>(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.AddCaravelOutbox();
    }
}

/// <summary>Factory for relay reads. Request/handler writes use the normal scoped IdentityContext.</summary>
public sealed class IdentityContextFactory(string provider, string connection) : IDbContextFactory<IdentityContext>
{
    public IdentityContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityContext>();
        SampleDatabase.Configure(options, provider, connection);
        return new(options.Options);
    }
}

/// <summary>Rechecks current membership before submitting a generic notice. Provider acceptance can repeat after a crash.</summary>
public sealed class WorkItemNoticeHandler(IdentityContext db, IMailSender mail) : IJobHandler<WorkItemNotice>
{
    public async Task HandleAsync(WorkItemNotice job, JobContext context, CancellationToken cancellationToken)
    {
        if (context.Queue != "work-items" || !Guid.TryParseExact(context.TenantId, "D", out var workspace))
            throw new InvalidOperationException("Invalid work item envelope.");
        var receipt = await db.Set<WorkItemReceipt>().SingleOrDefaultAsync(x => x.Id == job.ReceiptId && x.WorkspaceId == workspace, cancellationToken)
            ?? throw new InvalidOperationException("Work item receipt is unavailable.");
        if (receipt.Notice != NoticeState.Pending) return;
        var member = await db.Set<WorkspaceMember>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.UserId == receipt.ActorId, cancellationToken);
        var actor = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.ActorId, cancellationToken);
        var item = await db.Set<WorkItem>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == receipt.ItemId, cancellationToken);
        if (member is not { Active: true, CanComplete: true } || !string.Equals(member.UserId, receipt.ActorId, StringComparison.Ordinal)
            || actor is not { EmailConfirmed: true } || !string.Equals(actor.Id, receipt.ActorId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(actor.Email) || item is not { Completed: true })
        {
            receipt.Notice = NoticeState.Suppressed;
        }
        else
        {
            // No title, comment or recipient address is stored in the job payload.
            await mail.SendAsync(new CaravelMailMessage { To = [actor.Email], Subject = "Work item updated",
                TextBody = "A work item was updated. Sign in to the application to view it." }, cancellationToken);
            receipt.Notice = NoticeState.Submitted;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
