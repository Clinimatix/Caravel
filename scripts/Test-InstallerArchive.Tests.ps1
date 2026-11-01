# Offline archive and transport contracts; payloads are never installed or executed.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/install.ps1"
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$sandbox = Join-Path $temporaryRoot ('caravel-archive-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$script:checks = 0
function Assert-ArchiveFixture($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message, [string]$ExpectedMessage, [type]$ExpectedType) {
    try { & $Action; throw "Unexpected acceptance: $Message" }
    catch {
        if ($_.Exception.Message -like 'Unexpected acceptance:*') { throw }
        if ($ExpectedType) {
            $exception = $_.Exception
            while ($exception -and $exception -isnot $ExpectedType) { $exception = $exception.InnerException }
            if (!$exception) { throw }
        } elseif (!$ExpectedMessage -or $_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    $script:checks++
}
function Write-FixtureZip([string]$Path, [object[]]$Entries) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Entries) {
            $stream = $zip.CreateEntry($entry.Name).Open()
            try { $stream.Write($entry.Bytes, 0, $entry.Bytes.Length) } finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
$version = '26.1.1-rc2'
$commit = '1' * 40
$packageName = "Clinimatix.Caravel.Bosun.$version.nupkg"
$manifest = '<package><metadata><id>Clinimatix.Caravel.Bosun</id><version>26.1.1-rc2</version><repository url="https://github.com/Clinimatix/Caravel" commit="' + $commit + '"/></metadata></package>'
try {
    $cases = [ordered]@{
        valid = @($manifest)
        wrongId = @($manifest.Replace('Clinimatix.Caravel.Bosun', 'Other.Tool'))
        wrongIdCase = @($manifest.Replace('Clinimatix.Caravel.Bosun', 'clinimatix.caravel.bosun'))
        wrongVersion = @($manifest.Replace($version, '26.1.0-rc2'))
        wrongRepository = @($manifest.Replace('https://github.com/Clinimatix/Caravel', 'https://example.invalid/Caravel'))
        wrongCommit = @($manifest.Replace($commit, ('2' * 40)))
        missingManifest = @()
        duplicateManifest = @($manifest, $manifest)
        malformedXml = @('<package>')
        prohibitedDtd = @('<!DOCTYPE package [<!ENTITY name "Clinimatix.Caravel.Bosun">]>' + $manifest.Replace('Clinimatix.Caravel.Bosun', '&name;'))
    }
    $validBytes = $null
    foreach ($case in $cases.GetEnumerator()) {
        $package = Join-Path $sandbox ($case.Key + '.nupkg')
        $entries = @()
        for ($i = 0; $i -lt $case.Value.Count; $i++) {
            $entries += @{ Name = "manifest$i.nuspec"; Bytes = [Text.Encoding]::UTF8.GetBytes($case.Value[$i]) }
        }
        Write-FixtureZip $package $entries
        $bytes = [IO.File]::ReadAllBytes($package)
        $archive = Join-Path $sandbox ($case.Key + '.zip')
        Write-FixtureZip $archive @(@{ Name = $packageName; Bytes = $bytes })
        $output = New-Item -ItemType Directory -Path (Join-Path $sandbox ($case.Key + '-feed'))
        $release = @{ sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash; commit = $commit }
        if ($case.Key -eq 'valid') {
            Export-CaravelBosun $archive $output.FullName $version $release
            Assert-ArchiveFixture ((Get-FileHash (Join-Path $output $packageName)).Hash -eq (Get-FileHash $package).Hash) 'Verified package bytes changed'
            $validBytes = $bytes
        } else {
            $expectedMessage = if ($case.Key -in @('missingManifest', 'duplicateManifest')) { 'Expected one package manifest.' } else { 'Bosun package provenance mismatch.' }
            $expectedType = if ($case.Key -in @('malformedXml', 'prohibitedDtd')) { [Xml.XmlException] } else { $null }
            Assert-Rejected { Export-CaravelBosun $archive $output.FullName $version $release } $case.Key $expectedMessage $expectedType
        }
    }
    foreach ($shape in @('missing', 'nested', 'wrongCase', 'duplicate', 'extraPaths')) {
        $entries = switch ($shape) {
            'missing' { @(@{ Name = 'Other.nupkg'; Bytes = $validBytes }) }
            'nested' { @(@{ Name = "nested/$packageName"; Bytes = $validBytes }) }
            'wrongCase' { @(@{ Name = $packageName.ToLowerInvariant(); Bytes = $validBytes }) }
            'duplicate' { @(@{ Name = $packageName; Bytes = $validBytes }, @{ Name = $packageName; Bytes = $validBytes }) }
            'extraPaths' { @(@{ Name = $packageName; Bytes = $validBytes }, @{ Name = '../escape.txt'; Bytes = [Text.Encoding]::UTF8.GetBytes('never extract') }) }
        }
        $archive = Join-Path $sandbox ($shape + '.zip')
        Write-FixtureZip $archive $entries
        $output = New-Item -ItemType Directory -Path (Join-Path $sandbox ($shape + '-output'))
        $release = @{ sha256 = (Get-FileHash -LiteralPath $archive).Hash; commit = $commit }
        if ($shape -eq 'extraPaths') {
            Export-CaravelBosun $archive $output.FullName $version $release
            Assert-ArchiveFixture (@(Get-ChildItem -LiteralPath $output).Count -eq 1) 'Extracted unselected entries'
            Assert-ArchiveFixture (!(Test-Path -LiteralPath (Join-Path $sandbox 'escape.txt'))) 'Archive path escaped the feed'
        } else { Assert-Rejected { Export-CaravelBosun $archive $output.FullName $version $release } $shape 'Expected exactly one bounded Bosun package.' }
    }
    $invalid = Join-Path $sandbox 'invalid.zip'
    [IO.File]::WriteAllText($invalid, 'not a ZIP')
    $output = New-Item -ItemType Directory -Path (Join-Path $sandbox 'invalid-output')
    Assert-Rejected { Export-CaravelBosun $invalid $output.FullName $version @{sha256 = (Get-FileHash $invalid).Hash; commit = $commit} } 'invalid ZIP' '' ([IO.InvalidDataException])

    # The fake HTTP boundary replaces partial bytes; verify retry orchestration and stop.
    $script:attempts = 0
    $script:delays = @()
    function Start-Sleep([int]$Seconds) { $script:delays += $Seconds }
    function Invoke-WebRequest {
        param($Uri, $OutFile, $MaximumRedirection, $TimeoutSec, $ErrorAction)
        $script:attempts++
        Assert-ArchiveFixture ($MaximumRedirection -eq 5 -and $TimeoutSec -eq 120 -and $ErrorAction -eq 'Stop') 'Transport limits changed'
        if ($script:attempts -lt 3) {
            [IO.File]::WriteAllText($OutFile, 'partial response')
            throw 'synthetic transport interruption'
        }
        [IO.File]::WriteAllText($OutFile, 'complete response')
    }
    $download = Join-Path $sandbox 'download.zip'
    Receive-CaravelArchive 'https://example.invalid/fixture' $download
    Assert-ArchiveFixture ($script:attempts -eq 3) 'Successful retry did not stop at third attempt'
    Assert-ArchiveFixture (($script:delays -join ',') -eq '1,2') 'Transport retry delay changed'
    Assert-ArchiveFixture ([IO.File]::ReadAllText($download) -ceq 'complete response') 'Partial bytes survived successful transport'
    Write-Output "PASS: $script:checks installer archive/transport boundary checks; no downloads, installation or payload execution."
} finally {
    $resolved = (Resolve-Path -LiteralPath $sandbox).ProviderPath
    $item = Get-Item -LiteralPath $resolved -Force
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporaryRoot -or
        [IO.Path]::GetFileName($resolved) -cnotmatch '^caravel-archive-fixture-[0-9a-f]{32}$' -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing fixture cleanup outside its temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
