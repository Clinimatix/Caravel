param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Destination) {
    if (-not (Test-Path -LiteralPath $Destination -PathType Container) -or
        @(Get-ChildItem -LiteralPath $Destination -Force).Count) { throw 'Evidence destination must be an empty directory.' }
}
$root = Split-Path $PSScriptRoot
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Directory -Filter 'service packages *' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $_.FullName 'evidence') -File -ErrorAction SilentlyContinue } |
    Where-Object { $_.Name -in @('run-evidence.json', 'package-evidence.json', 'Core.trx', 'Clarion.trx', 'Queues.trx', 'Storage.trx', 'Scheduling.trx', 'Mail.trx', 'Notifications.trx') })
# Only allowlisted metadata and synthetic test results are exported, never caches,
# package payloads, configs, source, arbitrary logs or environment snapshots.
if ($files.Count -gt 100 -or @($files | Where-Object Length -gt 2MB).Count -or
    ($files | Measure-Object Length -Sum).Sum -gt 20MB) { throw 'Service-package evidence exceeds the bounded export size.' }
$null = New-Item -ItemType Directory -Path $Destination -Force
foreach ($file in $files) {
    $runName = $file.Directory.Parent.Name
    $target = Join-Path $Destination $runName
    $null = New-Item -ItemType Directory -Path $target -Force
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
Write-Output "Exported $($files.Count) allowlisted service-package evidence files."
