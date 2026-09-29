$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N')
$run = Join-Path $root "artifacts/provider containers $runId"
$null = New-Item -ItemType Directory -Path $run
$context = (& docker context show).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable; start the local Linux engine first.' }
$endpoint = & docker context inspect $context --format '{{.Endpoints.docker.Host}}'
if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^(unix:///|npipe:////\./pipe/)') { throw 'Only a local Docker socket is allowed.' }
$dockerArguments = @('--context', $context)
if ((& docker @dockerArguments info --format '{{.OSType}}') -ne 'linux' -or $LASTEXITCODE -ne 0) {
    throw 'A running local Linux Docker engine is required.'
}

# Random credentials and unmounted containers belong only to this synthetic run.
$password = 'Caravel_Test_' + [guid]::NewGuid().ToString('N') + '!'
$label = "clinimatix.caravel.provider-test=$runId"
$postgres = "caravel-test-pg-$runId"
$sqlserver = "caravel-test-sql-$runId"
$previousSql = $env:CARAVEL_TEST_SQLSERVER
$previousPostgres = $env:CARAVEL_TEST_POSTGRES
$previousCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Docker([string[]]$Arguments) {
    $output = & docker @dockerArguments @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker operation failed: $($Arguments[0])." }
    return $output
}

function Wait-Healthy([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        $state = Invoke-Docker @('inspect', '--format', '{{.State.Status}} {{.State.Health.Status}}', $Name)
        if ($state -eq 'running healthy') { return }
        if ($state -notlike 'running *') { throw "Synthetic database container stopped: $Name ($state)." }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Synthetic database did not become healthy within three minutes: $Name."
}

function Get-Port([string]$Name, [string]$ContainerPort) {
    $binding = Invoke-Docker @('port', $Name, $ContainerPort)
    if ($binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Expected one loopback-only port binding.' }
    return $Matches[1]
}

Push-Location $root
try {
    $null = Invoke-Docker @('run', '-d', '--name', $postgres, '--label', $label, '--memory', '512m',
        '--publish', '127.0.0.1::5432', '--env', "POSTGRES_PASSWORD=$password",
        '--health-cmd', 'pg_isready -U postgres -d postgres', '--health-interval', '2s', '--health-timeout', '5s',
        '--health-retries', '60', 'postgres:18')
    $null = Invoke-Docker @('run', '-d', '--name', $sqlserver, '--label', $label, '--memory', '3g',
        '--publish', '127.0.0.1::1433', '--env', 'ACCEPT_EULA=Y', '--env', 'MSSQL_PID=Developer',
        '--env', "MSSQL_SA_PASSWORD=$password", '--health-cmd',
        "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -Q 'SELECT 1' -b",
        '--health-interval', '2s', '--health-timeout', '5s', '--health-retries', '60',
        'mcr.microsoft.com/mssql/server:2022-latest')
    Wait-Healthy $postgres
    Wait-Healthy $sqlserver
    $env:CARAVEL_TEST_POSTGRES = "Host=127.0.0.1;Port=$(Get-Port $postgres '5432/tcp');Username=postgres;Password=$password"
    $env:CARAVEL_TEST_SQLSERVER = "Server=127.0.0.1,$(Get-Port $sqlserver '1433/tcp');User ID=sa;Password=$password;TrustServerCertificate=true"
    Invoke-Docker @('inspect', '--format', '{{.Name}} {{.Config.Image}} {{.Image}}', $postgres, $sqlserver) |
        Set-Content -LiteralPath (Join-Path $run 'images.txt')
    & dotnet restore tests/Caravel.Provider.Tests --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Provider test locked restore failed.' }
    & dotnet test tests/Caravel.Provider.Tests -c Release --no-restore --logger trx --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw 'Provider contract tests failed.' }
    $results = @(Get-ChildItem -LiteralPath $run -Filter '*.trx' | ForEach-Object {
        ([xml](Get-Content -LiteralPath $_.FullName -Raw)).TestRun.Results.UnitTestResult
    })
    if ($results.Count -eq 0 -or @($results | Where-Object outcome -ne 'Passed').Count -gt 0) {
        throw 'Provider qualification requires executed passing tests with no skips.'
    }
    & (Join-Path $PSScriptRoot 'Test-ProviderMigrationSmoke.ps1')
    Write-Output "Provider contracts and server migrations passed against disposable SQL Server, PostgreSQL and SQLite. Artifacts: $run"
} finally {
    $env:CARAVEL_TEST_SQLSERVER = $previousSql
    $env:CARAVEL_TEST_POSTGRES = $previousPostgres
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificate
    # Match the exact per-run label, including if docker run created a container before failing.
    try {
        $owned = @(& docker @dockerArguments ps -aq --filter "label=$label")
        if ($LASTEXITCODE -ne 0) { throw "Could not identify this run's containers for cleanup: $runId." }
        if ($owned.Count -gt 0) {
            $null = Invoke-Docker (@('rm', '--force', '--volumes') + $owned)
        }
    } finally { Pop-Location }
}
