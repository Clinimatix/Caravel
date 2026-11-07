#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('stable', 'prerelease')][string]$Channel = 'stable',
    [string]$Version,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.caravel/tools')
)

# Dot-source to inspect/test functions without installing. The published script is standalone.
function Get-CaravelInstallerCatalog {
    @{
        stable = $null
        prerelease = '26.1.1-rc2'
        releases = @{
            '26.1.1-rc2' = @{
                sha256 = 'd695d8d3c179b7542a34d99015eaa354a05ac61f2661a5c6fb0b4fa15c6796bc'
                commit = 'fcaa760cef15a81925e389b8727b18a2dadd0271'
            }
        }
    }
}

function Invoke-CaravelDotnet([string[]]$Arguments) {
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $LASTEXITCODE). $output" }
    $output
}

function Receive-CaravelArchive([string]$Uri, [string]$Destination) {
    # Retry transport failures only. Hash/provenance errors never trigger installation retries.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $Destination -MaximumRedirection 5 -TimeoutSec 120 -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq 3) { throw "Release download failed after three attempts: $($_.Exception.Message)" }
            Start-Sleep -Seconds $attempt
        }
    }
}

function Export-CaravelBosun([string]$Archive, [string]$Feed, [string]$Version, [hashtable]$Release) {
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Release.sha256) { throw 'Release ZIP checksum mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $name = "Clinimatix.Caravel.Bosun.$Version.nupkg"
        $entries = @($zip.Entries | Where-Object FullName -CEQ $name)
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 50MB) { throw 'Expected exactly one bounded Bosun package.' }
        # Copy only the exact entry; never extract archive paths.
        $packagePath = Join-Path $Feed $name
        $inputStream = $entries[0].Open()
        $outputStream = [IO.File]::Create($packagePath)
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    } finally { $zip.Dispose() }
    $package = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $specs = @($package.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw 'Expected one package manifest.' }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { $xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlReader = [Xml.XmlReader]::Create([IO.StringReader]::new($xml), $settings)
        $document = [Xml.XmlDocument]::new()
        try { $document.Load($xmlReader) } finally { $xmlReader.Dispose() }
        $meta = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($meta.id -cne 'Clinimatix.Caravel.Bosun' -or $meta.version -cne $Version -or
            $meta.repository.url -cne 'https://github.com/Clinimatix/Caravel' -or
            $meta.repository.commit -cne $Release.commit) { throw 'Bosun package provenance mismatch.' }
    } finally { $package.Dispose() }
}

