$ErrorActionPreference = 'Stop'
$run = Join-Path ([IO.Path]::GetTempPath()) ('caravel-service-inputs-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$components = @('Core', 'Clarion', 'Queues', 'Storage', 'Scheduling', 'Mail', 'Notifications')
$commit = 'a' * 40
. (Join-Path $PSScriptRoot 'ServicePackageGraph.ps1')

function New-Fixture([string]$Directory, [string]$Component, [string]$Version = '0.0.0-fixture', [string]$Commit = $commit, [string]$Id) {
    $null = New-Item -ItemType Directory -Path $Directory -Force
    if (-not $Id) { $Id = "Clinimatix.Caravel.$Component" }
    $path = Join-Path $Directory "Clinimatix.Caravel.$Component.$Version.nupkg"
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry("$Component.nuspec").Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version><repository commit=`"$Commit`" /></metadata></package>") }
        finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
}

function Assert-Rejected([string]$Name, [string]$Expected, [hashtable]$Arguments) {
    $failure = $null
    try { & (Join-Path $PSScriptRoot 'Test-ServicePackages.ps1') @Arguments | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch $Expected) { throw "$Name failed: expected '$Expected', got '$failure'." }
    Write-Output "PASS $Name"
}

try {
    $stale = Join-Path $run 'stale-export'
    $null = New-Item -ItemType Directory -Path $stale
    'synthetic sentinel' | Set-Content -LiteralPath (Join-Path $stale 'sentinel.txt')
    $failure = $null
    try { & (Join-Path $PSScriptRoot 'Export-ServicePackageEvidence.ps1') -Destination $stale | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -cne 'Evidence destination must be an empty directory.' -or @(Get-ChildItem $stale).Count -ne 1) {
        throw 'Nonempty evidence destination must be rejected before copying.'
    }
    Write-Output 'PASS nonempty evidence destination'
    Assert-Rejected 'no relabelling' 'cannot relabel' @{ PackageVersion = '1.0.0'; CandidatePackageDirectory = $run }
    Assert-Rejected 'missing package' 'exactly one supplied Core' @{ CandidatePackageDirectory = $run }
    foreach ($component in $components) { New-Fixture (Join-Path $run 'valid') $component }
    # Real artifacts include a distinct Storage.Azure package alongside Storage.
    New-Fixture (Join-Path $run 'valid') 'Storage.Azure'
    Assert-Rejected 'expected source SHA' 'does not match expected SHA: Core' @{ CandidatePackageDirectory = (Join-Path $run 'valid'); ExpectedRepositoryCommit = ('b' * 40) }
    $duplicate = Join-Path $run 'duplicate'
    Copy-Item -LiteralPath (Join-Path $run 'valid') -Destination $duplicate -Recurse
    $null = New-Item -ItemType Directory -Path (Join-Path $duplicate 'nested')
    Copy-Item -LiteralPath (Join-Path $duplicate 'Clinimatix.Caravel.Core.0.0.0-fixture.nupkg') -Destination (Join-Path $duplicate 'nested')
    Assert-Rejected 'nested duplicate' 'exactly one supplied Core' @{ CandidatePackageDirectory = $duplicate }
    foreach ($case in @('version', 'commit', 'missing-commit', 'id')) {
        $directory = Join-Path $run $case
        foreach ($component in $components) {
            $arguments = @{ Directory = $directory; Component = $component }
            if ($component -eq 'Queues') {
                switch ($case) {
                    'version' { $arguments.Version = '0.0.0-other' }
                    'commit' { $arguments.Commit = 'b' * 40 }
                    'missing-commit' { $arguments.Commit = '' }
                    'id' { $arguments.Id = 'Clinimatix.Caravel.Core' }
                }
            }
            New-Fixture @arguments
        }
        New-Fixture $directory 'Storage.Azure'
        $expected = switch ($case) {
            'version' { 'versions must agree' }
            'commit' { 'source commits must agree' }
            'missing-commit' { 'source commit is missing' }
            'id' { 'metadata does not match filename' }
        }
        Assert-Rejected $case $expected @{ CandidatePackageDirectory = $directory }
    }
    # Exercise the same post-restore graph guard used by the real package suites.
    # A valid package graph must pass first; each fault then has a specific failure.
    $cache = Join-Path $run 'cache'
    $installedDirectory = Join-Path $cache 'clinimatix.caravel.core/0.0.0-fixture'
    $null = New-Item -ItemType Directory -Path $installedDirectory -Force
    $installed = Join-Path $installedDirectory 'clinimatix.caravel.core.0.0.0-fixture.nupkg'
    Copy-Item -LiteralPath (Join-Path $run 'valid/Clinimatix.Caravel.Core.0.0.0-fixture.nupkg') -Destination $installed
    $evidence = @([pscustomobject]@{ Package = 'Clinimatix.Caravel.Core'; Version = '0.0.0-fixture'; SHA256 = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash })
    $assets = [pscustomobject]@{ libraries = [pscustomobject]@{ 'Clinimatix.Caravel.Core/0.0.0-fixture' = [pscustomobject]@{ type = 'package' } } }
    Assert-ServicePackageGraph $assets $evidence $cache
    Write-Output 'PASS valid installed package graph'
    foreach ($case in @('checksum', 'dependency', 'dependency-version', 'project')) {
        $fault = switch ($case) {
            'checksum' { Add-Content -LiteralPath $installed 'changed bytes'; $assets }
            'dependency' { [pscustomobject]@{ libraries = [pscustomobject]@{ 'Clinimatix.Caravel.Unexpected/0.0.0-fixture' = [pscustomobject]@{ type = 'package' } } } }
            'dependency-version' { [pscustomobject]@{ libraries = [pscustomobject]@{ 'Clinimatix.Caravel.Core/0.0.0-other' = [pscustomobject]@{ type = 'package' } } } }
            'project' { [pscustomobject]@{ libraries = [pscustomobject]@{ 'Caravel.Core/0.0.0-fixture' = [pscustomobject]@{ type = 'project' } } } }
        }
        $expected = switch ($case) {
            'checksum' { 'Installed package bytes differ: Clinimatix.Caravel.Core/0.0.0-fixture' }
            'dependency' { 'Unexpected Caravel package graph: Clinimatix.Caravel.Unexpected/0.0.0-fixture' }
            'dependency-version' { 'Unexpected Caravel package graph: Clinimatix.Caravel.Core/0.0.0-other' }
            'project' { 'Package tests must not resolve source projects.' }
        }
        $failure = $null
        try { Assert-ServicePackageGraph $fault $evidence $cache } catch { $failure = $_.Exception.Message }
        if ($failure -cne $expected) { throw "Graph $case failed: expected '$expected', got '$failure'." }
        Write-Output "PASS installed graph $case"
    }
} finally {
    # The generated fixture directory is unique and entirely owned by this script.
    $resolved = [IO.Path]::GetFullPath($run)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup must remain under the temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
