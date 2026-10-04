param(
    [ValidateSet('sqlite', 'sqlserver', 'postgres')][string]$Provider = 'sqlite',
    [switch]$Browser,
    [string]$CandidatePackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path ([IO.Path]::GetTempPath()) ('caravel identity starter ' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
if (-not $IsWindows) {
    # NuGet project graphs must use one physical path, including through macOS /var symlinks.
    Push-Location -LiteralPath $run
    try {
        $physicalPath = & /bin/pwd -P
        if ($LASTEXITCODE -ne 0 -or $physicalPath -isnot [string] -or
            -not [IO.Path]::IsPathFullyQualified($physicalPath)) { throw 'Cannot resolve the test directory physical path.' }
        $run = $physicalPath.Trim()
    } finally { Pop-Location }
}
$packages = Join-Path $run 'packages'
$tool = Join-Path $run 'tool'
$tests = Join-Path $run 'Tests'
$app = Join-Path $run 'Workbench'
$null = New-Item -ItemType Directory -Path $packages, $tool, $tests
$variables = @('NUGET_PACKAGES', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'Caravel__DatabaseProvider', 'Caravel__IdentityDatabase', 'CARAVEL_IDENTITY_TEST_PROVIDER')
if ($CandidatePackageDirectory) { $CandidatePackageDirectory = (Resolve-Path -LiteralPath $CandidatePackageDirectory).Path }
$previous = @{}
foreach ($variable in $variables) { $previous[$variable] = [Environment]::GetEnvironmentVariable($variable) }
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $components = @('Core', 'AspNetCore', 'Clarion', 'Auth', 'Queues', 'Mail', 'Bosun')
    $candidateHashes = @{}
    if ($CandidatePackageDirectory) {
        [xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
        $candidateVersion = [string]$properties.Project.PropertyGroup.Version
        $supplied = @(Get-ChildItem -LiteralPath $CandidatePackageDirectory -Filter '*.nupkg' -File -Recurse)
        foreach ($name in $components) {
            $fileName = "Clinimatix.Caravel.$name.$candidateVersion.nupkg"
            $matches = @($supplied | Where-Object Name -CEQ $fileName)
            if ($matches.Count -ne 1) { throw "Expected one supplied candidate package: $fileName" }
            Copy-Item -LiteralPath $matches[0].FullName -Destination $packages
            $candidateHashes[$name] = (Get-FileHash -LiteralPath $matches[0].FullName -Algorithm SHA256).Hash
            if ((Get-FileHash -LiteralPath (Join-Path $packages $fileName) -Algorithm SHA256).Hash -ne $candidateHashes[$name]) {
                throw "Copied candidate bytes differ: $fileName"
            }
        }
    } else {
        foreach ($name in $components) {
            Invoke-Checked dotnet @('pack', "src/Caravel.$name/Caravel.$name.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
        }
    }
    & (Join-Path $PSScriptRoot 'Test-PackagePaths.ps1') -PackageDirectory $packages
    $version = (Get-ChildItem -LiteralPath $packages -Filter 'Clinimatix.Caravel.Bosun.*.nupkg').BaseName.Substring('Clinimatix.Caravel.Bosun.'.Length)
    $feed = Join-Path $run 'NuGet.Config'
    $escaped = [Security.SecurityElement]::Escape($packages)
    @"
<configuration><packageSources><clear/><add key="local" value="$escaped"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="local"><package pattern="Clinimatix.Caravel.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $feed
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    Invoke-Checked dotnet @('tool', 'install', 'Clinimatix.Caravel.Bosun', '--version', $version, '--tool-path', $tool, '--configfile', $feed)
    $cli = Join-Path $tool $(if ($IsWindows) { 'caravel.exe' } else { 'caravel' })
    Set-Location -LiteralPath $run
    Invoke-Checked $cli @('new', 'Workbench', '--stack', 'identity')
    foreach ($relative in @('.config/dotnet-tools.json', 'Properties/launchSettings.json',
        'Database/Identity/IdentityContextModelSnapshot.cs', 'Database/Queue/QueueDbContextModelSnapshot.cs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $relative) -PathType Leaf)) { throw "Generated nested file is missing: $relative" }
    }
    if (-not $IsWindows -and @(Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object { $_.Name.Contains('\') }).Count) {
        throw 'Generated filenames contain a Windows directory separator.'
    }
    $env:Caravel__DatabaseProvider = $Provider
    $env:CARAVEL_IDENTITY_TEST_PROVIDER = $Provider
    $env:Caravel__IdentityDatabase = if ($Provider -eq 'sqlite') { Join-Path $run 'must-not-exist.db' } else {
        $variable = if ($Provider -eq 'sqlserver') { 'CARAVEL_TEST_SQLSERVER' } else { 'CARAVEL_TEST_POSTGRES' }
        $value = [Environment]::GetEnvironmentVariable($variable)
        if (-not $value) { throw "Set $variable to a disposable loopback server." }
        $value
    }
    Set-Location -LiteralPath $app
    if ($Provider -ne 'sqlite') {
        # EF's source-file discovery can reuse an excluded snapshot; move this never-applied bundle outside the project.
        $migrationSource = (Resolve-Path -LiteralPath (Join-Path $app 'Database')).Path
        $migrationArchive = [IO.Path]::GetFullPath((Join-Path $run 'bundled-sqlite-migrations'))
        $runRoot = [IO.Path]::GetFullPath($run) + [IO.Path]::DirectorySeparatorChar
        foreach ($target in @($migrationSource, $migrationArchive)) {
            if (-not $target.StartsWith($runRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Migration paths must remain inside this owned test run.' }
        }
        Move-Item -LiteralPath $migrationSource -Destination $migrationArchive
    }
    Invoke-Checked dotnet @('restore', '--configfile', $feed)
    Invoke-Checked dotnet @('restore', '--locked-mode', '--configfile', $feed)
    Invoke-Checked dotnet @('tool', 'restore', '--configfile', $feed)
    if ($CandidatePackageDirectory) {
        foreach ($name in $components) {
            $id = "clinimatix.caravel.$name".ToLowerInvariant()
            $consumed = if ($name -eq 'Bosun') { Join-Path $tool ".store/$id/$version/$id/$version/$id.$version.nupkg" }
                else { Join-Path $env:NUGET_PACKAGES "$id/$version/$id.$version.nupkg" }
            if ((Get-FileHash -LiteralPath $consumed -Algorithm SHA256).Hash -ne $candidateHashes[$name]) {
                throw "Installed package differs from supplied candidate: $id"
            }
        }
        $candidateHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'consumed-candidate-sha256.json')
    }
    if ($Provider -ne 'sqlite') {
        Invoke-Checked dotnet @('ef', 'migrations', 'add', 'InitialIdentity', '--context', 'IdentityContext', '--output-dir', 'Database/Identity')
        Invoke-Checked dotnet @('ef', 'migrations', 'add', 'InitialQueue', '--context', 'QueueDbContext', '--output-dir', 'Database/Queue')
    }
    Invoke-Checked $cli @('route:list', '--json')
    if ($Provider -eq 'sqlite' -and (Test-Path -LiteralPath $env:Caravel__IdentityDatabase)) { throw 'Generation or route inspection created a database.' }
    foreach ($file in @('WorkItemApplicationTests.cs', 'IdentityTestDatabase.cs')) {
        (Get-Content -LiteralPath (Join-Path $root "tests/Caravel.Identity.Tests/$file") -Raw).Replace('Caravel.IdentitySample', 'WorkbenchApplication') | Set-Content -LiteralPath (Join-Path $tests $file)
    }
    foreach ($file in @('IdentityStarterFixture.cs', 'IdentityStarterLifecycleTests.cs')) {
        (Get-Content -LiteralPath (Join-Path $root "tests/fixtures/$file") -Raw).Replace('Caravel.IdentitySample', 'WorkbenchApplication') | Set-Content -LiteralPath (Join-Path $tests $file)
    }
    @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include="../Workbench/Workbench.csproj"/><PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12"/><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="4.0.0"/></ItemGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $tests 'StarterTests.csproj')
    '<Solution><Project Path="Workbench/Workbench.csproj"/><Project Path="Tests/StarterTests.csproj"/></Solution>' | Set-Content -LiteralPath (Join-Path $run 'StarterQualification.slnx')
    Invoke-Checked dotnet @('restore', (Join-Path $tests 'StarterTests.csproj'), '--configfile', $feed)
    Invoke-Checked dotnet @('test', (Join-Path $tests 'StarterTests.csproj'), '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', (Join-Path $run 'results'))
    $results = @(Get-ChildItem -LiteralPath (Join-Path $run 'results') -Filter '*.trx' | ForEach-Object { ([xml](Get-Content -LiteralPath $_.FullName -Raw)).TestRun.Results.UnitTestResult })
    if ($results.Count -eq 0 -or @($results | Where-Object outcome -ne 'Passed').Count -gt 0) { throw 'Generated application qualification requires executed passing tests without skips.' }
    if ($Browser) {
        $previousBrowserRoot = $env:CARAVEL_BROWSER_APP_ROOT
        try {
            $env:CARAVEL_BROWSER_APP_ROOT = $app
            Invoke-Checked node @('--test', (Join-Path $root 'tests/browser/work-items.browser.test.cjs'))
        } finally { $env:CARAVEL_BROWSER_APP_ROOT = $previousBrowserRoot }
    }
    Write-Output "Installed Identity starter passed against ${Provider}. Synthetic artifacts: $run"
} finally {
    foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $previous[$variable]) }
    Pop-Location
}
