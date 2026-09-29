# Bosun data commands

The `caravel` command-line tool can generate models, seeders, factories, and migrations, and manage your database. Migrations run through the standard EF Core tools, so they work exactly as EF Core documents them.

| Command | What it does | Changes data? |
| --- | --- | --- |
| `make:model User` | Creates a model class in `App/Models/` | No |
| `make:seeder UsersSeeder` | Creates a seeder in `Database/Seeders/` | No |
| `make:factory UserFactory --model App.Models.User` | Creates a factory in `Database/Factories/` | No |
| `make:migration InitialCreate` | Creates an EF Core migration in `Database/Migrations/` | No |
| `migrate:status` | Lists migrations and whether each has been applied | No |
| `migrate` | Applies all pending migrations | Yes |
| `db:seed` | Runs your registered seeders | Yes |
| `migrate:rollback <target> --force` | Rolls back to an earlier migration | **Yes, can delete data** |
| `migrate:fresh --force` | Drops the database and rebuilds it from migrations | **Yes, deletes all data** |

Until the packages are published, run these with `dotnet run --project src/Caravel.Bosun -- <command>` from a clone of this repository.

## Setup

Your app needs three things:

1. **An EF Core provider and context,** configured the usual way (see [Clarion](CLARION.md)).
2. **The `Microsoft.EntityFrameworkCore.Design` package** in the startup project.
3. **A local `dotnet-ef` tool pinned to a 10.0.x version.** Bosun uses the tool manifest in your project folder (or a parent folder), never a globally installed tool.

To set up the tool in a new app:

```powershell
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12
```

In an existing app that already has a manifest, run `dotnet tool restore`. Keep the EF Core runtime, Design package, and tool on matching versions.

**Connection strings** come from your app's normal configuration: `appsettings.json`, user secrets, or environment variables. Bosun has no `--connection` option, doesn't read your connection string itself, and never picks or creates a database server for you.

**Your code runs.** Migration and seed commands start your app's startup code and EF Core design-time code, so only run them on projects you trust.

## Generating files

```powershell
caravel make:model User --project ./App/App.csproj
caravel make:seeder UsersSeeder --project ./App/App.csproj
caravel make:factory UserFactory --model App.Models.User --project ./App/App.csproj
caravel make:migration InitialCreate --project ./App/App.csproj --context AppDbContext
```

- **`make:model`** creates a `[ClarionModel]` class with an integer `Id`. Make sure your context [discovers Clarion models](CLARION.md#models).
- **`make:seeder`** creates a seeder that throws until you fill it in, so an empty seeder can't look like a success. Register it with `AddClarionSeeder<T>()`.
- **`make:factory`** creates a factory built on `ModelFactory<T>`. The model needs a public parameterless constructor; fill in its required properties in the factory.
- **`make:migration`** calls EF Core, which names the files and updates the model snapshot. Review each migration before applying it.

The generators accept `--namespace`. Names must be PascalCase. They never overwrite an existing file, and they refuse unsafe output paths.

## Running migrations

```powershell
caravel migrate --project ./App/App.csproj --context AppDbContext
caravel migrate:status --project ./App/App.csproj
caravel migrate:status --project ./App/App.csproj --no-connect
```

Every migration command accepts `--project`, `--startup-project`, and `--context`:

- Without `--project`, the current folder must contain exactly one `.csproj`.
- `--startup-project` defaults to the same project.
- Commands run from the startup project's folder, so relative paths (such as a SQLite file name) resolve from there. Use absolute paths if that's ambiguous.

`migrate` always applies *all* pending migrations; to go backward, use `migrate:rollback`. `migrate:status --no-connect` lists migrations without contacting the database, and `--json` passes through EF Core's JSON output.

These are development commands. For production, use EF Core's [reviewed SQL scripts or migration bundles](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying). Keep in mind that a migration can contain destructive SQL, and Bosun can't tell whether yours is safe.

## Rolling back and starting fresh

> **Both of these commands can permanently delete data.** Check which database your app is configured for, and make sure you can recover it, before you run them. They refuse to run without `--force`.

```powershell
caravel migrate:rollback InitialCreate --project ./App/App.csproj --force
caravel migrate:rollback 0 --project ./App/App.csproj --force
caravel migrate:fresh --project ./App/App.csproj --force
```

**Rollback** takes the migration you want to end up *at*, not a number of steps. `0` removes every migration. EF Core runs each migration's `Down` method to get there.

**Fresh** first checks that your migrations build, then drops the database, then applies every migration. If anything fails after the drop, the data is gone. There's no backup and no automatic seeding, and rebuilding recreates the tables, not the rows.

## Seeding

Add one line to your app, after building the host and before running it:

```csharp
using Caravel.Clarion;

// Register AddClarion<AppDbContext>(...) and AddClarionSeeder<UsersSeeder>() first.
using var host = builder.Build();
if (await host.RunCaravelSeedersAsync(args)) return;
await host.RunAsync();
```

Then run:

```powershell
caravel db:seed --project ./App/App.csproj
caravel db:seed --project ./App/App.csproj --seeder UsersSeeder --timeout 300
```

Seeders run in the order you registered them. `--seeder` runs just one. When your app has several seeders, naming the one you want is safer.

What to expect:

- **It really checks.** Bosun confirms that the seeders finished, so a missing hook or a failed seeder reports as a failure, not a success.
- **The web server doesn't start**, but your app's startup code does run.
- **No hidden extras.** `db:seed` doesn't migrate first and doesn't wrap seeders in a transaction. Each seeder handles its own transactions and re-run safety.
- **Timeouts.** The default is 300 seconds, and you can set anything from 1 to 86,400. On timeout or Ctrl+C, the app is stopped; whether partial data remains depends on your seeder.
- **Secrets.** Don't put secrets in command arguments or log output.

## What's been tested

Every command above is tested end to end against a throwaway SQLite database, and the migration workflow (apply, seed, upgrade, roll back and reapply) is also tested against SQL Server and PostgreSQL. Your own migrations and database permissions are specific to your app, so try them on a copy of your database before running them for real.
