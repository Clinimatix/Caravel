# Database providers

Clarion and the queue package integrate with **SQL Server, PostgreSQL and SQLite**. Azure SQL uses the SQL Server provider but has not yet been verified as a deployment target. **MariaDB/MySQL** support is planned.

Caravel doesn't bundle a database driver. Add the EF Core provider package for your database to your app and pass its usual options to `AddClarion`:

```csharp
builder.Services.AddClarion<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("App")));
```

## Supported databases

| Database | EF Core provider | Tested with |
| --- | --- | --- |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.x | SQL Server 2022 |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.x | PostgreSQL 18 |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` 10.0.x | Windows, Linux and macOS 26 on Apple silicon |

All three run the same shared test suite, which covers:

- sessions, queries, inserts and updates, timestamps and generated keys
- transactions, rollback and optimistic concurrency conflicts
- soft deletes and restores that leave your other query filters (such as tenant filters) in place
- the durable queue: competing workers, duplicate submissions, lease recovery, retries, dead letters and replay
- a database-backed authorization check, including case-sensitivity of identifiers
- migrations: apply, seed, upgrade, roll back and reapply, keeping existing rows intact

The migration checks preserve baseline rows; rolling back an added column deliberately discards its values. A schema rollback is not a general data-recovery operation.

The [backend sample](BACKEND-SAMPLE.md) also exercises the packages together on all three providers: account security, durable acceptance, duplicate/conflicting requests, concurrent workers, reports and recovery after an application-host restart. Server runs generate native migrations in a disposable sample copy. This complements the Windows, Linux and macOS SQLite checks; it does not imply every operating-system/database combination or deployment topology has been tested.

SQL Server tests run against SQL Server 2022. Azure SQL needs deployment verification, including its compatibility level and retry settings; sharing a provider does not establish that coverage.

These tests show that Caravel behaves consistently across databases. They can't cover every schema or workload, so run your app's own tests against the database you'll deploy on.

## Differences to keep in mind

Databases genuinely differ, and Caravel doesn't hide that. The most common things to check:

- **Dates and times.** SQL Server stores `DateTimeOffset` natively, PostgreSQL requires UTC offsets, and SQLite can't sort or compare every `DateTimeOffset` expression. Add EF Core value conversions where needed.
- **Case sensitivity.** SQL Server's default collations are case-insensitive, while PostgreSQL and SQLite compare text case-sensitively by default. If identifiers such as tenant IDs must match exactly, choose collations deliberately.
- **Migrations are per database.** A migration generated for SQLite isn't a SQL Server migration. Keep a reviewed migration set for each database you target.
- **Provider-specific features** such as JSON columns, generated values and concurrency tokens vary. EF Core's raw access is always available for intentional database-specific work.

## MariaDB and MySQL

MariaDB support is planned, but it's waiting on the ecosystem. The established provider, [Pomelo.EntityFrameworkCore.MySql](https://www.nuget.org/packages/Pomelo.EntityFrameworkCore.MySql), currently targets EF Core 9, while Caravel uses EF Core 10. EF Core providers need to match EF's major version, so Caravel will add MariaDB once Pomelo releases an EF Core 10–compatible version.

## Testing with your own database

To run the shared tests yourself against SQL Server or PostgreSQL, see [Testing Caravel](TESTING.md#test-against-sql-server-and-postgresql). Use disposable servers only.
