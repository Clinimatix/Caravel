using System.Collections.Concurrent;

namespace Caravel.Events.Testing;

/// <summary>A test replacement that records events without executing listeners.</summary>
public sealed class RecordingEventDispatcher : IEventDispatcher
{
    private readonly ConcurrentQueue<object> messages = new();

    /// <summary>A snapshot of recorded event objects in dispatch order.</summary>
    public IReadOnlyList<object> DispatchedEvents => messages.ToArray();

    public Task DispatchAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
