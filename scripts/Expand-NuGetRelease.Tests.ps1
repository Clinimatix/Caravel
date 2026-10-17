$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('caravel-release-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$version = '26.1.1-rc2'
$commit = 'a' * 40
function New-Candidate([string]$Name, [string]$Repository = 'https://github.com/Clinimatix/Caravel', [switch]$Extra, [switch]$MissingPackage, [string]$CandidateVersion = $version) {
    $path = Join-Path $root "$Name.zip"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($project in Get-ChildItem "$PSScriptRoot/../src" -Filter '*.csproj' -Recurse) {
            [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
            $id = [string]$xml.Project.PropertyGroup.PackageId
            if (-not $id -or ($MissingPackage -and $id -eq 'Clinimatix.Caravel.Storage.Azure')) { continue }
            $stream = $zip.CreateEntry("$id.$CandidateVersion.nupkg").Open()
            $package = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $writer = [IO.StreamWriter]::new($package.CreateEntry("$id.nuspec").Open())
                try { $writer.Write("<package><metadata><id>$id</id><version>$CandidateVersion</version><description>Fixture</description><license type='expression'>MIT</license><readme>README.md</readme><repository url='$Repository' commit='$commit'/></metadata></package>") } finally { $writer.Dispose() }
                $writer = [IO.StreamWriter]::new($package.CreateEntry('README.md').Open())
                try { $writer.Write('Fixture') } finally { $writer.Dispose() }
            } finally { $package.Dispose(); $stream.Dispose() }
        }
        if ($Extra) { $zip.CreateEntry('../outside.nupkg') | Out-Null }
    } finally { $zip.Dispose() }
    return $path
}
function Assert-Rejected([hashtable]$Parameters, [string]$Message) {
    $rejected = $false
    try { & "$PSScriptRoot/Expand-NuGetRelease.ps1" @Parameters } catch { $rejected = $_.Exception.Message -like $Message }
    if (-not $rejected) { throw "Expected rejection: $Message" }
    if (Test-Path -LiteralPath $Parameters.Destination) { throw 'Invalid candidate was extracted.' }
}
try {
    $workflow = Get-Content -LiteralPath "$PSScriptRoot/../.github/workflows/publish-nuget.yml" -Raw
    $match = [regex]::Match($workflow, "RELEASE_VERSION -cnotmatch '([^']+)'")
    if (-not $match.Success) { throw 'Publication version validator was not found.' }
    $publicationPattern = $match.Groups[1].Value
    $archivePattern = ((Get-Command "$PSScriptRoot/Expand-NuGetRelease.ps1").Parameters['Version'].Attributes |
        Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] }).RegexPattern
    $validVersions = @('26.1.0', '26.1.0-m1', '26.1.0-rc2', '26.1.1-rc2', '26.1.1-rc3', '26.1.1')
    foreach ($candidateVersion in $validVersions) {
        if ($candidateVersion -cnotmatch $publicationPattern -or $candidateVersion -cnotmatch $archivePattern) {
            throw "Release validators rejected a supported version: $candidateVersion"
        }
        $candidate = New-Candidate "version-$candidateVersion" -CandidateVersion $candidateVersion
        & "$PSScriptRoot/Expand-NuGetRelease.ps1" -Archive $candidate -Sha256 (Get-FileHash $candidate).Hash `
            -Version $candidateVersion -Commit $commit -Destination (Join-Path $root "version-$candidateVersion")
    }
    $archive = New-Candidate 'valid'
    $parameters = @{ Archive = $archive; Sha256 = (Get-FileHash $archive).Hash; Version = $version; Commit = $commit; Destination = (Join-Path $root 'valid') }
    & "$PSScriptRoot/Expand-NuGetRelease.ps1" @parameters
    if (@(Get-ChildItem $parameters.Destination -Filter '*.nupkg').Count -eq 0) { throw 'No packages extracted.' }
    $parameters.Destination = Join-Path $root 'rejected'
    $invalidVersions = @('26.1.0-rc2.1', '26.1.0-rc2.01', '26.1.1-rc2.1', '26.1.0-m1.1',
        '26.1.0-rc2+patch.1', '26.1.0-rc2.', '26.1.0.1-rc2')
    foreach ($invalidVersion in $invalidVersions) {
        if ($invalidVersion -cmatch $publicationPattern -or $invalidVersion -cmatch $archivePattern) {
            throw "Release validators accepted an unsupported version: $invalidVersion"
        }
        $parameters.Version = $invalidVersion
        Assert-Rejected $parameters '*does not match*pattern*'
    }
    $parameters.Version = $version
    $parameters.Sha256 = '0' * 64
    Assert-Rejected $parameters 'Release ZIP checksum mismatch.'
    $parameters.Sha256 = (Get-FileHash $archive).Hash
    $parameters.Commit = 'b' * 40
    Assert-Rejected $parameters 'Package provenance mismatch:*'
    $parameters.Commit = $commit
    $parameters.Version = '26.1.0-rc2'
    Assert-Rejected $parameters 'Release ZIP package inventory mismatch.'
    $parameters.Version = $version
    $parameters.Archive = New-Candidate 'wrong-repository' -Repository 'https://example.invalid/repo'
    $parameters.Sha256 = (Get-FileHash $parameters.Archive).Hash
    Assert-Rejected $parameters 'Package provenance mismatch:*'
    $parameters.Archive = New-Candidate 'extra' -Extra
    $parameters.Sha256 = (Get-FileHash $parameters.Archive).Hash
    Assert-Rejected $parameters 'Release ZIP package inventory mismatch.'
    $parameters.Archive = New-Candidate 'missing-package' -MissingPackage
    $parameters.Sha256 = (Get-FileHash $parameters.Archive).Hash
    Assert-Rejected $parameters 'Release ZIP package inventory mismatch.'
    Write-Output "Release validation self-check passed: $($validVersions.Count) supported versions, $($invalidVersions.Count) rejected version strings and six rejected candidates."
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'caravel-release-*') { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
