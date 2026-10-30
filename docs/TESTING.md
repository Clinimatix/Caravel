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

The `scripts/` folder has PowerShell 7 scripts that pack the framework, install it into an isolated package cache and exercise it the way an application would. Scratch files and evidence use `artifacts/` or an owned system temporary directory outside the checkout; scripts report their run directories. Nothing is installed globally or published.

| Script | What it checks | Run it when you change… |
| --- | --- | --- |
| `Test-PackageSmoke.ps1` | Discovers and packs every source package ID, checks compiled debug paths, installs the `caravel` tool locally, then checks Razor, API and Identity starters; `-Browser` includes generated browser regressions | Bosun, the starters or packaging |
| `Test-DataSmoke.ps1` | Migrates, seeds, upgrades and rolls back a throwaway SQLite database using the packaged tool and Clarion | Clarion or the data commands |
| `Test-ServicePackages.ps1` | Runs the Clarion, Queues, Storage, Scheduling, Mail and Notifications tests against packed packages | Those packages or their packaging |
| `Test-IdentitySmoke.ps1` | Runs the Identity, backend and OIDC sample tests against packed packages | Auth or the Identity/OIDC samples |
| `Test-IdentityStarterSmoke.ps1` | Installs Bosun and checks the generated Identity app, explicit native migrations, commands, sessions and readiness; `-Browser` includes its browser regressions | The Identity starter or shared sample code |
| `Test-ReleaseUpgrade.ps1` | Creates persisted Identity/application/queue state using published RC2 packages, upgrades exact pins and locks to candidate packages, then checks persistence and behavior with SQLite | Release preparation or compatibility-sensitive package changes |
| `Test-WorkerSmoke.ps1` | Runs the worker sample end to end, including a crash after a result is saved, recovery and duplicate-free totals | Queues, hosting or the worker sample |
| `Test-LtsPolicy.ps1` and `Test-LtsPolicy.Tests.ps1` | Confirms the repository targets the latest .NET LTS | The target framework or SDK pin |

Run them with `pwsh -File scripts/<name>.ps1`. `Test-ServicePackages.ps1` expects the solution to be restored first.

Service-package checks also exercise Core's in-process Fathom contracts. For an isolated
development candidate, pass `-PackageVersion 0.0.0-local.<unique-id>` to
`Test-ServicePackages.ps1`; all tested packages use that version in a fresh mapped cache.
Keep its checksums and source revision separate from published package identities.

To test existing CI or release artifacts without rebuilding them, pass
`-CandidatePackageDirectory <downloaded-artifact>` instead. Nested package directories
are supported. The script requires one package per tested component, matching versions
and source commits, and verifies that restored package bytes match the supplied files.
It records the seven service-package identities and SHA-256 hashes, run metadata and
TRX results in its run directory's `evidence` folder. This manifest does not inventory
the other packages in a full release artifact. Do not combine
this mode with `-PackageVersion`: supplied artifacts keep their original identities.
Run `scripts/Test-ServicePackages.Tests.ps1` to check rejection of incomplete,
ambiguous or inconsistent artifact inputs and restored-graph integrity guards without
building the framework. Add `-ExpectedRepositoryCommit <sha>` to compare declared
source metadata with an independently selected revision; CI portability uses the run SHA.
`-CandidateArtifactName <name>` records the supplied artifact's identity. Matching declared
commits and bytes does not authenticate their origin: obtain artifacts through a trusted
CI/release channel and independently review its run, revision and checksums.

CI retains separate per-OS source and portability evidence artifacts for 14 days, identified
by workflow SHA, run ID and attempt. Collection runs even after test failure. The exporter
allowlists only run/package JSON and these seven synthetic suites' TRX files, limits each
file to 2 MiB and the complete export to 100 files/20 MiB, requires an empty destination,
and excludes caches, package
payloads, configuration and arbitrary logs. A missing or failed suite is not a pass.

Run `pwsh -File scripts/Test-ReleaseUpgrade.ps1` for the published-RC2-to-source-candidate gate. To consume already packed candidate artifacts, add `-CandidatePackageDirectory <directory>`. Select an earlier published baseline explicitly with `-BaselineVersion <version>` when qualifying an additional upgrade path. This check uses real package references and disposable SQLite state, rather than substituting candidate source projects for the published baseline.

Release builds map the source root to `/_/` so compiled debug metadata does not disclose the build machine's checkout path. Debug builds retain local paths for normal debugging. To inspect an existing package directory separately, run `pwsh -File scripts/Test-PackagePaths.ps1 -PackageDirectory <directory>`.

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

## Browser state regressions

`pwsh scripts/Test-IdentityStarterSmoke.ps1` packs and installs Bosun, generates the Identity profile outside the checkout with isolated package resolution, and exercises commands, session revocation, readiness and explicit migrations. Add `-Browser` after installing the browser test dependencies below to run the generated browser regressions too. `Test-PackageSmoke.ps1 -Browser` also includes generated Identity checks. `Test-ProviderContainers.ps1` checks generated SQL Server/PostgreSQL migrations; pass `-IdentityStarterOnly` for just those generated-provider checks. These use disposable synthetic data and remove owned containers.

Pass `-CandidatePackageDirectory <downloaded-artifact>` to consume existing candidate packages without repacking the framework. This mode accepts the downloaded artifact's nested package directories, uses fresh mapped caches, and checks that the installed package hashes match the supplied bytes. It also verifies nested generated files before compiling and exercising the application.

