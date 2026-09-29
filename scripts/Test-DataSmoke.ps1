$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/data smoke ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$sample = Join-Path $run 'DataApp'
$null = New-Item -ItemType Directory -Path $packages, $sample
$priorCache = $env:NUGET_PACKAGES
$priorVersion = $env:CaravelPackageVersion
$priorDatabase = $env:CARAVEL_SAMPLE_DATABASE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    foreach ($name in @('Clarion', 'Bosun')) {
        Invoke-Checked dotnet @('pack', "src/Caravel.$name/Caravel.$name.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
    }
    $version = (Get-ChildItem -LiteralPath $packages -Filter 'Clinimatix.Caravel.Clarion.*.nupkg').BaseName.Substring('Clinimatix.Caravel.Clarion.'.Length)
    $escaped = [Security.SecurityElement]::Escape($packages)
    $feed = Join-Path $run 'NuGet.Config'
    "<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>" | Set-Content -LiteralPath $feed
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    $toolDirectory = Join-Path $run 'tool'
    Invoke-Checked dotnet @('tool', 'install', 'Clinimatix.Caravel.Bosun', '--version', $version, '--tool-path', $toolDirectory, '--configfile', $feed)
    $tool = Join-Path $toolDirectory $(if ($IsWindows) { 'caravel.exe' } else { 'caravel' })
    Get-ChildItem (Join-Path $root 'samples/Caravel.Data') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $sample
    Copy-Item -LiteralPath (Join-Path $root 'samples/Caravel.Data/Database') -Destination $sample -Recurse
    Copy-Item -LiteralPath (Join-Path $root '.config') -Destination $run -Recurse
    $env:CaravelPackageVersion = $version
    $env:CARAVEL_SAMPLE_DATABASE = Join-Path $run 'synthetic.db'
    $project = Join-Path $sample 'Caravel.Data.csproj'
    Push-Location $run
    try {
        Invoke-Checked dotnet @('tool', 'restore', '--configfile', $feed)
        Invoke-Checked dotnet @('restore', $project, '--configfile', $feed)
        Invoke-Checked $tool @('migrate', '--project', $project)
        Invoke-Checked $tool @('db:seed', '--project', $project)
        Invoke-Checked $tool @('db:seed', '--project', $project)
        $report = (& dotnet run --project $project --no-build) | Select-Object -Last 1 | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $report.events -ne 3 -or $report.quantity -ne 6) { throw 'Synthetic seeded aggregation failed.' }

        # Exercise an actual schema upgrade while preserving existing synthetic rows.
        $model = Join-Path $sample 'ActivityContext.cs'
        (Get-Content -LiteralPath $model -Raw).Replace('public int Quantity { get; set; }', 'public int Quantity { get; set; } public string? Label { get; set; }') | Set-Content -LiteralPath $model
        Invoke-Checked $tool @('make:migration', 'AddActivityLabel', '--project', $project)
        Invoke-Checked $tool @('migrate', '--project', $project)
        Invoke-Checked $tool @('migrate:status', '--project', $project, '--json')
        $report = (& dotnet run --project $project --no-build) | Select-Object -Last 1 | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $report.events -ne 3 -or $report.quantity -ne 6) { throw 'Migration did not preserve synthetic activity rows.' }

        # Destructive command checks are restricted to this run's newly created synthetic database.
        & $tool migrate:fresh --project $project
        if ($LASTEXITCODE -eq 0) { throw 'Fresh accepted missing --force.' }
        Invoke-Checked $tool @('migrate:rollback', 'InitialActivities', '--project', $project, '--force')
        Invoke-Checked $tool @('migrate', '--project', $project)
        Invoke-Checked $tool @('migrate:fresh', '--project', $project, '--force')
        Invoke-Checked $tool @('db:seed', '--project', $project, '--seeder', 'SyntheticActivitySeeder')
        $report = (& dotnet run --project $project --no-build) | Select-Object -Last 1 | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $report.events -ne 3 -or $report.quantity -ne 6) { throw 'Fresh/seed cycle failed.' }
        Write-Output "Data smoke passed: installed Clarion/Bosun packages, migration, seed repeatability, aggregation, schema upgrade, rollback and force guard. Synthetic artifacts: $run"
    } finally { Pop-Location }
} finally {
    $env:NUGET_PACKAGES = $priorCache
    $env:CaravelPackageVersion = $priorVersion
    $env:CARAVEL_SAMPLE_DATABASE = $priorDatabase
    Pop-Location
}
