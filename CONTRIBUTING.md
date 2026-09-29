# Contributing to Clinimatix Caravel

Thanks for helping make Caravel better. Bug reports, fixes, documentation improvements, and ideas are all welcome.

Caravel is maintained by Clinimatix, LLC under the [MIT license](LICENSE). Please keep existing authorship and license notices intact.

## Reporting a bug

1. Search the existing [issues](https://github.com/Clinimatix/Caravel/issues) and [pull requests](https://github.com/Clinimatix/Caravel/pulls) first.
2. Include:
   - the Caravel version or commit, your .NET SDK version, and your OS
   - the smallest example that shows the problem
   - what you expected, and what actually happened
   - any error output, with sensitive details removed
3. If something on the [roadmap](docs/ROADMAP.md) doesn't exist yet, that's a feature request rather than a bug, and those are welcome too.

**Keep private data out.** Everything you post is public. Leave out personal or customer data, credentials, connection strings, production logs and private source code, and write a small made-up example instead.

**Security issues** go through [SECURITY.md](SECURITY.md), not public issues.

## Making a change

For a small fix (a typo, a clear bug, a doc correction), just open a pull request. Link the issue if there is one.

For anything bigger, such as a new feature, a public API change, or a new dependency, please open an issue first so we can agree on the approach. The [design and roadmap](docs/ROADMAP.md) page explains the principles Caravel follows.

A few guidelines:

- Keep changes focused, and add a test that shows the fix works.
- Prefer built-in .NET features to new dependencies. Any new dependency needs a clear reason and a compatible license.
- If you change a public API, explain what breaks and how to migrate.
- Document features as they actually behave today; describe planned work as planned.

## Building and testing

Use the SDK version pinned in `global.json` (currently .NET 10):

```powershell
dotnet restore Caravel.slnx --locked-mode
dotnet build Caravel.slnx -c Release --no-restore
dotnet test Caravel.slnx -c Release --no-build --no-restore
```

SQLite tests always run. SQL Server and PostgreSQL tests are skipped unless you point them at disposable servers or run the Docker-based script.

[Testing Caravel](docs/TESTING.md) covers the package and end-to-end scripts, database testing, and which checks to run for different kinds of changes.

## After you open a pull request

A maintainer will review it. In the description, mention which checks you ran locally and any platforms you couldn't test. Maintainers arrange additional verification checkpoints as needed; every release requires passing cross-platform CI on its exact commit. See [versions and releases](docs/RELEASE-POLICY.md) for how changes ship.
