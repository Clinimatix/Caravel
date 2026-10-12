# Dependency maintenance

Review dependencies against the exact Caravel commit and release line you use. Compatible bug and security fixes stay on that line with a new immutable patch version; feature additions and platform changes follow the separate [release policy](RELEASE-POLICY.md). Keep every first-party package on the same version.

## Check the resolved graph

Use the SDK pinned in `global.json` and the committed package locks:

```powershell
dotnet restore Caravel.slnx --locked-mode --no-http-cache
dotnet list Caravel.slnx package --vulnerable --include-transitive --no-restore --format json
dotnet list Caravel.slnx package --outdated --include-transitive --no-restore --format json
```

Use isolated package and HTTP caches when a fresh dependency check is needed; do not clear someone else's shared cache. A locked restore must preserve the recorded graph. Compare central requested versions, project references, committed locks and resolved assets. Investigate a resolution below its requested minimum even if restore reports success.

A vulnerability feed's empty result is limited to the advisories it supplies. Read vendor advisories and release notes too: a security-relevant correction may not appear in that feed. Record unavailable feeds and failed checks explicitly. Review private repository alerts only through authorized access, and distinguish an outdated package from a confirmed vulnerability.

## Assess a correction

For each advisory or vendor fix, identify the affected package and versions, severity, fixed version and the options or application paths needed to trigger it. Inspect direct and transitive dependencies. Confirm whether Caravel or its generated applications reach the affected code; distinguish framework packages from separate documentation or website dependencies.

Prefer a supported, compatible fix for the affected line. Do not automatically migrate to a new dependency major, suppress a conflict or raise the family's minimum .NET version. Where an older stable family remains explicitly supported, assess a compatible backport on that family's baseline. Coordinate newly discovered vulnerability details through the [private reporting process](../SECURITY.md).

Review Dependabot changes as code changes. Check immutable Actions pins, supported runner/runtime requirements and meaningful build qualification. A neutral or missing analysis configuration does not qualify a package, workflow or release.

## Update and qualify

Change the reviewed dependency pins, then deliberately regenerate affected locks and inspect the diff. Keep first-party project-version references aligned with the candidate version and verify that unrelated resolved dependencies did not change. Restore in locked mode afterward. Tests should exercise the defect and the corrected behavior, using synthetic data and isolated resources.

Qualify source and installed-package behavior on the supported hosts and provider paths. Release approval needs a fresh, passing CI run on the exact final release commit, with the required portability and upgrade checks. Record failed, skipped and never-run checks honestly; a passing repeat alone does not resolve a reproduced correctness defect.

Promote only the retained, reviewed package artifacts and checksums from that qualified commit. Never move a published tag, replace package bytes or rebuild an existing version. Follow the established human review and publication gates, and verify exact-version installation after the feed has indexed the new release.
