using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Caravel.IdentitySample;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Bounded_concurrent_ingestion_retries_and_workers_preserve_every_owners_results()
    {
        const int eventsPerOwner = 16;
        await using var factory = new IdentityFactory();
        factory.Settings["Caravel:Limits:Concurrency"] = "4";
        await factory.InitializeAsync();
        using var alice = factory.Client();
        using var bob = factory.Client();
        var clients = new[] { alice, bob };
        Assert.Equal(HttpStatusCode.NoContent, (await Login(alice, "alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(bob, "bob", Password)).StatusCode);
        await Csrf(alice);
        await Csrf(bob);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var receipts = new ConcurrentDictionary<(int Owner, int Event), Guid>();
        var newlyAccepted = 0;
        var processed = 0;
        var producersDone = 0;
        var worker = factory.Services.GetRequiredService<QueueWorker>();
        var workers = Enumerable.Range(0, 4).Select(async _ =>
        {
            while (Volatile.Read(ref producersDone) == 0 || Volatile.Read(ref processed) < eventsPerOwner * 2)
            {
                if (await worker.RunOnceAsync("counter", deadline.Token)) Interlocked.Increment(ref processed);
                else await Task.Delay(10, deadline.Token);
            }
        }).ToArray();
        try
        {
            var submissions = (from owner in Enumerable.Range(0, 2)
                               from index in Enumerable.Range(0, eventsPerOwner)
                               from repeat in Enumerable.Range(0, 3)
                               orderby (index * 7 + repeat * 13 + owner * 17) % 31
                               select (Owner: owner, Event: index)).ToArray();
            await Parallel.ForEachAsync(submissions,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = deadline.Token },
                async (submission, token) =>
                {
                    // Bounded producers retry the same logical event after native backpressure.
                    for (var attempt = 0; attempt < 50; attempt++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/counter/events")
                        { Content = JsonContent.Create(new { quantity = submission.Event + 1, ownerId = "forged-owner" }) };
                        request.Headers.Add("Idempotency-Key", $"burst-{submission.Event}");
                        using var response = await clients[submission.Owner].SendAsync(request, token);
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            await Task.Delay(20, token);
                            continue;
                        }
                        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                        var receipt = (await response.Content.ReadFromJsonAsync<CounterAccepted>(token))!;
                        if (!receipt.AlreadyEnqueued) Interlocked.Increment(ref newlyAccepted);
                        var expected = receipts.GetOrAdd(submission, receipt.JobId);
                        Assert.Equal(expected, receipt.JobId);
                        return;
                    }
                    Assert.Fail("The bounded producer exhausted its backpressure retries.");
                });
            Volatile.Write(ref producersDone, 1);
            await Task.WhenAll(workers);
        }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        Assert.Equal(eventsPerOwner * 2, newlyAccepted);
        Assert.Equal(eventsPerOwner * 2, receipts.Count);
        Assert.Equal(eventsPerOwner * 2, receipts.Values.Distinct().Count());
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        for (var owner = 0; owner < clients.Length; owner++)
        {
            Assert.Equal(new CounterSummary(eventsPerOwner, eventsPerOwner * (eventsPerOwner + 1) / 2),
                await clients[owner].GetFromJsonAsync<CounterSummary>("/counter/summary"));
            var rows = (await clients[owner].GetFromJsonAsync<CounterResponse[]>("/counter/results"))!;
            Assert.Equal(eventsPerOwner, rows.Length);
            Assert.Equal(receipts.Where(pair => pair.Key.Owner == owner).Select(pair => pair.Value).Order(),
                rows.Select(row => row.JobId).Order());
        }
        await using var queueDb = await factory.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        var jobs = await queueDb.Jobs.AsNoTracking().ToListAsync();
        Assert.Equal(eventsPerOwner * 2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(QueueJobState.Completed, job.State);
            Assert.Equal(1, job.Attempts);
        });
    }
}
