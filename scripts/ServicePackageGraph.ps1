function Assert-ServicePackageGraph($Assets, $PackageEvidence, [string]$Cache) {
    foreach ($library in $Assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'project') { throw 'Package tests must not resolve source projects.' }
        if ($library.Name -like 'Clinimatix.Caravel.*/*') {
            $identity = $library.Name.Split('/')
            $expected = @($PackageEvidence | Where-Object { $_.Package -ieq $identity[0] -and $_.Version -ceq $identity[1] })
            if ($expected.Count -ne 1) { throw "Unexpected Caravel package graph: $($library.Name)" }
            $id = $identity[0].ToLowerInvariant()
            $version = $identity[1]
            $installed = Join-Path $Cache "$id/$version/$id.$version.nupkg"
            if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $expected[0].SHA256) {
                throw "Installed package bytes differ: $($library.Name)"
            }
        }
    }
}
