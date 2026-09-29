using Caravel.Events.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Events.Tests;

public sealed class EventTests
{
    [Fact]
    public async Task ListenersAreAwaitedInRegistrationOrderAndShareTheCallersScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<Observations>();
        services.AddEventListener<Message, FirstListener>();
        services.AddEventListener<Message, SecondListener>();
        services.AddEventListener<Message, FirstListener>(); // Repeated setup does not duplicate delivery.
        using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = container.CreateScope();
        using var secondScope = container.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<Observations>();
        var second = secondScope.ServiceProvider.GetRequiredService<Observations>();
        var message = new Message();
        await firstScope.ServiceProvider.GetRequiredService<IEventDispatcher>().DispatchAsync(message);
        Assert.Equal(new[] { "first-start", "first-end", "second" }, first.Calls);
        Assert.All(first.Messages, received => Assert.Same(message, received));
        Assert.Empty(second.Calls);
        await secondScope.ServiceProvider.GetRequiredService<IEventDispatcher>().DispatchAsync(new Message());
        Assert.Equal(first.Calls, second.Calls);
        Assert.Throws<InvalidOperationException>(() => container.GetRequiredService<IEventDispatcher>());
    }

    [Fact]
    public async Task FailurePropagatesWithoutInvokingLaterListeners()
    {
        var services = new ServiceCollection();
        services.AddScoped<Observations>();
        services.AddEventListener<Message, FailingListener>();
        services.AddEventListener<Message, SecondListener>();
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        var observed = scope.ServiceProvider.GetRequiredService<Observations>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IEventDispatcher>().DispatchAsync(new Message()));
        Assert.Same(observed.Failure, exception);
        Assert.Equal(new[] { "failure" }, observed.Calls);
    }

    [Fact]
    public async Task CancellationIsForwardedAndStopsDispatchEvenIfListenerReturnsNormally()
    {
        using var cancellation = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddSingleton(cancellation);
        services.AddScoped<Observations>();
        services.AddEventListener<Message, CancellingListener>();
        services.AddEventListener<Message, SecondListener>();
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventDispatcher>();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new Message(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(new[] { "cancel" }, scope.ServiceProvider.GetRequiredService<Observations>().Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new Message(), cancellation.Token));
        Assert.Equal(new[] { "cancel" }, scope.ServiceProvider.GetRequiredService<Observations>().Calls);
    }

    [Fact]
    public async Task UnhandledEventsAreAllowedAndDispatchUsesTheDeclaredGenericType()
    {
        var services = new ServiceCollection();
        services.AddScoped<Observations>();
        services.AddEventListener<Message, SecondListener>();
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventDispatcher>();
        await dispatcher.DispatchAsync<object>(new Message());
        Assert.Empty(scope.ServiceProvider.GetRequiredService<Observations>().Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync<object>(new Message(), new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync<Message>(null!));
    }

    [Fact]
    public async Task RecordingFakeReplacesDeliveryAndExposesSnapshots()
    {
        var fake = new RecordingEventDispatcher();
        var services = new ServiceCollection();
        services.AddSingleton<IEventDispatcher>(fake);
        services.AddEventListener<Message, FailingListener>();
        using var container = services.BuildServiceProvider();
        var dispatcher = container.GetRequiredService<IEventDispatcher>();
        var message = new Message();
        await dispatcher.DispatchAsync(message);
        var snapshot = fake.DispatchedEvents;
        await dispatcher.DispatchAsync("another event");
        Assert.Same(message, Assert.Single(snapshot));
        Assert.Equal(2, fake.DispatchedEvents.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(message, new CancellationToken(true)));
        Assert.Equal(2, fake.DispatchedEvents.Count);
    }

    public sealed record Message;
    public sealed class Observations
    {
        public List<string> Calls { get; } = [];
        public List<Message> Messages { get; } = [];
        public InvalidOperationException Failure { get; } = new("Listener failed.");
    }

    public sealed class FirstListener(Observations observed) : IEventListener<Message>
    {
        public async Task HandleAsync(Message message, CancellationToken cancellationToken)
        {
            observed.Calls.Add("first-start");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            observed.Messages.Add(message);
            observed.Calls.Add("first-end");
        }
    }

    public sealed class SecondListener(Observations observed) : IEventListener<Message>
    {
        public Task HandleAsync(Message message, CancellationToken cancellationToken)
        {
            observed.Calls.Add("second");
            observed.Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    public sealed class FailingListener(Observations observed) : IEventListener<Message>
    {
        public Task HandleAsync(Message message, CancellationToken cancellationToken)
        {
            observed.Calls.Add("failure");
            return Task.FromException(observed.Failure);
        }
    }

    public sealed class CancellingListener(Observations observed, CancellationTokenSource cancellation)
        : IEventListener<Message>
    {
        public Task HandleAsync(Message message, CancellationToken cancellationToken)
        {
            Assert.Equal(cancellation.Token, cancellationToken);
            observed.Calls.Add("cancel");
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }
}
