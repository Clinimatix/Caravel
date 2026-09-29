$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/identity smoke ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$sample = Join-Path $run 'samples/Caravel.Identity'
$tests = Join-Path $run 'tests/Caravel.Identity.Tests'
$null = New-Item -ItemType Directory -Path $packages, $sample, $tests
$priorCache = $env:NUGET_PACKAGES
$priorVersion = $env:CaravelPackageVersion
$priorCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

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
    Copy-Item -LiteralPath (Join-Path $root 'samples/Caravel.Identity/Database') -Destination $sample -Recurse
    Get-ChildItem (Join-Path $root 'tests/Caravel.Identity.Tests') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $tests
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    $env:CaravelPackageVersion = $version
    $project = Join-Path $tests 'Caravel.Identity.Tests.csproj'
    Invoke-Checked dotnet @('restore', $project, '--configfile', $feed)
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
    Pop-Location
}
