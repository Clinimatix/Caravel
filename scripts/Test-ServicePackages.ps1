$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/service packages ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$null = New-Item -ItemType Directory -Path $packages
$previousPackages = $env:NUGET_PACKAGES
$previousCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$components = @('Clarion', 'Queues', 'Storage', 'Scheduling', 'Mail', 'Notifications')

function Invoke-Checked([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    # Source projects must already be restored with the pinned SDK.
    foreach ($component in $components) {
        Invoke-Checked @('pack', "src/Caravel.$component/Caravel.$component.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
    }
    $version = $null
    foreach ($component in $components) {
        $prefix = "Clinimatix.Caravel.$component."
        $files = @(Get-ChildItem -LiteralPath $packages -Filter "$prefix*.nupkg")
        if ($files.Count -ne 1) { throw "Expected exactly one $component package." }
        $packageVersion = $files[0].BaseName.Substring($prefix.Length)
        if ($null -ne $version -and $packageVersion -ne $version) { throw 'Service package versions must agree.' }
        $version = $packageVersion
    }

    $escaped = [Security.SecurityElement]::Escape($packages)
    $feed = Join-Path $run 'NuGet.Config'
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escaped" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="Clinimatix.Caravel.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $feed
    $env:NUGET_PACKAGES = Join-Path $run 'cache'

    foreach ($component in $components) {
        $testName = "Caravel.$component.Tests"
        $source = Join-Path $root "tests/$testName"
        $destination = Join-Path $run "tests/$testName"
        $null = New-Item -ItemType Directory -Path $destination
        Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Copy-Item -Destination $destination
        $project = Join-Path $destination "$testName.csproj"
        Copy-Item -LiteralPath (Join-Path $source "$testName.csproj") -Destination $project

        # Only the scratch project changes. Its real tests now consume the fresh NuGet graph.
        [xml]$xml = Get-Content -LiteralPath $project -Raw
        $references = @($xml.SelectNodes('//ProjectReference'))
        if ($references.Count -eq 0) { throw "$testName has no project references to replace." }
        foreach ($reference in $references) {
            $projectName = [IO.Path]::GetFileNameWithoutExtension($reference.GetAttribute('Include').Replace('\', '/'))
            if ($projectName -notin ($components | ForEach-Object { "Caravel.$_" })) {
                throw "Unexpected project reference in ${testName}: $projectName"
            }
            $package = $xml.CreateElement('PackageReference')
            $package.SetAttribute('Include', "Clinimatix.$projectName")
            $package.SetAttribute('VersionOverride', $version)
            $null = $reference.ParentNode.ReplaceChild($package, $reference)
        }
        $xml.Save($project)
        Invoke-Checked @('restore', $project, '--configfile', $feed)
        Invoke-Checked @('test', $project, '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', (Join-Path $run 'results'))
    }
    Write-Output "Service package tests passed against isolated $version NuGet packages. Artifacts: $run"
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificate
    Pop-Location
}
