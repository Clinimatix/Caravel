# A small backend, end to end

The Identity sample shows the main pieces of a Caravel backend working together: sign-in, an authenticated API, a durable queue and per-user reports. Its data model is deliberately tiny. Users submit counter events, each adding a quantity from 1 to 1000, which keeps the interesting parts in view: retries, duplicates, crashes and ownership.

The sample lives in `samples/Caravel.Identity`, alongside the notes and account endpoints described in the [authentication guide](AUTHENTICATION.md). It uses local Identity cookies; each signed-in user owns their own events.

## Prepare the two database contexts

Choose a disposable SQLite file and apply both sets of migrations:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
New-Item -ItemType Directory -Force artifacts | Out-Null
$env:Caravel__IdentityDatabase = Join-Path (Get-Location) 'artifacts/backend-demo.db'
dotnet tool restore
dotnet ef database update --project samples/Caravel.Identity --context IdentityContext
dotnet ef database update --project samples/Caravel.Identity --context QueueDbContext
```

`IdentityContext` holds users, notes and counter results. `QueueDbContext` holds the queue in the same file, with its own `__CaravelQueueMigrations` history table. Neither creates its schema when the app starts. The included migrations are for SQLite; other databases need their own.

The sample also accepts `Caravel:DatabaseProvider` set to `sqlserver` or `postgres`. For those providers, `Caravel:IdentityDatabase` is a connection string instead of a file path. Generate and review provider-specific migrations for both contexts in your own application; do not apply the included SQLite migrations to a server database. The [packaged backend tests](TESTING.md#test-against-sql-server-and-postgresql) demonstrate this using disposable copies and databases.

Follow [the authentication guide](AUTHENTICATION.md#try-the-sample) to create a demo user. There are no built-in credentials, and registration is disabled by default. The same guide explains how to opt into registration, confirmation email and MFA.

To run this demo with its queue worker enabled:

```powershell
$env:Caravel__RunWorker = 'true'
dotnet run --project samples/Caravel.Identity -- --urls https://localhost:7246
```

The worker is off unless that setting is true. It processes the `counter` queue through the normal Caravel queue worker. Tests leave it off and drive individual work attempts with `QueueWorker.RunOnceAsync`.

## Submit an event

Sign in and fetch a fresh token from `GET /auth/csrf`. Keep the authentication and antiforgery cookies, then send the token with the event:

```http
POST /counter/events
Content-Type: application/json
Idempotency-Key: synthetic-event-001
RequestVerificationToken: <fresh token>

