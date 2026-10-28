param(
    [Parameter(Mandatory)][string]$CandidatePackageDirectory,
    [Parameter(Mandatory)][string]$ExpectedRepositoryCommit,
    [Parameter(Mandatory)][string]$RuntimeImage
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkerContainerInputs.ps1')
. (Join-Path $PSScriptRoot 'ServicePackageGraph.ps1')
. (Join-Path $PSScriptRoot 'WorkerContainerCleanup.ps1')
. (Join-Path $PSScriptRoot 'WorkerContainerShutdown.ps1')
$package = Get-WorkerContainerPackage $CandidatePackageDirectory $ExpectedRepositoryCommit $RuntimeImage
if (-not $IsLinux) { throw 'Worker runtime-container qualification requires Linux.' }
if ((& docker info --format '{{.OSType}}') -ne 'linux' -or $LASTEXITCODE -ne 0) { throw 'A running Linux Docker engine is required.' }
$root = Split-Path $PSScriptRoot
$id = [guid]::NewGuid().ToString('N')
$label = "caravel.worker-qualification=$id"
$image = "caravel-worker-qualification:$id"
$volume = "caravel-worker-$id"
$run = Join-Path $root "artifacts/worker-container-$id"
$context = Join-Path $run 'context'
$sample = Join-Path $run 'WorkerApp'
$data = Join-Path $context 'data'
$evidence = Join-Path $root 'artifacts/worker-container-evidence'
if (Test-Path -LiteralPath $evidence) { throw 'Worker container evidence destination must not exist.' }
$null = New-Item -ItemType Directory -Path $sample, $data, $evidence
$priorCache = $env:NUGET_PACKAGES
$priorVersion = $env:CaravelPackageVersion
$priorDirectory = $env:CARAVEL_WORKER_DIRECTORY
$record = [ordered]@{ WorkflowSha = $env:GITHUB_SHA; WorkflowRunId = $env:GITHUB_RUN_ID
    WorkflowRunAttempt = $env:GITHUB_RUN_ATTEMPT; RuntimeImage = $RuntimeImage
    Package = $package; Outcome = 'failed'; Checks = @() }
function Invoke-ContainerDotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments[0])" }
}
function Invoke-ContainerDocker([string[]]$Arguments) {
    $output = @(& docker @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "docker failed: $($Arguments[0])" }
    return $output
}
function Run-Worker([string]$Command, [string[]]$Extra = @(), [int]$ExitCode = 0) {
    $name = "caravel-worker-$id-$([guid]::NewGuid().ToString('N'))"
    $options = @('run', '--name', $name, '--label', $label, '--network', 'none', '--read-only',
        '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--tmpfs', '/tmp',
        '--mount', "type=volume,src=$volume,dst=/data")
    # Detached execution makes every command bounded, including expected infrastructure failures.
    $null = Invoke-ContainerDocker ($options + @('-d') + $Extra + @($image, $Command))
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $state = (Invoke-ContainerDocker @('inspect', '--format', '{{json .State}}', $name) | ConvertFrom-Json)
        if (-not $state.Running) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Container command timed out: $Command" }
        Start-Sleep -Milliseconds 200
    } while ($true)
    $output = Invoke-ContainerDocker @('logs', $name)
    $output | Set-Content -LiteralPath (Join-Path $evidence "$Command-$name.log")
    if ($state.ExitCode -ne $ExitCode) { throw "Container $Command exited $($state.ExitCode), expected $ExitCode." }
    if ($ExitCode -eq 0) { return ($output | Select-Object -Last 1 | ConvertFrom-Json) }
}
$testFailure = $null
Push-Location $root
try {
    Get-ChildItem (Join-Path $root 'samples/Caravel.Worker') -File | Where-Object Extension -In '.cs', '.csproj' | Copy-Item -Destination $sample
    Copy-Item (Join-Path $root 'samples/Caravel.Worker/Database') -Destination $sample -Recurse
    # Instrument only the disposable sample copy, never the package or application source.
    $handler = Join-Path $sample 'RecordQuantityHandler.cs'
    Add-WorkerContainerPause (Get-Content -LiteralPath $handler -Raw) | Set-Content -LiteralPath $handler
    Copy-Item (Join-Path $root '.config') -Destination $run -Recurse
    $feedDirectory = Join-Path $run 'packages'
    $null = New-Item -ItemType Directory -Path $feedDirectory
    Copy-Item -LiteralPath $package.Path -Destination $feedDirectory
    $feed = Join-Path $run 'NuGet.Config'
    $escaped = [Security.SecurityElement]::Escape($feedDirectory)
    @"
<configuration><packageSources><clear/><add key="candidate" value="$escaped"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Clinimatix.Caravel.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $feed
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    $env:CaravelPackageVersion = $package.Version
    $env:CARAVEL_WORKER_DIRECTORY = $data
    $project = Join-Path $sample 'Caravel.Worker.csproj'
    Invoke-ContainerDotnet @('restore', $project, '--configfile', $feed)
    Assert-ServicePackageGraph (Get-Content (Join-Path $sample 'obj/project.assets.json') -Raw | ConvertFrom-Json) @($package) $env:NUGET_PACKAGES
    Invoke-ContainerDotnet @('restore', $project, '--configfile', $feed, '--locked-mode')
    Invoke-ContainerDotnet @('publish', $project, '-c', 'Release', '--no-restore', '-o', (Join-Path $context 'app'))
    Push-Location $run
    try {
        Invoke-ContainerDotnet @('tool', 'restore', '--configfile', $feed)
        foreach ($db in 'QueueDbContext', 'ResultContext') {
            Invoke-ContainerDotnet @('ef', 'database', 'update', '--context', $db, '--project', $project, '--startup-project', $project, '--configuration', 'Release', '--no-build')
        }
    } finally { Pop-Location }
    @"
FROM $RuntimeImage
WORKDIR /app
COPY app/ /app/
COPY --chown=app:app data/ /data/
USER app
ENV CARAVEL_WORKER_DIRECTORY=/data CARAVEL_WORKER_LEASE_SECONDS=2
ENTRYPOINT ["dotnet", "Caravel.Worker.dll"]
"@ | Set-Content -LiteralPath (Join-Path $context 'Dockerfile')
    $null = Invoke-ContainerDocker @('build', '--label', $label, '-t', $image, $context)
    $record.ImageId = (Invoke-ContainerDocker @('image', 'inspect', '--format', '{{.Id}}', $image) | Select-Object -Last 1)
    $user = Invoke-ContainerDocker @('image', 'inspect', '--format', '{{.Config.User}}', $image)
    if ($user -ne 'app') { throw 'Worker image must run as app.' }
    $record.RuntimeInventory = Invoke-ContainerDocker @('run', '--rm', '--network', 'none', '--entrypoint', 'dotnet', $image, '--list-runtimes')
    $null = Invoke-ContainerDocker @('volume', 'create', '--label', $label, $volume)
    $null = Run-Worker '--check-model'
    if ((Run-Worker '--queue-status').ready -ne 0) { throw 'New queue was not empty.' }
    $accepted = @(Run-Worker '--enqueue-demo')
    $repeated = @(Run-Worker '--enqueue-demo')
    if ($accepted.Count -ne 3 -or @($accepted | Where-Object AlreadyEnqueued).Count -ne 0 -or
        @($repeated | Where-Object AlreadyEnqueued).Count -ne 3 -or
        @(Compare-Object ($accepted.JobId | Sort-Object) ($repeated.JobId | Sort-Object)).Count -ne 0) { throw 'Container enqueue deduplication failed.' }
    Run-Worker '--work-once' @('-e', 'CARAVEL_WORKER_CRASH_AFTER_RESULT=1') 73
    if ((Run-Worker '--report').events -ne 1) { throw 'Crash did not persist exactly one result.' }
    Start-Sleep -Seconds 3
    foreach ($attempt in 1..3) { if (-not (Run-Worker '--work-once').claimed) { throw 'Recovery lost a job.' } }
    if ((Run-Worker '--work-once').claimed) { throw 'Queue did not drain.' }
    $report = Run-Worker '--report'
    if ($report.events -ne 3 -or $report.total -ne 6 -or (Run-Worker '--queue-status').completed -ne 3) { throw 'Container recovery lost or repeated results.' }
    $record.Checks += 'exact-package-graph', 'explicit-migrations', 'non-root', 'deduplication', 'crash-recovery', 'persistent-results'
    $name = "caravel-worker-$id-host"
    $null = Invoke-ContainerDocker @('run', '-d', '--name', $name, '--label', $label, '--network', 'none', '--read-only', '--tmpfs', '/tmp',
        '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--mount', "type=volume,src=$volume,dst=/data", $image, '--work')
    Start-Sleep -Seconds 2
    if ((Invoke-ContainerDocker @('inspect', '--format', '{{.State.Running}}', $name)) -ne 'true') { throw 'Idle hosted container exited.' }
    $null = Invoke-ContainerDocker @('stop', '--time', '10', $name)
    $state = Invoke-ContainerDocker @('inspect', '--format', '{{json .State}}', $name) | ConvertFrom-Json
    if ($state.ExitCode -ne 0 -or $state.OOMKilled) { throw 'SIGTERM did not stop worker gracefully.' }
    $record.Checks += 'idle-sigterm'
    Run-Worker '--work' @('-e', 'CARAVEL_WORKER_DIRECTORY=/tmp') 1
    $record.Checks += 'infrastructure-failure-exit'

    # A separate volume starts with the explicitly migrated, empty image data.
    $volume += '-inflight'
    $null = Invoke-ContainerDocker @('volume', 'create', '--label', $label, $volume)
    $accepted = @(Run-Worker '--enqueue-demo')
    if ($accepted.Count -ne 3 -or @($accepted | Where-Object AlreadyEnqueued).Count) { throw 'In-flight fixture needs three new jobs.' }
    $name = "caravel-worker-$id-inflight"
    $null = Invoke-ContainerDocker @('run', '-d', '--name', $name, '--label', $label, '--network', 'none', '--read-only', '--tmpfs', '/tmp',
        '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--mount', "type=volume,src=$volume,dst=/data",
        '-e', 'CARAVEL_CONTAINER_PAUSE_AFTER_RESULT=1', '-e', 'CARAVEL_WORKER_LEASE_SECONDS=30', $image, '--work')
    $ready = Wait-WorkerContainerPause $name $accepted.JobId { param([string[]]$Arguments) Invoke-ContainerDocker $Arguments }
    $record.InFlight = [ordered]@{ PausedJobId = $ready }
    $null = Invoke-ContainerDocker @('stop', '--signal', 'SIGTERM', '--time', '10', $name)
    $state = Invoke-ContainerDocker @('inspect', '--format', '{{json .State}}', $name) | ConvertFrom-Json
    Invoke-ContainerDocker @('logs', $name) | Set-Content -LiteralPath (Join-Path $evidence 'inflight-worker.log')
    # Read the marker from the stopped container's volume using the same non-root image.
    $cancelled = @(Invoke-ContainerDocker @('run', '--rm', '--label', $label, '--network', 'none', '--read-only', '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges', '--mount', "type=volume,src=$volume,dst=/data,readonly", '--entrypoint', 'cat',
        $image, '/data/handler-cancelled')) -join ''
    Assert-WorkerContainerShutdown $state $ready $cancelled
    $record.InFlight.CancelledJobId = $cancelled
    $record.InFlight.ExitCode = $state.ExitCode
    $record.InFlight.OOMKilled = $state.OOMKilled
    $partial = Run-Worker '--report'
    $status = Run-Worker '--queue-status'
    if ($partial.events -ne 1 -or $partial.total -notin 1..3 -or $status.ready -ne 2 -or $status.activeLeases -ne 1 -or
        $status.completed -ne 0 -or $status.deadLetter -ne 0 -or $status.delayed -ne 0 -or $status.expiredLeases -ne 0) {
        throw 'Shutdown acknowledged or failed the interrupted job instead of leaving its active lease.'
    }
    $record.InFlight.BeforeRecovery = $status
    $record.InFlight.PartialResult = $partial
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        $status = Run-Worker '--queue-status'
        if ($status.expiredLeases -eq 1) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Interrupted lease never became recoverable.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    $record.InFlight.ExpiredLease = $status
    foreach ($attempt in 1..3) { if (-not (Run-Worker '--work-once').claimed) { throw 'In-flight recovery lost a job.' } }
    if ((Run-Worker '--work-once').claimed) { throw 'In-flight recovery did not drain.' }
    $report = Run-Worker '--report'
    $status = Run-Worker '--queue-status'
    if ($report.events -ne 3 -or $report.total -ne 6 -or $status.completed -ne 3 -or $status.deadLetter -ne 0 -or
        ($status.ready + $status.delayed + $status.activeLeases + $status.expiredLeases) -ne 0) { throw 'In-flight recovery lost or repeated persisted results.' }
    $repeated = @(Run-Worker '--enqueue-demo')
    if (@($repeated | Where-Object AlreadyEnqueued).Count -ne 3 -or
        @(Compare-Object ($accepted.JobId | Sort-Object) ($repeated.JobId | Sort-Object)).Count -ne 0 -or
        (Run-Worker '--work-once').claimed) { throw 'Recovered jobs lost their deduplication identities.' }
    $record.InFlight.AfterRecovery = $status
    $record.InFlight.RecoveredResult = $report
    $record.Checks += 'inflight-sigterm', 'inflight-lease-recovery', 'inflight-result-deduplication'
    $record.Outcome = 'succeeded'
} catch {
    $testFailure = $_
    $record.Failure = $_.Exception.Message
    throw
} finally {
    $env:NUGET_PACKAGES = $priorCache
    $env:CaravelPackageVersion = $priorVersion
    $env:CARAVEL_WORKER_DIRECTORY = $priorDirectory
    Pop-Location
    # Try every exact-label cleanup stage and retain the receipt even when Docker fails.
    $cleanupErrors = @(Complete-WorkerContainerEvidence $record $evidence $label {
        param([string[]]$Arguments) Invoke-ContainerDocker $Arguments
    })
    if ($cleanupErrors.Count -and -not $testFailure) { throw 'Worker container cleanup failed; see result.json.' }
}
