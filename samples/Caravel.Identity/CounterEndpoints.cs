using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

public sealed record CounterEvent([property: Range(1, 1000)] int Quantity);
public sealed record CounterAccepted(Guid JobId, bool AlreadyEnqueued);
public sealed record CounterResponse(Guid JobId, int Quantity, DateTimeOffset RecordedAt);
public sealed record CounterSummary(long Count, long Quantity);

public static class CounterEndpoints
{
    public static void MapCounterEndpoints(this WebApplication app)
    {
        var counter = app.MapGroup("/counter").RequireAuthorization();
        counter.MapPost("/events", async (CounterEvent message, HttpContext context, IDatabaseQueue queue, CancellationToken token) =>
        {
            if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values) || values.Count != 1 ||
                string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 128 || values[0]!.Any(char.IsControl))
                return Results.Problem(statusCode: 400, title: "Supply one Idempotency-Key with 1-128 nonblank characters and no controls.");
            try
            {
                var receipt = await queue.EnqueueAsync(message,
                    new("counter", context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, values[0]!), token);
                return Results.Accepted($"/counter/jobs/{receipt.JobId}", new CounterAccepted(receipt.JobId, receipt.AlreadyEnqueued));
            }
            catch (QueueIdempotencyConflictException)
            {
                return Results.Problem(statusCode: 409, title: "This Idempotency-Key was already used for a different event.");
            }
        }).AddEndpointFilter<RequireAntiforgery>();

        counter.MapGet("/jobs/{id:guid}", async (Guid id, ClaimsPrincipal user, IDatabaseQueue queue, CancellationToken token) =>
        {
            var status = await queue.GetStatusAsync(id, "counter", user.FindFirstValue(ClaimTypes.NameIdentifier)!, token);
            return status is null ? Results.NotFound() : Results.Ok(new { status.JobId, status.State, status.Attempts });
        }).DisableCookieRedirect();

        counter.MapGet("/results", async (DateTimeOffset? from, DateTimeOffset? to, int? offset, int? limit,
            ClaimsPrincipal user, IdentityContext db, TimeProvider clock, CancellationToken token) =>
        {
            var skip = offset ?? 0;
            var take = limit ?? 50;
            if (!TryWindow(from, to, clock, out var minimum, out var maximum) || skip is < 0 or > 10000 || take is < 1 or > 100)
                return Results.Problem(statusCode: 400, title: "Use a UTC window up to 31 days, offset 0-10000 and limit 1-100.");
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var rows = await db.CounterResults.AsNoTracking().Where(row => row.OwnerId == owner && row.RecordedAt >= minimum && row.RecordedAt < maximum)
                .OrderBy(row => row.RecordedAt).ThenBy(row => row.JobId).Skip(skip).Take(take).ToListAsync(token);
            return Results.Ok(rows.Select(row => new CounterResponse(row.JobId, row.Quantity, DateTimeOffset.FromUnixTimeMilliseconds(row.RecordedAt))));
        }).DisableCookieRedirect();

        counter.MapGet("/summary", async (DateTimeOffset? from, DateTimeOffset? to,
            ClaimsPrincipal user, IdentityContext db, TimeProvider clock, CancellationToken token) =>
        {
            if (!TryWindow(from, to, clock, out var minimum, out var maximum))
                return Results.Problem(statusCode: 400, title: "Use a UTC window of more than zero and at most 31 days.");
            var owner = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var summary = await db.CounterResults.AsNoTracking()
                .Where(row => row.OwnerId == owner && row.RecordedAt >= minimum && row.RecordedAt < maximum)
                .GroupBy(_ => 1).Select(group => new CounterSummary(group.LongCount(), group.Sum(row => (long)row.Quantity)))
                .SingleOrDefaultAsync(token);
            return Results.Ok(summary ?? new CounterSummary(0, 0));
        }).DisableCookieRedirect();
    }

    private static bool TryWindow(DateTimeOffset? from, DateTimeOffset? to, TimeProvider clock, out long minimum, out long maximum)
    {
        minimum = maximum = 0;
        var end = to ?? clock.GetUtcNow();
        if (end.Offset != TimeSpan.Zero || (from is null && end < DateTimeOffset.MinValue.AddDays(1))) return false;
        var start = from ?? end.AddDays(-1);
        if (start.Offset != TimeSpan.Zero || end <= start || end - start > TimeSpan.FromDays(31)) return false;
        // Integer receipt times satisfy [start, end) when both query bounds round up.
        // An equal rounded pair is a valid empty window; the upper bound is never converted to a date.
        minimum = CeilingMillisecond(start);
        maximum = CeilingMillisecond(end);
        return true;
    }

    private static long CeilingMillisecond(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds()
        + (instant.UtcTicks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
}

/// <summary>The saved result is the idempotency receipt. Its key prevents redelivery from incrementing a counter twice.</summary>
public sealed class CounterEventHandler(IdentityContext db, TimeProvider clock) : IJobHandler<CounterEvent>
{
    public async Task HandleAsync(CounterEvent message, JobContext context, CancellationToken cancellationToken)
    {
        if (message.Quantity is < 1 or > 1000 || context.Queue != "counter" || string.IsNullOrWhiteSpace(context.TenantId))
            throw new InvalidOperationException("Invalid counter job envelope.");
        var existing = await db.CounterResults.AsNoTracking().SingleOrDefaultAsync(row => row.JobId == context.JobId, cancellationToken);
        if (existing is not null) { ValidateReceipt(existing, message, context); return; }
        var result = new CounterResult { JobId = context.JobId, OwnerId = context.TenantId, Quantity = message.Quantity,
            RecordedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() };
        db.CounterResults.Add(result);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.Entry(result).State = EntityState.Detached;
            existing = await db.CounterResults.AsNoTracking().SingleOrDefaultAsync(row => row.JobId == context.JobId, cancellationToken);
            if (existing is null) throw;
            ValidateReceipt(existing, message, context);
        }
    }

    private static void ValidateReceipt(CounterResult existing, CounterEvent message, JobContext context)
    {
        if (!string.Equals(existing.OwnerId, context.TenantId, StringComparison.Ordinal) || existing.Quantity != message.Quantity)
            throw new InvalidOperationException("The existing counter receipt does not match this job.");
    }
}
