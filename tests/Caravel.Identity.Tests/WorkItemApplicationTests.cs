using System.Net;
using System.Net.Http.Json;
using Caravel.IdentitySample;
using Caravel.Mail;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Work_item_commands_require_current_workspace_permission_antiforgery_and_a_strong_revision()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        var (workspace, item, other) = await SeedWorkItems(factory);
        using var alice = factory.Client();
        using var page = await alice.GetAsync("/work-items");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.CacheControl!.NoStore);
        Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        foreach (var asset in new[] { "/work-items.js", "/work-items.css" })
            Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(asset)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync($"/workspaces/{workspace}/items")).StatusCode);
        await Login(alice, "alice", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/workspaces/{other}/items")).StatusCode);
        await Csrf(alice);
        Assert.Equal(HttpStatusCode.Forbidden, (await Complete(alice, other, item, "foreign", "\"1\"")).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Complete(alice, workspace, item, "missing", null)).StatusCode);
        foreach (var tag in new[] { "*", "W/\"1\"", "1", "\"01\"", "\"1\", \"2\"", "\"0\"" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Complete(alice, workspace, item, "malformed", tag)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Complete(alice, workspace, item, "stale", "\"2\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Complete(alice, workspace, item, "invalid", "\"1\"", "")).StatusCode);
        alice.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await Complete(alice, workspace, item, "no-csrf", "\"1\"")).StatusCode);
        using var reader = factory.Client();
        await Login(reader, "admin", Password); await Csrf(reader);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/workspaces/{workspace}/items/{item}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Complete(reader, workspace, item, "read-only", "\"1\"")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        Assert.Empty(await db.Set<WorkItemReceipt>().ToListAsync());
        Assert.Empty(await db.Set<OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task Work_item_retry_survives_a_lost_response_and_projects_current_state_after_reauthorization()
    {
        await using var database = new IdentityTestDatabase();
        Guid workspace, item, other, receiptId;
        await using (var first = new IdentityFactory(database))
        {
            await first.InitializeAsync();
            (workspace, item, other) = await SeedWorkItems(first);
            using var alice = first.Client(); await Login(alice, "alice", Password); await Csrf(alice);
            // The server commits, but the caller does not use the response to acknowledge its command.
            using var response = await Complete(alice, workspace, item, "lost-response", "\"1\"");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            receiptId = (await response.Content.ReadFromJsonAsync<WorkItemOutcome>())!.ReceiptId;
            await using var scope = first.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            (await db.Set<WorkItem>().SingleAsync(x => x.Id == item)).Title = "Current authorized title";
            await db.SaveChangesAsync();
        }
        await using var restarted = new IdentityFactory(database);
        using var client = restarted.Client(); await Login(client, "alice", Password); await Csrf(client);
        using var retry = await Complete(client, workspace, item, "lost-response", "\"1\"");
        var outcome = (await retry.Content.ReadFromJsonAsync<WorkItemOutcome>())!;
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(receiptId, outcome.ReceiptId);
        Assert.Equal("Current authorized title", outcome.Item.Title);
        Assert.Equal(2, outcome.Item.Revision);
        Assert.Equal("\"2\"", retry.Headers.ETag!.Tag);
        Assert.True(retry.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Conflict, (await Complete(client, workspace, item, "lost-response", "\"1\"", "Changed intent")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Complete(client, workspace, item, "new", "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Complete(client, workspace, item, "new", "\"2\"")).StatusCode);
        await using (var scope = restarted.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            Assert.Single(await db.Set<WorkItemReceipt>().ToListAsync());
            Assert.Single(await db.Set<WorkItemChange>().ToListAsync());
            var intent = Assert.Single(await db.Set<OutboxMessage>().ToListAsync());
            Assert.DoesNotContain("Completed after review", intent.Payload);
            Assert.DoesNotContain("@", intent.Payload);
            Assert.Equal(workspace.ToString("D"), intent.TenantId);
            (await db.Set<WorkspaceMember>().SingleAsync(x => x.WorkspaceId == workspace && x.UserId == "alice")).Active = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Complete(client, workspace, item, "lost-response", "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/workspaces/{workspace}/receipts/{receiptId}")).StatusCode);
        using var bob = restarted.Client(); await Login(bob, "bob", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/workspaces/{other}/receipts/{receiptId}")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_work_item_retries_commit_one_change_receipt_and_intent()
    {
        await using var factory = new IdentityFactory(); await factory.InitializeAsync();
        var (workspace, item, _) = await SeedWorkItems(factory);
        using var first = factory.Client(); using var second = factory.Client();
        await Login(first, "alice", Password); await Csrf(first);
        await Login(second, "alice", Password); await Csrf(second);
        var responses = await Task.WhenAll(Complete(first, workspace, item, "simultaneous", "\"1\""),
            Complete(second, workspace, item, "simultaneous", "\"1\""));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var outcomes = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<WorkItemOutcome>()));
        Assert.Equal(outcomes[0]!.ReceiptId, outcomes[1]!.ReceiptId);
        foreach (var response in responses) response.Dispose();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        Assert.Single(await db.Set<WorkItemChange>().ToListAsync());
        Assert.Single(await db.Set<WorkItemReceipt>().ToListAsync());
        Assert.Single(await db.Set<OutboxMessage>().ToListAsync());
        Assert.Equal(2, (await db.Set<WorkItem>().SingleAsync(x => x.Id == item)).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_saving_rolls_back_domain_history_receipt_and_outbox_and_can_be_retried(bool wrapped)
    {
        var fault = new WorkItemSaveFailure(wrapped);
        await using var factory = new IdentityFactory { ExtraServices = services =>
            services.AddDbContext<IdentityContext>(options => options.AddInterceptors(fault)) };
        await factory.InitializeAsync(); var (workspace, item, _) = await SeedWorkItems(factory);
        using var client = factory.Client(); await Login(client, "alice", Password); await Csrf(client);
        fault.Enabled = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Complete(client, workspace, item, "rollback", "\"1\"")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            Assert.False((await db.Set<WorkItem>().SingleAsync(x => x.Id == item)).Completed);
            Assert.Empty(await db.Set<WorkItemReceipt>().ToListAsync());
            Assert.Empty(await db.Set<WorkItemChange>().ToListAsync());
            Assert.Empty(await db.Set<OutboxMessage>().ToListAsync());
        }
        fault.Enabled = false;
        Assert.Equal(HttpStatusCode.OK, (await Complete(client, workspace, item, "rollback", "\"1\"")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reference_notice_rechecks_membership_and_recovers_a_lost_worker_acknowledgement(bool revoke)
    {
        await using var factory = new IdentityFactory(); await factory.InitializeAsync();
        var (workspace, item, _) = await SeedWorkItems(factory);
        using var client = factory.Client(); await Login(client, "alice", Password); await Csrf(client);
        using var response = await Complete(client, workspace, item, "notice", "\"1\"");
        var receipt = (await response.Content.ReadFromJsonAsync<WorkItemOutcome>())!;
        if (revoke)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            (await db.Set<WorkspaceMember>().SingleAsync(x => x.WorkspaceId == workspace && x.UserId == "alice")).Active = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await factory.Services.GetRequiredService<OutboxRelay<IdentityContext>>().RunOnceAsync());
        Assert.Equal(0, await factory.Services.GetRequiredService<OutboxRelay<IdentityContext>>().RunOnceAsync());
        var queue = factory.Services.GetRequiredService<IDatabaseQueue>();
        var lease = (await queue.TryClaimAsync("work-items"))!;
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WorkItemNoticeHandler>().HandleAsync(new(receipt.ReceiptId), lease.Context, default);
        // Simulate process loss after the application receipt was saved, before queue acknowledgement.
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("work-items"));
        Assert.Equal(QueueJobState.Completed, (await queue.GetStatusAsync(lease.Context.JobId, "work-items", workspace.ToString("D")))!.State);
        Assert.Equal(revoke ? 0 : 1, factory.Services.GetRequiredService<MailCapture>().Messages.Count);
        await using var check = factory.Services.CreateAsyncScope();
        var saved = await check.ServiceProvider.GetRequiredService<IdentityContext>().Set<WorkItemReceipt>().SingleAsync();
        Assert.Equal(revoke ? NoticeState.Suppressed : NoticeState.Submitted, saved.Notice);
    }

    private static async Task<(Guid Workspace, Guid Item, Guid Other)> SeedWorkItems(IdentityFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
        var workspace = Guid.NewGuid(); var other = Guid.NewGuid(); var item = Guid.NewGuid();
        db.AddRange(new Workspace { Id = workspace, Name = "North" }, new Workspace { Id = other, Name = "South" });
        db.AddRange(new WorkspaceMember { WorkspaceId = workspace, UserId = "alice", Active = true, CanComplete = true },
            new WorkspaceMember { WorkspaceId = workspace, UserId = "admin", Active = true },
            new WorkspaceMember { WorkspaceId = other, UserId = "bob", Active = true, CanComplete = true });
        db.Add(new WorkItem { Id = item, WorkspaceId = workspace, Title = "Prepare a summary" });
        await db.SaveChangesAsync(); return (workspace, item, other);
    }

    private static async Task<HttpResponseMessage> Complete(HttpClient client, Guid workspace, Guid item, string key, string? revision,
        string comment = "Completed after review")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/workspaces/{workspace}/items/{item}/complete")
        { Content = JsonContent.Create(new { comment, workspaceId = Guid.NewGuid(), actorId = "bob" }) };
        request.Headers.Add("Idempotency-Key", key);
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        return await client.SendAsync(request);
    }

    private sealed class WorkItemSaveFailure(bool wrapped) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<WorkItemReceipt>().Any())
            {
                var error = new DbUpdateException("Synthetic failure after SQL writes, before transaction commit.");
                throw wrapped ? new InvalidOperationException("Synthetic provider wrapper.", error) : error;
            }
            return ValueTask.FromResult(result);
        }
    }
}
