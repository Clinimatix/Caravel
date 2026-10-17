# Security policy

**Please don't report security issues in public issues, discussions, or pull requests.**

## Reporting a vulnerability

On the [Clinimatix/Caravel](https://github.com/Clinimatix/Caravel) repository, open **Security → Report a vulnerability** if that option is available. If it isn't, contact a maintainer through an established private channel to arrange a report. Do not post vulnerability details publicly or guess a contact address.

Please include:

- the affected version or commit, your .NET SDK version, and your OS
- a minimal, made-up example that reproduces the problem
- what you expected to be protected, and what actually happened
- any workaround you know of

Send only what's needed to reproduce the issue. Please don't include real personal data, secrets, live connection strings or unredacted logs.

Keep the details private, including any fix that would reveal them, until a maintainer has coordinated disclosure with you.

## What to expect

Caravel is prerelease software, and milestone builds and release candidates are meant for evaluation. There's no guaranteed response time. Maintainers assess severity, exposure and affected supported lines. Security fixes ship with regression coverage in a new immutable patch version on the affected supported line, subject to the [release gates](docs/RELEASE-POLICY.md). Compatible corrections to RC2 retain its suffix, for example `26.1.0-rc2` to `26.1.1-rc2`; they do not require a new candidate scope.

Where an older stable family is explicitly supported, assess a compatible backport that preserves its documented API and minimum .NET baseline. Do not replace existing packages, move tags or raise a supported family's platform minimum to obtain a dependency fix. Earlier candidate scopes and milestones are not patched by default; upgrade to the current supported line. See [dependency maintenance](docs/DEPENDENCY-MAINTENANCE.md) for review of package advisories and vendor fixes.

## Supported versions

| Version | Supported |
| --- | --- |
| Current milestone or release-candidate line, including its maintenance patches | Yes |
| Earlier candidate scopes and milestones | No, unless maintainers explicitly announce support |
| Stable families explicitly listed as supported by maintainers | Yes, within their stated support period |
| Other versions | No |
