# Schedule jobs at fixed intervals

`Clinimatix.Caravel.Scheduling` turns a fixed UTC interval into a durable queue job. Use it for periodic application work such as checking for pending reports. The scheduler decides when to enqueue; your existing [queue worker](QUEUES.md) runs the registered handler.

## Register a schedule

First configure `QueueDbContext`, its schema, and `AddCaravelDatabaseQueue()` as described in the queue guide. Then register a typed job and its schedule:

```csharp
using Caravel.Queues;
using Caravel.Scheduling;

builder.Services.AddQueueJob<CheckReports, CheckReportsHandler>("reports.check.v1");
builder.Services.AddCaravelSchedule(
    name: "reports.hourly.v1",
    interval: TimeSpan.FromHours(1),
    job: new CheckReports("pending"),
    queue: "reports",
    tenantId: "trusted-tenant-id");

builder.Services.AddCaravelScheduler();
builder.Services.AddCaravelQueueWorker("reports");
```

`CheckReports` and `CheckReportsHandler` are application types; the handler implements `IJobHandler<CheckReports>`. The queue and tenant come from trusted application configuration. Scheduling does not authorize a user-supplied tenant or discover tenants automatically.

`AddCaravelSchedule` registers the schedule but starts no background process. `AddCaravelScheduler` adds a .NET `BackgroundService`, polling once per second by default. You can supply a polling interval between 100 milliseconds and one minute. The queue worker is a separate opt-in and can run in another host.

For a manual tick or deterministic test, resolve `QueueScheduler` and call:

```csharp
var added = await scheduler.RunOnceAsync(cancellationToken);
```

The return value counts newly enqueued jobs. A repeated tick in the same slot returns zero for jobs already persisted. Register a fake `TimeProvider` before building the service provider to control the clock in tests.

## How time and duplicates work

Intervals align to the Unix epoch in UTC. With a one-hour interval, starting the scheduler at 10:37 UTC enqueues the current 10:00–11:00 slot immediately. The next slot starts at 11:00 UTC. Intervals must be at least one second, at most 365 days, and an exact number of milliseconds.

Each pass captures one UTC instant and considers only its current slots. After downtime, it enqueues the current slot and **skips missed slots**. There is no catch-up queue, cron parser, local-time calendar, DST handling or promise of execution exactly at a clock boundary. Slow polling and clock skew can miss slots; keep deployment clocks synchronized and polling shorter than the shortest useful interval.

The queue's persisted idempotency key includes the schedule name, interval and slot. Queue and tenant also participate in queue deduplication. Multiple scheduler instances sharing the same queue database and configuration therefore converge on one stored job for a slot. Retain the relevant queue history: deleting the current slot's job removes its deduplication record. Queue execution still has the queue's at-least-once delivery contract; handlers must make side effects idempotent.

A tick is not an atomic batch. Schedules enqueue in registration order; if a later schedule fails, earlier accepted jobs remain durable and may already be executing. The tick surfaces the failure. Correcting the configuration and retrying within the same slot deduplicates unchanged jobs already accepted; retrying in a later slot follows the normal current-slot policy.

## Keep schedule definitions stable

Names use 1–64 lowercase ASCII letters, digits, dots, hyphens or underscores, and must be unique in a host. Use versioned names such as `reports.hourly.v1`.

The job is serialized at registration with web JSON conventions and a maximum depth of 32. Later changes to the original object do not change the schedule. Use plain deterministic job DTOs; avoid custom converters or properties that produce a different value each time they are serialized.

Change the versioned schedule name when changing its payload or meaning, and coordinate removal of the old schedule across hosts. Reusing the same queue/tenant/name/interval/slot with a different payload raises the queue's existing idempotency conflict instead of silently accepting the change. Changing the interval or version creates a distinct key, so overlapping deployments can enqueue both definitions.

Snapshots larger than one MiB are rejected at registration. The queue can configure a smaller payload limit, checked when the scheduler enqueues. Unknown job types, smaller-limit violations, incompatible payloads and database failures surface as tick errors. The background service passes errors to the host, which stops by default rather than silently dropping scheduled work. Make sure your service manager restarts it and that you're alerted.

## What the scheduler doesn't do

A job from one slot can still be running when the next slot's job starts. The scheduler doesn't add global locks or a workflow engine, so if two jobs must never overlap, coordinate that in your app.

It also doesn't create operating-system scheduled tasks; it runs inside your .NET host. Calendar and time-zone-aware schedules are on the [roadmap](ROADMAP.md).
