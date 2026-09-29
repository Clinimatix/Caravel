# Events

An event lets one part of your app tell other parts that something happened. Caravel's optional `Clinimatix.Caravel.Events` package runs registered listeners in order and waits for them to finish.

It works in web apps, console apps and workers. It uses standard dependency injection and doesn't depend on MediatR, EF Core or the Caravel host.

## Register and dispatch

```csharp
using Caravel.Events;

builder.Services.AddCaravelEvents()
    .AddEventListener<OrderPlaced, LogOrderPlaced>();

public sealed record OrderPlaced(Guid OrderId);

public sealed class LogOrderPlaced(ILogger<LogOrderPlaced> logger)
    : IEventListener<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Order {OrderId} placed", message.OrderId);
        return Task.CompletedTask;
    }
}
```

Inject `IEventDispatcher` into a scoped service or endpoint:

```csharp
await events.DispatchAsync(new OrderPlaced(order.Id), cancellationToken);
```

Listeners use the caller's DI scope, so they can share a scoped database context. In a console app or worker, create a scope before resolving the dispatcher. Don't resolve it from the root container or use the same scope concurrently.

## Execution rules

- Listeners run sequentially in registration order. Register the same event/listener pair twice and it still runs once.
- Dispatch uses the declared generic event type. Dispatching `OrderPlaced` doesn't also call listeners registered for `object` or a base interface.
- An exception stops dispatch and reaches the caller. Completed listeners are not undone.
- Cancellation is checked before dispatch and between listeners; each listener also receives the token so it can cancel its own work.
- An event with no listeners completes normally.

This is in-process work. Events are not stored, retried or delivered after a crash. If you save a record and then a listener fails, the saved record is still there. Use a durable queue or transactional outbox when the notification must survive failures; don't treat successful database persistence and event dispatch as one transaction.

## Testing code that emits events

You can test real listeners with normal DI, or replace dispatch with a recording fake:

```csharp
using Caravel.Events.Testing;

var recorded = new RecordingEventDispatcher();
services.AddSingleton<IEventDispatcher>(recorded);
services.AddCaravelEvents();
```

The fake records messages without running listeners. Its `DispatchedEvents` property returns a snapshot you can inspect with your preferred test library. Register it before `AddCaravelEvents`, or use DI's `Replace` helper afterward.

The [Identity sample](../samples/Caravel.Identity/Program.cs) shows a saved note followed by an event. Its tests verify that a listener sees the same scoped context and the already-saved record.
