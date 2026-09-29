# Durable jobs with Clinimatix Caravel

Use a database queue when work must survive an application restart. The caller saves a small, typed job; a worker claims it, runs its handler and records the outcome. Failed jobs retry with a delay. Jobs that use up their attempts stay in the database for inspection and deliberate replay.

`Clinimatix.Caravel.Queues` works with any .NET host and EF Core; it doesn't need the Caravel web host or Clarion. You choose the database, connection and migrations. It's tested on SQL Server, PostgreSQL and SQLite (see [database providers](DATABASE-PROVIDERS.md)).

## Set up a worker

Reference `Clinimatix.Caravel.Queues`, your EF Core provider and the usual hosting packages.

This example uses SQLite. Put the connection string in the application's configuration or secret store, under `ConnectionStrings:Queue`.

```csharp
using Caravel.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("Queue")
    ?? throw new InvalidOperationException("Configure the queue database connection.");

builder.Services.AddDbContextFactory<QueueDbContext>(options =>
    options.UseSqlite(connection, sqlite =>
        sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName!)));

builder.Services.AddCaravelDatabaseQueue(options =>
{
    options.MaxAttempts = 3;
    options.MaxPayloadBytes = 64 * 1024;
    options.LeaseDuration = TimeSpan.FromMinutes(5);
});
builder.Services.AddQueueJob<GenerateReport, GenerateReportHandler>("reports.generate.v1");
builder.Services.AddCaravelQueueWorker("reports");

await builder.Build().RunAsync();

public sealed record GenerateReport(Guid ReportId);

public sealed class GenerateReportHandler(ILogger<GenerateReportHandler> logger)
    : IJobHandler<GenerateReport>
{
    public Task HandleAsync(GenerateReport job, JobContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Replace this demonstration with application work that is safe to repeat.
        logger.LogInformation("Handling report job {JobId}, attempt {Attempt}", context.JobId, context.Attempt);
        return Task.CompletedTask;
    }
}
```

The wire name, `reports.generate.v1`, is the persisted contract name. Use stable, versioned names, and keep an older handler registered while its jobs still exist. Dispatch only accepts registered job types; workers never load a CLR type named by a payload. Duplicate wire names or duplicate job-type registrations are rejected.

`AddCaravelQueueWorker` starts one sequential polling worker. It creates and disposes an asynchronous DI scope for each job, so scoped application dependencies are fresh for each execution. Multiple application instances can compete for the same queue. To control when work runs yourself, inject `QueueWorker` and call `RunOnceAsync("reports", cancellationToken)` instead of registering the hosted worker. Its return value says whether a job was claimed, not whether the handler succeeded.

### Create the database schema deliberately

The queue does not create tables, migrate databases or start a server. Add EF's design package and the repository's pinned local EF tool to the application, then generate and review its migration:

```powershell
dotnet tool restore
dotnet ef migrations add CreateQueue --context QueueDbContext --project ./MyWorker --startup-project ./MyWorker
dotnet ef database update --context QueueDbContext --project ./MyWorker --startup-project ./MyWorker
```

Run the update as a deliberate step in development and deployment, and keep migrations in source control. Each database needs its own migrations; a SQLite migration won't work on SQL Server or PostgreSQL.

The package uses an application-configured `IDbContextFactory<QueueDbContext>`. Each queue operation creates and asynchronously disposes its own context. This avoids saving unrelated tracked entities and allows independent workers to operate safely.

## Enqueue once, even when a caller retries

Inject `IDatabaseQueue` where the application accepts work:

```csharp
var accepted = await queue.EnqueueAsync(
    new GenerateReport(reportId),
    new QueueDispatchOptions(
        Queue: "reports",
        TenantId: authorizedTenantId,
        IdempotencyKey: requestId),
    cancellationToken);
```

The call returns the job ID and an `AlreadyEnqueued` flag. Tell your caller the work was accepted only after it succeeds.

