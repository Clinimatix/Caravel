$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkerContainerShutdown.ps1')
$cases = 0
function Reject([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw 'Expected rejection was missing.' }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw } }
    $script:cases++
}
$source = Get-Content (Join-Path $PSScriptRoot '../samples/Caravel.Worker/RecordQuantityHandler.cs') -Raw
$paused = Add-WorkerContainerPause $source
if ($paused.IndexOf('SaveChangesAsync') -gt $paused.IndexOf('CARAVEL_CONTAINER_PAUSE_AFTER_RESULT') -or
    $paused.IndexOf('CARAVEL_CONTAINER_PAUSE_AFTER_RESULT') -gt $paused.IndexOf('Environment.Exit(73)') -or
    $paused -notmatch 'CancellationToken.None') { throw 'Pause must follow durable save, precede acknowledgement loss, and retain cancellation evidence.' }
$cases++
Reject { Add-WorkerContainerPause 'no anchor' } 'exactly one'
Reject { Add-WorkerContainerPause ($source + $source) } 'exactly one'
$ready = Wait-WorkerContainerPause 'owned' @('job-a') {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'inspect') { return '{"Running":true}' }
    if ($Arguments[-1] -like '*handler-ready*') { return 'job-a' }
}
if ($ready -cne 'job-a') { throw 'Expected observed job identity.' }
$cases++
Reject { Wait-WorkerContainerPause 'owned' @('job-a') {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'inspect') { return '{"Running":false}' }
    return 'job-a'
} } 'exited before SIGTERM'
Reject { Wait-WorkerContainerPause 'owned' @('job-a') {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'inspect') { return '{"Running":true}' }
    return 'foreign-job'
} } 'unexpected job identity'
Reject { Wait-WorkerContainerPause 'owned' @('job-a') {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'inspect') { return '{"Running":true}' }
    return 'job-a'
} } 'cancelled before SIGTERM'
Reject { Wait-WorkerContainerPause 'owned' @('job-a') {
    param([string[]]$Arguments)
    if ($Arguments[0] -eq 'inspect') { return '{"Running":true}' }
} 0 } 'did not reach'
$stopped = [pscustomobject]@{ Running=$false; ExitCode=0; OOMKilled=$false }
Assert-WorkerContainerShutdown $stopped 'job-a' 'job-a'
$cases++
foreach ($state in @(
    [pscustomobject]@{ Running=$true; ExitCode=0; OOMKilled=$false },
    [pscustomobject]@{ Running=$false; ExitCode=137; OOMKilled=$false },
    [pscustomobject]@{ Running=$false; ExitCode=0; OOMKilled=$true }
)) { Reject { Assert-WorkerContainerShutdown $state 'job-a' 'job-a' } 'did not stop worker gracefully' }
Reject { Assert-WorkerContainerShutdown $stopped 'job-a' '' } 'cancellation was not observed'
Reject { Assert-WorkerContainerShutdown $stopped 'job-a' 'job-b' } 'cancellation was not observed'
Write-Output "Worker container shutdown cases passed: $cases"
