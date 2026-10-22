using Caravel.Queues;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Queues.Tests;

public sealed class SynchronousJobTests
{
    public sealed record Work(string Value);
    public sealed class Receipt { public List<string> Values { get; } = []; public int Disposed; public bool Fail; public JobContext? Context; }
    public sealed class Handler(Receipt receipt) : IJobHandler<Work>, IAsyncDisposable
    {
        public Task HandleAsync(Work job, JobContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receipt.Context = context;
            if (receipt.Fail) throw new InvalidOperationException("synthetic failure");
            receipt.Values.Add(job.Value);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { receipt.Disposed++; return ValueTask.CompletedTask; }
    }
    private static ServiceProvider Services() => new ServiceCollection().AddSingleton<Receipt>()
        .AddQueueJob<Work, Handler>("test.work.v1").AddCaravelSynchronousJobs()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Fact]
    public async Task Runs_registered_handler_without_database_and_disposes_each_scope()
    {
        await using var services = Services();
        var dispatcher = services.GetRequiredService<SynchronousJobDispatcher>();
        var receipt = services.GetRequiredService<Receipt>();
        await dispatcher.DispatchAsync(new Work("first"), "tenant-a");
        var first = receipt.Context!;
        await dispatcher.DispatchAsync(new Work("second"), "tenant-b");
        Assert.Equal(new[] { "first", "second" }, receipt.Values);
        Assert.Equal(2, receipt.Disposed);
        Assert.Equal("tenant-a", first.TenantId);
        Assert.Equal("sync", first.Queue);
        Assert.Equal(1, first.Attempt);
        Assert.NotEqual(first.JobId, receipt.Context!.JobId);
        Assert.Equal("tenant-b", receipt.Context.TenantId);
        Assert.Null(services.GetService<IDatabaseQueue>());
    }

    [Fact]
    public async Task Failure_propagates_without_retry_and_scope_is_disposed()
    {
        await using var services = Services();
        var receipt = services.GetRequiredService<Receipt>(); receipt.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<SynchronousJobDispatcher>()
            .DispatchAsync(new Work("failed"), "tenant-a"));
        Assert.Empty(receipt.Values); Assert.Equal(1, receipt.Disposed);
    }

    [Fact]
    public async Task Invalid_or_cancelled_dispatch_never_invokes_handler()
    {
        await using var services = Services();
        var dispatcher = services.GetRequiredService<SynchronousJobDispatcher>();
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new Work("bad"), " "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync<Work>(null!, "tenant-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync("unregistered", "tenant-a"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(new Work("cancelled"), "tenant-a", cancelled.Token));
        Assert.Empty(services.GetRequiredService<Receipt>().Values);
    }
}
