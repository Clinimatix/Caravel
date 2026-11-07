function Get-WorkerContainerPackage([string]$Directory, [string]$ExpectedCommit, [string]$RuntimeImage) {
    if ($RuntimeImage -cnotmatch '^mcr\.microsoft\.com/dotnet/runtime@sha256:[a-f0-9]{64}$') {
        throw 'Select an official .NET runtime image by SHA256 digest.'
    }
    if ($ExpectedCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Expected source SHA must contain forty hexadecimal characters.' }
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.nupkg' |
        Where-Object Name -cmatch '^Clinimatix\.Caravel\.Queues\.[0-9]')
    if ($files.Count -ne 1) { throw 'Expected exactly one supplied Queues package.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($files[0].FullName)
    try {
        $entries = @($archive.Entries | Where-Object FullName -like '*.nuspec')
        if ($entries.Count -ne 1) { throw 'Expected one Queues nuspec.' }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.package.metadata
        $version = [string]$metadata.version
        if ([string]$metadata.id -cne 'Clinimatix.Caravel.Queues' -or
            $version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$' -or
            $files[0].Name -cne "Clinimatix.Caravel.Queues.$version.nupkg") { throw 'Queues package identity does not match filename.' }
        if ([string]$metadata.repository.commit -ine $ExpectedCommit) { throw 'Queues declared source does not match expected SHA.' }
        return [pscustomobject]@{ Package = 'Clinimatix.Caravel.Queues'; Version = $version
            RepositoryCommit = [string]$metadata.repository.commit; Path = $files[0].FullName
            SHA256 = (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash }
    } finally { $archive.Dispose() }
}
