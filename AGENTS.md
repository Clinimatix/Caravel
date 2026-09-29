# Working on Clinimatix Caravel

Guidance for coding agents and contributors working in this repository. Start with the [documentation index](docs/README.md) and [design and roadmap](docs/ROADMAP.md).

## Naming

- The product is **Clinimatix Caravel**, maintained by **Clinimatix, LLC**. Use the full legal name for copyright and formal attribution. Keep original authorship and the MIT license intact.
- NuGet package IDs are `Clinimatix.Caravel.*`. C# namespaces stay `Caravel.*`, the CLI command is `caravel`, and Bosun (CLI) and Clarion (data layer) are subsystem names.

## Platform

- Target the latest generally available .NET **LTS**: currently .NET 10, C# 14 and ASP.NET Core 10. Newer STS or preview releases don't change the target. When the next LTS ships, update the target framework, SDK pin, dependencies, templates, tests and docs together.
- Build and test `Caravel.slnx` with the SDK pinned in `global.json`. Restore with `--locked-mode`. See [Testing Caravel](docs/TESTING.md) for the package and end-to-end scripts; changes to Bosun, starters or packaging should be verified with the installed-tool smoke test, not only unit tests.

## Architecture

- Compose standard .NET hosting, dependency injection, configuration and endpoint routing. Don't build a replacement container, pipeline, ORM or agent runtime. No mandatory MediatR or commercially licensed dependency.
- Keep packages optional and independent. Core must not depend on web or EF packages.
- Clarion uses scoped DI sessions over EF Core. Don't introduce an ambient `AsyncLocal<DbContext>` or reflection-based controller invocation.
- Database providers (SQL Server/Azure SQL, PostgreSQL, SQLite) are chosen by the application. MariaDB waits for an EF Core 10–compatible Pomelo release; don't fork a provider, mix EF major versions or suppress dependency conflicts to get there early.

## Safety and quality

- Preserve secret handling, input validation, cancellation, safe path handling and accurate exit codes.
- `caravel doctor` is read-only. Destructive commands require `--force` or explicit confirmation.
- Never create databases or run migrations implicitly.
- Tests and examples use made-up data and disposable databases only.

## Documentation style

- Write for developers exploring or using Caravel. Lead with what they can build, show a working example, then explain behavior and limits in plain language.
- Describe what works today confidently, and label planned work as planned (link to the [roadmap](docs/ROADMAP.md)).
- Keep internal process notes, verification logs, test counts and artifact paths out of the docs. Summarize test coverage in a sentence when it helps readers trust a feature.
- Keep the Laravel non-affiliation notice in the README's license section only.
- Credit Laravel respectfully as an inspiration for developer experience. Explain features in terms of the problems they solve for .NET developers; avoid implying an official relationship or using another framework's feature list as their rationale.

## Releases

Follow [versions and releases](docs/RELEASE-POLICY.md): calendar versions `YY.Release.Patch`, compact prereleases such as `26.1.0-m1` and `26.1.0-rc1`, compatibility within a stable release family, exact version pins, and deliberate tagging and publishing. Never move or reuse a published version. Tags, releases and package publishing are separate, deliberate steps, not part of routine pushes.
