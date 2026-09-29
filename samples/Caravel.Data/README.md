# Clarion data sample

A small console app (Generic Host) that uses Clarion on its own, with no web server. It has a SQLite database, EF Core migrations, a seeder and a LINQ aggregate query.

From the repository root, with the pinned .NET SDK:

```powershell
dotnet tool restore
$env:CARAVEL_SAMPLE_DATABASE = Join-Path (Get-Location) 'artifacts/activity-demo.db'
New-Item -ItemType Directory artifacts -Force | Out-Null
dotnet run --project src/Caravel.Bosun -- migrate --project samples/Caravel.Data
dotnet run --project src/Caravel.Bosun -- db:seed --project samples/Caravel.Data
dotnet run --project samples/Caravel.Data
```

You should see three events with a total quantity of six. Running the seeder again doesn't duplicate them: a unique index on the event key prevents it.

`scripts/Test-DataSmoke.ps1` runs this sample against freshly packed packages, including a schema upgrade with existing rows, a rollback and a fresh rebuild, all on a throwaway SQLite database. For SQL Server and PostgreSQL, see [Testing Caravel](../../docs/TESTING.md#test-against-sql-server-and-postgresql).
