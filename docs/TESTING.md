# Testing Caravel

This guide is for contributors. It explains how to build and test the framework locally and which extra checks to run for different kinds of changes.

## Build and run the tests

Use the SDK pinned in `global.json` (currently .NET 10). From the repository root:

```powershell
dotnet restore Caravel.slnx --locked-mode
dotnet build Caravel.slnx -c Release --no-restore
dotnet test Caravel.slnx -c Release --no-build --no-restore
```

A clean build should finish with zero warnings; please keep it that way. Tests use temporary files and SQLite databases, and clean up after themselves.

A handful of database tests are skipped unless you point them at SQL Server or PostgreSQL (see [below](#test-against-sql-server-and-postgresql)). A skipped test is reported as skipped, never as passed.

## Package and end-to-end checks

The `scripts/` folder has PowerShell 7 scripts that pack the framework, install it into an isolated package cache and exercise it the way an application would. Everything they create goes under `artifacts/`. Nothing is installed globally or published.

| Script | What it checks | Run it when you change… |
| --- | --- | --- |
| `Test-PackageSmoke.ps1` | Packs every package, installs the `caravel` tool locally, then generates, builds and runs Razor and API starters | Bosun, the starters or packaging |
| `Test-DataSmoke.ps1` | Migrates, seeds, upgrades and rolls back a throwaway SQLite database using the packaged tool and Clarion | Clarion or the data commands |
| `Test-ServicePackages.ps1` | Runs the Clarion, Queues, Storage and Scheduling tests against packed packages | Those packages or their packaging |
| `Test-IdentitySmoke.ps1` | Runs the Identity, backend and OIDC sample tests against packed packages | Auth or the Identity/OIDC samples |
| `Test-WorkerSmoke.ps1` | Runs the worker sample end to end, including a crash after a result is saved, recovery and duplicate-free totals | Queues, hosting or the worker sample |
| `Test-LtsPolicy.ps1` and `Test-LtsPolicy.Tests.ps1` | Confirms the repository targets the latest .NET LTS | The target framework or SDK pin |

Run them with `pwsh -File scripts/<name>.ps1`. `Test-ServicePackages.ps1` expects the solution to be restored first.

## Test against SQL Server and PostgreSQL

The shared provider tests in `tests/Caravel.Provider.Tests` check that Clarion, the queue and a database-backed authorization example behave the same way on every supported database.

**With Docker.** If you have a local Linux Docker engine running, this script does everything:

```powershell
pwsh -File scripts/Test-ProviderContainers.ps1
```

It starts fresh SQL Server and PostgreSQL containers with random credentials on loopback-only ports, runs the provider tests, a migration workflow (`Test-ProviderMigrationSmoke.ps1`) and the packaged backend sample against both, then removes the containers and their volumes. It doesn't start Docker for you or connect to a remote engine.

**With your own servers.** Point the tests at disposable servers you control, using a loopback address (`localhost`, `127.0.0.1` or `::1`). The fixtures reject remote hosts:

```powershell
$env:CARAVEL_TEST_SQLSERVER = '<connection string>'
$env:CARAVEL_TEST_POSTGRES = '<connection string>'
dotnet test tests/Caravel.Provider.Tests
```

Each test creates its own randomly named database and drops only that database afterward. Use throwaway servers, never a database that holds real data.

To run the composed backend against either server, restore the solution first, then run:

```powershell
pwsh -File scripts/Test-IdentitySmoke.ps1 -Provider sqlserver
pwsh -File scripts/Test-IdentitySmoke.ps1 -Provider postgres
```

Each command requires the corresponding connection variable above. It packs the framework into an isolated feed, copies the sample, generates migrations for that provider and runs the shared account/backend tests with no skips. Those tests include duplicate and conflicting submissions, concurrent processing, schema failures, application-host restart, lost-acknowledgement recovery, schema upgrade/rollback and real Kestrel request-limit checks. The default `sqlite` run also exercises OIDC.

## Linux containers and child processes

Some Bosun tests start and stop child processes. When you run the suite inside a Linux container, start it with Docker's `--init` option (or your runtime's equivalent). Without an init process, stopped child processes linger as zombies and the cleanup tests will correctly fail.

## Continuous integration

`.github/workflows/build.yml` defines the Windows, Linux and macOS matrix, including package checks and SQL Server/PostgreSQL service containers. It is configured for manual dispatch, with no automatic push or pull-request triggers. In your pull request, list the checks you ran locally and any platforms you couldn't test.

The `platform` input defaults to `all`. Select a single OS to investigate that platform using the same build, test and package checks. A single-platform run skips the server-provider job and does not replace the complete release matrix.

## Changing dependencies

Packages are pinned centrally with lock files. To change one, restore normally, review the lock-file changes, then confirm that `dotnet restore --locked-mode` passes. Don't disable NuGet vulnerability auditing to get a green build.
