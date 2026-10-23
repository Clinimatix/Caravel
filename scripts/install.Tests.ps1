# Local fixtures: never download or execute dotnet/tool payloads.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/install.ps1"
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('caravel-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $sandbox | Out-Null
if (!$IsWindows) {
    # macOS exposes its temporary directory through /var, which is a system symlink.
    # Use the physical fixture path so installer link rejection is tested deliberately.
    Push-Location -LiteralPath $sandbox
    try {
        $physicalPath = & /bin/pwd -P
        if ($LASTEXITCODE -ne 0 -or $physicalPath -isnot [string] -or
            ![IO.Path]::IsPathFullyQualified($physicalPath)) { throw 'Cannot resolve the fixture physical path.' }
        $sandbox = $physicalPath.Trim()
    } finally { Pop-Location }
}
$script:installs = 0
$script:downloads = 0
$script:failInstall = $false
$script:badSdk = $false
$script:badSelectedSdk = $false
$script:omitCommand = $false
$receiveImplementation = ${function:Receive-CaravelArchive}
$originalPackages = $env:NUGET_PACKAGES
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
function MustFail([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -like "*$Message*") { return }; throw }
    throw "Expected failure: $Message"
}
function Invoke-CaravelDotnet([string[]]$Arguments) {
    switch ($Arguments[0]) {
        '--list-sdks' { if ($script:badSdk) { '9.0.100 [fixture]' } else { '10.0.402 [fixture]' } }
        '--version' { if ($script:badSelectedSdk) { '11.0.100-preview.1' } else { '10.0.402' } }
        'tool' {
            if ($Arguments[1] -eq 'install') {
                $script:installs++
                if ($script:failInstall) { throw 'simulated install failure' }
                $config = $Arguments[[array]::IndexOf($Arguments, '--configfile') + 1]
                $text = Get-Content $config -Raw
                Assert ($text.Contains('<clear/>') -and $text.Contains('packageSourceMapping')) 'Feed isolation absent'
                $toolPath = $Arguments[[array]::IndexOf($Arguments, '--tool-path') + 1]
                if (!$script:omitCommand) {
                    $commandName = if ($IsWindows) { 'caravel.exe' } else { 'caravel' }
                    'fixture bytes; never executed' | Set-Content -LiteralPath (Join-Path $toolPath $commandName)
                }
                'fixture install'
            } else { 'clinimatix.caravel.bosun 26.1.1-rc2 caravel' }
        }
        default { throw 'Unexpected dotnet invocation' }
    }
}
# Build a synthetic NuGet package/release archive and pin it in the fixture catalog.
$feed = New-Item -ItemType Directory (Join-Path $sandbox 'input')
$pkgdir = New-Item -ItemType Directory (Join-Path $sandbox 'package')
$commit = '1111111111111111111111111111111111111111'
@"
<package><metadata><id>Clinimatix.Caravel.Bosun</id><version>26.1.1-rc2</version><repository url="https://github.com/Clinimatix/Caravel" commit="$commit"/></metadata></package>
"@ | Set-Content (Join-Path $pkgdir 'Bosun.nuspec')
$package = Join-Path $feed 'Clinimatix.Caravel.Bosun.26.1.1-rc2.nupkg'
[IO.Compression.ZipFile]::CreateFromDirectory($pkgdir, $package)
$script:fixtureArchive = Join-Path $sandbox 'fixture.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($feed, $script:fixtureArchive)
$script:fixtureHash = (Get-FileHash $script:fixtureArchive).Hash
function Get-CaravelInstallerCatalog { @{ stable = $null; prerelease = '26.1.1-rc2'; releases = @{ '26.1.1-rc2' = @{ sha256 = $script:fixtureHash; commit = '1111111111111111111111111111111111111111' } } } }
function Receive-CaravelArchive([string]$Uri, [string]$Destination) {
    $script:downloads++
    Assert ($Uri -ceq 'https://github.com/Clinimatix/Caravel/releases/download/v26.1.1-rc2/Clinimatix.Caravel.26.1.1-rc2.packages.zip') 'Unpinned URL'
    Copy-Item -LiteralPath $script:fixtureArchive -Destination $Destination
}
try {
    $root = Join-Path $sandbox 'tools & spaces'
    MustFail { Install-CaravelBosun -InstallRoot $root } 'No reviewed stable'
    MustFail { Install-CaravelBosun -Version '../escape' -InstallRoot $root } 'reviewed installer catalog'
    $script:badSdk = $true
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $root } '.NET 10 SDK'
    $script:badSdk = $false
    $script:badSelectedSdk = $true
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $root } 'current directory must select'
    $script:badSelectedSdk = $false
    Install-CaravelBosun -Channel prerelease -InstallRoot $root
    Install-CaravelBosun -Version '26.1.1-rc2' -InstallRoot $root
    Assert ($script:installs -eq 1 -and $script:downloads -eq 1) 'Repeat invocation installed/downloaded again'
    $commandName = if ($IsWindows) { 'caravel.exe' } else { 'caravel' }
    $installedCommand = Join-Path $root "26.1.1-rc2/$commandName"
    Remove-Item -LiteralPath $installedCommand
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $root } 'Existing installation is incomplete'
    Assert ($script:installs -eq 1 -and $script:downloads -eq 1) 'Incomplete repeat mutated the installation'
    'fixture bytes; never executed' | Set-Content -LiteralPath $installedCommand
    Assert ($env:NUGET_PACKAGES -ceq $originalPackages) 'Process cache setting leaked'
    $unknown = Join-Path $sandbox 'unknown/26.1.1-rc2'
    New-Item -ItemType Directory $unknown -Force | Out-Null
    'preserve' | Set-Content (Join-Path $unknown 'unrelated')
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot (Split-Path $unknown) } 'no installer receipt'
    Assert (Test-Path (Join-Path $unknown 'unrelated')) 'Unknown content lost'
    $badRoot = Join-Path $sandbox 'failed'
    $script:failInstall = $true
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $badRoot } 'simulated install failure'
    Assert (!(Test-Path (Join-Path $badRoot '26.1.1-rc2'))) 'Partial install retained'
    $script:failInstall = $false
    Install-CaravelBosun -Channel prerelease -InstallRoot $badRoot
    $script:omitCommand = $true
    $missingCommandRoot = Join-Path $sandbox 'missing-command'
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $missingCommandRoot } 'identity or command did not match'
    Assert (!(Test-Path (Join-Path $missingCommandRoot '26.1.1-rc2'))) 'Missing-command install was retained'
    $script:omitCommand = $false
    $script:fixtureHash = '0' * 64
    MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot (Join-Path $sandbox 'hashfail') } 'checksum mismatch'
    $script:fixtureHash = (Get-FileHash $script:fixtureArchive).Hash
    $release = @{sha256 = $script:fixtureHash; commit = '2' * 40}
    MustFail { Export-CaravelBosun $script:fixtureArchive $feed '26.1.1-rc2' $release } 'provenance mismatch'
    Assert ($env:NUGET_PACKAGES -ceq $originalPackages) 'Failure cache setting leaked'
    # Exercise the real bounded transport retry implementation with a fake HTTP boundary.
    $script:attempts = 0
    function Invoke-WebRequest { $script:attempts++; throw 'transport fixture failure' }
    function Start-Sleep { }
    MustFail { & $receiveImplementation 'https://example.invalid/fixture' (Join-Path $sandbox 'unused') } 'after three attempts'
    Assert ($script:attempts -eq 3) 'Download retry budget was not three'
    if (!$IsWindows) {
        $linkTarget = New-Item -ItemType Directory (Join-Path $sandbox 'link-target')
        $linkPath = Join-Path $sandbox 'linked-root'
        New-Item -ItemType SymbolicLink -Path $linkPath -Target $linkTarget.FullName | Out-Null
        try { MustFail { Install-CaravelBosun -Channel prerelease -InstallRoot $linkPath } 'must not contain links' }
        finally { Remove-Item -LiteralPath $linkPath }
        Assert (!(Test-Path (Join-Path $linkTarget '26.1.1-rc2'))) 'Link target was modified'
    }
    # A valid ZIP/hash is insufficient when the package manifest is absent or malformed.
    foreach ($manifest in @('missing', 'malformed')) {
        $packageDir = New-Item -ItemType Directory (Join-Path $sandbox "$manifest-package")
        if ($manifest -eq 'malformed') { '<package>' | Set-Content (Join-Path $packageDir 'broken.nuspec') }
        else { 'fixture' | Set-Content (Join-Path $packageDir 'README.txt') }
        $archiveFeed = New-Item -ItemType Directory (Join-Path $sandbox "$manifest-feed")
        [IO.Compression.ZipFile]::CreateFromDirectory($packageDir, (Join-Path $archiveFeed 'Clinimatix.Caravel.Bosun.26.1.1-rc2.nupkg'))
        $archive = Join-Path $sandbox "$manifest.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($archiveFeed, $archive)
        $outputFeed = New-Item -ItemType Directory (Join-Path $sandbox "$manifest-output")
        $pin = @{sha256 = (Get-FileHash $archive).Hash; commit = $commit}
        $rejected = $false
        try { Export-CaravelBosun $archive $outputFeed '26.1.1-rc2' $pin } catch { $rejected = $true }
        Assert $rejected "Accepted $manifest package manifest"
    }
    Write-Output 'PASS: stable/pinned validation, SDK failure, verified package/provenance, isolated feeds, repeat, failure recovery, unknown-directory preservation, cache restoration.'
} finally {
    # Fixed task-created absolute root only.
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
