using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

public static class WorkItemEndpoints
{
    public static void MapWorkItemEndpoints(this WebApplication app)
    {
        var workspaces = app.MapGroup("/workspaces").RequireAuthorization();
        workspaces.MapGet("/", async (ClaimsPrincipal user, IdentityContext db, CancellationToken token) =>
        {
            var actor = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            // Recheck ordinal identifiers after provider comparison (SQL collations can be case-insensitive).
            var memberships = await db.Set<WorkspaceMember>().AsNoTracking()
                .Where(x => x.UserId == actor && x.Active).Take(100).ToListAsync(token);
            var ids = memberships.Where(x => string.Equals(x.UserId, actor, StringComparison.Ordinal)).Select(x => x.WorkspaceId).ToArray();
            return Results.Ok(await db.Set<Workspace>().AsNoTracking().Where(x => ids.Contains(x.Id))
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(token));
        }).DisableCookieRedirect();

        workspaces.MapGet("/{workspace:guid}/items", async (Guid workspace, ClaimsPrincipal user, IdentityContext db, CancellationToken token) =>
        {
            var member = await Member(db, workspace, user.FindFirstValue(ClaimTypes.NameIdentifier)!, token);
            if (member is null) return Results.Forbid();
            var items = await db.Set<WorkItem>().AsNoTracking().Where(x => x.WorkspaceId == workspace)
                .OrderBy(x => x.Id).Take(50).Select(x => new WorkItemView(x.Id, x.Title, x.Revision, x.Completed)).ToListAsync(token);
            return Results.Ok(new { member.CanComplete, items });
        }).DisableCookieRedirect();

        workspaces.MapGet("/{workspace:guid}/items/{id:guid}", async (Guid workspace, Guid id, HttpContext http, IdentityContext db, CancellationToken token) =>
        {
            if (await Member(db, workspace, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, token) is null) return Results.Forbid();
            var item = await db.Set<WorkItem>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, token);
            if (item is null) return Results.NotFound();
            http.Response.Headers.ETag = RevisionTag(item.Revision);
            return Results.Ok(View(item));
        }).DisableCookieRedirect();

        workspaces.MapGet("/{workspace:guid}/receipts/{id:guid}", async (Guid workspace, Guid id, ClaimsPrincipal user, IdentityContext db, CancellationToken token) =>
        {
            var actor = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (await Member(db, workspace, actor, token) is null) return Results.Forbid();
            var receipt = await db.Set<WorkItemReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, token);
            // Receipts are private to the initiating actor, even within a workspace.
            if (receipt is null || !string.Equals(receipt.ActorId, actor, StringComparison.Ordinal)) return Results.NotFound();
            var item = await db.Set<WorkItem>().AsNoTracking().SingleAsync(x => x.WorkspaceId == workspace && x.Id == receipt.ItemId, token);
            return Results.Ok(Outcome(receipt, item));
        }).DisableCookieRedirect();

        workspaces.MapPost("/{workspace:guid}/items/{id:guid}/complete", async (Guid workspace, Guid id, CompleteWorkItem command,
            HttpContext http, IServiceScopeFactory scopes, CancellationToken token) =>
        {
            if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1
                || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128 || keys[0]!.Any(char.IsControl))
                return Problem(400, "Supply one Idempotency-Key of 1-128 nonblank characters without controls.");
            if (!http.Request.Headers.TryGetValue("If-Match", out var tags)) return Problem(428, "Supply the item's If-Match revision.");
            if (tags.Count != 1 || !TryRevision(tags[0], out var revision)) return Problem(400, "Use one strong quoted positive revision, such as \"1\".");
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var comment = command.Comment.Trim();
            var scopeKey = Hash(new[] { workspace.ToString("D"), actor, "complete.v1", id.ToString("D"), keys[0]! });
            var fingerprint = Hash(new[] { id.ToString("D"), revision.ToString(CultureInfo.InvariantCulture), comment });

            // Only database work is retried. Every attempt has a fresh context and transaction.
            // An ambiguous commit is recovered by finding its receipt, never by caching an HTTP response.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                    var member = await Member(db, workspace, actor, token);
                    if (member is not { CanComplete: true }) return Results.Forbid();
                    var item = await db.Set<WorkItem>().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, token);
                    if (item is null) return Results.NotFound();
                    var existing = await db.Set<WorkItemReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == scopeKey, token);
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                            return Problem(409, "This Idempotency-Key describes a different command.");
                        http.Response.Headers.ETag = RevisionTag(item.Revision);
                        return Results.Ok(Outcome(existing, item));
                    }
                    if (item.Revision != revision) return Problem(412, "The item changed. Reload it before starting a new command.");
                    if (item.Completed) return Problem(409, "The item is already complete.");
                    if (item.Revision == int.MaxValue) return Problem(409, "The item's revision limit has been reached.");
                    item.Completed = true;
                    item.Revision++;
                    var receipt = new WorkItemReceipt { Id = Guid.NewGuid(), ScopeKey = scopeKey, Fingerprint = fingerprint,
                        WorkspaceId = workspace, ItemId = id, ActorId = actor,
                        CreatedAt = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds() };
                    db.Add(receipt);
                    db.Add(new WorkItemChange { ReceiptId = receipt.Id, Comment = comment, FromRevision = revision });
                    await scope.ServiceProvider.GetRequiredService<QueueOutbox<IdentityContext>>()
                        .StageAsync(new WorkItemNotice(receipt.Id), new("work-items", workspace.ToString("D"), receipt.Id.ToString("N")), token);
                    await db.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                    http.Response.Headers.ETag = RevisionTag(item.Revision);
                    http.Response.Headers.Location = $"/workspaces/{workspace:D}/receipts/{receipt.Id:D}";
                    return Results.Ok(Outcome(receipt, item));
                }
                catch (Exception error) when (error is DbUpdateException or DbException
                    || error is InvalidOperationException { InnerException: DbUpdateException or DbException })
                {
                    // Non-retrying EF server strategies can wrap transient database failures.
                    // Constraint races, serialization failures and uncertain commits are safe to revisit.
                    // Persistent faults remain visible; never infer success from an exception.
                    if (attempt == 2) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), token);
                }
            }
            http.Response.Headers.RetryAfter = "1";
            return Problem(503, "The command could not be confirmed. Retry the same command and key.");
        }).AddEndpointFilter<RequireAntiforgery>();
    }

    private static async Task<WorkspaceMember?> Member(IdentityContext db, Guid workspace, string actor, CancellationToken token)
    {
        var member = await db.Set<WorkspaceMember>().AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.UserId == actor, token);
        return member is { Active: true } && string.Equals(member.UserId, actor, StringComparison.Ordinal) ? member : null;
    }

    private static bool TryRevision(string? tag, out int revision)
    {
        revision = 0;
        return tag is { Length: >= 3 and <= 12 } && tag[0] == '"' && tag[^1] == '"'
            && int.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision > 0 && tag == RevisionTag(revision);
    }

    private static string Hash(string[] values) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static string RevisionTag(int revision) => "\"" + revision.ToString(CultureInfo.InvariantCulture) + "\"";
    private static WorkItemView View(WorkItem item) => new(item.Id, item.Title, item.Revision, item.Completed);
    private static WorkItemOutcome Outcome(WorkItemReceipt receipt, WorkItem item) => new(receipt.Id, View(item), receipt.Notice.ToString());
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}
