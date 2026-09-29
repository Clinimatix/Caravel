using System.Net;
using System.Net.Http.Json;
using Caravel.IdentitySample;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Original_identity_schema_upgrade_preserves_accounts_and_notes_and_adds_durable_ingestion()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync(initializeQueue: false, identityMigration: "InitialIdentity");
        var noteId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            db.Notes.Add(new Note { Id = noteId, OwnerId = "alice", Text = "Preserved across the counter upgrade" });
            await db.SaveChangesAsync();
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.False(db.Database.HasPendingModelChanges());
            // Roll back the new, still-empty result table before admitting work, then upgrade again.
            var initial = db.Database.GetMigrations().Single(migration => migration.EndsWith("_InitialIdentity", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(initial);
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            db.ChangeTracker.Clear();
            Assert.Equal("Preserved across the counter upgrade", (await db.Notes.SingleAsync()).Text);
            Assert.Equal(4, await db.Users.CountAsync());
            await db.Database.MigrateAsync();
            Assert.Empty(await db.CounterResults.ToListAsync());
            await using var queueDb = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
            await queueDb.Database.MigrateAsync();
            Assert.Single(await queueDb.Database.GetAppliedMigrationsAsync());
            Assert.False(queueDb.Database.HasPendingModelChanges());
        }
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        Assert.Equal("Preserved across the counter upgrade", (await client.GetFromJsonAsync<NoteResponse>($"/notes/{noteId}"))!.Text);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(client, "after-upgrade", 9)).StatusCode);
        Assert.True(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("counter"));
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new CounterSummary(1, 9), await client.GetFromJsonAsync<CounterSummary>("/counter/summary"));
    }

    [Fact]
    public async Task Counter_ingestion_requires_authentication_antiforgery_valid_quantity_and_key()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(client, "anonymous", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/counter/results")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        client.DefaultRequestHeaders.Remove("RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(client, "csrf", 1)).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/counter/events", new { quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(client, new string('x', 129), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(client, "zero", 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Submit(client, "large", 1001)).StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/counter/events")
        { Content = JsonContent.Create(new { quantity = 1, ignored = new string('x', 17000) }) };
        await oversized.Content.LoadIntoBufferAsync();
        oversized.Headers.Add("Idempotency-Key", "oversized");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        using var empty = new HttpRequestMessage(HttpMethod.Post, "/counter/events") { Content = JsonContent.Create(new { }) };
        empty.Headers.Add("Idempotency-Key", "missing-quantity");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(empty)).StatusCode);
        await using var queueDb = await factory.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        Assert.Empty(await queueDb.Jobs.ToListAsync());
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(client, "maximum", 1000)).StatusCode);
        Assert.Equal("alice", (await queueDb.Jobs.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Durable_acceptance_deduplicates_and_keeps_status_results_and_reports_owner_scoped()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var alice = factory.Client();
        using var bob = factory.Client();
        await Login(alice, "alice", Password);
        await Login(bob, "bob", Password);
        await Csrf(alice);
        await Csrf(bob);
        var accepted = await Submit(alice, "same-key", 5);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var first = (await accepted.Content.ReadFromJsonAsync<CounterAccepted>())!;
        var retry = await Submit(alice, "same-key", 5);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var duplicate = (await retry.Content.ReadFromJsonAsync<CounterAccepted>())!;
        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.AlreadyEnqueued);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(alice, "same-key", 6)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/counter/jobs/{first.JobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/counter/jobs/{first.JobId}")).StatusCode);
        var second = (await (await Submit(bob, "same-key", 7)).Content.ReadFromJsonAsync<CounterAccepted>())!;
        Assert.NotEqual(first.JobId, second.JobId);
        var worker = factory.Services.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("counter"));
        Assert.True(await worker.RunOnceAsync("counter"));
        Assert.False(await worker.RunOnceAsync("counter"));
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new CounterSummary(1, 5), await alice.GetFromJsonAsync<CounterSummary>("/counter/summary?ownerId=bob"));
        Assert.Equal(new CounterSummary(1, 7), await bob.GetFromJsonAsync<CounterSummary>("/counter/summary"));
        Assert.Equal(first.JobId, Assert.Single((await alice.GetFromJsonAsync<CounterResponse[]>("/counter/results"))!).JobId);
        Assert.Equal(second.JobId, Assert.Single((await bob.GetFromJsonAsync<CounterResponse[]>("/counter/results"))!).JobId);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await db.Jobs.CountAsync());
        Assert.All(await db.Jobs.ToListAsync(), row => Assert.Equal(QueueJobState.Completed, row.State));
    }

    [Fact]
    public async Task Result_commit_before_lost_ack_survives_redelivery_without_double_counting()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Login(client, "alice", Password);
        await Csrf(client);
        var accepted = (await (await Submit(client, "lost-ack", 4)).Content.ReadFromJsonAsync<CounterAccepted>())!;
        var queue = factory.Services.GetRequiredService<IDatabaseQueue>();
        var lease = (await queue.TryClaimAsync("counter"))!;
        Assert.Equal(accepted.JobId, lease.Context.JobId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CounterEventHandler>();
            await handler.HandleAsync(new CounterEvent(4), lease.Context, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new CounterEvent(5), lease.Context, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new CounterEvent(4), lease.Context with { TenantId = "bob" }, CancellationToken.None));
        }
        // Simulate a process dying after its result commit, without acknowledging its queue lease.
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("counter"));
        var status = (await queue.GetStatusAsync(accepted.JobId, "counter", "alice"))!;
        Assert.Equal(QueueJobState.Completed, status.State);
        Assert.Equal(2, status.Attempts);
        Assert.Equal(new CounterSummary(1, 4), await client.GetFromJsonAsync<CounterSummary>("/counter/summary"));
        await using var resultScope = factory.Services.CreateAsyncScope();
        Assert.Single(await resultScope.ServiceProvider.GetRequiredService<IdentityContext>().CounterResults.ToListAsync());
    }

    [Fact]
    public async Task Queue_storage_failure_does_not_return_durable_acceptance()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync(initializeQueue: false);
        using var client = factory.Client();
        await Login(client, "alice", Password);
        await Csrf(client);
        var failed = await Submit(client, "missing-queue-schema", 1);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("CaravelQueueJobs", await failed.Content.ReadAsStringAsync());
        await using var queueDb = await factory.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        await queueDb.Database.MigrateAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(client, "missing-queue-schema", 1)).StatusCode);
        Assert.True(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("counter"));
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new CounterSummary(1, 1), await client.GetFromJsonAsync<CounterSummary>("/counter/summary"));
    }

    [Fact]
    public async Task Host_restart_recovers_pending_work_and_lost_ack_after_sessions_are_revoked()
    {
        await using var database = new IdentityTestDatabase();
        var clock = new TestClock();
        Guid committedJob;
        Guid pendingJob;
        await using (var original = new IdentityFactory(database, clock))
        {
            await original.InitializeAsync();
            using var client = original.Client();
            Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
            await Csrf(client);
            committedJob = (await (await Submit(client, "committed-before-stop", 4)).Content.ReadFromJsonAsync<CounterAccepted>())!.JobId;
            var queue = original.Services.GetRequiredService<IDatabaseQueue>();
            var lease = (await queue.TryClaimAsync("counter"))!;
            Assert.Equal(committedJob, lease.Context.JobId);
            await using (var scope = original.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<CounterEventHandler>()
                    .HandleAsync(new CounterEvent(4), lease.Context, CancellationToken.None);
            // The receipt is committed, but acknowledgement has not reached the queue.
            pendingJob = (await (await Submit(client, "pending-before-stop", 6)).Content.ReadFromJsonAsync<CounterAccepted>())!.JobId;
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/auth/logout-all", null)).StatusCode);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(client, "after-revocation", 100)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/counter/summary")).StatusCode);
        }

        // Dispose the whole host and construct a new service provider over the same owned database.
        clock.Advance(TimeSpan.FromMinutes(6));
        await using var restarted = new IdentityFactory(database, clock);
        using var freshClient = restarted.Client();
        Assert.Equal("Healthy", await freshClient.GetStringAsync("/health/ready"));
        Assert.Equal(HttpStatusCode.NoContent, (await Login(freshClient, "alice", Password)).StatusCode);
        var worker = restarted.Services.GetRequiredService<QueueWorker>();
        Assert.True(await worker.RunOnceAsync("counter"));
        Assert.True(await worker.RunOnceAsync("counter"));
        Assert.False(await worker.RunOnceAsync("counter"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new CounterSummary(2, 10), await freshClient.GetFromJsonAsync<CounterSummary>("/counter/summary"));
        var results = (await freshClient.GetFromJsonAsync<CounterResponse[]>("/counter/results"))!;
        Assert.Equal(new[] { committedJob, pendingJob }.Order(), results.Select(result => result.JobId).Order());
        var recoveredQueue = restarted.Services.GetRequiredService<IDatabaseQueue>();
        var committed = (await recoveredQueue.GetStatusAsync(committedJob, "counter", "alice"))!;
        Assert.Equal(QueueJobState.Completed, committed.State);
        Assert.Equal(2, committed.Attempts);
        var pending = (await recoveredQueue.GetStatusAsync(pendingJob, "counter", "alice"))!;
        Assert.Equal(QueueJobState.Completed, pending.State);
        Assert.Equal(1, pending.Attempts);
        await using var queueDb = await restarted.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await queueDb.Jobs.CountAsync());
    }

    [Fact]
    public async Task Expired_handler_losing_a_receipt_insert_race_reconciles_without_double_counting()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Login(client, "alice", Password);
        await Csrf(client);
        var accepted = (await (await Submit(client, "overlapping-receipts", 4)).Content.ReadFromJsonAsync<CounterAccepted>())!;
        var queue = factory.Services.GetRequiredService<IDatabaseQueue>();
        var staleLease = (await queue.TryClaimAsync("counter"))!;
        Assert.Equal(accepted.JobId, staleLease.Context.JobId);
        var gate = new ReceiptInsertGate();
        var staleOptions = new DbContextOptionsBuilder<IdentityContext>();
        factory.Database.Configure(staleOptions);
        await using var staleDb = new IdentityContext(staleOptions.AddInterceptors(gate).Options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var staleHandler = new CounterEventHandler(staleDb, factory.Clock);
        var staleInsert = staleHandler.HandleAsync(new CounterEvent(4), staleLease.Context, deadline.Token);
        try
        {
            // The stale handler has read no receipt and is paused immediately before its INSERT.
            await gate.Entered.Task.WaitAsync(deadline.Token);
            factory.Clock.Advance(TimeSpan.FromMinutes(6));
            Assert.True(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("counter", deadline.Token));
        }
        finally
        {
            gate.Release.TrySetResult();
            await staleInsert.WaitAsync(deadline.Token);
        }
        Assert.Equal(1, gate.InsertFailures);
        Assert.False(await queue.CompleteAsync(staleLease));
        var status = (await queue.GetStatusAsync(accepted.JobId, "counter", "alice"))!;
        Assert.Equal(QueueJobState.Completed, status.State);
        Assert.Equal(2, status.Attempts);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new CounterSummary(1, 4), await client.GetFromJsonAsync<CounterSummary>("/counter/summary"));
        var receipt = Assert.Single((await client.GetFromJsonAsync<CounterResponse[]>("/counter/results"))!);
        Assert.Equal(accepted.JobId, receipt.JobId);
        Assert.Equal(4, receipt.Quantity);
        Assert.False(await factory.Services.GetRequiredService<QueueWorker>().RunOnceAsync("counter"));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<IdentityContext>().CounterResults.ToListAsync());
    }

    [Theory]
    [InlineData(0L, 0L, 10000L, 1)]
    [InlineData(0L, -10000L, 0L, 0)]
    [InlineData(0L, 1L, 10000L, 0)]
    [InlineData(0L, -10000L, 1L, 1)]
    [InlineData(0L, 1L, 2L, 0)]
    [InlineData(-1L, 1L, 10000L, 0)]
    [InlineData(-1L, -10000L, 1L, 1)]
    [InlineData(253402300799999L, 0L, 9999L, 1)]
    public async Task Report_windows_preserve_exact_half_open_bounds_over_millisecond_receipts(
        long recordedMilliseconds, long startTicks, long endTicks, int expectedCount)
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        var jobId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            db.CounterResults.Add(new CounterResult { JobId = jobId, OwnerId = "alice", Quantity = 4, RecordedAt = recordedMilliseconds });
            await db.SaveChangesAsync();
        }
        using var client = factory.Client();
        await Login(client, "alice", Password);
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(recordedMilliseconds);
        var from = Uri.EscapeDataString(instant.AddTicks(startTicks).ToString("O"));
        var to = Uri.EscapeDataString(instant.AddTicks(endTicks).ToString("O"));
        using var summary = await client.GetAsync($"/counter/summary?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(new CounterSummary(expectedCount, expectedCount * 4), await summary.Content.ReadFromJsonAsync<CounterSummary>());
        using var results = await client.GetAsync($"/counter/results?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        var rows = (await results.Content.ReadFromJsonAsync<CounterResponse[]>())!;
        Assert.Equal(expectedCount, rows.Length);
        if (expectedCount != 0) Assert.Equal(jobId, Assert.Single(rows).JobId);
    }

    [Fact]
    public async Task Result_pagination_and_utc_report_windows_are_bounded_and_half_open()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Login(client, "alice", Password);
        await Csrf(client);
        for (var index = 0; index < 3; index++) await Submit(client, $"event-{index}", index + 1);
        var worker = factory.Services.GetRequiredService<QueueWorker>();
        while (await worker.RunOnceAsync("counter")) { }
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(factory.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var from = Uri.EscapeDataString(instant.ToString("O"));
        var to = Uri.EscapeDataString(instant.AddSeconds(1).ToString("O"));
        Assert.Equal(new CounterSummary(3, 6), await client.GetFromJsonAsync<CounterSummary>($"/counter/summary?from={from}&to={to}"));
        var prior = Uri.EscapeDataString(instant.AddSeconds(-1).ToString("O"));
        Assert.Equal(new CounterSummary(0, 0), await client.GetFromJsonAsync<CounterSummary>($"/counter/summary?from={prior}&to={from}"));
        Assert.Single((await client.GetFromJsonAsync<CounterResponse[]>($"/counter/results?from={from}&to={to}&offset=1&limit=1"))!);
        foreach (var query in new[] { "offset=-1", "offset=10001", "limit=0", "limit=101", "limit=wrong",
            "from=2026-01-01T00:00:00Z&to=2026-03-01T00:00:00Z", "from=2026-01-01T00:00:00Z&to=2026-01-01T00:00:00Z",
            "from=2026-01-01T01:00:00%2B01:00&to=2026-01-02T00:00:00Z", "to=0001-01-01T00:00:00Z" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/counter/results?" + query)).StatusCode);
    }

    private static Task<HttpResponseMessage> Submit(HttpClient client, string key, int quantity)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/counter/events")
        {
            Content = JsonContent.Create(new { quantity, ownerId = "forged-owner", tenantId = "forged-tenant" })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private sealed class ReceiptInsertGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InsertFailures { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Assert.IsType<DbUpdateException>(eventData.Exception);
            InsertFailures++;
            return Task.CompletedTask;
        }
    }
}
