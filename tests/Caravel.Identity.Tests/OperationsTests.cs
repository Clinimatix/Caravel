using System.Net;
using System.Net.Http.Json;
using Caravel.Events;
using Caravel.IdentitySample;
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Identity.Tests;

public sealed partial class IdentityApplicationTests
{
    [Fact]
    public async Task Probes_report_missing_database_and_schema_without_creating_or_leaking_them()
    {
        await using var factory = new IdentityFactory();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var unavailable = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("Unhealthy", await unavailable.Content.ReadAsStringAsync());
        Assert.False(await factory.Database.ExistsAsync());
        await factory.InitializeAsync(initializeQueue: false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        await using var queue = await factory.Services.GetRequiredService<IDbContextFactory<QueueDbContext>>().CreateDbContextAsync();
        await queue.Database.MigrateAsync();
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        await queue.Database.ExecuteSqlRawAsync("ALTER TABLE \"CaravelQueueJobs\" DROP COLUMN \"Payload\"");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
    }

    [Fact]
    public async Task Liveness_does_not_validate_cookies_against_an_unavailable_identity_schema()
    {
        await using var factory = new IdentityFactory();
        await factory.InitializeAsync();
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, "alice", Password)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityContext>().Database.ExecuteSqlRawAsync("ALTER TABLE \"AspNetUsers\" DROP COLUMN \"PasswordHash\"");
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Shared_login_quota_rejects_generically_recovers_and_leaves_other_endpoints_available()
    {
        await using var factory = new IdentityFactory();
        factory.Settings["Caravel:Limits:LoginPermitLimit"] = "1";
        factory.Settings["Caravel:Limits:LoginWindowSeconds"] = "2";
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Csrf(client);
        var credentials = new { userName = "unknown@example.invalid", password = "synthetic invalid" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", credentials)).StatusCode);
        var limited = await client.PostAsJsonAsync("/auth/login", credentials);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.DoesNotContain("unknown@example.invalid", await limited.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/csrf")).StatusCode);
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        HttpStatusCode status;
        do
        {
            await Task.Delay(100, deadline.Token);
            status = (await client.PostAsJsonAsync("/auth/login", credentials, deadline.Token)).StatusCode;
        } while (status == HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Global_concurrency_rejects_excess_endpoint_work_and_recovers_when_the_request_finishes()
    {
        await using var factory = new IdentityFactory();
        var gate = new NoteGate();
        factory.Settings["Caravel:Limits:Concurrency"] = "1";
        factory.ExtraServices = services => services.AddSingleton(gate).AddEventListener<NoteCreated, WaitForNote>();
        await factory.InitializeAsync();
        using var client = factory.Client();
        await Login(client, "alice", Password);
        await Csrf(client);
        var pending = client.PostAsJsonAsync("/notes/", new { text = "Hold one synthetic request" });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/")).StatusCode);
            Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
            Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.Created, (await pending).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    public sealed class NoteGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WaitForNote(NoteGate gate) : IEventListener<NoteCreated>
    {
        public async Task HandleAsync(NoteCreated message, CancellationToken cancellationToken)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }
}
