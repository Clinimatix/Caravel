$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/worker smoke ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$sample = Join-Path $run 'WorkerApp'
$databaseDirectory = Join-Path $run 'synthetic databases'
$null = New-Item -ItemType Directory -Path $packages, $sample, $databaseDirectory
$priorCache = $env:NUGET_PACKAGES
$priorVersion = $env:CaravelPackageVersion
$priorDirectory = $env:CARAVEL_WORKER_DIRECTORY
$priorLease = $env:CARAVEL_WORKER_LEASE_SECONDS
$priorCrash = $env:CARAVEL_WORKER_CRASH_AFTER_RESULT
$priorSignalReady = $env:CARAVEL_SMOKE_SIGTERM_READY
$priorSignalCancelled = $env:CARAVEL_SMOKE_SIGTERM_CANCELLED
$priorCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-Checked([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

function Invoke-SampleJson([string]$Command) {
    $output = @(& dotnet run --project $project --no-build -- $Command)
    if ($LASTEXITCODE -ne 0) { throw "Worker command $Command failed with exit code $LASTEXITCODE." }
    return ($output | Select-Object -Last 1 | ConvertFrom-Json)
}

Push-Location $root
try {
    $env:CARAVEL_SMOKE_SIGTERM_READY = $null
    $env:CARAVEL_SMOKE_SIGTERM_CANCELLED = $null
    Invoke-Checked @('restore', 'src/Caravel.Queues/Caravel.Queues.csproj', '--locked-mode')
    Invoke-Checked @('pack', 'src/Caravel.Queues/Caravel.Queues.csproj', '-c', 'Release', '--no-restore', '-o', $packages)
    $package = @(Get-ChildItem -LiteralPath $packages -Filter 'Clinimatix.Caravel.Queues.*.nupkg')
    if ($package.Count -ne 1) { throw 'Expected exactly one queue package.' }
    $version = $package[0].BaseName.Substring('Clinimatix.Caravel.Queues.'.Length)
    $escaped = [Security.SecurityElement]::Escape($packages)
    $feed = Join-Path $run 'NuGet.Config'
    "<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>" | Set-Content -LiteralPath $feed
    $env:NUGET_PACKAGES = Join-Path $run 'cache'
    $env:CaravelPackageVersion = $version
    $env:CARAVEL_WORKER_DIRECTORY = $databaseDirectory
    $env:CARAVEL_WORKER_LEASE_SECONDS = '5'
    $env:CARAVEL_WORKER_CRASH_AFTER_RESULT = $null

    Get-ChildItem (Join-Path $root 'samples/Caravel.Worker') -File | Where-Object Extension -In @('.cs', '.csproj') | Copy-Item -Destination $sample
    Copy-Item -LiteralPath (Join-Path $root 'samples/Caravel.Worker/Database') -Destination $sample -Recurse
    Copy-Item -LiteralPath (Join-Path $root '.config') -Destination $run -Recurse
    if ($IsLinux) {
        # Inject only into the disposable copy, at the existing after-result fault point.
        $handler = Join-Path $sample 'RecordQuantityHandler.cs'
        $source = Get-Content -LiteralPath $handler -Raw
        $anchor = '// Explicit fault injection for this synthetic sample only: exit after the result commits, before queue acknowledgement.'
        if ([regex]::Matches($source, [regex]::Escape($anchor)).Count -ne 1) { throw 'Expected exactly one worker fault-injection point.' }
        $pause = @'
        if (Environment.GetEnvironmentVariable("CARAVEL_SMOKE_SIGTERM_READY") is { Length: > 0 } ready)
        {
            var cancelled = Environment.GetEnvironmentVariable("CARAVEL_SMOKE_SIGTERM_CANCELLED")
                ?? throw new InvalidOperationException("Missing synthetic cancellation marker.");
            await File.WriteAllTextAsync(ready + ".tmp", context.JobId.ToString(), cancellationToken);
            File.Move(ready + ".tmp", ready);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(cancelled, context.JobId.ToString(), CancellationToken.None);
                throw;
            }
        }

'@
        $source.Replace($anchor, $pause + $anchor) | Set-Content -LiteralPath $handler
    }
    $project = Join-Path $sample 'Caravel.Worker.csproj'
    Push-Location $run
    try {
        Invoke-Checked @('tool', 'restore', '--configfile', $feed)
        Invoke-Checked @('restore', $project, '--configfile', $feed)
        Invoke-Checked @('build', $project, '--no-restore')
        foreach ($context in @('QueueDbContext', 'ResultContext')) {
            Invoke-Checked @('ef', 'database', 'update', '--context', $context, '--project', $project, '--startup-project', $project, '--no-build')
        }
        $model = Invoke-SampleJson '--check-model'
        if ($model.pendingModelChanges -or $model.pendingMigrations) { throw 'Worker migrations are incomplete.' }
        $emptyStatus = Invoke-SampleJson '--queue-status'
        if (($emptyStatus.ready + $emptyStatus.delayed + $emptyStatus.activeLeases + $emptyStatus.expiredLeases + $emptyStatus.deadLetter + $emptyStatus.completed) -ne 0) { throw 'Empty migrated queue did not report zero jobs.' }
        $initial = @(Invoke-SampleJson '--enqueue-demo')
        if ($initial.Count -ne 3 -or @($initial | Where-Object AlreadyEnqueued).Count -ne 0) { throw 'Initial enqueue failed.' }
        if ((Invoke-SampleJson '--queue-status').ready -ne 3) { throw 'Persisted pending jobs were missing from queue status.' }
        $repeated = @(Invoke-SampleJson '--enqueue-demo')
        if ($repeated.Count -ne 3 -or @($repeated | Where-Object AlreadyEnqueued).Count -ne 3) { throw 'Repeated enqueue was not deduplicated.' }
        if (@(Compare-Object ($initial.JobId | Sort-Object) ($repeated.JobId | Sort-Object)).Count -ne 0) { throw 'Repeated enqueue returned different job IDs.' }

        # Only the child synthetic sample exits: its result is committed while its queue lease remains unacknowledged.
        $env:CARAVEL_WORKER_CRASH_AFTER_RESULT = '1'
        & dotnet run --project $project --no-build -- --work-once
        $crashExitCode = $LASTEXITCODE
        $env:CARAVEL_WORKER_CRASH_AFTER_RESULT = $null
        if ($crashExitCode -ne 73) { throw "Expected the synthetic acknowledgement-loss exit73, received $crashExitCode." }
        $partial = Invoke-SampleJson '--report'
        if ($partial.events -ne 1 -or $partial.total -lt 1 -or $partial.total -gt 3) { throw 'The interrupted worker did not persist exactly one synthetic result.' }
        Start-Sleep -Seconds 6

        # Each call starts a new process, including recovery of the expired lease from the interrupted attempt.
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            $work = Invoke-SampleJson '--work-once'
            if (-not $work.claimed) { throw 'A persisted synthetic job was lost before the drain completed.' }
        }
        if ((Invoke-SampleJson '--work-once').claimed) { throw 'Queue did not drain after three distinct jobs.' }
        $report = Invoke-SampleJson '--report'
        if ($report.events -ne 3 -or $report.total -ne 6) { throw 'Recovery double-counted or lost synthetic results.' }
        $afterCompletion = @(Invoke-SampleJson '--enqueue-demo')
        if (@($afterCompletion | Where-Object AlreadyEnqueued).Count -ne 3) { throw 'Completed jobs lost their deduplication keys.' }
        if ((Invoke-SampleJson '--work-once').claimed) { throw 'Duplicate acceptance repeated completed work.' }
        $report = Invoke-SampleJson '--report'
        if ($report.events -ne 3 -or $report.total -ne 6) { throw 'Repeated acceptance changed the aggregate.' }
        $null = Invoke-SampleJson '--check-model'

        # A second, fresh database pair proves the continuous native host consumes new submissions.
        $hostedDirectory = Join-Path $run 'hosted synthetic databases'
        $null = New-Item -ItemType Directory -Path $hostedDirectory
        $env:CARAVEL_WORKER_DIRECTORY = $hostedDirectory
        foreach ($context in @('QueueDbContext', 'ResultContext')) {
            Invoke-Checked @('ef', 'database', 'update', '--context', $context, '--project', $project, '--startup-project', $project, '--no-build')
        }
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add((Join-Path $sample 'bin/Debug/net10.0/Caravel.Worker.dll'))
        $start.ArgumentList.Add('--work')
        $ownedWorker = [Diagnostics.Process]::Start($start)
        $workerOutput = $ownedWorker.StandardOutput.ReadToEndAsync()
        $workerError = $ownedWorker.StandardError.ReadToEndAsync()
        try {
            $empty = Invoke-SampleJson '--report'
            if ($empty.events -ne 0 -or $empty.total -ne 0) { throw 'Continuous worker fixture was not empty.' }
            if ($ownedWorker.HasExited) { throw 'Continuous worker exited while idle.' }
            $hostedAccepted = @(Invoke-SampleJson '--enqueue-demo')
            if ($hostedAccepted.Count -ne 3 -or @($hostedAccepted | Where-Object AlreadyEnqueued).Count -ne 0) { throw 'Continuous worker fixture did not accept three new jobs.' }
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            do {
                if ($ownedWorker.HasExited) { throw 'Continuous worker exited before the report completed.' }
                $hostedReport = Invoke-SampleJson '--report'
                if ($hostedReport.events -eq 3 -and $hostedReport.total -eq 6 -and
                    (Invoke-SampleJson '--queue-status').completed -eq 3) { break }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($hostedReport.events -ne 3 -or $hostedReport.total -ne 6) { throw 'Continuous worker did not produce the expected aggregate.' }
            $hostedRepeated = @(Invoke-SampleJson '--enqueue-demo')
            if (@($hostedRepeated | Where-Object AlreadyEnqueued).Count -ne 3) { throw 'Continuous worker lost duplicate acceptance.' }
            if ($ownedWorker.HasExited) { throw 'Continuous worker did not remain running after processing.' }
        } finally {
            # Terminate only the process created above. Cooperative host shutdown is checked by HostedWorkerTests.
            if (-not $ownedWorker.HasExited) { $ownedWorker.Kill($true) }
            $ownedWorker.WaitForExit()
            $workerOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'hosted-worker.stdout.log')
            $workerError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'hosted-worker.stderr.log')
            $ownedWorker.Dispose()
        }
        $hostedReport = Invoke-SampleJson '--report'
        if ($hostedReport.events -ne 3 -or $hostedReport.total -ne 6) { throw 'Stopping the continuous worker changed its persisted report.' }
        if ((Invoke-SampleJson '--queue-status').completed -ne 3) { throw 'Queue status lost completed jobs after worker shutdown.' }
        $null = Invoke-SampleJson '--check-model'

        if ($IsLinux) {
            $signalDirectory = Join-Path $run 'sigterm synthetic databases'
            $null = New-Item -ItemType Directory -Path $signalDirectory
            $env:CARAVEL_WORKER_DIRECTORY = $signalDirectory
            foreach ($context in @('QueueDbContext', 'ResultContext')) {
                Invoke-Checked @('ef', 'database', 'update', '--context', $context, '--project', $project, '--startup-project', $project, '--no-build')
            }
            $signalAccepted = @(Invoke-SampleJson '--enqueue-demo')
            if ($signalAccepted.Count -ne 3 -or @($signalAccepted | Where-Object AlreadyEnqueued).Count -ne 0) { throw 'SIGTERM fixture must contain three new jobs.' }
            $readyMarker = Join-Path $signalDirectory 'handler-ready'
            $cancelMarker = Join-Path $signalDirectory 'handler-cancelled'
            foreach ($phase in @('signal', 'recovery')) {
                $signalStart = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
                $signalStart.UseShellExecute = $false
                $signalStart.RedirectStandardOutput = $true
                $signalStart.RedirectStandardError = $true
                $signalStart.ArgumentList.Add((Join-Path $sample 'bin/Debug/net10.0/Caravel.Worker.dll'))
                $signalStart.ArgumentList.Add('--work')
                $null = $signalStart.Environment.Remove('CARAVEL_SMOKE_SIGTERM_READY')
                $null = $signalStart.Environment.Remove('CARAVEL_SMOKE_SIGTERM_CANCELLED')
                if ($phase -eq 'signal') {
                    $signalStart.Environment['CARAVEL_SMOKE_SIGTERM_READY'] = $readyMarker
                    $signalStart.Environment['CARAVEL_SMOKE_SIGTERM_CANCELLED'] = $cancelMarker
                }
                $signalWorker = [Diagnostics.Process]::Start($signalStart)
                $signalOutput = $signalWorker.StandardOutput.ReadToEndAsync()
                $signalError = $signalWorker.StandardError.ReadToEndAsync()
                try {
                    $deadline = [DateTime]::UtcNow.AddSeconds(30)
                    if ($phase -eq 'signal') {
                        while (-not (Test-Path -LiteralPath $readyMarker)) {
                            if ($signalWorker.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'SIGTERM handler did not reach its committed-result pause.' }
                            Start-Sleep -Milliseconds 20
                        }
                        $pausedJob = (Get-Content -LiteralPath $readyMarker -Raw).Trim()
                        if ($pausedJob -notin $signalAccepted.JobId -or (Test-Path -LiteralPath $cancelMarker)) { throw 'SIGTERM handler identity was wrong or cancellation occurred before the signal.' }
                    } else {
                        do {
                            if ($signalWorker.HasExited) { throw 'Recovery worker stopped before acknowledgement.' }
                            $recoveredStatus = Invoke-SampleJson '--queue-status'
                            if ($recoveredStatus.completed -eq 3) { break }
                            Start-Sleep -Milliseconds 100
                        } while ([DateTime]::UtcNow -lt $deadline)
                        if ($recoveredStatus.completed -ne 3) { throw 'SIGTERM recovery did not complete all three jobs within 30 seconds.' }
                    }
                    if ($signalWorker.HasExited) { throw 'Owned worker exited before SIGTERM.' }
                    & /bin/kill -TERM $signalWorker.Id
                    if ($LASTEXITCODE -ne 0) { throw 'Could not send SIGTERM to the owned worker.' }
                    if (-not $signalWorker.WaitForExit(30000)) { throw 'Worker did not stop within 30 seconds after SIGTERM.' }
                    if ($signalWorker.ExitCode -ne 0) { throw "Native SIGTERM shutdown returned $($signalWorker.ExitCode), expected 0." }
                } finally {
                    try {
                        if (-not $signalWorker.HasExited) { $signalWorker.Kill($true) }
                        if (-not $signalWorker.WaitForExit(30000)) { throw 'Could not confirm owned SIGTERM worker cleanup.' }
                        $signalOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run "$phase-worker.stdout.log")
                        $signalError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run "$phase-worker.stderr.log")
                    } finally { $signalWorker.Dispose() }
                }
                if ($phase -eq 'signal') {
                    if (-not (Test-Path -LiteralPath $cancelMarker) -or (Get-Content -LiteralPath $cancelMarker -Raw).Trim() -cne $pausedJob) { throw 'SIGTERM did not cancel the paused handler.' }
                    $signalReport = Invoke-SampleJson '--report'
                    $signalStatus = Invoke-SampleJson '--queue-status'
                    if ($signalReport.events -ne 1 -or $signalReport.total -lt 1 -or $signalReport.total -gt 3 -or
                        ($signalStatus.activeLeases + $signalStatus.expiredLeases) -ne 1 -or $signalStatus.ready -ne 2 -or
                        $signalStatus.completed -ne 0 -or $signalStatus.deadLetter -ne 0 -or $signalStatus.delayed -ne 0) { throw 'SIGTERM acknowledged, lost or repeated the interrupted job.' }
                    Start-Sleep -Seconds 6 # The fixture lease is five seconds; recovery must reclaim the original job.
                }
            }
            $signalReport = Invoke-SampleJson '--report'
            if ($signalReport.events -ne 3 -or $signalReport.total -ne 6 -or (Invoke-SampleJson '--queue-status').completed -ne 3) { throw 'SIGTERM recovery changed the expected three events / total six.' }
            Write-Output 'Linux SIGTERM passed: native cancellation, exit0, unacknowledged lease and idempotent restart recovery.'
        } else { Write-Output 'Linux SIGTERM check skipped on this operating system.' }

        # Seed every status category in a separate migrated fixture; no product mutation commands are added.
        $statusDirectory = Join-Path $run 'status synthetic database'
        $fixtureDirectory = Join-Path $run 'StatusFixture'
        $null = New-Item -ItemType Directory -Path $statusDirectory, $fixtureDirectory
        $env:CARAVEL_WORKER_DIRECTORY = $statusDirectory
        Invoke-Checked @('ef', 'database', 'update', '--context', 'QueueDbContext', '--project', $project, '--startup-project', $project, '--no-build')
        $fixtureProject = Join-Path $fixtureDirectory 'StatusFixture.csproj'
        $escapedProject = [Security.SecurityElement]::Escape($project)
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=`"$escapedProject`" /></ItemGroup></Project>" | Set-Content -LiteralPath $fixtureProject
        @'