The idempotency key makes retries safe. Sending the same queue, tenant, key and payload again returns the original job instead of creating a new one, even if the first job already finished. Sending the same key with a *different* payload throws `QueueIdempotencyConflictException`, which an API can turn into a 409 Conflict. A duplicate never changes the original job's due time or attempt count.

Payloads are compared exactly as serialized, so keep retried requests identical. A unique database index settles races between simultaneous submissions.

To delay a job, set `NotBefore` to a UTC time. Queue times are stored to the millisecond and rounded up, so a job never runs early.

Queue names are at most 64 characters; wire names are at most 128. Both use lowercase letters, digits, dots, hyphens or underscores. Tenant IDs and idempotency keys are nonblank, at most 128 characters, with no control characters. Payloads have a configurable UTF-8 byte limit (64 KiB by default, at most 1 MiB) and a JSON depth limit of 32. Store large documents separately and enqueue a reference the handler is authorized to use.

## Tenant identity and authorization

The queue trusts whatever tenant ID you give it, so your app must check who is allowed to enqueue, view and replay jobs. Take `TenantId` from the signed-in user or verified caller, never from a field in the request body.

Handlers receive a `JobContext` with the job's stored tenant, queue, job ID and attempt number. Use that tenant to scope the handler's data access. Caravel doesn't apply tenant filters to your database for you, and a tenant field inside the job payload shouldn't be trusted.

`IDatabaseQueue` is an internal API for your app and workers. Don't expose its methods directly to clients.

```csharp
var status = await queue.GetStatusAsync(jobId, "reports", authorizedTenantId, cancellationToken);
var replayed = await queue.ReplayAsync(jobId, "reports", authorizedTenantId, cancellationToken);
```

Both need the exact tenant ID, including case. An unknown job or wrong tenant returns `null` from status and `false` from replay. Replay only works on dead-lettered jobs: it keeps the job ID and payload, resets the attempt count and makes the job due immediately. Treat replay as an administrative action, since it can repeat side effects.

## Failures, leases and recovery

