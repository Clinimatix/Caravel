# Versions and releases

Clinimatix Caravel moves from milestone releases to release candidates to stable releases. This page explains what each stage promises and how to install prerelease packages.

## Version numbers

Caravel uses calendar versions in the form **`YY.Release.Patch`**, with compact prerelease suffixes. Every first-party package shares the same version. The year is part of the package version, not just its release title.

| Stage | Example version | Git tag | What it means |
| --- | --- | --- | --- |
| Milestone | `26.1.0-m1`, `26.1.0-m2` | `v26.1.0-m1` | A usable, tested slice. APIs may still change, and changes come with migration notes. |
| Release candidate | `26.1.0-rc1` | `v26.1.0-rc1` | The scope and APIs are settled. Remaining work is bug fixes, upgrade testing and documentation. |
| Stable | `26.1.0` | `v26.1.0` | A compatibility commitment for the documented API within the 26.1 release family. |

The first release family begun in 2026 is **Caravel 26.1**. Its first stable package is `26.1.0`; compatible bug fixes become `26.1.1`, `26.1.2` and so on. The next feature release is `26.2.0`. The second component counts release families, not months, and implies no fixed release schedule. Use the full package version in install instructions, bug reports and tags.

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

**Release candidate.** The planned APIs have settled and been verified together in the framework's own sample applications, using the supported databases and hosts. Install, upgrade and recovery have been tested, known limitations are documented and critical issues are resolved. If a breaking change turns out to be necessary, the next build goes back to being a milestone.

**Stable.** The documented API is stable, the install and upgrade path is repeatable, the framework has been proven in real applications, and maintainers are ready to honor compatibility within the release family. Optional packages added later can carry their own maturity notes.

Every release requires a fresh, passing CI run on its exact commit.

## Using prerelease packages

The 26.1 candidate version is **`26.1.0-rc1`**, available from [nuget.org](https://www.nuget.org/profiles/Clinimatix). Install libraries into an existing project or install Bosun to create an application:

```powershell
dotnet add package Clinimatix.Caravel.AspNetCore --version 26.1.0-rc1
dotnet tool install --global Clinimatix.Caravel.Bosun --version 26.1.0-rc1
caravel new MyApp
```

Enable **Include prerelease** when browsing packages in Visual Studio. Pin exact versions and use locked restore in automation. You can also build from source using the [getting started guide](GETTING-STARTED.md).

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
   dotnet add package Clinimatix.Caravel.AspNetCore --version 26.1.0-rc1
   dotnet tool install Clinimatix.Caravel.Bosun --tool-path <tools-folder> --version 26.1.0-rc1
   ```

4. Commit your lock file, and upgrade deliberately by changing the selected version. Use locked restore in automation to enforce the recorded dependency graph.

Avoid floating version ranges for prereleases. Keep a known working version on hand so you can roll back.

## How releases are made

For each release, a maintainer:

1. Chooses the version and commit, then updates the changelog, known limitations and any migration notes.
2. Confirms that CI passed on that commit and builds the packages from it, recording SHA-256 checksums.
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

Fixes go to the current prerelease or stable line. Older milestones aren't patched. Upgrade to the latest release and follow its migration notes. Report bugs and propose changes through [GitHub issues](https://github.com/Clinimatix/Caravel/issues) and pull requests.
