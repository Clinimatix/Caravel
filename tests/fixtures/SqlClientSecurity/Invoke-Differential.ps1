param(
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$ApprovedSdkVersion,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$FixtureDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'OutputRoot must be absent; existing work is never overwritten.' }
New-Item -ItemType Directory -Path $root | Out-Null
$names = @('DOTNET_CLI_HOME','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_CLI_TELEMETRY_OPTOUT')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:DOTNET_CLI_HOME = Join-Path $root 'cli-home'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $root 'http-cache'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $outcomes = @()
    foreach ($version in @('6.1.7','6.1.8')) {
        $lane = Join-Path $root $version
        New-Item -ItemType Directory -Path $lane | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureDirectory 'SqlClientSecurityTests.cs'),(Join-Path $FixtureDirectory 'SqlClientSecurity.csproj') -Destination $lane
        @{sdk=@{version=$ApprovedSdkVersion;rollForward='disable';allowPrerelease=$false}} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $lane 'global.json')
        '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $lane 'NuGet.Config')
        $env:NUGET_PACKAGES = Join-Path $lane 'packages'
        Push-Location -LiteralPath $lane
        try {
            $actualSdk = (& $dotnet --version).Trim()
            if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $ApprovedSdkVersion) { throw "SDK mismatch: $actualSdk" }
            & $dotnet --info *> dotnet-info.txt
            & $dotnet restore SqlClientSecurity.csproj "-p:SqlClientVersion=$version" --configfile NuGet.Config *> restore.txt
            if ($LASTEXITCODE -ne 0) { throw "Restore failed for $version; see $lane" }
            & $dotnet restore SqlClientSecurity.csproj "-p:SqlClientVersion=$version" --locked-mode --configfile NuGet.Config *> locked-restore.txt
            if ($LASTEXITCODE -ne 0) { throw "Locked restore failed for $version" }
            & $dotnet build SqlClientSecurity.csproj "-p:SqlClientVersion=$version" --no-restore -c Release *> build.txt
            if ($LASTEXITCODE -ne 0) { throw "Build failed for $version; no test result inferred" }
            # No restore/build and no database, network, machine trust or configuration operations in tests.
            & $dotnet test SqlClientSecurity.csproj "-p:SqlClientVersion=$version" --no-build --no-restore -c Release --logger 'trx;LogFileName=results.trx' --results-directory evidence *> test.txt
            $testExit = $LASTEXITCODE
            [xml]$trx = Get-Content -LiteralPath evidence/results.trx -Raw
            $results = @($trx.TestRun.Results.UnitTestResult | ForEach-Object {
                [ordered]@{name=$_.testName;outcome=$_.outcome;errorMessage=[string]$_.Output.ErrorInfo.Message}
            })
            $package = Join-Path $env:NUGET_PACKAGES "microsoft.data.sqlclient/$version/microsoft.data.sqlclient.$version.nupkg"
            $outcomes += [ordered]@{version=$version;sdk=$actualSdk;exitCode=$testExit;packageSha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash;testSourceSha256=(Get-FileHash SqlClientSecurityTests.cs -Algorithm SHA256).Hash;results=$results}
        } finally { Pop-Location }
    }
    $outcomes | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'differential.json')
    if ($outcomes[0].exitCode -eq 0) { throw 'Old baseline unexpectedly passed; inspect differential evidence.' }
    if ($outcomes[1].exitCode -ne 0) { throw 'Fixed version failed; inspect differential evidence.' }
    if ($outcomes[0].results.Count -ne 20 -or $outcomes[1].results.Count -ne 20) { throw 'Expected exactly 20 cases in each lane.' }
    if (@($outcomes[1].results | Where-Object outcome -ne 'Passed').Count) { throw 'Fixed lane has non-passing cases.' }
    $oldFailed = @($outcomes[0].results | Where-Object outcome -eq 'Failed')
    if ($oldFailed.Count -ne 9) { throw 'Old baseline must reproduce exactly nine expected failures.' }
    foreach ($case in $outcomes[0].results) {
        $expectedFailure = $case.name -match 'EnclaveCacheIsolatesAttestationPolicies|EnclaveCachePreservesFieldBoundaries|ExplicitCertificateNameRejectsMissingCertificate' -or
            ($case.name -match 'ExplicitCertificateNameIsEnforced' -and $case.name -match 'other.example' -and $case.name -match 'errors: None')
        $expectedOutcome = if ($expectedFailure) { 'Failed' } else { 'Passed' }
        if ($case.outcome -ne $expectedOutcome) { throw "Unexpected old baseline outcome: $($case.name) = $($case.outcome)" }
        if ($expectedFailure) {
            $expectedAssertion = if ($case.name -match 'EnclaveCacheIsolatesAttestationPolicies|EnclaveCachePreservesFieldBoundaries') {
                'Assert.Null() Failure: Value is not null'
            } else {
                'Assert.Throws() Failure: No exception was thrown'
            }
            # xUnit v2's first error line identifies the actual assertion category.
            # Match it exactly so setup, reflection, runtime or wrong-exception failures cannot pass.
            $firstErrorLine = ([string]$case.errorMessage -split '\r?\n', 2)[0]
            if ($firstErrorLine -cne $expectedAssertion) {
                throw "Wrong old baseline assertion for $($case.name): expected '$expectedAssertion', received '$firstErrorLine'"
            }
        }
    }
    # Full assertion messages remain in differential.json and the original TRX for review.
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name],'Process') }
}
