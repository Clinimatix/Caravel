param([ValidateSet('sqlite', 'sqlserver', 'postgres')][string]$Provider = 'sqlite')

$ErrorActionPreference = 'Stop'
if ($Provider -ne 'sqlite') {
    $connectionVariable = if ($Provider -eq 'sqlserver') { 'CARAVEL_TEST_SQLSERVER' } else { 'CARAVEL_TEST_POSTGRES' }
    if (-not [Environment]::GetEnvironmentVariable($connectionVariable)) { throw "Set $connectionVariable to a disposable loopback server." }
}
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/identity smoke ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$sample = Join-Path $run 'samples/Caravel.Identity'
$tests = Join-Path $run 'tests/Caravel.Identity.Tests'
$null = New-Item -ItemType Directory -Path $packages, $sample, $tests
$priorCache = $env:NUGET_PACKAGES
$priorVersion = $env:CaravelPackageVersion
$priorCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$priorProvider = $env:CARAVEL_IDENTITY_TEST_PROVIDER
$priorSampleProvider = $env:Caravel__DatabaseProvider
$priorDatabase = $env:Caravel__IdentityDatabase
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:CARAVEL_IDENTITY_TEST_PROVIDER = $Provider

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    foreach ($name in @('Core', 'AspNetCore', 'Clarion', 'Auth', 'Events', 'Queues')) {
        Invoke-Checked dotnet @('pack', "src/Caravel.$name/Caravel.$name.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
    }
    $version = (Get-ChildItem -LiteralPath $packages -Filter 'Clinimatix.Caravel.Auth.*.nupkg').BaseName.Substring('Clinimatix.Caravel.Auth.'.Length)
    $escaped = [Security.SecurityElement]::Escape($packages)
    $feed = Join-Path $run 'NuGet.Config'
    @"
<configuration>
  <packageSources><clear/><add key="local" value="$escaped"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping>
    <clear/>
    <packageSource key="local"><package pattern="Clinimatix.Caravel.*"/></packageSource>
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $feed
    Get-ChildItem (Join-Path $root 'samples/Caravel.Identity') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $sample
    if ($Provider -eq 'sqlite') {
        Copy-Item -LiteralPath (Join-Path $root 'samples/Caravel.Identity/Database') -Destination $sample -Recurse
    }
    Get-ChildItem (Join-Path $root 'tests/Caravel.Identity.Tests') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $tests
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    $env:CaravelPackageVersion = $version
    $project = Join-Path $tests 'Caravel.Identity.Tests.csproj'
    Invoke-Checked dotnet @('restore', $project, '--configfile', $feed)
    if ($Provider -ne 'sqlite') {
        $env:Caravel__DatabaseProvider = $Provider
        $env:Caravel__IdentityDatabase = [Environment]::GetEnvironmentVariable($connectionVariable)
        Invoke-Checked dotnet @('tool', 'restore', '--configfile', $feed)
        $sampleProject = Join-Path $sample 'Caravel.Identity.csproj'
        $modelPath = Join-Path $sample 'IdentityContext.cs'
        $model = Get-Content -LiteralPath $modelPath -Raw
        if (-not $model.Contains('builder.ApplyClarionConventions();')) { throw 'Identity model migration insertion point is missing.' }
        try {
            $model.Replace('builder.ApplyClarionConventions();', "builder.Ignore<CounterResult>();`n        builder.ApplyClarionConventions();") | Set-Content -LiteralPath $modelPath
            Invoke-Checked dotnet @('ef', 'migrations', 'add', 'InitialIdentity', '--project', $sampleProject, '--context', 'IdentityContext', '--output-dir', 'Database/Identity')
        } finally { Set-Content -LiteralPath $modelPath -Value $model }
        Invoke-Checked dotnet @('ef', 'migrations', 'add', 'AddCounterResults', '--project', $sampleProject, '--context', 'IdentityContext', '--output-dir', 'Database/Identity')
        Invoke-Checked dotnet @('ef', 'migrations', 'add', 'InitialQueue', '--project', $sampleProject, '--context', 'QueueDbContext', '--output-dir', 'Database/Queue')
        $resultsPath = Join-Path $run 'results'
        Invoke-Checked dotnet @('test', $project, '-c', 'Release', '--no-restore', '--filter', 'FullyQualifiedName~IdentityApplicationTests', '--logger', 'trx', '--results-directory', $resultsPath)
        $results = @(Get-ChildItem -LiteralPath $resultsPath -Filter '*.trx' | ForEach-Object {
            ([xml](Get-Content -LiteralPath $_.FullName -Raw)).TestRun.Results.UnitTestResult
        })
        if ($results.Count -eq 0 -or @($results | Where-Object outcome -ne 'Passed').Count -gt 0) {
            throw 'Packaged server backend qualification requires executed passing tests with no skips.'
        }
        Write-Output "Packaged backend passed against ${Provider}: accounts, authorization, durable work, recovery and native schema upgrades/rollback. Synthetic artifacts: $run"
        return
    }
    Invoke-Checked dotnet @('test', $project, '-c', 'Release', '--no-restore')
    $oidcSample = Join-Path $run 'samples/Caravel.Oidc'
    $oidcTests = Join-Path $run 'tests/Caravel.Oidc.Tests'
    $null = New-Item -ItemType Directory -Path $oidcSample, $oidcTests
    Get-ChildItem (Join-Path $root 'samples/Caravel.Oidc') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $oidcSample
    Get-ChildItem (Join-Path $root 'tests/Caravel.Oidc.Tests') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $oidcTests
    $oidcProject = Join-Path $oidcTests 'Caravel.Oidc.Tests.csproj'
    Invoke-Checked dotnet @('restore', $oidcProject, '--configfile', $feed)
    Invoke-Checked dotnet @('test', $oidcProject, '-c', 'Release', '--no-restore')
    Write-Output "Identity/OIDC package smoke passed: local accounts, policies, ownership, durable counters and native external sign-in security checks. Synthetic artifacts: $run"
} finally {
    $env:NUGET_PACKAGES = $priorCache
    $env:CaravelPackageVersion = $priorVersion
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $priorCertificate
    $env:CARAVEL_IDENTITY_TEST_PROVIDER = $priorProvider
    $env:Caravel__DatabaseProvider = $priorSampleProvider
    $env:Caravel__IdentityDatabase = $priorDatabase
    Pop-Location
}
