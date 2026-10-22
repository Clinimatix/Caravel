# Agent development context

Give a coding assistant a small, model-free description of a project's declared Caravel packages:

```sh
caravel agent:manifest --project ./MyApp/MyApp.csproj --json
caravel schema --json
```

`agent:manifest` reads one project as XML. It never evaluates MSBuild, restores packages,
builds, loads application assemblies, starts the host or contacts a database/model provider.
It writes JSON to standard output; errors produce exit code 1 and a fixed diagnostic on
standard error. `--json` is optional because JSON is the only format. Directory selection
uses the same one-project rule as other Bosun commands.

## Version 1 contract

The [JSON Schema](agent-manifest.schema.json) defines the output. `schemaVersion: 1` and
`kind: "caravel.project-capabilities"` identify this contract independently of the package
release. Breaking field/type/meaning changes require a new schema version. Consumers
should reject unsupported versions and tolerate additional fields in a supported version.
`toolVersion` identifies the installed Bosun, not the application's dependency version.

`packages` is a deterministic, deduplicated list of known Caravel package IDs explicitly
declared by `PackageReference Include` in that file. Matching ignores case; output uses
canonical IDs. Arbitrary identifiers and versions are omitted. Conditions are not evaluated:
a conditional reference can appear even if disabled. Imported files, central versions,
transitive dependencies and project references are not inspected. Absence is not evidence
that a capability is unavailable; presence does not prove runtime registration or behavior.

The file must be valid project XML, at most 1 MiB. DTDs/external entities are prohibited.
Only known package IDs are copied from the file. Project names/paths, arbitrary properties,
dependency versions, source, configuration, `.env`, credentials and application/database
payloads are never emitted. This is data minimization by allowlist, not a general-purpose
secret scanner. Treat project selection and its filesystem permissions as caller-owned.

## Guidance for agents

Start with repository `AGENTS.md` and this static manifest. Use `schema` to discover the
installed CLI's actual arguments and documented effects. The manifest links existing
inspection commands; it does not invoke them. `doctor` explicitly starts SDK/development
dependency diagnostic processes, so review its output before sharing it.

`route:list --project ... --json` uses the existing route exporter. It builds and executes
application startup code and may restore dependencies even though it does not start the
host. Review and authorize the project before using it. The manifest does not assert that
an exporter is configured, expose routes or claim startup code is free of side effects.
No database schema inspection, arbitrary execution tool or database-write tool is added.

This foundation supplies development context, not an MCP server, model workflow, semantic
search stack or complete AI integration. Application tools/MCP and model workflows are
separate opt-in stages in the [release plan](NEXT-RELEASE.md). No feature-set brand is selected.
