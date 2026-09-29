$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/provider migrations ' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$priorCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Checked([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Read-Fixture([string]$Command) {
    $output = @(& dotnet $fixtureDll $Command)
    if ($LASTEXITCODE -ne 0) { throw "Migration fixture $Command failed." }
    return ($output | Select-Object -Last 1 | ConvertFrom-Json)
}

function Assert-Rows([object]$Report, [string]$ExpectedRows, [int]$Applied, [int]$Pending) {
    if ($Report.events -ne 3 -or $Report.quantity -ne 6 -or $Report.applied -ne $Applied -or
        $Report.pending -ne $Pending -or $Report.modelChanges -or
        ($Report.rows | ConvertTo-Json -Depth 5 -Compress) -cne $ExpectedRows) {
        throw 'Native migration changed baseline synthetic rows or migration/model state unexpectedly.'
    }
}

Push-Location $root
try {
    foreach ($variable in @('CARAVEL_TEST_SQLSERVER', 'CARAVEL_TEST_POSTGRES')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variable))) {
            throw "Set $variable to a disposable loopback server; this script never provisions containers."
        }
    }
    Invoke-Checked @('build', 'src/Caravel.Bosun/Caravel.Bosun.csproj', '-c', 'Release')
    $bosun = Join-Path $root 'src/Caravel.Bosun/bin/Release/net10.0/Caravel.Bosun.dll'
    Copy-Item -LiteralPath (Join-Path $root '.config') -Destination $run -Recurse
    Push-Location $run
    try {
        Invoke-Checked @('tool', 'restore')
        foreach ($provider in @('sqlserver', 'postgres')) {
            $directory = Join-Path $run $provider
            $null = New-Item -ItemType Directory -Path $directory
            $databaseName = 'caravel_migration_' + [guid]::NewGuid().ToString('N')
            $variable = if ($provider -eq 'sqlserver') { 'CARAVEL_TEST_SQLSERVER' } else { 'CARAVEL_TEST_POSTGRES' }
            $originalConnection = [Environment]::GetEnvironmentVariable($variable)
            $created = $false
            try {
                # Deliberately supply another catalog; the compiled fixture must ignore it, never connect to it.
                [Environment]::SetEnvironmentVariable($variable, $originalConnection + ';Database=caravel_supplied_do_not_touch')
                $project = Join-Path $directory 'MigrationFixture.csproj'
                $clarion = [Security.SecurityElement]::Escape((Join-Path $root 'src/Caravel.Clarion/Caravel.Clarion.csproj'))
                $queues = [Security.SecurityElement]::Escape((Join-Path $root 'src/Caravel.Queues/Caravel.Queues.csproj'))
                @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><IsPackable>false</IsPackable></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$clarion" />
    <ProjectReference Include="$queues" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project
                Copy-Item -LiteralPath (Join-Path $root 'samples/Caravel.Data/ActivityContext.cs') -Destination $directory
                @'
using Caravel.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

public static class FixtureSettings
{
    public const string DatabaseName = "__DATABASE__";
    public static readonly string Provider = "__PROVIDER__";
    public static string SuppliedDatabase { get; private set; } = "";
    public static void Configure(DbContextOptionsBuilder options)
    {
        if (Provider == "sqlserver")
        {
            var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CARAVEL_TEST_SQLSERVER"));
            RequireLoopback(connection.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0]);
            if (!string.IsNullOrEmpty(connection.AttachDBFilename)) throw new InvalidOperationException("Attached database files are not permitted.");
            SuppliedDatabase = connection.InitialCatalog;
            connection.InitialCatalog = DatabaseName;
            connection.Pooling = false;
            options.UseSqlServer(connection.ConnectionString);
        }
        else
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CARAVEL_TEST_POSTGRES"));
            RequireLoopback(connection.Host);
            SuppliedDatabase = connection.Database ?? "";
            connection.Database = DatabaseName;
            connection.Pooling = false;
            options.UseNpgsql(connection.ConnectionString);
        }
    }
    private static void RequireLoopback(string? host)
    {
        if (host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Migration fixtures accept only loopback database servers.");
    }
}

public sealed class FixtureFactory : IDesignTimeDbContextFactory<ActivityContext>
{
    public ActivityContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ActivityContext>();
        FixtureSettings.Configure(options);
        return new(options.Options);
    }
}
'@.Replace('__DATABASE__', $databaseName).Replace('__PROVIDER__', $provider) | Set-Content -LiteralPath (Join-Path $directory 'FixtureSettings.cs')
                @'
using System.Text.Json;
using Caravel.Queues;
using Caravel.Clarion;
using Caravel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddClarion<ActivityContext>(FixtureSettings.Configure);
builder.Services.AddClarionSeeder<SyntheticActivitySeeder>();
using var host = builder.Build();
if (await host.RunCaravelSeedersAsync(args)) return;
await using var scope = host.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<IClarion>();
var creator = db.Context.GetService<IRelationalDatabaseCreator>();
switch (args.Single())
{
    case "--target":
        Console.WriteLine(JsonSerializer.Serialize(new { database = db.Context.Database.GetDbConnection().Database, supplied = FixtureSettings.SuppliedDatabase }));
        break;
    case "--create":
        if (await creator.ExistsAsync()) throw new InvalidOperationException("Refusing to reuse an existing fixture database.");
        await creator.CreateAsync(); // Native provider creates an empty database; EF migrations own every table.
        break;
    case "--drop":
        if (await creator.ExistsAsync()) await creator.DeleteAsync();
        break;
    case "--verify-absent":
        if (await creator.ExistsAsync()) throw new InvalidOperationException("Fixture database cleanup did not complete.");
        break;
    case "--report":
        var rows = await db.Models<Activity>().AsNoTracking().OrderBy(row => row.EventKey)
            .Select(row => new { row.Id, row.EventKey, row.Kind, row.Quantity, row.CreatedAt, row.UpdatedAt, row.DeletedAt }).ToListAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            events = rows.Count, quantity = rows.Sum(row => row.Quantity), rows,
            applied = (await db.Context.Database.GetAppliedMigrationsAsync()).Count(),
            pending = (await db.Context.Database.GetPendingMigrationsAsync()).Count(),
            modelChanges = db.Context.Database.HasPendingModelChanges()
        }));
        break;
    case "--write-label":
        var updated = await db.Models<Activity>().Where(row => row.EventKey == "synthetic-0")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => EF.Property<string?>(row, "Label"), "synthetic-upgrade"));
        if (updated != 1) throw new InvalidOperationException("Expected exactly one upgraded synthetic row.");
        break;
    case "--labels":
        Console.WriteLine(JsonSerializer.Serialize(await db.Models<Activity>().AsNoTracking().OrderBy(row => row.EventKey)
            .Select(row => EF.Property<string?>(row, "Label")).ToListAsync()));
        break;
    case "--stage-outbox":
    {
        // Stage through the public API against the migrated app database, without creating queue tables.
        var services = new ServiceCollection();
        services.AddDbContextFactory<ActivityContext>(FixtureSettings.Configure);
        services.AddDbContextFactory<QueueDbContext>(FixtureSettings.Configure);
        services.AddCaravelDatabaseQueue();
        services.AddQueueJob<FixtureNotice, FixtureNoticeHandler>("migration.notice.v1");
        services.AddCaravelOutbox<ActivityContext>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var dispatchScope = provider.CreateAsyncScope();
        var stage = await dispatchScope.ServiceProvider.GetRequiredService<QueueOutbox<ActivityContext>>()
            .StageAsync(new FixtureNotice(6), new("reports", "synthetic-tenant", "migration-outbox"));
        await dispatchScope.ServiceProvider.GetRequiredService<ActivityContext>().SaveChangesAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { stage.OutboxId, stage.AlreadyStaged }));
        break;
    }
    case "--outbox":
        Console.WriteLine(JsonSerializer.Serialize(await db.Context.Set<OutboxMessage>().AsNoTracking()
            .OrderBy(row => row.CreatedAt).Select(row => new
            {
                row.Id, row.Queue, row.TenantId, row.IdempotencyKey, row.JobType, row.Payload,
                row.MaxAttempts, row.DispatchedAt, row.QueueJobId
            }).ToListAsync()));
        break;
    case "--outbox-table-count":
        await db.Context.Database.OpenConnectionAsync();
        await using (var command = db.Context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CaravelOutboxMessages'";
            Console.WriteLine(JsonSerializer.Serialize(new { tables = Convert.ToInt32(await command.ExecuteScalarAsync()) }));
        }
        break;
    default: throw new ArgumentException("Unknown fixture operation.");
}

