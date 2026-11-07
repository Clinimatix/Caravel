using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;
using FathomPolicyExample;
using Xunit;

namespace Caravel.Core.Tests;

public sealed class ApplicationPolicyTests
{
    private static ClaimsPrincipal User(string user = "alice", string tenant = "north", bool authenticated = true) =>
        new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user), new("tenant", tenant)],
            authenticated ? "synthetic-session" : null));
    private static JsonElement Args(string resource = "project-1") => JsonSerializer.SerializeToElement(new { resource });

    private sealed class Store : IProjectAccess
    {
        internal readonly ConcurrentDictionary<(string User, string Tenant, string Resource, ToolEffect Effect), byte> Grants = new();
        internal readonly ConcurrentQueue<(string User, string Tenant, string Resource, ToolEffect Effect)> Calls = new();
        internal Func<CancellationToken, Task>? BeforeExecute;
        internal Exception? PolicyFailure;
        internal Exception? HandlerFailure;
        internal int Checks;
        public ValueTask<bool> AllowsAsync(string user, string tenant, string resource, ToolEffect effect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Checks);
            if (PolicyFailure is not null) throw PolicyFailure;
            return ValueTask.FromResult(Grants.ContainsKey((user, tenant, resource, effect)));
        }
        public async ValueTask<JsonElement> ExecuteAsync(string user, string tenant, string resource, ToolEffect effect, CancellationToken token)
        {
            Calls.Enqueue((user, tenant, resource, effect));
            if (BeforeExecute is not null) await BeforeExecute(token);
            token.ThrowIfCancellationRequested();
            if (HandlerFailure is not null) throw HandlerFailure;
            return JsonSerializer.SerializeToElement(new { user, tenant, resource, effect = effect.ToString() });
        }
        internal void Grant(string user = "alice", string tenant = "north", string resource = "project-1", ToolEffect effect = ToolEffect.Read) =>
            Grants[(user, tenant, resource, effect)] = 0;
    }

    [Theory]
    [InlineData("read_project", ToolEffect.Read)]
    [InlineData("archive_project", ToolEffect.Write)]
    public async Task TrustedApplicationIdentityAndDeclaredEffectReachHandler(string name, ToolEffect effect)
    {
        var store = new Store();
        store.Grant(effect: effect);
        var principal = User();
        var scope = new ProjectToolScope(principal, store);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new("tenant", "forged-after-binding"));
        var reply = await scope.InvokeAsync(name, Args());
        Assert.True(reply.Success);
        Assert.Null(reply.Error);
        Assert.Equal("north", reply.Value!.Value.GetProperty("tenant").GetString());
        Assert.Equal(("alice", "north", "project-1", effect), Assert.Single(store.Calls));
    }

    [Theory]
    [InlineData("read_project")]
    [InlineData("archive_project")]
    public async Task MissingPermissionAndCrossTenantResourceNeverReachHandler(string name)
    {
        var store = new Store();
        store.Grant(tenant: "south", resource: "secret", effect: ToolEffect.Read);
        store.Grant(tenant: "south", resource: "secret", effect: ToolEffect.Write);
        var scope = new ProjectToolScope(User(), store);
        Assert.False((await scope.InvokeAsync(name, Args())).Success);
        Assert.False((await scope.InvokeAsync(name, Args("secret"))).Success);
        Assert.Equal(2, store.Checks);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData("{\"resource\":\"project-1\",\"tenant\":\"south\"}")]
    [InlineData("{\"resource\":\"project-1\",\"principal\":\"admin\"}")]
    [InlineData("{\"resource\":\"project-1\",\"effect\":\"Read\"}")]
    [InlineData("{\"resource\":\"project-1\",\"resource\":\"secret\"}")]
    [InlineData("{\"resource\":1}")]
    [InlineData("{\"resource\":\"\"}")]
    [InlineData("{}")]
    public async Task ForgedBindingsAndInvalidShapeFailClosed(string json)
    {
        var store = new Store();
        store.Grant();
        using var document = JsonDocument.Parse(json);
        Assert.False((await new ProjectToolScope(User(), store).InvokeAsync("read_project", document.RootElement)).Success);
        Assert.Equal(0, store.Checks);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task UnauthenticatedAmbiguousAndWrongUsersCannotBorrowPermissions()
    {
        var store = new Store();
        store.Grant();
        var ambiguous = User();
        ((ClaimsIdentity)ambiguous.Identity!).AddClaim(new("tenant", "south"));
        foreach (var principal in new[] { User(authenticated: false), ambiguous, User("mallory"), new ClaimsPrincipal() })
            Assert.False((await new ProjectToolScope(principal, store).InvokeAsync("read_project", Args())).Success);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData("read_project", ToolEffect.Read)]
    [InlineData("archive_project", ToolEffect.Write)]
    public async Task SameTenantResourceGrantDoesNotAuthorizeAnotherResource(string name, ToolEffect effect)
    {
        var store = new Store();
        store.Grant(effect: effect);
        var scope = new ProjectToolScope(User(), store);
        Assert.True((await scope.InvokeAsync(name, Args())).Success);
        Assert.False((await scope.InvokeAsync(name, Args("project-2"))).Success);
        Assert.Equal(2, store.Checks);
        Assert.Equal(("alice", "north", "project-1", effect), Assert.Single(store.Calls));
    }

    [Theory]
    [InlineData("read_project", ToolEffect.Read)]
    [InlineData("archive_project", ToolEffect.Write)]
    public async Task RevocationIsCheckedBetweenCalls(string name, ToolEffect effect)
    {
        var store = new Store();
        store.Grant(effect: effect);
        var scope = new ProjectToolScope(User(), store);
        Assert.True((await scope.InvokeAsync(name, Args())).Success);
        Assert.True(store.Grants.TryRemove(("alice", "north", "project-1", effect), out _));
        Assert.False((await scope.InvokeAsync(name, Args())).Success);
        Assert.Equal(2, store.Checks);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task ReadPermissionDoesNotPermitWrite()
    {
        var store = new Store();
        store.Grant();
        var scope = new ProjectToolScope(User(), store);
        Assert.True((await scope.InvokeAsync("read_project", Args())).Success);
        Assert.False((await scope.InvokeAsync("archive_project", Args())).Success);
        Assert.Equal(2, store.Checks);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task ConcurrentScopesKeepApplicationPrincipalsAndDependenciesSeparate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Gate(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }
        var north = new Store { BeforeExecute = Gate };
        var south = new Store { BeforeExecute = Gate };
        north.Grant();
        south.Grant("bob", "south");
        var first = new ProjectToolScope(User(), north).InvokeAsync("read_project", Args()).AsTask();
        var second = new ProjectToolScope(User("bob", "south"), south).InvokeAsync("read_project", Args()).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var replies = await Task.WhenAll(first, second);
        Assert.All(replies, reply => Assert.True(reply.Success));
        Assert.Equal("alice", replies[0].Value!.Value.GetProperty("user").GetString());
        Assert.Equal("south", replies[1].Value!.Value.GetProperty("tenant").GetString());
        Assert.Equal(("alice", "north", "project-1", ToolEffect.Read), Assert.Single(north.Calls));
        Assert.Equal(("bob", "south", "project-1", ToolEffect.Read), Assert.Single(south.Calls));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationErrorsAreSafeWhileRegistryPreservesExactException(bool policyFails)
    {
        var failure = new InvalidOperationException("synthetic-private-database-detail");
        var store = new Store { PolicyFailure = policyFails ? failure : null, HandlerFailure = policyFails ? null : failure };
        store.Grant();
        var scope = new ProjectToolScope(User(), store);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Registry.ExecuteAsync("read_project", User(), Args()).AsTask()));
        var reply = await scope.InvokeAsync("read_project", Args());
        Assert.False(reply.Success);
        Assert.Null(reply.Value);
        Assert.Equal("Operation failed.", reply.Error);
        Assert.DoesNotContain(failure.Message, JsonSerializer.Serialize(reply));
        Assert.Equal(policyFails ? 0 : 2, store.Calls.Count);
    }

    [Fact]
    public async Task CancellationRemainsCancellationAndUnknownToolsDoNotInvokeHandlers()
    {
        var store = new Store();
        store.Grant();
        var scope = new ProjectToolScope(User(), store);
        Assert.Equal("Operation unavailable.", (await scope.InvokeAsync("unknown", Args())).Error);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.InvokeAsync("read_project", Args(), canceled.Token).AsTask());
        Assert.Empty(store.Calls);
    }
}