{"quantity":5}
```

A successful response is **202 Accepted**, containing `jobId` and `alreadyEnqueued`, with a Location pointing to `/counter/jobs/{jobId}`. The response is sent only after the queue row has been saved. It means durable acceptance, not completed processing.

Retry the same account/key/quantity after a lost response: the API returns the original job ID. Reusing that key with a different quantity returns **409 Conflict**. Keys must be a single nonblank header value of at most 128 characters, without control characters. Authentication, antiforgery and quantity validation remain required on retries.

The server derives ownership from the signed-in user's identifier. Client-supplied `tenantId` or `ownerId` fields have no authority. Queue failures produce a server error, never a false 202 acknowledgement.

Request bodies are limited to 16 KiB. Requests that declare a larger `Content-Length` are rejected with 413 before any JSON is read, and chunked requests are cut off at the limit. Kestrel may close the connection after rejecting a body, so clients shouldn't count on reusing it. If IIS or a reverse proxy sits in front of your app, configure its limits too. See [Kestrel request limits](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-10.0#maximum-request-body-size).

## Watch processing and read results

| Endpoint | Response |
| --- | --- |
| `GET /counter/jobs/{jobId}` | The current user's job status and attempt count; another user's ID returns 404 |
| `GET /counter/results` | The current user's recorded events, ordered by recording time and job ID |
| `GET /counter/summary` | The current user's event count and total quantity |

Results and summary accept `from` and `to` in UTC, for example `2026-09-28T00:00:00Z`. The range includes `from` and excludes `to`. It must span more than zero and at most 31 days. With neither supplied, it covers the 24 hours ending at the server's current UTC time. When only `to` is supplied, the default start is 24 hours earlier.

Results also accept `offset` (0–10000, default 0) and `limit` (1–100, default 50). Invalid ranges, non-UTC times and malformed values return 400. Counts and sums are computed in the database. Reports use the time each result was recorded on the server, not a timestamp supplied by the client.

## Why redelivery does not count twice

The handler writes one result whose primary key is the queue job ID. That row is both the report data and the processing receipt. On redelivery, the handler compares its saved owner and quantity with the incoming envelope and payload. Matching work is already complete; conflicting work fails.

This matters when a worker saves the result and dies before acknowledging its queue lease. After lease expiry, another attempt finds the existing result and acknowledges the job without adding the quantity again. The uniqueness constraint also handles competing inserts. No transaction spanning the queue acknowledgement and application result is implied.

Retain both the queue's idempotency history and result receipts for the retry/replay period you intend to support. Deleting them changes those guarantees. A new idempotency key describes new work, even if its quantity matches another event.

## Control load and check readiness

The sample uses [ASP.NET Core's rate limiter](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0). One shared concurrency limiter admits up to 32 requests into endpoint work, with no waiting queue. A separate fixed window permits 10 login requests per 60 seconds. Excess requests return a generic **429 Too Many Requests**, without revealing whether an account exists.

The login allowance is shared by everyone using the demo, so one caller can use it up and briefly block others. That keeps the sample simple; a real app should limit by user, IP address or at the network edge, depending on its callers. The concurrency limiter runs after authentication, so it limits endpoint work but not the authentication lookup itself.

Configure limits before startup if needed:

| Configuration key | Default | Allowed range |
| --- | --- | --- |
| `Caravel:Limits:Concurrency` | 32 | 1–1024 |
| `Caravel:Limits:LoginPermitLimit` | 10 | 1–1000 |
| `Caravel:Limits:LoginWindowSeconds` | 60 | 1–3600 |

These limits apply per running instance. They aren't a shared fleet-wide quota.

Two paths use [ASP.NET Core health checks](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0) before authentication and rate limiting:

| Probe | What it checks |
| --- | --- |
| `/health/live` | The process can answer; no database or cookie validation |
| `/health/ready` | Required Identity, notes, counter-result and queue tables and columns can be read |

Responses are just `Healthy` (200) or `Unhealthy` (503), with no error details. Readiness opens the database read-only, reads at most one row from each required table, and times out after five seconds. It never creates the database or schema, so a missing or incomplete schema shows up as `Unhealthy`.

Only these two paths skip the rate limiters, so restrict access to them as your monitoring setup allows. A ready web app doesn't prove that the worker is running or that the queue is keeping up; watch [queue metrics](OBSERVABILITY.md) for that.

## What the tests cover

The sample's shared tests in `tests/Caravel.Identity.Tests` run the real endpoints against fresh SQLite, SQL Server or PostgreSQL databases with synthetic accounts. They cover:

- anonymous requests, missing antiforgery tokens and invalid input
- oversized bodies, including a check against a real Kestrel server
- retries with the same key, conflicting payloads and queue failures
- users only ever seeing their own jobs, results and totals
- a worker that saves its result and then loses the acknowledgement, followed by redelivery without double-counting
- disposing and recreating the application host over the same database, recovering pending work and unacknowledged results after sessions have been revoked
- 96 concurrent submissions processed by four workers, with exact per-user totals
- upgrading a notes-only schema, rolling back the still-empty results table and reapplying the upgrade without losing accounts or notes
- rate limits and health checks

`scripts/Test-IdentitySmoke.ps1` runs these tests against freshly packed packages. SQLite is the default; `-Provider sqlserver` and `-Provider postgres` select the server checks. The default lane also runs the OIDC sample tests.

One behavior to be aware of: a queued job keeps the owner it was submitted with. If a user loses access while their job waits in the queue, the job still runs. If your app needs pending work canceled when access is withdrawn, build that in explicitly.