public sealed record FixtureNotice(int Quantity);
public sealed class FixtureNoticeHandler : IJobHandler<FixtureNotice>
{
    public Task HandleAsync(FixtureNotice job, JobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The migration fixture stages dispatch intents only.");
}
'@ | Set-Content -LiteralPath (Join-Path $directory 'Program.cs')
                Invoke-Checked @('build', $project)
                $fixtureDll = Join-Path $directory 'bin/Debug/net10.0/MigrationFixture.dll'
                $target = Read-Fixture '--target'
                if ($target.database -cne $databaseName -or $target.supplied -cne 'caravel_supplied_do_not_touch') {
                    throw 'Fixture did not replace the supplied catalog with its immutable random database name.'
                }
                Invoke-Checked @($fixtureDll, '--verify-absent')
                # This immutable random target is now proven absent. Cleanup also owns a partial provider creation.
                $created = $true
                Invoke-Checked @($fixtureDll, '--create')
                & dotnet $fixtureDll --create *> (Join-Path $directory 'refused-existing.log')
                if ($LASTEXITCODE -eq 0) { throw 'Fixture accepted reuse of an existing database.' }
                Invoke-Checked @($bosun, 'make:migration', 'InitialActivities', '--project', $project, '--context', 'ActivityContext')
                Invoke-Checked @($bosun, 'migrate', '--project', $project, '--context', 'ActivityContext')
                Invoke-Checked @($bosun, 'db:seed', '--project', $project)
                Invoke-Checked @($bosun, 'db:seed', '--project', $project)
                $initial = Read-Fixture '--report'
                $baseline = $initial.rows | ConvertTo-Json -Depth 5 -Compress
                Assert-Rows $initial $baseline 1 0

