$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkerContainerInputs.ps1')
. (Join-Path $PSScriptRoot 'WorkerContainerCleanup.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('caravel-container-inputs-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$sha = 'a' * 40
$image = 'mcr.microsoft.com/dotnet/runtime@sha256:' + ('b' * 64)
$cases = 0
function Reject([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw 'Expected rejection was missing.' }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw } }
    $script:cases++
}
function Package([string]$Directory, [string]$Id, [string]$Commit, [string]$Version = '26.1.1-rc2') {
    $path = Join-Path $Directory "Clinimatix.Caravel.Queues.$Version.nupkg"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('package.nuspec')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write("<package><metadata><id>$Id</id><version>$Version</version><repository commit=`"$Commit`"/></metadata></package>") }
        finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    return $path
}
try {
    Reject { Get-WorkerContainerPackage $scratch $sha 'mcr.microsoft.com/dotnet/runtime:10.0' } 'by SHA256 digest'
    Reject { Get-WorkerContainerPackage $scratch $sha ('example.test/runtime@sha256:' + ('b' * 64)) } 'by SHA256 digest'
    Reject { Get-WorkerContainerPackage $scratch 'invalid' $image } 'forty hexadecimal'
    Reject { Get-WorkerContainerPackage $scratch $sha $image } 'exactly one supplied'
    foreach ($case in @(
        @{ Name='valid'; Id='Clinimatix.Caravel.Queues'; Commit=$sha },
        @{ Name='foreign'; Id='Foreign.Queues'; Commit=$sha },
        @{ Name='wrong-source'; Id='Clinimatix.Caravel.Queues'; Commit=('c' * 40) }
    )) {
        $directory = Join-Path $scratch $case.Name
        $null = New-Item -ItemType Directory -Path $directory
        $path = Package $directory $case.Id $case.Commit
        if ($case.Name -eq 'valid') {
            $result = Get-WorkerContainerPackage $directory $sha $image
            if ($result.Version -cne '26.1.1-rc2' -or $result.SHA256 -ne (Get-FileHash $path -Algorithm SHA256).Hash) { throw 'Valid package identity/hash mismatch.' }
            $cases++
            $null = Package $directory $case.Id $case.Commit '26.1.0-rc2'
            Reject { Get-WorkerContainerPackage $directory $sha $image } 'exactly one supplied'
        } elseif ($case.Name -eq 'foreign') {
            Reject { Get-WorkerContainerPackage $directory $sha $image } 'identity does not match'
        } else {
            Reject { Get-WorkerContainerPackage $directory $sha $image } 'declared source does not match'
        }
    }
    $script:cleanupCalls = @()
    $receipt = [ordered]@{ Outcome = 'failed'; Failure = 'original test failure' }
    $cleanupErrors = @(Complete-WorkerContainerEvidence $receipt $scratch 'synthetic=owned' {
        param([string[]]$Arguments)
        $script:cleanupCalls += ($Arguments -join ' ')
        if ($Arguments[0] -eq 'ps') { throw 'synthetic enumeration failure' }
        if ($Arguments[1] -eq 'ls') { return 'owned-resource' }
    })
    $saved = Get-Content (Join-Path $scratch 'result.json') -Raw | ConvertFrom-Json
    if ($cleanupErrors.Count -ne 1 -or $saved.Failure -ne 'original test failure' -or
        $saved.Outcome -ne 'failed' -or $saved.CleanupErrors[0] -notlike '*synthetic enumeration failure*' -or
        $script:cleanupCalls.Count -ne 5 -or $script:cleanupCalls[-1] -ne 'image rm owned-resource') {
        throw 'Cleanup failed to preserve receipt/original failure or attempt remaining stages.'
    }
    $cases++
    $receipt = [ordered]@{ Outcome = 'succeeded' }
    $cleanupErrors = @(Complete-WorkerContainerEvidence $receipt $scratch 'synthetic=owned' {
        param([string[]]$Arguments)
        if ($Arguments[0] -eq 'ps' -or $Arguments[1] -eq 'ls') { return 'owned-resource' }
        if ($Arguments[0] -eq 'image') { throw 'synthetic removal failure' }
    })
    $saved = Get-Content (Join-Path $scratch 'result.json') -Raw | ConvertFrom-Json
    if ($cleanupErrors.Count -ne 1 -or $saved.Outcome -ne 'failed') { throw 'Cleanup failure incorrectly retained success.' }
    $cases++
    Write-Output "Worker container input/cleanup cases passed: $cases"
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Scratch cleanup escaped temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
