$ErrorActionPreference = 'Stop'
$run = Join-Path ([IO.Path]::GetTempPath()) ('caravel-service-inputs-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$components = @('Core', 'Clarion', 'Queues', 'Storage', 'Scheduling', 'Mail', 'Notifications')
$commit = 'a' * 40

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
    Assert-Rejected 'no relabelling' 'cannot relabel' @{ PackageVersion = '1.0.0'; CandidatePackageDirectory = $run }
    Assert-Rejected 'missing package' 'exactly one supplied Core' @{ CandidatePackageDirectory = $run }
    foreach ($component in $components) { New-Fixture (Join-Path $run 'valid') $component }
    # Real artifacts include a distinct Storage.Azure package alongside Storage.
    New-Fixture (Join-Path $run 'valid') 'Storage.Azure'
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
} finally {
    # The generated fixture directory is unique and entirely owned by this script.
    $resolved = [IO.Path]::GetFullPath($run)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup must remain under the temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