                $model = Join-Path $directory 'ActivityContext.cs'
                $original = Get-Content -LiteralPath $model -Raw
                $upgraded = $original.Replace('public int Quantity { get; set; }', 'public int Quantity { get; set; } public string? Label { get; set; }')
                if ($upgraded -ceq $original) { throw 'Could not locate the synthetic sample upgrade insertion point.' }
                Set-Content -LiteralPath $model -Value $upgraded
                Invoke-Checked @($bosun, 'make:migration', 'AddActivityLabel', '--project', $project, '--context', 'ActivityContext')
                Invoke-Checked @($bosun, 'migrate', '--project', $project, '--context', 'ActivityContext')
                Assert-Rows (Read-Fixture '--report') $baseline 2 0
                Invoke-Checked @($fixtureDll, '--write-label')
                $labels = @(Read-Fixture '--labels')
                if ($labels.Count -ne 3 -or $labels[0] -cne 'synthetic-upgrade' -or $null -ne $labels[1] -or $null -ne $labels[2]) { throw 'Additive column read/write failed.' }
                & dotnet $bosun migrate:rollback InitialActivities --project $project --context ActivityContext *> (Join-Path $directory 'refused-rollback.log')
                if ($LASTEXITCODE -eq 0) { throw 'Rollback accepted missing --force.' }
                Assert-Rows (Read-Fixture '--report') $baseline 2 0
                Invoke-Checked @($bosun, 'migrate:rollback', 'InitialActivities', '--project', $project, '--context', 'ActivityContext', '--force')
                Assert-Rows (Read-Fixture '--report') $baseline 1 1
                Invoke-Checked @($bosun, 'migrate', '--project', $project, '--context', 'ActivityContext')
                Assert-Rows (Read-Fixture '--report') $baseline 2 0
                $labels = @(Read-Fixture '--labels')
                if ($labels.Count -ne 3 -or @($labels | Where-Object { $null -ne $_ }).Count -ne 0) { throw 'Reapplying the nullable column did not reflect the deliberate loss of its rolled-back values.' }
                Invoke-Checked @($fixtureDll, '--write-label')
                Assert-Rows (Read-Fixture '--report') $baseline 2 0
                # Add the app-owned outbox with a native EF migration, not EnsureCreated.
                $withOutbox = $upgraded.Replace('model.ApplyClarionConventions();', 'model.AddCaravelOutbox(); model.ApplyClarionConventions();')
                if ($withOutbox -ceq $upgraded) { throw 'Could not locate the outbox model insertion point.' }
                Set-Content -LiteralPath $model -Value ("using Caravel.Queues;`n" + $withOutbox)
                Invoke-Checked @($bosun, 'make:migration', 'AddActivityOutbox', '--project', $project, '--context', 'ActivityContext')
                Invoke-Checked @($bosun, 'migrate', '--project', $project, '--context', 'ActivityContext')
                Assert-Rows (Read-Fixture '--report') $baseline 3 0
                if ((Read-Fixture '--outbox-table-count').tables -ne 1 -or @(Read-Fixture '--outbox').Count -ne 0) {
                    throw 'The additive outbox migration did not create an empty native table.'
                }
                $staged = Read-Fixture '--stage-outbox'
                $duplicate = Read-Fixture '--stage-outbox'
                $intents = @(Read-Fixture '--outbox')
                if ($staged.AlreadyStaged -or -not $duplicate.AlreadyStaged -or $duplicate.OutboxId -cne $staged.OutboxId -or
                    $intents.Count -ne 1 -or $intents[0].Id -cne $staged.OutboxId -or
                    $intents[0].Queue -cne 'reports' -or $intents[0].TenantId -cne 'synthetic-tenant' -or
                    $intents[0].IdempotencyKey -cne 'migration-outbox' -or $intents[0].JobType -cne 'migration.notice.v1' -or
                    $intents[0].Payload -cne '{"quantity":6}' -or $intents[0].MaxAttempts -ne 3 -or
                    $null -ne $intents[0].DispatchedAt -or $null -ne $intents[0].QueueJobId) {
                    throw 'The migrated outbox did not preserve a single validated pending dispatch intent.'
                }
                Assert-Rows (Read-Fixture '--report') $baseline 3 0
                # Deliberate rollback drops the synthetic pending intent. Application rows must remain unchanged.
                Invoke-Checked @($bosun, 'migrate:rollback', 'AddActivityLabel', '--project', $project, '--context', 'ActivityContext', '--force')
                Assert-Rows (Read-Fixture '--report') $baseline 2 1
                if ((Read-Fixture '--outbox-table-count').tables -ne 0) { throw 'Outbox rollback did not remove its table.' }
                Invoke-Checked @($bosun, 'migrate', '--project', $project, '--context', 'ActivityContext')
                Assert-Rows (Read-Fixture '--report') $baseline 3 0
                if ((Read-Fixture '--outbox-table-count').tables -ne 1 -or @(Read-Fixture '--outbox').Count -ne 0) {
                    throw 'Reapplying the outbox migration did not reflect the deliberate loss of its rolled-back intents.'
                }
                $restaged = Read-Fixture '--stage-outbox'
                if ($restaged.AlreadyStaged -or $restaged.OutboxId -ceq $staged.OutboxId -or @(Read-Fixture '--outbox').Count -ne 1) {
                    throw 'A reapplied outbox did not accept a fresh synthetic intent.'
                }
                Assert-Rows (Read-Fixture '--report') $baseline 3 0
                $baseline | Set-Content -LiteralPath (Join-Path $directory 'preserved-synthetic-rows.json')
                Write-Output "$provider native migration/upgrade/rollback/reapply passed; baseline rows preserved, rolled-back Label values and outbox intents deliberately discarded."
            } finally {
                try {
                    if ($created) {
                        Invoke-Checked @($fixtureDll, '--drop')
                        Invoke-Checked @($fixtureDll, '--verify-absent')
                    }
                } finally { [Environment]::SetEnvironmentVariable($variable, $originalConnection) }
            }
        }
        Write-Output "Provider migration smoke passed for SQL Server and PostgreSQL. Synthetic artifacts: $run"
    } finally { Pop-Location }
} finally {
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $priorCertificate
    Pop-Location
}
