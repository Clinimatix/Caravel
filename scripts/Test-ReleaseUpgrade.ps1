param(
    [string]$BaselineVersion = '26.1.0-rc1',
    [string]$CandidatePackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/release upgrade ' + [guid]::NewGuid().ToString('N'))
$localPackages = Join-Path $run 'packages'
$components = @('Core', 'AspNetCore', 'Auth', 'Clarion', 'Queues')
$null = New-Item -ItemType Directory -Path $run, $localPackages
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$candidateVersion = [string]$buildProperties.Project.PropertyGroup.Version
if (-not $candidateVersion -or $candidateVersion -eq $BaselineVersion) {
    throw 'Upgrade qualification requires a candidate version distinct from the published baseline.'
}
[xml]$packageProperties = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw
$platformVersion = [string]($packageProperties.Project.ItemGroup.PackageVersion |
    Where-Object Include -eq 'Microsoft.EntityFrameworkCore.Sqlite').Version
if (-not $platformVersion) { throw 'The platform package version is missing.' }
$priorCache = $env:NUGET_PACKAGES
$priorCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Checked([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE. Synthetic evidence: $run" }
}

function Assert-PackageGraph([string]$AppDirectory, [string]$Version) {
    $assets = Get-Content -LiteralPath (Join-Path $AppDirectory 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $caravel = @($assets.libraries.PSObject.Properties | Where-Object Name -like 'Clinimatix.Caravel.*/*')
    if ($caravel.Count -ne $components.Count) { throw 'The upgrade fixture resolved an unexpected Caravel package graph.' }
    foreach ($component in $components) {
        $expected = "Clinimatix.Caravel.$component/$Version"
        if (@($caravel | Where-Object { $_.Name -ceq $expected -and $_.Value.type -eq 'package' }).Count -ne 1) {
            throw "The fixture must consume the exact real package $expected."
        }
    }
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count) {
        throw 'Upgrade qualification must not use project references.'
    }
}

Push-Location $root
try {
    # Scratch apps cannot inherit the repository's source references or central package policy.
    '<Project><PropertyGroup><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetAudit>true</NuGetAudit><NuGetAuditMode>all</NuGetAuditMode><WarningsAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsAsErrors></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $run 'Directory.Build.props')
    '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $run 'Directory.Packages.props')
    Copy-Item -LiteralPath (Join-Path $root 'global.json') -Destination $run
    if ($CandidatePackageDirectory) {
        $sourcePackages = (Resolve-Path -LiteralPath $CandidatePackageDirectory).Path
        foreach ($component in $components) {
            Copy-Item -LiteralPath (Join-Path $sourcePackages "Clinimatix.Caravel.$component.$candidateVersion.nupkg") -Destination $localPackages
        }
    } else {
        # As with the other package gates, the source solution must already have a locked restore.
        foreach ($component in $components) {
            Invoke-Checked @('pack', "src/Caravel.$component/Caravel.$component.csproj", '-c', 'Release', '--no-restore', '-o', $localPackages)
        }
    }
    $baselineFeed = Join-Path $run 'Baseline.NuGet.Config'
    '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' |
        Set-Content -LiteralPath $baselineFeed
    $escaped = [Security.SecurityElement]::Escape($localPackages)
    $candidateFeed = Join-Path $run 'Candidate.NuGet.Config'
    @"
<configuration>
  <packageSources><clear/><add key="candidate" value="$escaped"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping>
    <clear/>
    <packageSource key="candidate"><package pattern="Clinimatix.Caravel.*"/></packageSource>
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $candidateFeed
    $state = Join-Path $run 'synthetic state'
    $phases = @(
        @{ Name = 'baseline'; Command = 'seed'; Version = $BaselineVersion; Feed = $baselineFeed },
        @{ Name = 'candidate'; Command = 'verify'; Version = $candidateVersion; Feed = $candidateFeed }
    )
    $packageEvidence = @()
    foreach ($phase in $phases) {
        $appDirectory = Join-Path $run $phase.Name
        $null = New-Item -ItemType Directory -Path $appDirectory
        Get-ChildItem -LiteralPath (Join-Path $root 'tests/fixtures/ReleaseUpgrade') -File |
            Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $appDirectory
        $project = Join-Path $appDirectory 'ReleaseUpgrade.csproj'
        $env:NUGET_PACKAGES = Join-Path $run ($phase.Name + '-cache')
        $properties = @("-p:CaravelPackageVersion=$($phase.Version)", "-p:PlatformPackageVersion=$platformVersion")
        Invoke-Checked (@('restore', $project, '--configfile', $phase.Feed) + $properties)
        Invoke-Checked (@('restore', $project, '--locked-mode', '--configfile', $phase.Feed) + $properties)
        Assert-PackageGraph $appDirectory $phase.Version
        foreach ($component in $components) {
            $id = "Clinimatix.Caravel.$component".ToLowerInvariant()
            $package = Join-Path $env:NUGET_PACKAGES "$id/$($phase.Version)/$id.$($phase.Version).nupkg"
            $packageEvidence += [ordered]@{ Phase = $phase.Name; Package = $id; Version = $phase.Version; SHA256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash }
            if ($phase.Name -eq 'candidate') {
                $sourceHash = (Get-FileHash -LiteralPath (Join-Path $localPackages "Clinimatix.Caravel.$component.$candidateVersion.nupkg") -Algorithm SHA256).Hash
                if ($sourceHash -ne $packageEvidence[-1].SHA256) { throw 'The consumed candidate bytes differ from the local package.' }
            }
        }
        Invoke-Checked (@('build', $project, '-c', 'Release', '--no-restore') + $properties)
        # Separate processes and caches; only the original databases and key ring cross this boundary.
        Invoke-Checked @((Join-Path $appDirectory 'bin/Release/net10.0/ReleaseUpgrade.dll'), $phase.Command, $state, $phase.Version)
    }
    if ((Get-FileHash -LiteralPath (Join-Path $run 'baseline/Program.cs')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $run 'candidate/Program.cs')).Hash) { throw 'The app source changed during qualification.' }
    $summary = [ordered]@{
        BaselineVersion = $BaselineVersion
        CandidateVersion = $candidateVersion
        Provider = 'sqlite'
        Seed = Get-Content -LiteralPath (Join-Path $state 'seed-result.json') -Raw | ConvertFrom-Json
        Upgrade = Get-Content -LiteralPath (Join-Path $state 'verify-result.json') -Raw | ConvertFrom-Json
        Packages = $packageEvidence
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'upgrade-results.json')
    Write-Output "Release upgrade passed: published $BaselineVersion -> local $candidateVersion; Identity/session/MFA, Clarion data and durable queue recovery; no schema changes required. Synthetic evidence: $run"
} finally {
    $env:NUGET_PACKAGES = $priorCache
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $priorCertificate
    Pop-Location
}
