# Changelog

## 26.1.0-rc1 — 2026-09-29

The first release candidate brings together the 26.1 application foundation, data layer and services. It replaces the earlier .NET 9 prototype with a modular framework built on .NET 10 and C# 14. The documented scope and APIs are settled for release qualification; stable follows real-application feedback and upgrade validation.

### Application foundation

- Packages for Core, ASP.NET Core integration and the Bosun CLI, using `Clinimatix.Caravel.*` IDs.
- Service providers with dependency ordering and async boot that finishes before the app accepts requests.
- Configuration from JSON, `.env`, environment variables and the command line, with concise aliases for common settings.
- Named and grouped routes that produce ordinary ASP.NET Core endpoints, plus route inspection.
- HTTP defaults: ProblemDetails error responses, HSTS, antiforgery and authorization middleware.
- Release builds normalize compiled source paths; package checks reject unmapped local debug paths.

### Clarion data layer

- Scoped `IClarion` sessions over EF Core that work in any .NET host.
- Opt-in model discovery, automatic timestamps and soft deletes with restore. Restoring respects your other query filters, such as tenant filters.
- Soft deletes refuse to share a save with hard deletes or child-relationship changes, so nothing is lost silently.
- Soft deletes keep explicitly changed concurrency tokens, so stale writers are still detected.
- Model factories and ordered seeders.
- Tested on SQL Server, PostgreSQL and SQLite.

### Bosun CLI

- `new` with Razor (default) and API starters. The API starter includes request validation, Development-only OpenAPI and a liveness endpoint.
- `serve`, `dev` (watch the web app and supervise an optional worker), `route:list`, `doctor` and `schema --json`.
- Data commands: `make:model`, `make:seeder`, `make:factory`, `make:migration`, `migrate`, `migrate:status`, `migrate:rollback`, `migrate:fresh` and `db:seed`. Rollback and fresh require `--force`.
- Service generators: `make:job`, `make:event` and `make:listener`. Generated handlers throw until implemented and must be registered explicitly.
- `schema --json` describes each option's type, whether it's required, and how many values it takes.

### Authentication

- `Clinimatix.Caravel.Auth`: ASP.NET Core Identity with secure cookie, lockout, password and security-stamp defaults.
- Opt-in account endpoints for registration, email confirmation, password recovery, authenticator-app MFA and recovery codes. Pending MFA cookies are bound to the account security stamp; sensitive MFA changes require reauthentication.
- `Clinimatix.Caravel.Auth.Windows`: Windows (Negotiate) authentication with an authenticated-by-default fallback policy.
- Guides and samples for OpenID Connect sign-in and bearer-token APIs, including checking a caller's current access on each request.

### Application services

- `Clinimatix.Caravel.Events`: ordered, scoped in-process events with a recording test fake.
- `Clinimatix.Caravel.Mail`: SMTP with required TLS by default, text/HTML templates, attachments, development capture and queued delivery.
- `Clinimatix.Caravel.Notifications`: replaceable mail and SMS channels, independent queued delivery, SMS capture and an optional Twilio HTTPS adapter.
- Bounded automatic lease renewal and an application-context transactional outbox with idempotent relay to the database queue. Applications explicitly add and migrate the outbox model; existing queue tables do not change.
- `Clinimatix.Caravel.Queues`: durable database jobs with idempotent enqueue, fenced leases, retries with backoff, dead letters, replay, scoped workers, and metrics and tracing through standard .NET diagnostics.
- `Clinimatix.Caravel.Scheduling`: fixed-interval UTC schedules that enqueue queue jobs, with duplicate-free slots across multiple scheduler instances.
- `Clinimatix.Caravel.Storage`: named local storage disks with streamed, create-only writes and path validation.
- Caching guidance using .NET's built-in memory and SQL Server caches.

### Samples

- A data-only Generic Host app using Clarion and SQLite.
- A queue worker that recovers cleanly from a crash without double-counting.
- An authenticated backend combining Identity, Clarion, events and queues: durable ingestion, owner-scoped reports, rate limits, health checks and account session management.
- An OpenID Connect administrator sign-in app.

### Breaking changes from the prototype

- The .NET 9 prototype, the Ardent ORM, the reflection-based controller dispatcher and the old destructive CLI commands have been removed.
- The command is now `caravel`. The old `bosun` command and unprefixed package IDs no longer work.
- There's no automated migration from the prototype.

### Known limitations

- Prerelease packages are distributed as GitHub release assets, not through nuget.org; see [installation instructions](docs/RELEASE-POLICY.md#using-prerelease-packages).
- Not yet included: passkeys, finished account-management screens, calendar/time-zone scheduling, cloud storage drivers, additional notification providers, a Caravel cache API, and the AI/MCP packages. See the [roadmap](docs/ROADMAP.md).
- SMTP and SMS adapters have automated protocol tests; provider acceptance does not guarantee recipient delivery. Configure and verify your chosen delivery service before use. SMS delivery is not SMS MFA.
- MariaDB is waiting on an EF Core 10–compatible provider.
- Linux and macOS storage requires a filesystem that supports hard links.
