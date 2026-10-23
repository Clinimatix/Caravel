# Install Bosun with PowerShell

The upcoming candidate includes `scripts/install.ps1`, a standalone PowerShell 7 installer
for the Bosun `caravel` command. It does not install framework libraries into an existing
application, provision a runtime, create a database or install frontend tools. Bosun creates
applications using its existing starter commands.

The planned public address is `https://caravel.clinimatix.com/install.ps1`. **Website
publication is pending; this address is not yet an available installation contract.**
For now, download the script from a reviewed source revision and inspect it locally.

## Choose and inspect

Require an existing stable .NET 10 SDK selected in the current directory and PowerShell 7.
Follow the [serviced runtime policy](DEPENDENCY-MAINTENANCE.md) for security updates. Run as
your ordinary user. The installer never elevates, changes execution policy, installs an SDK,
changes PATH or changes persistent NuGet configuration. If your policy blocks scripts,
follow your organization's approved signing/execution process; do not bypass it.

After website publication, download the script and compare its SHA-256 to the digest in the
reviewed website source/release record **before executing it**. A checksum downloaded from
the same endpoint alone provides no independent authenticity assurance.

```powershell
Invoke-WebRequest https://caravel.clinimatix.com/install.ps1 -OutFile ./caravel-install.ps1
Get-FileHash ./caravel-install.ps1 -Algorithm SHA256
Get-Content ./caravel-install.ps1
# After verifying the digest and inspecting the script:
./caravel-install.ps1 -Channel prerelease
```

The shorter execution line, once the local script is verified, is:

```powershell
./caravel-install.ps1 -Version 26.1.1-rc2
```

Avoid piping an unverified download into `Invoke-Expression`. The script's embedded catalog
pins immutable release ZIP SHA-256 values and source commits. It downloads only from the
Caravel GitHub releases path, checks the archive checksum, then validates Bosun's package
ID, exact version, repository URL and source commit. It never downloads another script.
NuGet resolves third-party dependencies from nuget.org using normal NuGet validation;
Caravel packages are mapped exclusively to the verified local feed with an isolated cache.

Once this exact script is published and its digest confirmed against reviewed source, the
download, verification and invocation can also be written as one line:

```powershell
$p = Join-Path ([IO.Path]::GetTempPath()) (([guid]::NewGuid().ToString('N')) + '.ps1'); Invoke-WebRequest https://caravel.clinimatix.com/install.ps1 -OutFile $p; if ((Get-FileHash $p -Algorithm SHA256).Hash -ne '8773178ca99336444542d6062f86d48cacf4267d981a2c15fd5cdc52bc5860ac') { throw 'Installer checksum mismatch' }; & $p -Channel prerelease
```

An endpoint update deliberately causes an older digest-pinned command to fail. Review the
new script/digest rather than removing the verification. Keep or remove the downloaded
file according to your normal review workflow.

## Channels and repeated calls

The default `-Channel stable` selects the catalog's reviewed stable release. No stable
release is cataloged yet, so it stops with guidance instead of silently installing an RC.
`-Channel prerelease` currently selects `26.1.1-rc2`. `-Version` overrides the channel and
must match an exact catalog entry. Unsupported versions fail before download. Channel
advancement requires a reviewed catalog update after immutable release qualification;
this change does not publish or invent RC3 packages.

The default destination is `$HOME/.caravel/tools/<version>`; `-InstallRoot <directory>`
selects another writable root. Invoke the printed full path, for example:

```powershell
& "$HOME/.caravel/tools/26.1.1-rc2/caravel" version
& "$HOME/.caravel/tools/26.1.1-rc2/caravel" new MyApp
```

Earlier version directories remain available for rollback. Repeating the same version
checks its receipt, command file and `dotnet tool list`, then returns without download or reinstall.
Unknown or inconsistent existing directories are preserved and rejected; inspect them or
choose a different root. A receipt is local state, not a tamper-proof attestation.

Downloads receive at most three attempts. Integrity and provenance failures stop before
installation. Tool installation runs once; failure removes only the newly created version
directory and temporary files, restores the process cache setting and propagates the error.
Retry deliberately after fixing the reported problem. An abrupt process termination may
leave an incomplete directory; inspect it and choose a fresh root rather than overwriting
unknown contents. User/project feeds, dependency locks and earlier installations are untouched.
