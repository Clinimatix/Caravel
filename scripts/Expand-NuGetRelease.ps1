param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{2}\.[0-9]+\.[0-9]+(?:-(?:rc|m)[0-9]+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$Commit,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Sha256) {
    throw 'Release ZIP checksum mismatch.'
}
if (Test-Path -LiteralPath $Destination) { throw 'Destination must not already exist.' }
$projects = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../src') -Filter '*.csproj' -Recurse
$ids = @($projects | ForEach-Object {
    [xml]$project = Get-Content -LiteralPath $_.FullName -Raw
    $project.Project.PropertyGroup.PackageId | Where-Object { $_ }
})
if ($ids.Count -eq 0) { throw 'No package IDs found.' }
$expected = @($ids | ForEach-Object { "$_.$Version.nupkg" })
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    # Exact flat inventory also rejects traversal, extra files and duplicate entries.
    if ($zip.Entries.Count -ne $expected.Count -or
        (Compare-Object ($expected | Sort-Object) (@($zip.Entries.FullName) | Sort-Object) -CaseSensitive)) {
        throw 'Release ZIP package inventory mismatch.'
    }
    foreach ($entry in $zip.Entries) {
        $stream = $entry.Open()
        $buffer = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($buffer)
            $buffer.Position = 0
            $package = [IO.Compression.ZipArchive]::new($buffer, [IO.Compression.ZipArchiveMode]::Read)
            try {
                $specs = @($package.Entries | Where-Object FullName -Like '*.nuspec')
                if ($specs.Count -ne 1) { throw "Expected one manifest in $($entry.Name)." }
                $reader = [IO.StreamReader]::new($specs[0].Open())
                try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
                $meta = $spec.package.metadata
                if ($meta.version -cne $Version -or $entry.Name -cne "$($meta.id).$Version.nupkg" -or
                    $meta.repository.commit -ne $Commit -or
                    $meta.repository.url -cne 'https://github.com/Clinimatix/Caravel') {
                    throw "Package provenance mismatch: $($entry.Name)."
                }
                if ($meta.license.type -ne 'expression' -or $meta.license.InnerText -ne 'MIT' -or
                    [string]::IsNullOrWhiteSpace($meta.description) -or $meta.readme -ne 'README.md' -or
                    $null -eq $package.GetEntry('README.md')) {
                    throw "Package metadata missing: $($entry.Name)."
                }
                foreach ($dependency in $spec.SelectNodes('//*[local-name()="dependency"]')) {
                    if ($dependency.id -like 'Clinimatix.Caravel.*' -and
                        ($dependency.id -notin $ids -or $dependency.version -notin @($Version, "[$Version]"))) {
                        throw "First-party dependency mismatch: $($entry.Name)."
                    }
                }
            } finally { $package.Dispose() }
        } finally { $stream.Dispose(); $buffer.Dispose() }
    }
} finally { $zip.Dispose() }
# Extract only after every package has passed validation. Never rebuild release bytes.
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Archive).Path, $Destination)
Write-Output "Verified and extracted $($expected.Count) packages for $Version at $Commit."
