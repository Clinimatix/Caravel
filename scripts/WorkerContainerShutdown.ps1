function Add-WorkerContainerPause([string]$Source) {
    $anchor = '// Explicit fault injection for this synthetic sample only: exit after the result commits, before queue acknowledgement.'
    if ([regex]::Matches($Source, [regex]::Escape($anchor)).Count -ne 1) { throw 'Expected exactly one worker fault-injection point.' }
    $pause = @'
        if (Environment.GetEnvironmentVariable("CARAVEL_CONTAINER_PAUSE_AFTER_RESULT") == "1")
        {
            await File.WriteAllTextAsync("/data/handler-ready.tmp", context.JobId.ToString(), cancellationToken);
            File.Move("/data/handler-ready.tmp", "/data/handler-ready");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await File.WriteAllTextAsync("/data/handler-cancelled", context.JobId.ToString(), CancellationToken.None);
                throw;
            }
        }

'@
    return $Source.Replace($anchor, $pause + $anchor)
}

function Wait-WorkerContainerPause([string]$Name, [string[]]$AcceptedJobIds, [scriptblock]$Docker, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $state = & $Docker @('inspect', '--format', '{{json .State}}', $Name) | ConvertFrom-Json
        if (-not $state.Running) { throw 'In-flight worker exited before SIGTERM.' }
        $ready = @(& $Docker @('exec', $Name, 'sh', '-c', 'if test -f /data/handler-ready; then cat /data/handler-ready; fi')) -join ''
        if ($ready) {
            if ($ready -notin $AcceptedJobIds) { throw 'In-flight handler marker has an unexpected job identity.' }
            $cancelled = @(& $Docker @('exec', $Name, 'sh', '-c', 'if test -f /data/handler-cancelled; then cat /data/handler-cancelled; fi')) -join ''
            if ($cancelled) { throw 'In-flight handler was cancelled before SIGTERM.' }
            return $ready
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'In-flight handler did not reach its committed-result pause.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
}

function Assert-WorkerContainerShutdown($State, [string]$ReadyJobId, [string]$CancelledJobId) {
    if ($State.Running -or $State.ExitCode -ne 0 -or $State.OOMKilled) { throw 'In-flight SIGTERM did not stop worker gracefully.' }
    if ([string]::IsNullOrWhiteSpace($ReadyJobId) -or $CancelledJobId -cne $ReadyJobId) { throw 'In-flight handler cancellation was not observed for the paused job.' }
}
