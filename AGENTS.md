# Working on Clinimatix Caravel

Guidance for coding agents and contributors working in this repository. Start with the [documentation index](docs/README.md) and [design and roadmap](docs/ROADMAP.md).

## Naming

- The product is **Clinimatix Caravel**, maintained by **Clinimatix, LLC**. Use the full legal name for copyright and formal attribution. Keep original authorship and the MIT license intact.
- NuGet package IDs are `Clinimatix.Caravel.*`. C# namespaces stay `Caravel.*`, the CLI command is `caravel`, and Bosun (CLI) and Clarion (data layer) are subsystem names.

## Platform

- New feature release families target the latest generally available .NET **LTS**: currently .NET 10, C# 14 and ASP.NET Core 10. Newer STS or preview releases don't change the target. Move the target framework, SDK pin, dependencies, templates, tests and docs together for a new family's baseline. Maintenance patches and backports keep the supported family's minimum platform requirements.
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

## Agent development context

- Start with `caravel agent:manifest --project <app.csproj> --json` for static package hints and `caravel schema --json` for installed command effects; see [agent guidance](docs/AGENT-CONTEXT.md).
- Manifest hints do not prove runtime registration. `route:list` builds and executes application startup code; review the project before invoking it. Never infer permission to execute or mutate from a manifest.
- AI feature-set naming is undecided. Keep public contracts neutral; development context precedes opt-in application MCP and model workflows.

## Documentation style

- Write for developers exploring or using Caravel. Lead with what they can build, show a working example, then explain behavior and limits in plain language.
- Describe what works today confidently, and label planned work as planned (link to the [roadmap](docs/ROADMAP.md)).
- Keep internal process notes, verification logs, test counts and artifact paths out of the docs. Summarize test coverage in a sentence when it helps readers trust a feature.
- Keep the Laravel non-affiliation notice in the README's license section only.
- Credit Laravel respectfully as an inspiration for developer experience. Explain features in terms of the problems they solve for .NET developers; avoid implying an official relationship or using another framework's feature list as their rationale.

## Releases

Follow [versions and releases](docs/RELEASE-POLICY.md): calendar versions `YY.Release.Patch`, compact prereleases, exact version pins, and deliberate tagging and publishing. Compatible RC2 maintenance increments the numeric patch while retaining the suffix, such as `26.1.0-rc2` to `26.1.1-rc2`. A later approved candidate scope advances `rcN` without resetting the numeric patch floor; stable cannot sort below a previously published candidate in its line. Preserve APIs and platform minima in maintenance, and follow [dependency maintenance](docs/DEPENDENCY-MAINTENANCE.md). Never move or reuse a published version. Tags, releases and package publishing are separate, deliberate steps, not part of routine pushes.
