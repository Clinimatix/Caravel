# Design and roadmap

This page explains what Clinimatix Caravel is trying to be, how its packages fit together, what's available today and what's coming next.

## What Caravel is for

Caravel makes .NET application development more approachable through clear conventions, expressive APIs, integrated tooling and cohesive application services. It builds on ASP.NET Core, the Generic Host, Microsoft.Extensions, EF Core and ASP.NET Core Identity, so applications stay close to the platform and its ecosystem.

Caravel targets Windows, Linux and macOS, with platform-specific packages where needed. Current execution coverage includes Windows, Linux and macOS 26 on Apple silicon, including packaged applications and worker recovery. IIS, Windows Service and Windows authentication guides explain native .NET integration, while deployment verification remains separate from these framework checks.

## Design principles

- **Thin over .NET.** Caravel composes platform features instead of replacing them. Your app is still an ordinary ASP.NET Core or Generic Host app, and plain .NET code mixes freely with Caravel's conveniences.
- **Convention over ceremony.** Common tasks should have one obvious way to do them, and a new app should need much less setup than a typical ASP.NET Core project.
- **Batteries included, batteries replaceable.** Every capability ships as an optional package. Take what you need and replace the rest.
- **No surprise dependencies.** Caravel prefers permissively licensed dependencies and platform components. It doesn't require commercial license keys, MediatR or AI credentials.
- **Safe by default.** Destructive commands require `--force`, `caravel doctor` only reads, secrets stay out of logs and generated files, and nothing creates databases or runs migrations behind your back.
- **Latest .NET LTS.** Caravel targets the newest generally available long-term-support release of .NET (currently .NET 10 and C# 14). It moves to the next LTS when that version ships.

## How the packages fit together

Everything lives in this repository and shares one version number, but each package can be used independently.

| Package | Depends on | Notes |
| --- | --- | --- |
| `Clinimatix.Caravel.Core` | Microsoft.Extensions | Configuration, `.env` and service providers. No web or EF dependency. |
| `Clinimatix.Caravel.AspNetCore` | Core, ASP.NET Core | Routing syntax, HTTP defaults and route inspection. |
| `Clinimatix.Caravel.Clarion` | EF Core | The data layer. Works in any .NET host and doesn't need the rest of Caravel. You choose the database provider. |
| `Clinimatix.Caravel.Bosun` | — | The `caravel` command-line tool, installed as a .NET tool. |
| `Clinimatix.Caravel.Auth` | ASP.NET Core Identity | Local accounts with secure defaults. You choose the store. |
| `Clinimatix.Caravel.Auth.Windows` | ASP.NET Core Negotiate | Windows authentication for intranet apps. |
| `Clinimatix.Caravel.Events` | Microsoft.Extensions.DependencyInjection | In-process events. |
| `Clinimatix.Caravel.Queues` | EF Core | Durable database jobs and workers. |
| `Clinimatix.Caravel.Scheduling` | Queues | Fixed-interval schedules that enqueue jobs. |
| `Clinimatix.Caravel.Storage` | Microsoft.Extensions.DependencyInjection | Streaming local storage disks. |
| `Clinimatix.Caravel.Mail` | MailKit, Queues | SMTP, message templates, attachments, development capture and queued delivery. |
| `Clinimatix.Caravel.Notifications` | Mail, Microsoft.Extensions.Http | Replaceable mail/SMS channels, capture and an optional Twilio HTTP adapter. |

New packages are added when there's real integration work to do. Caching, for example, is a [recipe over .NET's own services](CACHING.md) until a Caravel package would add something useful.

## Where things stand

**`26.1.0-rc1` is published as Public Preview 1**, the first release candidate. All twelve packages are available on [nuget.org](https://www.nuget.org/profiles/Clinimatix), with [GitHub release assets](https://github.com/Clinimatix/Caravel/releases/tag/v26.1.0-rc1) retained for local-feed installation. Its scope is the available capability set below; broader development is organized into architecture milestones:

| Milestone | Focus | Status |
| --- | --- | --- |
| **M0: Foundation** | Core, ASP.NET Core integration, routing, configuration, service providers, `caravel new`, `serve`, `route:list` and `doctor` | Done |
| **M1: Clarion** | EF Core integration, models, queries, timestamps, soft deletes, factories, seeders, migrations and data commands | Done; broader provider coverage continues |
| **M2: Application services** | Identity, Windows authentication, events, durable queues and workers, scheduling, storage and caching | Largely done; see below |
| **M3: Developer experience** | `caravel dev`, starter kits, generators, testing helpers, Windows Service and IIS helpers, optional Aspire support | In progress |
| **M4: AI-native Caravel** | Microsoft.Extensions.AI integration, embeddings, structured output, MCP server support, agent tooling and search | Planned |

Architecture milestones (M0–M4) are separate from release version suffixes: `26.1.0-m1` identifies the first milestone candidate for the 26.1 family, not "milestone 1 only". See the [release policy](RELEASE-POLICY.md) for versioning.

### Available today

These capabilities form the 26.1 release scope. The later work listed below can arrive in subsequent release families; it is not a prerequisite for 26.1 stable.

- Routing, configuration, service providers, validation defaults and route inspection
- Clarion with SQL Server, PostgreSQL and SQLite
- Bosun: Razor and API starters, development sessions, data commands, and job/event/listener generators
- Local Identity accounts with opt-in registration, confirmation/password recovery and TOTP MFA/recovery codes; Windows authentication; and recipes for OpenID Connect sign-in and bearer-token APIs
- Events, durable queues with retries, replay and bounded lease renewal, transactional outbox dispatch, fixed-interval scheduling and local storage
- Transactional mail with templates, attachments and SMTP; replaceable mail/SMS notification channels with development capture and a Twilio adapter
- Queue metrics and tracing through standard .NET diagnostics
- Samples covering a data-only app, a worker, an authenticated backend and OIDC sign-in

### Coming next

- **Accounts:** passkeys and finished account-management screens built on the native account endpoints
- **Scheduling:** calendar and time-zone-aware schedules (cron-style)
- **Storage:** cloud drivers such as Azure Blob Storage and S3
- **Queues:** in-memory and synchronous drivers, batches and chains
- **Mail and notifications:** additional mail/SMS providers, in-app inboxes, browser/mobile push, chat channels, delivery receipts and recipient preferences; see [channel plans and current limits](NOTIFICATIONS.md#later-channel-work)
- **Starter kits:** Blazor and React, plus authentication scaffolding
- **Deployment helpers:** Windows Service and IIS publishing, and optional Aspire integration
- **Databases:** MariaDB/MySQL, once an EF Core 10–compatible provider is available (see [database providers](DATABASE-PROVIDERS.md))
- **AI and MCP:** the M4 packages listed above

### Toward stable 26.1

Public Preview 1 has passed release-candidate qualification, including cross-platform checks on Windows, Linux and macOS and the supported database/provider paths. Deployment and application-specific verification remain separate.

Work toward stable 26.1 focuses on integration feedback, fixes, documentation and upgrade validation. RCs remain public previews, not stable releases or a compatibility commitment. See the [release policy](RELEASE-POLICY.md) and [changelog](../CHANGELOG.md) for status and changes.

## Non-goals

Caravel succeeds by making excellent .NET technology feel cohesive, not by rewriting it. It won't include its own:

- web server, dependency injection container or logging framework
- ORM engine or identity database
- frontend framework or AI agent runtime
- message broker, admin panel or CMS

Where .NET already solves a problem well, Caravel's job is to make that solution easy to reach.
