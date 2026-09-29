param([string]$MetadataPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$metadata = if ($MetadataPath) {
    Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
} else {
    Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'
}
$latest = $metadata.'releases-index' |
    Where-Object { $_.'release-type' -eq 'lts' -and $_.'support-phase' -in @('active', 'maintenance') } |
    Sort-Object { [version]$_.'channel-version' } -Descending |
    Select-Object -First 1
if (-not $latest) { throw 'Could not determine the latest generally available .NET LTS.' }
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$target = [string]$props.Project.PropertyGroup.TargetFramework
$expected = 'net' + $latest.'channel-version'
if ($target -ne $expected) { throw "Latest LTS is $expected; update the framework target, SDK pin, packages, templates and docs together (currently $target)." }
$sdk = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk
if (-not $sdk.version.StartsWith($latest.'channel-version' + '.') -or $sdk.allowPrerelease) {
    throw 'The SDK pin must match the latest LTS and disallow preview SDKs.'
}
Write-Output "Clinimatix Caravel targets the latest generally available .NET LTS: $target."
