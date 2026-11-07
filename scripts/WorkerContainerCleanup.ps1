function Complete-WorkerContainerEvidence($Record, [string]$Directory, [string]$Label, [scriptblock]$InvokeDocker) {
    $errors = @()
    foreach ($kind in 'container', 'volume', 'image') {
        try {
            $list = switch ($kind) {
                container { @('ps', '-aq', '--filter', "label=$Label") }
                volume { @('volume', 'ls', '-q', '--filter', "label=$Label") }
                image { @('image', 'ls', '-q', '--filter', "label=$Label") }
            }
            $owned = @(& $InvokeDocker $list)
            if ($owned.Count) {
                $remove = switch ($kind) {
                    container { @('rm', '-f') }
                    volume { @('volume', 'rm') }
                    image { @('image', 'rm') }
                }
                $null = & $InvokeDocker ($remove + $owned)
            }
        } catch { $errors += "${kind}: $($_.Exception.Message)" }
    }
    $Record.CleanupErrors = $errors
    if ($errors.Count) { $Record.Outcome = 'failed' }
    $Record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'result.json')
    return $errors
}
