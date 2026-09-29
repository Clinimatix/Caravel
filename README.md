# Clinimatix Caravel

**Expressive applications. Native .NET foundations.**

Clinimatix Caravel is an open-source application framework that brings expressive routing, approachable data access, and integrated command-line tools to .NET. Inspired by Laravel's attention to developer experience, it builds on ASP.NET Core and Entity Framework Core, so the .NET tools and libraries you already know stay within reach.

Use the whole framework, or pick just the packages you need.

Caravel is in **prerelease development**. [Features](#what-you-can-build) · [Quick start](#quick-start) · [Documentation](https://github.com/Clinimatix/Caravel/blob/main/docs/README.md) · [Status](#project-status)

```csharp
using Caravel.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCaravel();
var app = builder.Build();
app.UseCaravel();

app.Routes(routes =>
    routes.Get("/hello/{name}", (string name) => $"Hello, {name}!")
        .Name("greeting"));

// Lets `caravel route:list` inspect your routes; otherwise does nothing.
if (await app.ExportCaravelRoutesAsync(args)) return;
await app.RunAsync();
```

## What you can build

- **Expressive routing.** Typed handlers, named routes, and route groups, with ASP.NET Core authorization and metadata at hand.
- **Clarion data access.** Query plain C# models with LINQ, and add timestamps, soft deletes, factories, and seeders. EF Core's relationships, transactions, and concurrency controls are still there when you need them. [Meet Clarion](https://github.com/Clinimatix/Caravel/blob/main/docs/CLARION.md).
- **Bosun tooling.** The `caravel` command creates Razor or API apps, runs development sessions, lists routes, generates models, jobs and listeners, and manages migrations. [Data commands](https://github.com/Clinimatix/Caravel/blob/main/docs/BOSUN-DATA.md) · [Development sessions](https://github.com/Clinimatix/Caravel/blob/main/docs/DEVELOPMENT.md).
- **Authentication that fits .NET.** Local Identity accounts with opt-in registration, email confirmation, password recovery and authenticator-app MFA, Windows authentication for intranets, and recipes for OpenID Connect sign-in and bearer-token APIs. [Set up authentication](https://github.com/Clinimatix/Caravel/blob/main/docs/AUTHENTICATION.md).
- **Simple events.** Dispatch events to ordered, scoped listeners and test them with a recording fake. [Use events](https://github.com/Clinimatix/Caravel/blob/main/docs/EVENTS.md).
- **Durable background work.** Save typed jobs in your database, renew leases for longer work, retry failures, replay dead letters, and enqueue recurring work on fixed intervals. An application outbox commits dispatch intent alongside business data. [Queues](https://github.com/Clinimatix/Caravel/blob/main/docs/QUEUES.md) · [Scheduling](https://github.com/Clinimatix/Caravel/blob/main/docs/SCHEDULING.md).
- **Mail and notifications.** Compose transactional email with templates and attachments, queue delivery, and choose replaceable mail or SMS channels. Development captures make messages easy to test. [Mail](https://github.com/Clinimatix/Caravel/blob/main/docs/MAIL.md) · [Notifications](https://github.com/Clinimatix/Caravel/blob/main/docs/NOTIFICATIONS.md).
- **Local storage disks.** Stream files into named, application-owned directories with safe, create-only writes. [Use storage](https://github.com/Clinimatix/Caravel/blob/main/docs/STORAGE.md).
- **A cohesive foundation.** Service providers, async startup, `.env` configuration, built-in validation, consistent HTTP errors, and queue metrics through standard .NET diagnostics.
- **Practical starting points.** A Razor starter, an API starter with OpenAPI, data and worker samples, and an [authenticated backend sample](https://github.com/Clinimatix/Caravel/blob/main/docs/BACKEND-SAMPLE.md) that takes you from sign-in to durable processing to reporting.

It is organized into optional packages, so you can take as much or as little as you like:

| Package | What it gives you |
| --- | --- |
| `Clinimatix.Caravel.Core` | Configuration and service providers |
| `Clinimatix.Caravel.AspNetCore` | Routing and web defaults for ASP.NET Core |
| `Clinimatix.Caravel.Clarion` | Data access on EF Core; works in any .NET app |
| `Clinimatix.Caravel.Bosun` | The `caravel` command-line tool |
| `Clinimatix.Caravel.Auth` | ASP.NET Core Identity with secure defaults |
| `Clinimatix.Caravel.Auth.Windows` | Windows (Negotiate) authentication for intranet apps |
| `Clinimatix.Caravel.Events` | In-process events and scoped listeners |
| `Clinimatix.Caravel.Queues` | Durable database jobs and workers |
| `Clinimatix.Caravel.Scheduling` | Fixed-interval schedules that enqueue jobs |
| `Clinimatix.Caravel.Storage` | Streaming local storage disks |
| `Clinimatix.Caravel.Mail` | SMTP email, templates, attachments, capture and queued delivery |
| `Clinimatix.Caravel.Notifications` | Mail/SMS channels, capture and an optional Twilio adapter |

## Quick start

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Nothing else is required: no database server, Node, or Docker.

The packages aren't on NuGet yet, so for now you run Caravel from a clone of this repository:

```powershell
git clone https://github.com/Clinimatix/Caravel.git
cd Caravel

# Create a new app that references this checkout, then run it
dotnet run --project src/Caravel.Bosun -- new MyApp --framework-source .
dotnet run --project src/Caravel.Bosun -- serve --project MyApp
```

Building a backend? Add `--stack api` to `new` for a starter with request validation, OpenAPI and a health endpoint. Use `dev --project MyApp` when you want .NET to watch for changes.

Other handy commands:

```powershell
dotnet run --project src/Caravel.Bosun -- route:list --project MyApp   # list your routes
dotnet run --project src/Caravel.Bosun -- doctor                       # check your setup
```

Once the packages are published, `caravel new MyApp` and `caravel serve` will do the same thing.

## Learn more

- [Getting started](https://github.com/Clinimatix/Caravel/blob/main/docs/GETTING-STARTED.md): app setup, service providers, validation, and security defaults
- [Configuration](https://github.com/Clinimatix/Caravel/blob/main/docs/CONFIGURATION.md): settings files, `.env`, and environment variables
- [Clarion](https://github.com/Clinimatix/Caravel/blob/main/docs/CLARION.md): models, queries, soft deletes, factories, and seeders
- [Database providers](https://github.com/Clinimatix/Caravel/blob/main/docs/DATABASE-PROVIDERS.md): which databases work today
- [Authentication](https://github.com/Clinimatix/Caravel/blob/main/docs/AUTHENTICATION.md): accounts, sign-in and policies
- [Queues](https://github.com/Clinimatix/Caravel/blob/main/docs/QUEUES.md) and [the backend sample](https://github.com/Clinimatix/Caravel/blob/main/docs/BACKEND-SAMPLE.md): durable background work, end to end
- [All documentation](https://github.com/Clinimatix/Caravel/blob/main/docs/README.md)

## Project status

The current source candidate is **`26.1.0-m1`**, a milestone that hasn't been published as a package release. The application foundation and Clarion data layer are in place, most application services have landed, and developer tooling is well underway. APIs may still change between milestones; the [changelog](https://github.com/Clinimatix/Caravel/blob/main/CHANGELOG.md) notes what changed.

- **Databases:** SQL Server, PostgreSQL, and SQLite are supported and tested. MariaDB is planned once an EF Core 10–compatible provider is available. See [database providers](https://github.com/Clinimatix/Caravel/blob/main/docs/DATABASE-PROVIDERS.md).
- **Coming next:** passkeys, calendar scheduling, cloud storage, more notification channels and starter kits, and AI tooling. See the [roadmap](https://github.com/Clinimatix/Caravel/blob/main/docs/ROADMAP.md).
- **Production use:** not recommended yet. The first release candidate is the point to start planning production use; see [versions and releases](https://github.com/Clinimatix/Caravel/blob/main/docs/RELEASE-POLICY.md).

This version is a fresh start that replaces an earlier prototype.

## License and stewardship

Clinimatix Caravel is developed and maintained by Clinimatix, LLC and available under the [MIT license](https://github.com/Clinimatix/Caravel/blob/main/LICENSE), with original authorship preserved. Contributions, bug reports, and ideas are welcome. See [Contributing](https://github.com/Clinimatix/Caravel/blob/main/CONTRIBUTING.md) and [Security](https://github.com/Clinimatix/Caravel/blob/main/SECURITY.md).

Clinimatix Caravel is not affiliated with Laravel or Laravel, Inc.