using Caravel.Queues;
using Caravel.Worker;
using Microsoft.EntityFrameworkCore;

var settings = SampleConfiguration.Load();
var options = new DbContextOptionsBuilder<QueueDbContext>();
settings.ConfigureQueue(options);
await using var database = new QueueDbContext(options.Options);
if (await database.Jobs.AnyAsync()) throw new InvalidOperationException("Only a new empty synthetic fixture may be seeded.");
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var day = (long)TimeSpan.FromDays(1).TotalMilliseconds;
var rows = new[]
{
    Row(QueueJobState.Pending, now - day),
    Row(QueueJobState.Pending, now + day),
    Row(QueueJobState.Leased, now - day, now + day),
    Row(QueueJobState.Leased, now - day, now - day),
    Row(QueueJobState.DeadLetter, now - day),
    Row(QueueJobState.Completed, now - day),
    Row(QueueJobState.Pending, now - day),
    Row(QueueJobState.Pending, now - day)
};
rows[6].TenantId = "other-tenant";
rows[7].Queue = "other-queue";
database.Jobs.AddRange(rows);
await database.SaveChangesAsync();

QueueJob Row(QueueJobState state, long available, long? expiry = null) => new()
{
    Id = Guid.NewGuid(), Queue = SampleConfiguration.QueueName, TenantId = SampleConfiguration.TenantId,
    IdempotencyKey = "synthetic-private-key", DeduplicationKey = Guid.NewGuid().ToString("N"),
    Payload = "synthetic-private-payload", JobType = "synthetic.quantity.v1", State = state,
    CreatedAt = now - day, AvailableAt = available, MaxAttempts = 3,
    Attempts = state == QueueJobState.Pending ? 0 : 1,
    LeaseToken = expiry is null ? null : Guid.NewGuid(), LeaseExpiresAt = expiry
};
'@ | Set-Content -LiteralPath (Join-Path $fixtureDirectory 'Program.cs')
        Invoke-Checked @('restore', $fixtureProject, '--configfile', $feed)
        Invoke-Checked @('run', '--project', $fixtureProject, '--no-restore')
        $statusDatabase = Join-Path $statusDirectory 'queue.db'
        $beforeStatus = (Get-FileHash -LiteralPath $statusDatabase -Algorithm SHA256).Hash
        foreach ($read in 1..2) {
            $snapshot = Invoke-SampleJson '--queue-status'
            foreach ($category in @('ready', 'delayed', 'activeLeases', 'expiredLeases', 'deadLetter', 'completed')) {
                if ($snapshot.$category -ne 1) { throw "Queue status category $category did not respect persisted state and ownership." }
            }
            if (-not $snapshot.observedAt) { throw 'Queue snapshot omitted its observation time.' }
            $json = $snapshot | ConvertTo-Json -Compress
            if ($json -match 'synthetic-private|Idempotency|Payload|LeaseToken|other-tenant|other-queue') { throw 'Queue snapshot disclosed private row data.' }
        }
        if ((Get-FileHash -LiteralPath $statusDatabase -Algorithm SHA256).Hash -ne $beforeStatus) { throw 'Read-only queue status changed the database.' }
        if (Test-Path -LiteralPath (Join-Path $statusDirectory 'results.db')) { throw 'Queue status created a result database.' }

        # A missing schema is an infrastructure failure, not an empty queue or a successful worker exit.
        $failureDirectory = Join-Path $run 'unmigrated synthetic database'
        $null = New-Item -ItemType Directory -Path $failureDirectory
        $env:CARAVEL_WORKER_DIRECTORY = $failureDirectory
        & dotnet run --project $project --no-build -- --queue-status *> (Join-Path $run 'missing-status.log')
        if ($LASTEXITCODE -eq 0) { throw 'Missing database was reported as a successful empty queue.' }
        if (Test-Path -LiteralPath (Join-Path $failureDirectory 'queue.db')) { throw 'Read-only queue status created a missing database.' }
        $failureStart = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
        $failureStart.UseShellExecute = $false
        $failureStart.CreateNoWindow = $true
        $failureStart.RedirectStandardOutput = $true
        $failureStart.RedirectStandardError = $true
        $failureStart.ArgumentList.Add((Join-Path $sample 'bin/Debug/net10.0/Caravel.Worker.dll'))
        $failureStart.ArgumentList.Add('--work')
        $failedWorker = [Diagnostics.Process]::Start($failureStart)
        $failureOutput = $failedWorker.StandardOutput.ReadToEndAsync()
        $failureError = $failedWorker.StandardError.ReadToEndAsync()
        try {
            if (-not $failedWorker.WaitForExit(30000)) { throw 'Worker did not stop after an infrastructure failure.' }
            if ($failedWorker.ExitCode -ne 1) { throw "Worker infrastructure failure returned $($failedWorker.ExitCode), expected 1." }
            if (Test-Path -LiteralPath (Join-Path $failureDirectory 'results.db')) { throw 'Unmigrated worker unexpectedly reached the result database.' }
        } finally {
            if (-not $failedWorker.HasExited) { $failedWorker.Kill($true) }
            $failedWorker.WaitForExit()
            $failureOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'failed-worker.stdout.log')
            $failureError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $run 'failed-worker.stderr.log')
            $failedWorker.Dispose()
        }
        & dotnet run --project $project --no-build -- --queue-status *> (Join-Path $run 'unmigrated-status.log')
        if ($LASTEXITCODE -eq 0) { throw 'Missing queue schema was reported as a successful empty queue.' }
        # The last native failure was expected and asserted; expose successful smoke completion to callers.
        $global:LASTEXITCODE = 0
        Write-Output "Worker smoke passed: installed queue package, explicit native migrations, duplicate acceptance, interruption after result commit, lease recovery, continuous native host, read-only scoped queue status, nonzero infrastructure-failure exit and idempotent aggregation (3 events, total6 in each processed fixture). Synthetic artifacts: $run"
    } finally { Pop-Location }
} finally {
    $env:NUGET_PACKAGES = $priorCache
    $env:CaravelPackageVersion = $priorVersion
    $env:CARAVEL_WORKER_DIRECTORY = $priorDirectory
    $env:CARAVEL_WORKER_LEASE_SECONDS = $priorLease
    $env:CARAVEL_WORKER_CRASH_AFTER_RESULT = $priorCrash
    $env:CARAVEL_SMOKE_SIGTERM_READY = $priorSignalReady
    $env:CARAVEL_SMOKE_SIGTERM_CANCELLED = $priorSignalCancelled
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $priorCertificate
    Pop-Location
}
