$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('caravel-release-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$version = '26.1.0-rc1'
$commit = 'a' * 40
function New-Candidate([string]$Name, [string]$Repository = 'https://github.com/Clinimatix/Caravel', [switch]$Extra, [switch]$MissingPackage) {
    $path = Join-Path $root "$Name.zip"
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($project in Get-ChildItem "$PSScriptRoot/../src" -Filter '*.csproj' -Recurse) {
            [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
            $id = [string]$xml.Project.PropertyGroup.PackageId
            if (-not $id -or ($MissingPackage -and $id -eq 'Clinimatix.Caravel.Storage.Azure')) { continue }
            $stream = $zip.CreateEntry("$id.$version.nupkg").Open()
            $package = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
            try {
                $writer = [IO.StreamWriter]::new($package.CreateEntry("$id.nuspec").Open())
                try { $writer.Write("<package><metadata><id>$id</id><version>$version</version><description>Fixture</description><license type='expression'>MIT</license><readme>README.md</readme><repository url='$Repository' commit='$commit'/></metadata></package>") } finally { $writer.Dispose() }
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
    $archive = New-Candidate 'valid'
    $parameters = @{ Archive = $archive; Sha256 = (Get-FileHash $archive).Hash; Version = $version; Commit = $commit; Destination = (Join-Path $root 'valid') }
    & "$PSScriptRoot/Expand-NuGetRelease.ps1" @parameters
    if (@(Get-ChildItem $parameters.Destination -Filter '*.nupkg').Count -eq 0) { throw 'No packages extracted.' }
    $parameters.Destination = Join-Path $root 'rejected'
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
    Write-Output 'Release validation self-check passed: valid candidate and six rejected candidates.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'caravel-release-*') { throw 'Unsafe cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
