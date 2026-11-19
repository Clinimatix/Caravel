using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;
using Xunit;

namespace Caravel.Core.Tests;

public sealed class ApplicationToolRegistryTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity());
    private static JsonElement Args => JsonSerializer.SerializeToElement(new { id = 1 });
    private sealed class Policy(Func<ToolDescriptor, bool> check) : IApplicationToolAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
            JsonElement arguments, CancellationToken cancellationToken) => ValueTask.FromResult(check(tool));
    }
    private static ApplicationTool Tool(string name, ToolEffect effect, Func<JsonElement> result) =>
        new(new(name, effect), (_, _, _) => ValueTask.FromResult(result()));

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task RegistryRetainsRegistrationSnapshotAfterCallerListChanges(ToolEffect effect)
    {
        var originalCalls = 0;
        var replacementCalls = 0;
        var original = Tool("lookup", effect, () => { originalCalls++; return Args; });
        var registrations = new List<ApplicationTool> { original };
        var registry = new ApplicationToolRegistry(registrations, new Policy(descriptor =>
        {
            Assert.Same(original.Descriptor, descriptor);
            return true;
        }));
        registrations.Clear();
        registrations.Add(Tool("lookup", effect, () => { replacementCalls++; return Args; }));
        registrations.Add(Tool("later", effect, () => { replacementCalls++; return Args; }));
        Assert.Same(original.Descriptor, Assert.Single(registry.Descriptors));
        Assert.Equal(1, (await registry.ExecuteAsync("lookup", Principal, Args)).GetProperty("id").GetInt32());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ExecuteAsync("later", Principal, Args).AsTask());
        Assert.Equal(1, originalCalls);
        Assert.Equal(0, replacementCalls);
    }

    [Fact]
    public async Task DiscoveryIsReadOnlyAndDoesNotInvokePolicyOrHandlers()
    {
        var checks = 0;
        var calls = 0;
        var descriptor = new ToolDescriptor("read", ToolEffect.Read);
        var registry = new ApplicationToolRegistry([new ApplicationTool(descriptor, (_, _, _) =>
        { calls++; return ValueTask.FromResult(Args); })], new Policy(_ => { checks++; return true; }));
        Assert.Same(descriptor, Assert.Single(registry.Descriptors));
        var list = (IList<ToolDescriptor>)registry.Descriptors;
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(new("write", ToolEffect.Write)));
        Assert.Throws<NotSupportedException>(() => list[0] = new("write", ToolEffect.Write));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Equal(0, checks);
        Assert.Equal(0, calls);
        await registry.ExecuteAsync("read", Principal, Args);
        Assert.Equal(1, checks);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CaseDistinctNamesRouteToTheirOwnDescriptorAndHandler()
    {
        var checks = new List<ToolDescriptor>();
        var lower = Tool("lookup", ToolEffect.Read, () => JsonSerializer.SerializeToElement("lower"));
        var upper = Tool("LOOKUP", ToolEffect.Write, () => JsonSerializer.SerializeToElement("upper"));
        var registry = new ApplicationToolRegistry([lower, upper], new Policy(descriptor =>
        { checks.Add(descriptor); return true; }));
        Assert.Equal(2, registry.Descriptors.Count);
        Assert.Equal("upper", (await registry.ExecuteAsync("LOOKUP", Principal, Args)).GetString());
        Assert.Equal("lower", (await registry.ExecuteAsync("lookup", Principal, Args)).GetString());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ExecuteAsync("LookUp", Principal, Args).AsTask());
        Assert.Equal(new[] { upper.Descriptor, lower.Descriptor }, checks);
    }

    [Fact]
    public async Task RegistrationEnumerableIsConsumedOnceAtConstructionOnly()
    {
        var enumerations = 0;
        IEnumerable<ApplicationTool> Registrations()
        {
            Assert.Equal(1, ++enumerations);
            yield return Tool("lookup", ToolEffect.Read, () => Args);
        }
        var registry = new ApplicationToolRegistry(Registrations(), new Policy(_ => true));
        Assert.Equal(1, enumerations);
        Assert.Single(registry.Descriptors);
        await registry.ExecuteAsync("lookup", Principal, Args);
        await registry.ExecuteAsync("lookup", Principal, Args);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void DuplicateRegistrationsFailWithoutExecutingCallbacks()
    {
        var checks = 0;
        var calls = 0;
        var first = Tool("lookup", ToolEffect.Read, () => { calls++; return Args; });
        var second = Tool("lookup", ToolEffect.Write, () => { calls++; return Args; });
        Assert.Throws<ArgumentException>(() => new ApplicationToolRegistry([first, second],
            new Policy(_ => { checks++; return true; })));
        Assert.Equal(0, checks);
        Assert.Equal(0, calls);
    }
}