**Delivery is at least once.** If a worker crashes after your handler saves its changes but before the queue records completion, the job runs again. Make handlers safe to repeat. A common approach is to save a row keyed by the job ID in the same transaction as the handler's result, and skip work that's already recorded. (The [worker sample](#try-the-runnable-worker-sample) does exactly this.)

**Leases.** A worker claims a job by taking a lease on it. Only the current lease holder can mark the job complete or failed, so a slow worker whose lease expired can't overwrite the result of the worker that took over. Every claim counts as an attempt, including ones cut short by a crash. When attempts run out, the job moves to `DeadLetter`.

**Retries.** Failed jobs retry with exponential backoff, from five seconds up to five minutes by default (`RetryDelay` and `MaxRetryDelay`). `MaxAttempts` sets the total number of attempts, from 1 to 100. Failures are recorded as a short category such as `HandlerFailed`, `InvalidPayload` (a stored job the worker couldn't read) or `UnknownJobType`. Exception messages and payloads are never copied into the queue's diagnostics.

**Timeouts and cancellation.** Leases last from one second to one hour. The worker cancels the handler's token when the lease runs out and when the host shuts down. Cancellation is cooperative, so pass the token to everything your handler awaits. There's no automatic lease renewal: keep jobs short, or split long work into repeatable steps. An interrupted job becomes available again once its lease expires.

**Clocks.** Leases use the worker's UTC clock, so keep clocks in sync across machines.

**Infrastructure failures**, such as the database going away while claiming or acknowledging, are passed to the host, which stops by default. See [hosting](HOSTING.md#run-a-durable-worker) for how to handle this.

**Retention.** Completed and dead-lettered jobs stay in the table, and their idempotency keys stay reserved. Nothing is deleted automatically. If you clean up old rows, remember that you also lose duplicate protection for them. Before removing a handler for an old job name, make sure no jobs with that name are left.

The queue also publishes metrics and tracing through standard .NET diagnostics; see [observability](OBSERVABILITY.md).

## What the queue doesn't do

The queue has its own `DbContext`, separate from your application's. Saving your data and then enqueueing a job are two separate transactions. If both must succeed or fail together, you'll need an outbox pattern in your app; the queue doesn't provide one.

This first driver covers durable enqueue, delayed work, scoped execution, leases, retries and replay. In-memory and synchronous drivers, message-broker adapters, batches, chains, debouncing and lease renewal are on the [roadmap](ROADMAP.md).

## Try the runnable worker sample

[Caravel.Worker](../samples/Caravel.Worker) is a small, complete worker. Three jobs add 1, 2 and 3 to a report. It keeps the queue in `queue.db` and results in `results.db`, both in a folder you choose. Like any Caravel app, it doesn't create its schema on startup, so you apply its two migrations first.

From the repository root, in PowerShell:

```powershell
$scratch = New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) ('caravel-worker-' + [guid]::NewGuid().ToString('N')))
$env:CARAVEL_WORKER_DIRECTORY = $scratch.FullName
dotnet tool restore
dotnet restore samples/Caravel.Worker
dotnet ef database update --context QueueDbContext --project samples/Caravel.Worker
dotnet ef database update --context ResultContext --project samples/Caravel.Worker
dotnet run --project samples/Caravel.Worker -- --check-model
dotnet run --project samples/Caravel.Worker -- --enqueue-demo
dotnet run --project samples/Caravel.Worker -- --enqueue-demo
1..3 | ForEach-Object { dotnet run --project samples/Caravel.Worker -- --work-once }
dotnet run --project samples/Caravel.Worker -- --report
```

The final JSON report is `{"events":3,"total":6}`. Repeating the demo enqueue after completion returns the original jobs and leaves that report unchanged. `--work-once` processes at most one job and reports whether it claimed one. `--check-model` checks that both contexts have no pending model changes or unapplied migrations.

Use `--queue-status` to inspect persisted queue counts without processing jobs. It reports ready and delayed jobs, active and expired leases, dead letters and completions. It opens the database read-only. See [observability](OBSERVABILITY.md#inspect-persisted-queue-counts) for details.

For a continuously running worker, use the same migrated scratch directory and run:

```powershell
dotnet run --project samples/Caravel.Worker -- --work
```

This runs `AddCaravelQueueWorker` on the Generic Host. It waits for work and handles jobs as they arrive; try enqueueing from a second terminal with the same `CARAVEL_WORKER_DIRECTORY`. Press Ctrl+C to stop. A job interrupted mid-run stays leased until its lease expires, then another worker picks it up.

If the queue's database fails, the host stops. .NET's `RunAsync` returns normally in that case, so the sample checks whether its worker faulted and exits with code **1** if it did, or **0** after a normal shutdown. That gives your service manager a clear signal to restart it.

**Why results never double-count:** the results table uses the job ID as its primary key and stores the tenant and quantity. If a job runs twice, the second run finds the existing row and, as long as it matches, simply acknowledges the job. The report is computed from those rows, so a retry can't add a quantity twice.

### How the sample is tested

`pwsh -File scripts/Test-WorkerSmoke.ps1` runs the sample end to end against freshly packed packages. Along the way it deliberately crashes a worker right after it saves a result but before it acknowledges the job, waits for the lease to expire, and confirms the restarted worker finishes with exactly three events and a total of six. It also checks continuous mode, the read-only status command, and that a worker whose database isn't migrated exits with code 1. On Linux it also sends a real SIGTERM mid-job and checks for a clean exit and recovery.

The sample reads two settings that exist only to support these tests: `CARAVEL_WORKER_CRASH_AFTER_RESULT=1` makes it exit (code 73) right after saving a result, and `CARAVEL_WORKER_LEASE_SECONDS` shortens the lease (60 seconds by default). They're part of the sample, not the queue package.
