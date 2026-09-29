param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -eq 0) { throw 'No packages found to inspect.' }
$inspected = 0
foreach ($package in $packages) {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        foreach ($entry in $archive.Entries | Where-Object Name -Like 'Caravel.*.dll') {
            $source = $entry.Open()
            $buffer = [IO.MemoryStream]::new()
            try {
                $source.CopyTo($buffer)
                $buffer.Position = 0
                $pe = [System.Reflection.PortableExecutable.PEReader]::new($buffer)
                try {
                    foreach ($debug in $pe.ReadDebugDirectory()) {
                        if ($debug.Type -ne 'CodeView') { continue }
                        $path = $pe.ReadCodeViewDebugDirectoryData($debug).Path.Replace('\', '/')
                        if ($path -match '^(?:[A-Za-z]:/|/)' -and -not $path.StartsWith('/_/', [StringComparison]::Ordinal)) {
                            throw "Unmapped build path in $($package.Name): $($entry.FullName). Rebuild in Release with path mapping."
                        }
                        $inspected++
                    }
                } finally { $pe.Dispose() }
            } finally { $source.Dispose(); $buffer.Dispose() }
        }
    } finally { $archive.Dispose() }
}
if ($inspected -eq 0) { throw 'No framework debug metadata found to inspect.' }
Write-Output "Package path check passed for $inspected framework debug records."
