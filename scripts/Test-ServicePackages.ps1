param(
    [string]$PackageVersion,
    [string]$CandidatePackageDirectory
)
$ErrorActionPreference = 'Stop'
if ($PackageVersion -and $CandidatePackageDirectory) {
    throw 'PackageVersion cannot relabel supplied candidate packages.'
}
if ($CandidatePackageDirectory) {
    $CandidatePackageDirectory = (Resolve-Path -LiteralPath $CandidatePackageDirectory).Path
}
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/service packages ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$null = New-Item -ItemType Directory -Path $packages
$previousPackages = $env:NUGET_PACKAGES
$previousCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$components = @('Core', 'Clarion', 'Queues', 'Storage', 'Scheduling', 'Mail', 'Notifications')

function Invoke-Checked([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    if ($CandidatePackageDirectory) {
        $supplied = @(Get-ChildItem -LiteralPath $CandidatePackageDirectory -Filter '*.nupkg' -File -Recurse)
        foreach ($component in $components) {
            $prefix = "Clinimatix.Caravel.$component."
            $pattern = '^' + [regex]::Escape($prefix) + '[0-9]'
            $matches = @($supplied | Where-Object { $_.Name -cmatch $pattern })
            if ($matches.Count -ne 1) { throw "Expected exactly one supplied $component package." }
            Copy-Item -LiteralPath $matches[0].FullName -Destination $packages
            if ((Get-FileHash -LiteralPath $matches[0].FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath (Join-Path $packages $matches[0].Name) -Algorithm SHA256).Hash) {
                throw "Copied candidate bytes differ: $component"
            }
        }
    } else {
        # Source projects must already be restored with the pinned SDK.
        foreach ($component in $components) {
            $packArguments = @('pack', "src/Caravel.$component/Caravel.$component.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
            if ($PackageVersion) { $packArguments += "-p:Version=$PackageVersion" }
            Invoke-Checked $packArguments
        }
    }
    $version = $null
    $repositoryCommit = $null
    $packageEvidence = @()
    foreach ($component in $components) {
        $prefix = "Clinimatix.Caravel.$component."
        $files = @(Get-ChildItem -LiteralPath $packages -Filter "$prefix*.nupkg")
        if ($files.Count -ne 1) { throw "Expected exactly one $component package." }
        $packageVersion = $files[0].BaseName.Substring($prefix.Length)
        if ($null -ne $version -and $packageVersion -ne $version) { throw 'Service package versions must agree.' }
        $version = $packageVersion
        $archive = [IO.Compression.ZipFile]::OpenRead($files[0].FullName)
        try {
            $nuspecs = @($archive.Entries | Where-Object FullName -like '*.nuspec')
            if ($nuspecs.Count -ne 1) { throw "Expected one nuspec in $component package." }
            $reader = [IO.StreamReader]::new($nuspecs[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $metadata = $nuspec.package.metadata
            if ([string]$metadata.id -cne "Clinimatix.Caravel.$component" -or [string]$metadata.version -cne $version) {
                throw "Package metadata does not match filename: $component"
            }
            $commit = [string]$metadata.repository.commit
            if ($CandidatePackageDirectory) {
                if ($commit -notmatch '^[a-fA-F0-9]{40}$') { throw "Candidate source commit is missing: $component" }
                if ($repositoryCommit -and $commit -ine $repositoryCommit) { throw 'Candidate source commits must agree.' }
                $repositoryCommit = $commit
            }
            $packageEvidence += [ordered]@{
                Package = "Clinimatix.Caravel.$component"; Version = $version
                RepositoryCommit = $commit; SHA256 = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash
            }
        } finally { $archive.Dispose() }
    }
    $packageEvidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'package-evidence.json')

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
        $assets = Get-Content -LiteralPath (Join-Path $destination 'obj/project.assets.json') -Raw | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -eq 'project') { throw 'Package tests must not resolve source projects.' }
            if ($library.Name -like 'Clinimatix.Caravel.*/*') {
                $identity = $library.Name.Split('/')
                $expected = @($packageEvidence | Where-Object { $_.Package -ieq $identity[0] -and $_.Version -ceq $identity[1] })
                if ($expected.Count -ne 1) { throw "Unexpected Caravel package graph: $($library.Name)" }
                $id = $identity[0].ToLowerInvariant()
                $installed = Join-Path $env:NUGET_PACKAGES "$id/$version/$id.$version.nupkg"
                if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $expected[0].SHA256) {
                    throw "Installed package bytes differ: $($library.Name)"
                }
            }
        }
        Invoke-Checked @('test', $project, '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', (Join-Path $run 'results'))
    }
    Write-Output "Service package tests passed against isolated $version NuGet packages. Artifacts: $run"
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificate
    Pop-Location
}
