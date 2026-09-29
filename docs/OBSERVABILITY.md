# See what your workers are doing

Caravel uses the .NET diagnostics you already know: `ILogger`, `ActivitySource`, `Meter` and ASP.NET Core health checks. The queue package publishes metrics and tracing under the name **`Caravel.Queues`**, and you choose where they go. Caravel doesn't install an exporter or change your logging setup.

Use these signals to see whether jobs are being accepted, completed, retried or left needing attention. For the status of an individual job, the [queue itself](QUEUES.md) is the source of truth.

## Queue metrics

Subscribe to the `Caravel.Queues` meter. All instruments below are cumulative `Counter<long>` values with unit `{job}`.

| Instrument | What increments it |
| --- | --- |
| `caravel.queue.jobs.accepted` | A new enqueue is successfully saved. |
| `caravel.queue.jobs.deduplicated` | A repeated enqueue matches an existing job's identity, wire name and serialized payload. Conflicting requests do not count. |
| `caravel.queue.jobs.claimed` | A worker acquires a lease with a successful conditional database update. |
| `caravel.queue.jobs.completed` | Completion is saved using the current, unexpired lease. |
| `caravel.queue.jobs.failed` | A failure is saved using the current, unexpired lease. |
| `caravel.queue.jobs.retried` | That saved failure returns the job to pending with a retry time. |
| `caravel.queue.jobs.deadlettered` | A saved failure exhausts the attempt budget, or an expired final lease is moved to dead letter during polling. |
| `caravel.queue.jobs.replayed` | An authorized caller's explicit dead-letter replay is saved. The application supplies the authorization. |
| `caravel.queue.jobs.lease_lost` | A completion/failure acknowledgement is rejected because its lease is expired or no longer current. No completion/failure transition is counted. |

Every measurement has a `queue` tag, and failure, retry and dead-letter measurements add a `failure` tag with the failure category. Tenants, job IDs, payloads and exception text are never used as tags. Keep queue names to a small fixed set; don't build them from tenant or user IDs.

A few things to keep in mind when reading the numbers:

- A job that dead-letters after a crash increments `deadlettered` without incrementing `failed`, because no failure was ever recorded.
- `retried` means the job went back to pending, not that the next attempt has started.
- Counters can't tell you the current backlog. Use the persisted counts below for that.
- Metrics are recorded after the database change succeeds, but they're in-memory process data. They reset when the process restarts and can be lost if it crashes, so don't use them as an audit log.

## Inspect persisted queue counts

The [worker sample](QUEUES.md#try-the-runnable-worker-sample) includes a read-only status command:

```powershell
dotnet run --project samples/Caravel.Worker -- --queue-status
```

It prints one JSON object with `observedAt`, `ready`, `delayed`, `activeLeases`, `expiredLeases`, `deadLetter` and `completed`. Expired leases are just reported; a worker recovers them the next time it polls. The command opens the database read-only and fails, rather than reporting zeros, if the database or schema is missing. It's part of the sample, not a `caravel` command.

Your app can run the same kind of query against its own `QueueDbContext`:

```csharp
var counts = await database.Jobs.AsNoTracking()
    .Where(job => job.Queue == authorizedQueue)
    .GroupBy(job => job.State)
    .Select(group => new { State = group.Key, Count = group.LongCount() })
    .ToListAsync(cancellationToken);
```

This counts every tenant in the queue, so show it only to operators allowed to see all of them. For a per-tenant view, filter on the signed-in tenant, and watch out for case-insensitive collations that could match more than one tenant. Counting in a single grouped query keeps the totals consistent with each other while workers are busy.

## Follow a processing attempt

Subscribe to the `Caravel.Queues` activity source. Each claimed job processed by `QueueWorker` produces a `caravel.queue.process` activity with `ActivityKind.Consumer`, when a listener samples it. Its duration covers dispatch and acknowledgement. Empty polls and claims made directly through `IDatabaseQueue` do not create processing activities.

The activity contains:

- `messaging.destination.name`: the queue name.
- `messaging.message.id`: the generated job GUID, useful for looking up persisted status.
- `caravel.job.type`: the registered wire name. Unrecognized names from stored data are omitted.
- `caravel.queue.outcome`: `completed`, `failed`, `lease_lost`, `cancelled` or `infrastructure_error`.
- `caravel.queue.failure`, when needed: a bounded failure code, never an exception message.

The activity gets an `Ok` status only once the database accepts the completion. Failures, cancellation, infrastructure errors and lost leases are marked `Error`. Payloads, tenants, idempotency keys and stack traces are never added.

Trace context isn't stored with queued jobs yet, so a worker's activity doesn't link back to the request that enqueued it. Use the job ID to connect the two.

## Connect your application's OpenTelemetry setup

If your host already uses OpenTelemetry, add `Caravel.Queues` to its source and meter subscriptions. For a host choosing OTLP export, the following is an application-level recipe after adding and pinning `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` in that application:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Caravel.Queues")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("Caravel.Queues")
        .AddOtlpExporter());
```

Configure the collector endpoint, sampling and so on as you normally would. Nothing leaves your app unless you add an exporter. See the [.NET metrics guide](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation) and [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/).

In tests, a plain `MeterListener` or `ActivityListener` can subscribe without any extra packages. That's how Caravel's own queue tests check these signals.

## Keep application logs focused

Inject the normal `ILogger<T>` into a handler or service. Prefer stable operation names and generated identifiers:

```csharp
logger.LogInformation("Finished job {JobId} on queue {Queue}", context.JobId, context.Queue);
```

Remember that a handler's log line doesn't mean the queue recorded completion; use the `completed` metric or the job's status when that matters. Avoid logging whole payloads, credentials or authorization headers. Caravel keeps sensitive data out of its own diagnostics, but it can't redact what your code or other libraries log.

## Use health checks

An ASP.NET Core application can expose a basic liveness endpoint using the platform APIs:

```csharp
builder.Services.AddHealthChecks();
// After building the WebApplication:
app.MapHealthChecks("/health/live");
```

On its own, this only shows the app can respond. Add readiness checks for your database and other dependencies (the [backend sample](BACKEND-SAMPLE.md#control-load-and-check-readiness) has an example), and keep detailed health output away from the public. A healthy web app doesn't mean your worker is processing jobs, so monitor the queue too. See [ASP.NET Core health checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0).
