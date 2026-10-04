# Clinimatix Caravel documentation

New here? Start with the [repository README](../README.md) and its quick start.

## The basics

- [Getting started](GETTING-STARTED.md): how a Caravel app starts up, and what it sets up for you
- [Configuration](CONFIGURATION.md): settings files, `.env` and environment variables
- [Development sessions](DEVELOPMENT.md): watch your web app and run a worker with one command
- [Hosting](HOSTING.md): run web apps and workers as services, containers or IIS sites

## Data

- [Clarion](CLARION.md): query and save models, soft deletes, factories and seeders
- [Bosun data commands](BOSUN-DATA.md): generate models and run migrations and seeders
- [Database providers](DATABASE-PROVIDERS.md): which databases you can use today

## Authentication

- [Local accounts](AUTHENTICATION.md): ASP.NET Core Identity with secure defaults
- [Sign in with an identity provider](OIDC-AUTHENTICATION.md): OpenID Connect for browser sign-in
- [Service authentication](SERVICE-AUTHENTICATION.md): protect APIs with bearer access tokens
- [Windows authentication](WINDOWS-AUTHENTICATION.md): intranet sign-in with organizational accounts

## Application services

- [Events](EVENTS.md): notify other parts of your app
- [Queues](QUEUES.md): durable background jobs that survive restarts
- [Mail](MAIL.md): transactional email, templates, attachments and queued sending
- [Notifications](NOTIFICATIONS.md): replaceable mail and SMS delivery channels
- [Scheduling](SCHEDULING.md): enqueue recurring work at fixed intervals
- [Bosun service generators](BOSUN-SERVICES.md): generate jobs, events and listeners
- [Local storage](STORAGE.md): stream files into named storage disks
- [Azure Blob storage](AZURE-STORAGE.md): optional Azure SDK adapter, conditional transfers and authorized downloads
- [Caching](CACHING.md): use .NET's memory and SQL Server caches
- [Observability](OBSERVABILITY.md): queue metrics, tracing and health checks

## Putting it together

- [Backend sample](BACKEND-SAMPLE.md): sign-in, durable ingestion and reporting working together
- [Authorized commands](AUTHORIZED-COMMANDS.md): workspace access, edit conflicts, retry receipts and background notices in a small browser application

## About the project

- [Design and roadmap](ROADMAP.md): principles, package layout, current status and what's next
- [Upgrading from RC1](UPGRADING.md): exact versions, dependency locks and explicit schema changes
- [Versions and releases](RELEASE-POLICY.md): what milestone, release candidate and stable mean
- [Testing Caravel](TESTING.md): build, test and verify changes as a contributor
- [Changelog](../CHANGELOG.md)
- [Contributing](../CONTRIBUTING.md) and [security](../SECURITY.md)
