using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Caravel.Events;

/// <summary>Dispatches to listeners for the exact generic event type in the current DI scope.</summary>
public interface IEventDispatcher
{
    Task DispatchAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}

/// <summary>Handles an in-process event. Listeners run sequentially in registration order.</summary>
public interface IEventListener<TEvent> where TEvent : notnull
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}

public static class EventServiceExtensions
{
    public static IServiceCollection AddCaravelEvents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IEventDispatcher, EventDispatcher>();
        return services;
    }

    /// <summary>Registers a scoped listener once per event/listener pair; registration order is execution order.</summary>
    public static IServiceCollection AddEventListener<TEvent, TListener>(this IServiceCollection services)
        where TEvent : notnull
        where TListener : class, IEventListener<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCaravelEvents();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEventListener<TEvent>, TListener>());
        return services;
    }
}

internal sealed class EventDispatcher(IServiceProvider services) : IEventDispatcher
{
    public async Task DispatchAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var listener in services.GetServices<IEventListener<TEvent>>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await listener.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
