# Upgrading from RC1 to RC2

The `26.1.0-rc2` candidate adds an installed Bosun Identity starter, the sample's authorized-command browser flow and optional Azure Blob storage. Applications remain ordinary .NET 10 projects. Adopt the additions you need; review your own policies, migrations and transport configuration before running them.

[GitHub releases](https://github.com/Clinimatix/Caravel/releases) and [NuGet](https://www.nuget.org/profiles/Clinimatix) are the canonical distribution locations. The commands below require a configured feed containing `26.1.0-rc2`. You can also use your selected source checkout with [source references](GETTING-STARTED.md#creating-and-running-an-app).

## Update package pins and locks

Keep all first-party `Clinimatix.Caravel.*` package references on the same exact version. Update every reference you use, including central package versions in `Directory.Packages.props` if applicable. For example:

```powershell
dotnet add package Clinimatix.Caravel.AspNetCore --version 26.1.0-rc2
dotnet add package Clinimatix.Caravel.Auth --version 26.1.0-rc2
dotnet add package Clinimatix.Caravel.Queues --version 26.1.0-rc2
dotnet tool update --global Clinimatix.Caravel.Bosun --version 26.1.0-rc2
```

For a manifest-managed Bosun tool, use `dotnet tool update Clinimatix.Caravel.Bosun --version 26.1.0-rc2` in its manifest directory. Update only tools and packages your application uses. Caravel namespaces and the `caravel` command stay the same.

Regenerate dependency locks deliberately, inspect the changes and commit them alongside version pins:

```powershell
dotnet restore --use-lock-file --force-evaluate
dotnet restore --locked-mode
dotnet build --no-restore
```

Run your application's tests and verify sign-in, command retries and worker recovery with disposable data. Keep the previous package pins, locks and application revision for rollback. Rolling back package versions does not roll back application data or migrations.

## Existing applications and schemas

Updating RC1 packages does **not** require a mandatory migration of existing Identity or queue tables. The new workspace, work-item, attributed-history and receipt models are application-owned examples; they are not automatically installed by upgrading a library.

The repository's `samples/Caravel.Identity` adds the explicit `20261003051001_AddWorkItems` migration to `IdentityContext`. If you run that updated sample against an existing sample database, inspect the additive migration and apply it deliberately using the [backend setup commands](BACKEND-SAMPLE.md#prepare-the-two-database-contexts). Keep its existing migration history. Adapted applications should define and review their own native migrations, including the outbox model if adopting transactional notices. Runtime startup never migrates a database.

The [authorized-command guide](AUTHORIZED-COMMANDS.md) explains current membership checks, version preconditions, semantic retry receipts and notice eligibility. Keep those boundaries together when copying or adapting the example. A successful command means business data and dispatch intent committed; it does not promise exactly-once external delivery.

## Try the Identity starter in a new directory

```powershell
caravel new MyWorkspace --stack identity
```

The generator refuses an existing destination. It does not overwrite or upgrade your application. Generate separately and review the editable C# and browser code before adopting it. Existing Razor and API starter choices remain available.

Follow the new application's README: restore packages and local tools, choose a disposable database, explicitly apply both Identity and queue migrations, supply synthetic credentials through process environment variables, and run the Development-only account/workspace seed commands. Workers are opt-in and mail is captured locally. Generation creates no database, account, credential or cloud resource.

Bundled migrations target SQLite. For SQL Server or PostgreSQL in a **new, never-migrated** generated app, first move the bundled `Database` directory outside the project, select `Caravel__DatabaseProvider` and your own connection string, then generate and review native migrations for both contexts. The generated README gives the commands. Never apply SQLite migrations to a server database or discard an existing application's applied migration history.

The small browser UI supports password sign-in/sign-out and work items. Registration is disabled by default; full registration, recovery and MFA screens are not generated. Account endpoints remain available, but you own the pages and confirmation/reset URLs. External OIDC/bearer identity and production mail require deliberate application integration and qualification.

## Opt into Azure Blob storage

```powershell
dotnet add package Clinimatix.Caravel.Storage.Azure --version 26.1.0-rc2
```

The new package is optional. Core, Bosun and local storage remain independent of Azure. Supply your own `BlobContainerClient` for an existing container; registration never provisions resources or discovers credentials.

Follow [Azure Blob storage](AZURE-STORAGE.md) for create-only writes, native ETags, ranges, revision-pinned downloads and current application authorization. An ETag is a revision selector, not a content hash or scan verdict. Cloud identity/RBAC, version retention, scanning, private networking and deployment need separate setup and qualification; local emulator checks do not establish those guarantees.