`tests/browser` uses Node.js 20 or later and a pinned Playwright development dependency. Run `npm ci`, `npx playwright install chromium`, then `npm test` in that directory. Set `PLAYWRIGHT_CHANNEL=msedge` to use an installed Edge browser instead. These tests load the real sample HTML/JavaScript with synthetic HTTP responses to reproduce delayed success/error races, current access loss and preserved retry commands. They do not replace the backend's authorization and transaction tests.

To check a generated application's same browser contract, set `CARAVEL_BROWSER_APP_ROOT` to its root directory. Browser tooling is a test dependency, not an application runtime requirement.

## Optional Azure storage

Run `pwsh ./scripts/Test-AzureStorage.ps1` with a local Linux Docker engine. It starts a loopback-only Azurite 3.37.0 container with synthetic credentials, runs source and fresh installed-package contracts, and removes the exact owned container/volumes in `finally`. No Azure account is used. Unconfigured emulator cases explicitly skip during ordinary solution tests; this script requires them to execute and pass. HTTP transport tests also compile and exercise the authorized-download example. Cloud deployment and historical version retention require separate verification.

## Linux containers and child processes

Some Bosun tests start and stop child processes. When you run the suite inside a Linux container, start it with Docker's `--init` option (or your runtime's equivalent). Without an init process, stopped child processes linger as zombies and the cleanup tests will correctly fail.

## Worker container shutdown

The provider suite also exercises Generic Host shutdown on SQLite, SQL Server and
PostgreSQL. It waits for a handler to persist a synthetic result, stops the host, and
checks both throwing and normally returning cancellation paths. The interrupted lease
must retain its attempt and availability without a failure or acknowledgement. A fresh
host recovers it after expiry and completes without duplicating the saved result.
These in-process host tests complement the Linux container signal checks below.

Use `Test-ServicePackages.ps1 -IncludeProviderTests` with both disposable server
configurations to run the provider contracts against isolated NuGet packages. Supplied
candidate mode verifies declared source revisions and restored archive hashes, with no
source project references. The full CI provider job consumes the retained Windows-built
packages and exports `Provider.trx` alongside its package evidence; skipped provider tests
fail that qualification. These checks do not establish application deployment acceptance.

`Test-WorkerContainer.ps1` exercises the supplied Queues package in a disposable, non-root
Linux runtime image. Alongside crash recovery and idle shutdown, it sends SIGTERM while a
synthetic handler is paused after committing a result. The receipt retains the paused and
cancelled job identity, exit status, uncompleted lease, expiry and recovered result counts.
A separate volume prevents earlier completed jobs from satisfying the shutdown check.
`Test-WorkerContainerShutdown.Tests.ps1` checks observation and failure handling without Docker.
The pause is injected only into the disposable sample copy; package and application code stay intact.
See [hosting](HOSTING.md#qualify-a-worker-runtime-image) for prerequisites and boundaries.

## Continuous integration

`Test-InstalledManifest.ps1 -ToolPath <installed-caravel> -BosunPackagePath <package>`
checks Fathom's static manifest through an existing installed Bosun shim. It compares the
installed archive with the selected package, then inspects synthetic modern and legacy XML
projects with SDK/import/build/startup traps and synthetic private values. Project and directory
selection must agree without changing files; malformed, DTD, oversized, missing and ambiguous
inputs must fail with the fixed content-free diagnostic. Add `-ExpectedRepositoryCommit <sha>`
to compare the package's declared source against an independently selected revision.
The helper installs nothing, restores nothing and invokes only `schema` and `agent:manifest`.

The package and Identity-starter smoke scripts run this helper using their existing tool
installations. Portability jobs exercise the Windows-built Bosun package without repacking it.
CI retains only bounded manifest receipts containing package/version/hash, declared/expected
revision, workflow identity and case names; project source, command output and configuration
are excluded. Hash equality and matching declarations do not authenticate provenance: retain
the trusted artifact/run identity separately. A receipt is written only after all checks pass;
missing receipts are not a pass. `Test-InstalledManifest.Tests.ps1` checks package-input guards
without installing or executing a tool.

`.github/workflows/build.yml` defines the Windows, Linux and macOS matrix, including source/generated browser regressions, package checks and the published-RC2 upgrade gate. The server-provider job checks provider contracts, packaged backends and generated Identity applications on SQL Server/PostgreSQL. A separate Ubuntu job runs source and installed Azure packages against disposable Azurite; it uses no cloud resource. The workflow is configured for manual dispatch, with no automatic push or pull-request triggers. In your pull request, list the checks you ran locally and any platforms you couldn't test.

The complete run also downloads the Windows package artifact into Linux and macOS jobs and exercises its installed Identity starter and browser without rebuilding the framework packages. Release packages must work on supported consumer platforms regardless of the build host.

The `platform` input defaults to `all`. Select a single OS to investigate that platform using the same build, test and package checks. A single-platform run skips the server-provider job and does not replace the complete release matrix.

## Changing dependencies

Packages are pinned centrally with lock files. To change one, restore normally, review the lock-file changes, then confirm that `dotnet restore --locked-mode` passes. Don't disable NuGet vulnerability auditing to get a green build.
