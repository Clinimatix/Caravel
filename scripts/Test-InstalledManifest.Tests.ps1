$ErrorActionPreference = 'Stop'
$run = Join-Path ([IO.Path]::GetTempPath()) ('caravel-manifest-inputs-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$commit = 'a' * 40
$cases = 0
function New-Package([string]$Path, [string]$Id = 'Clinimatix.Caravel.Bosun', [string]$Version = '0.0.0-fixture', [string]$Commit = $commit) {
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('Bosun.nuspec').Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version><repository commit=`"$Commit`" /></metadata></package>") }
        finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
}
function Reject([string]$Name, [string]$Message, [hashtable]$Arguments) {
    $failure = $null
    try { & (Join-Path $PSScriptRoot 'Test-InstalledManifest.ps1') @Arguments | Out-Null }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notlike "*$Message*") { throw "Rejection failed: $Name" }
    $script:cases++
}
try {
    # No process is started: each malformed identity must fail before the executable boundary.
    $tool = Join-Path $run 'must-not-execute'
    'not executable' | Set-Content -LiteralPath $tool
    foreach ($case in @(
        @{ Name = 'foreign-id'; Id = 'Other.Tool'; Version = '0.0.0-fixture'; Commit = $commit },
        @{ Name = 'missing-source'; Id = 'Clinimatix.Caravel.Bosun'; Version = '0.0.0-fixture'; Commit = '' },
        @{ Name = 'unsafe-version'; Id = 'Clinimatix.Caravel.Bosun'; Version = '../../escape'; Commit = $commit })) {
        $package = Join-Path $run ($case.Name + '.nupkg')
        New-Package $package $case.Id $case.Version $case.Commit
        Reject $case.Name 'Invalid Bosun package identity' @{ ToolPath = $tool; BosunPackagePath = $package }
    }
    $package = Join-Path $run 'valid.nupkg'
    New-Package $package
    Reject 'wrong-source' 'differs from expected candidate' @{ ToolPath = $tool; BosunPackagePath = $package; ExpectedRepositoryCommit = ('b' * 40) }
    $store = Join-Path $run '.store/clinimatix.caravel.bosun/0.0.0-fixture/clinimatix.caravel.bosun/0.0.0-fixture'
    $null = New-Item -ItemType Directory -Path $store
    'different bytes' | Set-Content -LiteralPath (Join-Path $store 'clinimatix.caravel.bosun.0.0.0-fixture.nupkg')
    Reject 'installed-byte-mismatch' 'Installed Bosun bytes differ' @{ ToolPath = $tool; BosunPackagePath = $package }
    Write-Output "Installed manifest input guards passed: $cases"
} finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($run).StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($run) -notmatch '^caravel-manifest-inputs-[a-f0-9]{32}$') { throw 'Unexpected input fixture cleanup path.' }
    Remove-Item -LiteralPath $run -Recurse -Force
}
