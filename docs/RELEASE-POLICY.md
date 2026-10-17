# Versions and releases

Clinimatix Caravel moves from milestone releases to release candidates to stable releases. This page explains what each stage promises and how to install prerelease packages.

## Version numbers

Caravel uses calendar versions in the form **`YY.Release.Patch`**, with compact prerelease suffixes. Every first-party package shares the same version. The year is part of the package version, not just its release title.

| Stage | Example version | Git tag | What it means |
| --- | --- | --- | --- |
| Milestone | `26.1.0-m1`, `26.1.0-m2` | `v26.1.0-m1` | A usable, tested slice. APIs may still change, and changes come with migration notes. |
| Release candidate | `26.1.0-rc2`, `26.1.1-rc2` | `v26.1.1-rc2` | A defined candidate scope with settled APIs; compatible maintenance keeps its RC suffix. |
| Stable | `26.1.1` | `v26.1.1` | A compatibility commitment for the documented API within the 26.1 release family. |

The first release family begun in 2026 is **Caravel 26.1**. `Release` counts families, not months, and `Patch` is the last numeric component. Compatible bug and security fixes increment Patch within the current line. The next feature release family is `26.2.0`; the calendar implies no fixed release schedule. Use the full package version in install instructions, bug reports and tags.

For the published `26.1.0-rc2` candidate, the first compatible maintenance version is `26.1.1-rc2`, followed by `26.1.2-rc2` if another patch is needed. The RC suffix identifies the candidate scope and does not advance for maintenance alone. A new, deliberately approved candidate scope advances that suffix, carrying forward the numeric patch floor: after `26.1.1-rc2`, a later candidate can be `26.1.1-rc3`. New scope and any feature additions require their own review and qualification; they are not maintenance patches.

Stable removes the prerelease suffix at a numeric version no lower than the highest published patch in that candidate line. After `26.1.1-rc2`, stable can be `26.1.1`, with later compatible fixes at `26.1.2` and above. Do not publish `26.1.0-rc3` or `26.1.0` as its successor: NuGet sorts both below `26.1.1-rc2`. If RC2 reaches `26.1.2-rc2` first, the later candidate and stable floor become `26.1.2-rc3` and `26.1.2`. Check actual NuGet ordering before choosing every successor; examples select neither a release date nor future feature scope.

Compatibility is promised within each stable release family. Moving from `26.1` to `26.2` is a deliberate upgrade: breaking changes are allowed when justified and must have migration guidance. Patch releases preserve documented APIs and minimum platform requirements. This is calendar versioning with NuGet-compatible syntax, not a claim of Semantic Versioning compatibility rules. Review migration notes when moving between families; do not assume every `26.*` package is interchangeable.

A family takes the year of its first published milestone, RC or stable release and keeps it afterward. A fix for 26.2 published in January 2027 is still `26.2.1`; the first new family begun in 2027 is `27.1`. The calendar never forces a release or extends a family's support lifetime.

A few details worth knowing:

- **Milestone releases and architecture milestones are different things.** The roadmap's M0–M4 describe feature areas. The `-m1` in `26.1.0-m1` is just a release counter. The maturity stages are milestone, RC and stable; there is no separate 0.x stage.
- **Suffixes sort as text.** NuGet sorts `m10` before `m2`, so pin exact versions rather than relying on "latest prerelease".
- **Published versions never change.** A bad release is fixed with a new version and upgrade guidance, never by moving a tag or replacing packages.
- **Raising the minimum .NET version is a breaking change** and requires a new feature release family, never a patch. New feature releases follow the latest .NET LTS. Patches to older supported families keep that family's baseline; already published packages do not change.
- **Release stages are tags, not permanent Git branches.** Development happens on `main` with short-lived feature/fix branches. A maintenance branch is created only when an older supported family actually needs a fix.

## What each stage requires

**Milestone.** The supported APIs and known gaps are listed in the changelog. The build, tests and package checks pass on the supported platforms. Missing features are fine. Broken or unsafe features described as ready are not.

**Release candidate.** The APIs for that approved candidate scope have settled and been verified together in the framework's own sample applications, using the supported databases and hosts. Compatible patches preserve that scope; an approved scope advance needs a new candidate and complete qualification. Install, upgrade and recovery have been tested, known limitations are documented and critical issues are resolved. If a breaking change turns out to be necessary, the next build goes back to being a milestone.

**Stable.** The documented API is stable, the install and upgrade path is repeatable, the framework has been proven in real applications, and maintainers are ready to honor compatibility within the release family. Optional packages added later can carry their own maturity notes.

Every release requires a fresh, passing CI run on its exact commit.

## Using prerelease packages