function Install-CaravelBosun {
    [CmdletBinding()]
    param([string]$Channel = 'stable', [string]$Version, [Parameter(Mandatory)][string]$InstallRoot)
    $ErrorActionPreference = 'Stop'
    $catalog = Get-CaravelInstallerCatalog
    if (!$Version) { $Version = $catalog[$Channel] }
    if (!$Version) { throw 'No reviewed stable release is available. Select -Channel prerelease explicitly, or pin a catalog version.' }
    if ($Version -cnotmatch '^[0-9]{2}\.[0-9]+\.[0-9]+(?:-(?:rc|m)[0-9]+)?$' -or !$catalog.releases.ContainsKey($Version)) {
        throw 'Version is not in this reviewed installer catalog. Obtain the installer for that release.'
    }
    $release = $catalog.releases[$Version]
    $sdk = @(Invoke-CaravelDotnet @('--list-sdks'))
    if (!($sdk | Where-Object { $_ -match '^10\.0\.\d+\s+\[' })) { throw 'Install a stable .NET 10 SDK separately, then retry. No prerequisites were installed.' }
    # Also respect global.json in the caller directory: do not silently select a different SDK.
    $selectedSdk = (Invoke-CaravelDotnet @('--version') | Out-String).Trim()
    if ($selectedSdk -notmatch '^10\.0\.\d+$') { throw 'The current directory must select a stable .NET 10 SDK; check global.json.' }
    $root = [IO.Path]::GetFullPath($InstallRoot)
    $target = Join-Path $root $Version
    $commandName = if ($IsWindows) { 'caravel.exe' } else { 'caravel' }
    $commandPath = Join-Path $target $commandName
    # Reject links in every existing ancestor before creating or cleaning task-owned content.
    for ($path = $target; $path; $path = [IO.Path]::GetDirectoryName($path)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation path must not contain links.' }
    }
    $receiptPath = Join-Path $target 'caravel-install.json'
    if (Test-Path -LiteralPath $target) {
        if (!(Test-Path -LiteralPath $receiptPath)) { throw 'Existing installation directory has no installer receipt; choose a new root.' }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($receipt.version -cne $Version -or $receipt.sha256 -ne $release.sha256 -or $receipt.commit -cne $release.commit) { throw 'Existing installation receipt does not match the selected release.' }
        $listed = Invoke-CaravelDotnet @('tool', 'list', '--tool-path', $target) | Out-String
        if (!(Test-Path -LiteralPath $commandPath -PathType Leaf) -or $listed -notmatch ('(?im)^clinimatix\.caravel\.bosun\s+' + [regex]::Escape($Version) + '\s+caravel\s*$')) { throw 'Existing installation is incomplete; inspect it or choose a new root.' }
        Write-Output "Already installed $Version. Run: & '$target/caravel'"
        return
    }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('caravel-install-' + [guid]::NewGuid().ToString('N'))
    $createdTarget = $false
    $oldPackages = $env:NUGET_PACKAGES
    try {
        $feed = Join-Path $temporary 'feed'
        New-Item -ItemType Directory -Path $feed -Force | Out-Null
        $archive = Join-Path $temporary 'release.zip'
        $uri = "https://github.com/Clinimatix/Caravel/releases/download/v$Version/Clinimatix.Caravel.$Version.packages.zip"
        Receive-CaravelArchive $uri $archive
        Export-CaravelBosun $archive $feed $Version $release
        # Isolate user/machine feeds and package caches. Only this verified Bosun comes from the local feed.
        $escapedFeed = [Security.SecurityElement]::Escape($feed)
        $config = Join-Path $temporary 'NuGet.Config'
        @"
<configuration><packageSources><clear/><add key="caravel" value="$escapedFeed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="caravel"><package pattern="Clinimatix.Caravel.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $config
        $env:NUGET_PACKAGES = Join-Path $temporary 'packages'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        # CreateDirectory would accept an existing directory: fail closed on concurrent creation.
        New-Item -ItemType Directory -Path $target -ErrorAction Stop | Out-Null
        $createdTarget = $true
        Invoke-CaravelDotnet @('tool', 'install', 'Clinimatix.Caravel.Bosun', '--version', $Version, '--tool-path', $target, '--configfile', $config, '--no-cache') | Write-Output
        $listed = Invoke-CaravelDotnet @('tool', 'list', '--tool-path', $target) | Out-String
        if (!(Test-Path -LiteralPath $commandPath -PathType Leaf) -or $listed -notmatch ('(?im)^clinimatix\.caravel\.bosun\s+' + [regex]::Escape($Version) + '\s+caravel\s*$')) { throw 'Installed tool identity or command did not match.' }
        @{ version = $Version; sha256 = $release.sha256; commit = $release.commit } | ConvertTo-Json | Set-Content -LiteralPath $receiptPath
        Write-Output "Installed Bosun $Version. Run: & '$target/caravel'"
    } catch {
        if ($createdTarget) { Remove-Item -LiteralPath $target -Recurse -Force }
        throw
    } finally {
        $env:NUGET_PACKAGES = $oldPackages
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
}

if ($MyInvocation.InvocationName -ne '.') { Install-CaravelBosun -Channel $Channel -Version $Version -InstallRoot $InstallRoot }
