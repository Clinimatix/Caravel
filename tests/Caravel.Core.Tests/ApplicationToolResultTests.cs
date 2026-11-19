using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;
using Xunit;

namespace Caravel.Core.Tests;

public sealed class ApplicationToolResultTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity());
    private static JsonElement Arguments => JsonSerializer.SerializeToElement(new { id = 1 });
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class AllowPolicy : IApplicationToolAuthorizer
    {
        public int Calls { get; private set; }
        public ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
            JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(true);
        }
    }

    private static ApplicationToolRegistry Registry(ToolEffect effect, AllowPolicy policy,
        Func<ClaimsPrincipal, JsonElement, CancellationToken, ValueTask<JsonElement>> handler) =>
        new([new ApplicationTool(new ToolDescriptor("result", effect), handler)], policy);

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task SuspendedHandlerFailurePreservesExceptionWithoutRetry(ToolEffect effect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("synthetic delayed failure");
        var policy = new AllowPolicy();
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var registry = Registry(effect, policy, async (principal, arguments, token) =>
        {
            Assert.Same(Principal, principal);
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(1, arguments.GetProperty("id").GetInt32());
            calls++;
            entered.SetResult();
            await release.Task;
            throw failure;
        });
        var pending = registry.ExecuteAsync("result", Principal, Arguments, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Timeout)));
        Assert.Equal(1, calls);
        Assert.Equal(1, policy.Calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task SuspendedHandlerCancellationPreservesTokenWithoutRetry(ToolEffect effect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new AllowPolicy();
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var registry = Registry(effect, policy, async (_, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            entered.SetResult();
            await never.Task.WaitAsync(token);
            return Arguments;
        });
        var pending = registry.ExecuteAsync("result", Principal, Arguments, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(pending.IsCompleted);
        }
        finally { cancellation.Cancel(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(1, policy.Calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task AdmittedHandlerMayCompleteAfterCancellationWithoutRollback(ToolEffect effect)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new AllowPolicy();
        var effects = 0;
        using var cancellation = new CancellationTokenSource();
        var registry = Registry(effect, policy, async (_, _, token) =>
        {
            effects++;
            entered.SetResult();
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            return JsonSerializer.SerializeToElement(new { saved = effects });
        });
        var pending = registry.ExecuteAsync("result", Principal, Arguments, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(Timeout)).GetProperty("saved").GetInt32());
        Assert.Equal(1, effects);
        Assert.Equal(1, policy.Calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task HandlerOwnedClonesRemainUsableAfterDocumentDisposal(ToolEffect effect)
    {
        var policy = new AllowPolicy();
        var calls = 0;
        foreach (var json in new[] { "null", "true", "42", "\"text\"", "[1,{\"nested\":true}]", "{\"items\":[null,1]}" })
        {
            var registry = Registry(effect, policy, (_, _, _) =>
            {
                calls++;
                using var document = JsonDocument.Parse(json);
                return ValueTask.FromResult(document.RootElement.Clone());
            });
            var result = await registry.ExecuteAsync("result", Principal, Arguments);
            Assert.Equal(json, result.GetRawText());
        }
        Assert.Equal(6, calls);
        Assert.Equal(6, policy.Calls);
    }

    [Theory]
    [InlineData(ToolEffect.Read)]
    [InlineData(ToolEffect.Write)]
    public async Task BorrowedResultsRetainApplicationOwnedDocumentLifetime(ToolEffect effect)
    {
        var policy = new AllowPolicy();
        var calls = 0;
        using var document = JsonDocument.Parse("{\"result\":1}");
        var registry = Registry(effect, policy, (_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult(document.RootElement);
        });
        var result = await registry.ExecuteAsync("result", Principal, Arguments);
        Assert.Equal(1, result.GetProperty("result").GetInt32());
        var retained = result.Clone();
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => result.GetRawText());
        Assert.Equal(1, retained.GetProperty("result").GetInt32());
        Assert.Equal(1, calls);
        Assert.Equal(1, policy.Calls);
    }
}
