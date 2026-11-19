using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;
using Xunit;

namespace Caravel.Core.Tests;

public sealed class ApplicationToolTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity());
    private static JsonElement Args => JsonSerializer.SerializeToElement(new { id = 1 });
    private sealed class Policy(Func<ToolDescriptor, CancellationToken, ValueTask<bool>> check) : IApplicationToolAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
            JsonElement arguments, CancellationToken cancellationToken) => check(tool, cancellationToken);
    }
    private static ApplicationTool Tool(Func<ClaimsPrincipal, JsonElement, CancellationToken, ValueTask<JsonElement>> handler,
        ToolEffect effect = ToolEffect.Read) => new(new("lookup", effect), handler);

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task DeniedCallsNeverRunHandler(ToolEffect effect)
    {
        var calls = 0;
        var registry = new ApplicationToolRegistry([Tool((_, _, _) => { calls++; return ValueTask.FromResult(Args); }, effect)],
            new Policy((descriptor, _) => { Assert.Equal(effect, descriptor.Effect); return ValueTask.FromResult(false); }));
        Assert.Single(registry.Descriptors);
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ExecuteAsync("lookup", Principal, Args).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AuthorizationIsCheckedEachTimeAndHandlerReceivesCallerAndToken()
    {
        var checks = 0;
        using var cts = new CancellationTokenSource();
        var registry = new ApplicationToolRegistry([Tool((principal, args, token) =>
        {
            Assert.Same(Principal, principal);
            Assert.Equal(cts.Token, token);
            return ValueTask.FromResult(args);
        })], new Policy((_, _) => ValueTask.FromResult(++checks == 1)));
        Assert.Equal(1, (await registry.ExecuteAsync("lookup", Principal, Args, cts.Token)).GetProperty("id").GetInt32());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ExecuteAsync("lookup", Principal, Args).AsTask());
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringAuthorizationPreventsHandler()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var checks = 0;
        var registry = new ApplicationToolRegistry([Tool((_, _, _) => { calls++; return ValueTask.FromResult(Args); })],
            new Policy((_, _) => { checks++; cts.Cancel(); return ValueTask.FromResult(true); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExecuteAsync("lookup", Principal, Args, cts.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExecuteAsync("lookup", Principal, Args, cts.Token).AsTask());
        Assert.Equal(1, checks);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HandlerFailurePropagatesWithoutRetry()
    {
        var calls = 0;
        var failure = new InvalidOperationException("synthetic");
        var registry = new ApplicationToolRegistry([Tool((_, _, _) => { calls++; throw failure; })],
            new Policy((_, _) => ValueTask.FromResult(true)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("lookup", Principal, Args).AsTask()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnknownNamesAndInvalidArgumentsNeverAuthorize()
    {
        var registry = new ApplicationToolRegistry([Tool((_, _, _) => ValueTask.FromResult(Args))],
            new Policy((_, _) => throw new InvalidOperationException("must not authorize")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.ExecuteAsync("LOOKUP", Principal, Args).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => registry.ExecuteAsync("lookup", Principal, default).AsTask());
    }

    [Fact]
    public async Task AuthorizationErrorsNeverRunHandler()
    {
        var failure = new InvalidOperationException("synthetic policy failure");
        var registry = new ApplicationToolRegistry([Tool((_, _, _) => throw new Exception("must not execute"))],
            new Policy((_, _) => throw failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("lookup", Principal, Args).AsTask()));
    }

    [Fact]
    public async Task HandlerCancellationPropagatesWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var registry = new ApplicationToolRegistry([Tool(async (_, _, token) =>
        {
            calls++;
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Args;
        })], new Policy((_, _) => ValueTask.FromResult(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExecuteAsync("lookup", Principal, Args, cts.Token).AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ArgumentsSurviveCallerDocumentDisposalDuringAuthorization()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ApplicationToolRegistry([Tool((_, args, _) => ValueTask.FromResult(args))],
            new Policy(async (_, _) => { entered.SetResult(); await release.Task; return true; }));
        var document = JsonDocument.Parse("{\"id\":1}");
        var pending = registry.ExecuteAsync("lookup", Principal, document.RootElement).AsTask();
        await entered.Task;
        document.Dispose();
        release.SetResult();
        Assert.Equal(1, (await pending).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task NonObjectArgumentsNeverAuthorizeOrExecute(ToolEffect effect)
    {
        var checks = 0;
        var calls = 0;
        var registry = new ApplicationToolRegistry([Tool((_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult(Args);
        }, effect)], new Policy((_, _) => { checks++; return ValueTask.FromResult(true); }));

        foreach (var json in new[] { "null", "[]", "[{}]", "\"text\"", "0", "1.5", "true", "false" })
        {
            using var document = JsonDocument.Parse(json);
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                registry.ExecuteAsync("lookup", Principal, document.RootElement).AsTask());
            Assert.Equal("arguments", error.ParamName);
            Assert.Equal(0, checks);
            Assert.Equal(0, calls);
        }
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.ExecuteAsync("lookup", Principal, default).AsTask());
        Assert.Equal(0, checks);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task DisposedArgumentsNeverAuthorizeOrExecute(ToolEffect effect)
    {
        var checks = 0;
        var calls = 0;
        var registry = new ApplicationToolRegistry([Tool((_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult(Args);
        }, effect)], new Policy((_, _) => { checks++; return ValueTask.FromResult(true); }));
        var document = JsonDocument.Parse("{}");
        var arguments = document.RootElement;
        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            registry.ExecuteAsync("lookup", Principal, arguments).AsTask());
        Assert.Equal(0, checks);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task ValidObjectsStillRequireAuthorizationAndReachHandlerUnchanged(ToolEffect effect)
    {
        var checks = 0;
        var calls = 0;
        var registry = new ApplicationToolRegistry([Tool((_, arguments, _) =>
        {
            Assert.Equal(++calls, checks);
            return ValueTask.FromResult(arguments);
        }, effect)], new Policy((_, _) => { checks++; return ValueTask.FromResult(true); }));

        foreach (var json in new[] { "{}", "{\"nested\":{\"items\":[null,true,1,\"text\"]}}" })
        {
            using var document = JsonDocument.Parse(json);
            var result = await registry.ExecuteAsync("lookup", Principal, document.RootElement);
            Assert.Equal(json, result.GetRawText());
        }
        Assert.Equal(2, checks);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RegistrationRejectsDuplicatesAndInvalidMetadata()
    {
        var tool = Tool((_, _, _) => ValueTask.FromResult(Args));
        Assert.Throws<ArgumentException>(() => new ApplicationToolRegistry([tool, tool], new Policy((_, _) => ValueTask.FromResult(true))));
        Assert.Throws<ArgumentException>(() => new ToolDescriptor("../shell", ToolEffect.Read));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolDescriptor("valid", (ToolEffect)99));
        Assert.Throws<ArgumentNullException>(() => new ApplicationToolRegistry([tool], null!));
    }
}
