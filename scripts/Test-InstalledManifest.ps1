param(
    [Parameter(Mandatory)][string]$ToolPath,
    [Parameter(Mandatory)][string]$BosunPackagePath,
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedRepositoryCommit
)
$ErrorActionPreference = 'Stop'
$ToolPath = (Resolve-Path -LiteralPath $ToolPath).Path
$BosunPackagePath = (Resolve-Path -LiteralPath $BosunPackagePath).Path
$root = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('caravel manifest ' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$checks = [Collections.Generic.List[string]]::new()
$diagnostic = 'Cannot inspect project. Specify one readable .csproj containing valid XML (maximum 1 MiB; DTDs prohibited).'

function Invoke-ManifestProcess([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($ToolPath)
    $start.WorkingDirectory = $fixture
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Installed manifest command exceeded its deadline.'
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)) {
            throw 'Installed manifest output did not close after process exit.'
        }
        return @{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

function Get-FixtureInventory {
    return @(Get-ChildItem -LiteralPath $fixture -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($fixture, $_.FullName)
        if ($_.PSIsContainer) { "directory:$relative" }
        else { "file:$relative`:$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    })
}

function Assert-Failure([string]$Name, [string]$Project) {
    $before = Get-FixtureInventory
    $result = Invoke-ManifestProcess @('agent:manifest', '--project', $Project, '--json')
    if ($result.ExitCode -ne 1 -or $result.Output -ne '' -or $result.Error.TrimEnd() -cne $diagnostic) {
        throw "Installed manifest failure contract differs: $Name"
    }
    if (Compare-Object $before (Get-FixtureInventory)) { throw "Manifest changed its input fixture: $Name" }
    $checks.Add($Name)
}

try {
    $archive = [IO.Compression.ZipFile]::OpenRead($BosunPackagePath)
    try {
        $entries = @($archive.Entries | Where-Object FullName -like '*.nuspec')
        if ($entries.Count -ne 1) { throw 'Expected one Bosun nuspec.' }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        $version = [string]$metadata.version
        $commit = [string]$metadata.repository.commit
        if ([string]$metadata.id -cne 'Clinimatix.Caravel.Bosun' -or
            $version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
            $commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid Bosun package identity.' }
        if ($ExpectedRepositoryCommit -and $commit -ine $ExpectedRepositoryCommit) { throw 'Bosun source revision differs from expected candidate.' }
    } finally { $archive.Dispose() }
    $id = 'clinimatix.caravel.bosun'
    $installed = Join-Path (Split-Path $ToolPath) ".store/$id/$version/$id/$version/$id.$version.nupkg"
    $packageHash = (Get-FileHash -LiteralPath $BosunPackagePath -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -cne $packageHash) {
        throw 'Installed Bosun bytes differ from the selected package.'
    }
    $checks.Add('installed-package-bytes')
    $result = Invoke-ManifestProcess @('schema', '--json')
    if ($result.ExitCode -ne 0 -or $result.Error) { throw 'Installed command schema failed.' }
    $schema = $result.Output | ConvertFrom-Json
    if ($schema.version -cne $version -or 'agent:manifest' -notin $schema.commands.name) { throw 'Installed command schema identity differs.' }
    $checks.Add('installed-command-schema')

    $sentinel = 'SYNTHETIC_PRIVATE_' + [guid]::NewGuid().ToString('N')
    foreach ($namespace in @('', 'http://schemas.microsoft.com/developer/msbuild/2003')) {
        $name = if ($namespace) { 'legacy' } else { 'modern' }
        $directory = Join-Path $fixture $name
        $null = New-Item -ItemType Directory -Path $directory
        $project = Join-Path $directory "$sentinel.csproj"
        @"
<Project xmlns="$namespace" xmlns:other="urn:foreign" Sdk="Missing.$sentinel" InitialTargets="Trap">
  <PropertyGroup><Secret>$sentinel</Secret><OutputType>Exe</OutputType></PropertyGroup>
  <Import Project="missing-$sentinel.props" />
  <ItemGroup>
    <PackageReference Include="Clinimatix.Caravel.Core" Version="$sentinel" />
    <PackageReference Include="clinimatix.caravel.core" />
    <PackageReference Include="Unknown.$sentinel" />
    <ProjectReference Include="missing-$sentinel.csproj" />
    <other:PackageReference Include="Clinimatix.Caravel.Storage" />
  </ItemGroup>
  <Choose><When Condition="false"><ItemGroup><PackageReference Include="Clinimatix.Caravel.Queues" /></ItemGroup></When>
    <Otherwise><ItemGroup><PackageReference Include="Clinimatix.Caravel.Mail" /></ItemGroup></Otherwise></Choose>
  <Target Name="Trap" BeforeTargets="Build;Restore">
    <WriteLinesToFile File="executed-target" Lines="$sentinel" />
    <Error Text="$sentinel" />
    <ItemGroup><PackageReference Include="Clinimatix.Caravel.Auth" /></ItemGroup>
  </Target>
  <other:ItemGroup><PackageReference Include="Clinimatix.Caravel.Events" /></other:ItemGroup>
  <PropertyGroup><ItemGroup><PackageReference Include="Clinimatix.Caravel.Clarion" /></ItemGroup></PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $project
        "System.IO.File.WriteAllText(`"executed-startup`", `"$sentinel`"); throw new System.Exception(`"$sentinel`");" | Set-Content -LiteralPath (Join-Path $directory 'Program.cs')
        $sentinel | Set-Content -LiteralPath (Join-Path $directory '.env')
        "{`"secret`":`"$sentinel`"}" | Set-Content -LiteralPath (Join-Path $directory 'appsettings.json')
        $before = Get-FixtureInventory
        $previousOutput = $null
        foreach ($selection in @($project, $directory)) {
            $result = Invoke-ManifestProcess @('agent:manifest', '--project', $selection, '--json')
            if ($result.ExitCode -ne 0 -or $result.Error -or $result.Output.Contains($sentinel) -or
                $result.Output.Contains($fixture)) { throw 'Installed manifest disclosed fixture data or failed static inspection.' }
            $manifest = $result.Output | ConvertFrom-Json
            if ($manifest.toolVersion -cne $schema.version) { throw 'Manifest guidance version differs from installed command schema.' }
            foreach ($hint in $manifest.discovery) {
                $commands = @($schema.commands | Where-Object name -CEQ $hint.command)
                if ($commands.Count -ne 1 -or [string]::IsNullOrWhiteSpace($commands[0].effects)) {
                    throw 'Manifest guidance references an absent, ambiguous or undocumented installed command.'
                }
            }
            $doctor = $schema.commands | Where-Object name -CEQ 'doctor'
            $routes = $schema.commands | Where-Object name -CEQ 'route:list'
            if ($doctor.effects -notlike '*diagnostic processes*' -or
                $routes.effects -notlike '*executes application startup code*' -or
                '--project' -cnotin $routes.options.name -or '--json' -cnotin $routes.options.name) {
                throw 'Installed discovery guidance omits execution effects or route inspection options.'
            }
            if ($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'caravel.project-capabilities' -or
                $manifest.product -cne 'Clinimatix Caravel' -or $manifest.toolVersion -cne $version -or
                $manifest.inspection -cne 'static-project-xml' -or
                ($manifest.packages -join ',') -cne 'Clinimatix.Caravel.Core,Clinimatix.Caravel.Queues,Clinimatix.Caravel.Mail' -or
                ($manifest.discovery.command -join ',') -cne 'schema,doctor,route:list' -or
                ($manifest.discovery | Where-Object command -eq 'schema').executesApplication -ne $false -or
                ($manifest.discovery | Where-Object command -eq 'doctor').executesApplication -ne $false -or
                ($manifest.discovery | Where-Object command -eq 'route:list').executesApplication -ne $true -or
                @($manifest.limits).Count -lt 4) { throw 'Installed manifest static contract differs.' }
            if ($null -ne $previousOutput -and $result.Output -cne $previousOutput) { throw 'Project and directory manifest selection differ.' }
            if (Compare-Object $before (Get-FixtureInventory)) { throw 'Manifest evaluated, built or changed its fixture.' }
            $previousOutput = $result.Output
            $checks.Add("$name-$(if ($selection -eq $project) { 'file' } else { 'directory' })")
        }
        $checks.Add("$name-installed-guidance")
    }
    $errors = Join-Path $fixture 'errors'
    $null = New-Item -ItemType Directory -Path $errors
    foreach ($encodingCase in @(
        @{ Name = 'utf8'; Encoding = [Text.UTF8Encoding]::new($false); Step = 1 },
        @{ Name = 'utf8-bom'; Encoding = [Text.UTF8Encoding]::new($true); Step = 1 },
        @{ Name = 'utf16'; Encoding = [Text.UnicodeEncoding]::new($false, $true); Step = 2 })) {
        foreach ($excess in @(0, $encodingCase.Step)) {
            $name = "$($encodingCase.Name)-$(if ($excess) { 'over-byte-limit' } else { 'at-byte-limit' })"
            $path = Join-Path $errors "$name.csproj"
            $encoding = $encodingCase.Encoding
            $prefix = '<Project><ItemGroup><PackageReference Include="Clinimatix.Caravel.Core" /></ItemGroup><!--'
            $suffix = '--></Project>'
            $expectedBytes = 1MB + $excess
            $paddingBytes = $expectedBytes - $encoding.GetPreamble().Length - $encoding.GetByteCount($prefix + $suffix)
            $xml = $prefix + (' ' * ($paddingBytes / $encoding.GetByteCount(' '))) + $suffix
            [IO.File]::WriteAllText($path, $xml, $encoding)
            if ((Get-Item -LiteralPath $path).Length -ne $expectedBytes) { throw 'Byte-limit fixture length differs.' }
            # Validate independently so an invalid XML fixture cannot satisfy size rejection.
            if ([Xml.Linq.XDocument]::Load($path).Root.Name.LocalName -cne 'Project') { throw 'Byte-limit fixture is not project XML.' }
            if ($excess) {
                Assert-Failure $name $path
            } else {
                $before = Get-FixtureInventory
                $result = Invoke-ManifestProcess @('agent:manifest', '--project', $path, '--json')
                if ($result.ExitCode -ne 0 -or $result.Error -or $result.Output.Contains($fixture)) {
                    throw "Installed manifest rejected exact byte limit: $name"
                }
                $manifest = $result.Output | ConvertFrom-Json
                if ($manifest.schemaVersion -ne 1 -or $manifest.toolVersion -cne $version -or
                    ($manifest.packages -join ',') -cne 'Clinimatix.Caravel.Core') { throw "Byte-limit manifest differs: $name" }
                if (Compare-Object $before (Get-FixtureInventory)) { throw "Manifest changed its byte-limit fixture: $name" }
                $checks.Add($name)
            }
        }
    }
    foreach ($case in @(
        @{ Name = 'malformed'; Xml = "<Project><$sentinel></Project>" },
        @{ Name = 'dtd'; Xml = "<!DOCTYPE Project [<!ENTITY secret SYSTEM 'file:///$sentinel'>]><Project>&secret;</Project>" },
        @{ Name = 'wrong-root'; Xml = "<$sentinel />" },
        @{ Name = 'foreign-root'; Xml = "<Project xmlns='urn:$sentinel' />" },
        @{ Name = 'oversized'; Xml = ('x' * (1MB + 1)) })) {
        $path = Join-Path $errors ($case.Name + '.csproj')
        [IO.File]::WriteAllText($path, $case.Xml)
        Assert-Failure $case.Name $path
    }
    Assert-Failure 'missing' (Join-Path $errors "$sentinel.csproj")
    Assert-Failure 'ambiguous-directory' $errors
    $empty = Join-Path $fixture 'empty'
    $null = New-Item -ItemType Directory -Path $empty
    Assert-Failure 'empty-directory' $empty

    $evidence = Join-Path $root 'artifacts/fathom-manifest-evidence'
    $null = New-Item -ItemType Directory -Path $evidence -Force
    [ordered]@{
        SchemaVersion = 1; Status = 'passed'; CheckCount = $checks.Count; Checks = @($checks)
        PackageId = 'Clinimatix.Caravel.Bosun'; Version = $version; Sha256 = $packageHash
        DeclaredRepositoryCommit = $commit; ExpectedRepositoryCommit = $ExpectedRepositoryCommit
        WorkflowSha = $env:GITHUB_SHA; WorkflowRunId = $env:GITHUB_RUN_ID
        WorkflowRunAttempt = $env:GITHUB_RUN_ATTEMPT; WorkflowJob = $env:GITHUB_JOB; RunnerOS = $env:RUNNER_OS
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence ([guid]::NewGuid().ToString('N') + '.json'))
    Write-Output "Installed Fathom manifest qualification passed: $($checks.Count) checks."
} finally {
    # Only this helper's freshly created fixture is removed; receipts contain no project content.
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($fixture) -notmatch '^caravel manifest [a-f0-9]{32}$') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
