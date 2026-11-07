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

    public sealed record MutableWork(string Key, List<string> Values);
    public sealed class ConcurrentReceipt(int expected)
    {
        private int started;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentDictionary<string, (Guid Scope, string[] Values)> Completed { get; } = new();
        public System.Collections.Concurrent.ConcurrentBag<Guid> Disposed { get; } = [];
        public void Enter() { if (Interlocked.Increment(ref started) == expected) Started.TrySetResult(); }
    }
    public sealed class ScopedMarker(ConcurrentReceipt receipt) : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ValueTask DisposeAsync() { receipt.Disposed.Add(Id); return ValueTask.CompletedTask; }
    }
    public sealed class WaitingHandler(ConcurrentReceipt receipt, ScopedMarker marker) : IJobHandler<MutableWork>
    {
        public async Task HandleAsync(MutableWork job, JobContext context, CancellationToken cancellationToken)
        {
            receipt.Enter();
            await receipt.Release.Task.WaitAsync(cancellationToken);
            receipt.Completed[job.Key] = (marker.Id, job.Values.ToArray());
        }
    }
    private static ServiceProvider ConcurrentServices(int expected) => new ServiceCollection()
        .AddSingleton(new ConcurrentReceipt(expected)).AddScoped<ScopedMarker>()
        .AddQueueJob<MutableWork, WaitingHandler>("test.mutable.v1").AddCaravelSynchronousJobs()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Fact]
    public async Task Concurrent_dispatch_snapshots_mutable_payloads_and_isolates_scoped_dependencies()
    {
        await using var services = ConcurrentServices(2);
        await using var caller = services.CreateAsyncScope();
        var callerMarker = caller.ServiceProvider.GetRequiredService<ScopedMarker>();
        var receipt = services.GetRequiredService<ConcurrentReceipt>();
        var dispatcher = caller.ServiceProvider.GetRequiredService<SynchronousJobDispatcher>();
        var values = new List<string> { "original" };
        var first = dispatcher.DispatchAsync(new MutableWork("first", values), "tenant-a");
        var second = dispatcher.DispatchAsync(new MutableWork("second", values), "tenant-b");
        try
        {
            await receipt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            values[0] = "changed";
        }
        finally { receipt.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var one = receipt.Completed["first"];
        var two = receipt.Completed["second"];
        Assert.Equal(new[] { "original" }, one.Values);
        Assert.Equal(new[] { "original" }, two.Values);
        Assert.NotEqual(one.Scope, two.Scope);
        Assert.NotEqual(callerMarker.Id, one.Scope);
        Assert.NotEqual(callerMarker.Id, two.Scope);
        Assert.Equal(2, receipt.Disposed.Count);
        Assert.Contains(one.Scope, receipt.Disposed);
        Assert.Contains(two.Scope, receipt.Disposed);
    }

    [Fact]
    public async Task In_flight_cancellation_propagates_and_disposes_the_handler_scope()
    {
        await using var services = ConcurrentServices(1);
        using var cancellation = new CancellationTokenSource();
        var receipt = services.GetRequiredService<ConcurrentReceipt>();
        var dispatch = services.GetRequiredService<SynchronousJobDispatcher>()
            .DispatchAsync(new MutableWork("cancelled", ["synthetic"]), "tenant-a", cancellation.Token);
        try { await receipt.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(receipt.Completed);
        Assert.Single(receipt.Disposed);
    }
}
