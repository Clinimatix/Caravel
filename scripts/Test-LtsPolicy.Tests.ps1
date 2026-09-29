$ErrorActionPreference = 'Stop'
$file = Join-Path ([IO.Path]::GetTempPath()) ('caravel-lts-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    # A newer preview LTS or GA STS must not supersede the current GA LTS.
    '{"releases-index":[{"channel-version":"10.0","release-type":"lts","support-phase":"active"},{"channel-version":"11.0","release-type":"sts","support-phase":"active"},{"channel-version":"12.0","release-type":"lts","support-phase":"preview"}]}' | Set-Content -LiteralPath $file
    & "$PSScriptRoot/Test-LtsPolicy.ps1" -MetadataPath $file
    '{"releases-index":[{"channel-version":"12.0","release-type":"lts","support-phase":"active"}]}' | Set-Content -LiteralPath $file
    $rejected = $false
    try { & "$PSScriptRoot/Test-LtsPolicy.ps1" -MetadataPath $file } catch { $rejected = $_.Exception.Message -like 'Latest LTS is net12.0*' }
    if (-not $rejected) { throw 'Policy failed to reject an obsolete LTS target.' }
    Write-Output 'LTS policy self-check passed.'
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
