$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N')
$run = Join-Path ([IO.Path]::GetTempPath()) "caravel azure storage $runId"
$null = New-Item -ItemType Directory -Path $run
$context = (& docker context show).Trim()
$endpoint = & docker context inspect $context --format '{{.Endpoints.docker.Host}}'
if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^(unix:///|npipe:////\./pipe/)') { throw 'Only a local Docker socket is allowed.' }
$dockerArguments = @('--context', $context)
$name = "caravel-azurite-$runId"
$label = "clinimatix.caravel.azure-test=$runId"
$oldEndpoint = $env:CARAVEL_TEST_AZURITE_ENDPOINT
$oldKey = $env:CARAVEL_TEST_AZURITE_KEY
$oldCache = $env:NUGET_PACKAGES
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Docker([string[]]$Arguments) {
    $result = & docker @dockerArguments @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker operation failed: $($Arguments[0])." }
    return $result
}
function Assert-TestResults([string]$Directory) {
    $results = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' | ForEach-Object {
        ([xml](Get-Content -LiteralPath $_.FullName -Raw)).TestRun.Results.UnitTestResult
    })
    if ($results.Count -eq 0 -or @($results | Where-Object outcome -ne 'Passed').Count -gt 0) {
        throw 'Azure qualification requires executed passing tests with no skips.'
    }
}

Push-Location $root
try {
    # Synthetic emulator-only key, no host mounts, no cloud credentials or resources.
    $env:CARAVEL_TEST_AZURITE_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $null = Invoke-Docker @('run', '-d', '--name', $name, '--label', $label, '--memory', '512m',
        '--publish', '127.0.0.1::10000', '--env', "AZURITE_ACCOUNTS=caraveltest:$env:CARAVEL_TEST_AZURITE_KEY",
        'mcr.microsoft.com/azure-storage/azurite:3.37.0', 'azurite-blob', '--blobHost', '0.0.0.0',
        '--silent', '--disableTelemetry')
    $binding = Invoke-Docker @('port', $name, '10000/tcp')
    if ($binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Expected one loopback-only binding.' }
    $env:CARAVEL_TEST_AZURITE_ENDPOINT = "http://127.0.0.1:$($Matches[1])/caraveltest"
    Invoke-Docker @('inspect', '--format', '{{.Config.Image}} {{.Image}}', $name) | Set-Content -LiteralPath (Join-Path $run 'image.txt')
    $ready = $false
    $http = [Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try { $response = $http.GetAsync($env:CARAVEL_TEST_AZURITE_ENDPOINT).GetAwaiter().GetResult(); $response.Dispose(); $ready = $true; break }
            catch { Start-Sleep -Seconds 1 }
        }
    } finally { $http.Dispose() }
    if (-not $ready) { throw 'Owned Azurite did not start.' }
    & dotnet restore tests/Caravel.Storage.Azure.Tests --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Azure locked restore failed.' }
    & dotnet test tests/Caravel.Storage.Azure.Tests -c Release --no-restore --logger trx --results-directory (Join-Path $run 'source-results')
    if ($LASTEXITCODE -ne 0) { throw 'Azure source tests failed.' }
    Assert-TestResults (Join-Path $run 'source-results')

    $packages = Join-Path $run 'packages'
    foreach ($project in @('Storage', 'Storage.Azure')) {
        & dotnet pack "src/Caravel.$project" -c Release --no-restore --output $packages
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
    }
    & (Join-Path $PSScriptRoot 'Test-PackagePaths.ps1') -PackageDirectory $packages
    $config = Join-Path $run 'NuGet.Config'
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    @"
<configuration><packageSources><clear /><add key="local" value="$escapedPackages" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources><packageSourceMapping><clear /><packageSource key="local"><package pattern="Clinimatix.Caravel.*" /></packageSource><packageSource key="nuget"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $config
    $app = Join-Path $run 'Installed'
    $null = New-Item -ItemType Directory -Path $app
    Copy-Item -Path 'tests/Caravel.Storage.Azure.Tests/*.cs' -Destination $app
    Copy-Item -LiteralPath 'docs/examples/AuthorizedBlobDownload.cs' -Destination $app
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject><IsPackable>false</IsPackable><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /><PackageReference Include="Clinimatix.Caravel.Storage.Azure" Version="$version" /><PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.12" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" /><PackageReference Include="xunit" Version="2.9.3" /><PackageReference Include="xunit.runner.visualstudio" Version="4.0.0" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $app 'Installed.csproj')
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    & dotnet restore $app --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Fresh Azure package restore failed.' }
    & dotnet restore $app --configfile $config --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Fresh Azure locked restore failed.' }
    & dotnet test $app -c Release --no-restore --logger trx --results-directory (Join-Path $run 'installed-results')
    if ($LASTEXITCODE -ne 0) { throw 'Installed Azure tests failed.' }
    Assert-TestResults (Join-Path $run 'installed-results')
    Write-Output "Azure source and fresh package checks passed. Synthetic artifacts: $run"
} finally {
    $env:CARAVEL_TEST_AZURITE_ENDPOINT = $oldEndpoint
    $env:CARAVEL_TEST_AZURITE_KEY = $oldKey
    $env:NUGET_PACKAGES = $oldCache
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $oldCertificate
    try {
        $owned = @(Invoke-Docker @('ps', '-aq', '--filter', "label=$label"))
        if ($owned.Count -gt 0) { $null = Invoke-Docker (@('rm', '-fv') + $owned) }
    } finally { Pop-Location }
}