The published RC2 version is **`26.1.0-rc2`**. The source maintenance candidate **`26.1.1-rc2`** is not yet published. [GitHub releases](https://github.com/Clinimatix/Caravel/releases) and [nuget.org](https://www.nuget.org/profiles/Clinimatix) are the canonical distribution locations. Use a configured feed containing the exact version you select. RC1's immutable packages omit the RC2 Identity starter and Azure adapter.

With a configured feed containing RC2, install libraries into an existing project or install Bosun to create an application:

```powershell
dotnet add package Clinimatix.Caravel.AspNetCore --version 26.1.0-rc2
dotnet tool install --global Clinimatix.Caravel.Bosun --version 26.1.0-rc2
caravel new MyApp
```

Enable **Include prerelease** when browsing packages in Visual Studio. Pin exact versions and use locked restore in automation. Follow [upgrading from RC1](UPGRADING.md) when updating an existing application. You can also build from source using the [getting started guide](GETTING-STARTED.md).

### Using a release ZIP

[GitHub releases](https://github.com/Clinimatix/Caravel/releases) retain the same package payloads for local-feed use:

1. Download the release's package ZIP and verify its SHA-256 against `SHA256SUMS.txt`. Extract the ZIP to a local folder, which becomes your package feed; keep the individual `.nupkg` files intact.
2. Add the folder as a source in your application's `NuGet.Config`, alongside nuget.org for Caravel's dependencies. Merge these sources into an existing configuration rather than replacing other feeds your app needs:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
       <add key="caravel" value="/absolute/path/to/feed-folder" />
     </packageSources>
   </configuration>
   ```

3. From that application's directory, install the selected version:

   ```powershell
   dotnet add package Clinimatix.Caravel.AspNetCore --version 26.1.0-rc2
   dotnet tool install Clinimatix.Caravel.Bosun --tool-path <tools-folder> --version 26.1.0-rc2
   ```

4. Commit your lock file, and upgrade deliberately by changing the selected version. Use locked restore in automation to enforce the recorded dependency graph.

Avoid floating version ranges for prereleases. Keep a known working version on hand so you can roll back.

## How releases are made

For each release, a maintainer:

1. Chooses the version and commit, then updates the changelog, known limitations and any migration notes.
2. Confirms that complete CI passed on that commit, including consumption of the Windows-built starter packages on Linux and macOS, and retains the tested package artifacts without rebuilding them, recording SHA-256 checksums.
3. Creates an annotated `v<version>` tag on that commit.
4. Drafts a GitHub release with the packages, checksums, tested platforms, upgrade notes and known issues.
5. Publishes it: as a prerelease for `-mN` and `-rcN` versions, and as a regular release otherwise.

After publication, check the README, getting-started guide, roadmap, changelog, release policy and versioned website against the published version and package feeds. Update completed milestones from future to past tense, verify exact-version installation commands, and keep planned capabilities distinct from implemented behavior. Correct installation/status prose without changing historical API contracts or package payloads.

Ordinary pushes never create tags or releases. Publishing to nuget.org is a separate, deliberate step.

### NuGet publication

The manual `publish-nuget.yml` workflow promotes an existing GitHub release's package ZIP to nuget.org. It does not rebuild packages or run on pushes or tags. Run it from `main` with the release version (without `v`) and the independently reviewed ZIP SHA-256. Confirm the release's exact-commit qualification before dispatching it.

Before the first publication, configure the GitHub `nuget` environment with a required maintainer reviewer, no administrator bypass, and a deployment branch rule allowing only `main`. Set its `NUGET_USER` variable to the individual NuGet username that creates the policy. Configure a [NuGet Trusted Publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) owned by the Clinimatix organization for repository `Clinimatix/Caravel`, workflow filename `publish-nuget.yml`, environment `nuget`, and package pattern `Clinimatix.Caravel.*`. Allow new packages and new versions; unlisting is unnecessary. The workflow exchanges GitHub identity for a short-lived key; no persistent NuGet API key is stored.

The workflow checks the ZIP checksum, complete package inventory, versions, source commit against the release tag, repository URL, license, README, descriptions, first-party dependency versions and mapped binary paths before authenticating to NuGet. Artifact review must still inspect package contents for secrets and inappropriate material; these automated checks do not replace that review. Package IDs are read from the workflow revision's source projects; publishing an older release with a different package inventory requires deliberate review and a workflow update.

Publication is not atomic across packages. A failed run may have published some packages. Inspect the feed before retrying with the same version and checksum. `--skip-duplicate` permits continuation but does not verify existing package contents. Never substitute new bytes for an already published version.

After NuGet finishes validation and indexing, verify organization ownership and package pages, then restore a clean sample and install Bosun from nuget.org using the exact version. Update installation documentation only after those checks succeed. Package icons and separate symbol packages are optional improvements for a subsequent release; never rebuild an existing release solely to add them.

## Support

Fixes go to the current prerelease or stable line. Older candidate scopes and milestones are not patched by default; upgrade to the current line and follow its migration notes. An older stable family receives backports only while maintainers explicitly identify it as supported. The year and platform baseline stay with that family; support is not extended automatically by a new calendar year or .NET release.

### Maintenance and backports

Assess each confirmed defect against the affected supported lines. A compatible correction preserves documented APIs, schemas and minimum platform requirements. Keep the current RC suffix for a prerelease maintenance patch, or increment the numeric patch of a stable family. Raising the minimum .NET version or making a necessary breaking change requires the separate feature/milestone process; do not disguise it as maintenance.

Every backport is a distinct reviewed change with regression coverage and complete qualification on its own exact release commit. Keep the supported family's LTS baseline and inspect the resolved dependency graph; an update to the latest dependency major is not automatically a compatible fix. Retain the qualified packages and checksums for each version independently. Never rebuild, replace or move a previously published version, including a release that needs correction.

See [dependency maintenance](DEPENDENCY-MAINTENANCE.md) for dependency review and [security reporting](../SECURITY.md) for private vulnerability coordination. Report ordinary bugs and propose changes through [GitHub issues](https://github.com/Clinimatix/Caravel/issues) and pull requests.
