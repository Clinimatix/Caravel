# Next candidate and first stable release

The next proposed candidate is **26.1.1-rc3**, followed by the first stable **26.1.1**.
These are planning targets, not published versions. Before release, check the highest
published 26.1 patch: if maintenance advances it, carry that higher numeric floor into RC3
and stable. Never replace published packages or move tags. See [release policy](RELEASE-POLICY.md).

## Bounded RC3 scope

Settle and qualify these additive APIs together with the existing 26.1 application foundation:

- Ordered, count-free Clarion query slices, preserving caller filtering and ordering.
- Validated transactional mail/notification staging through the existing application outbox.
- Explicit synchronous registered-job dispatch for development and tests.
- Model-free Bosun `agent:manifest` v1, agent guidance, a versioned JSON contract and
  regression coverage for data minimization and execution boundaries.

The query/staging/synchronous slices remain separately owned work; this foundation does
not imply they are merged or available in every checkout.

The candidate retains the .NET10 baseline and existing durable job wire contracts. Application
recipient selection, invitation validity, opt-out and authorization remain application-owned.
Synchronous dispatch is opt-in and does not replace durable enqueue or transactional staging.

## Candidate release gates

Every gate must have a retained result for the exact candidate commit:

1. Independent review finds no unresolved release-blocking correctness or disclosure issue.
2. All required Windows, Linux and macOS build/test/package/upgrade jobs pass, with no
   unexplained skips. Required SQL Server/PostgreSQL execution and migration jobs pass;
   translation and SQLite results alone do not satisfy those provider gates.
3. Package-only consumers exercise query slices and both staging APIs. A synchronous-job
   consumer runs without database registration and verifies failure, cancellation and scope
   disposal. Retain immutable package checksums and reconcile declared source identity.
4. Recovery verifies rollback, relay receipt-window deduplication, expired/stale leases and
   retry/dead-letter/replay. Document possible duplicate external acceptance after a lost
   acknowledgement; no exactly-once promise.
5. At least one participating application's synthetic integration confirms the intended
   contract; its owner signs off on application eligibility and audience behavior. Resolve
   that consumer's exact package/source/lock attribution before claiming reproducible adoption.
6. Changelog, API examples, known limitations and upgrade guidance match the tested bytes.
   Security/dependency review and serviced-runtime policy checks pass without trust bypasses.

Publication is a separate deliberate action after these gates. A branch push or passing
unit suite does not create a release or prove recipient delivery.

## Minimum AI capability gate for first stable

The first stable major release requires the Caravel Fathom agent-friendly development foundation:
installed Bosun exports the versioned static project/capability manifest, links accurate
command and route inspection guidance, and passes disclosure/non-execution regressions
and installed-tool checks on the exact release commit. Review must confirm no secrets,
configuration or application payloads escape and no arbitrary execution or database-write
tool is exposed. This minimum gate is required, not an optional future aspiration.

An opt-in [in-process application tool registry](FATHOM-TOOLS.md) is in development
ahead of the network transport. It does not qualify MCP protocol support or expand RC3
release scope. Qualify a synthetic consumer, trusted principal binding, resource-level
denial, cancellation and safe error presentation before adding a transport.

Develop in three ordered layers: (1) development context/tooling in RC3; (2) later,
opt-in application tools/MCP with explicit authorization, cancellation, protocol tests and
read/write boundaries; (3) application-driven model workflows with provider abstraction,
structured output and fake-provider tests. Embeddings/search require separate storage and
retrieval qualification. Later stages are not RC3 or first-stable prerequisites. No paid
model call, provider key or complete Laravel ecosystem is required for the foundation.
Caravel Fathom is the AI suite name. Existing public commands, schema identifiers and
package names remain compatible; suite naming does not expand this release gate.

## RC3 to first stable

Freeze the candidate API scope; fix defects and repeat affected qualification. Before stable,
retain repeatable clean-cache installation and upgrade evidence for the selected immutable
package set, participating-application feedback, recovery evidence and complete exact-commit
CI. Resolve critical defects and state the supported API/platform compatibility commitment.
Remove the suffix at the eligible numeric floor only after those checks and release review.

Calendar scheduling, S3, batches/chains, in-memory queues, passkeys, richer starters, extra
notification transports, application MCP and model workflows remain later work.
The model-free development foundation above is required for first stable. Laravel guides useful
conventions; parity with its entire feature set is not a gate for 26.1 stable. Container
images and managed-cloud deployment require their own runtime/provider qualification and
are not prerequisites for host-based framework development or claims outside that scope.
