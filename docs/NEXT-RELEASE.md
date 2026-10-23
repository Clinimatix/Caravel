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
- A verified PowerShell Bosun installer with explicit stable/prerelease or pinned selection,
  isolated version directories and a reviewed public `/install.ps1` serving path. See [installer](INSTALLER.md).
- Integration and qualification of Fathom application-tool contracts and the agent manifest
  foundation; Fathom is required for the first stable release.

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
7. Installer local fixtures cover integrity/provenance failures, prerequisites, repeated calls
   and install failure/retry without administrator access or PATH changes. Retain exact-package
   installed-tool and Windows/Linux/macOS evidence. Before website launch, verify the reviewed
   installer digest, text MIME type, cache headers and download-and-inspect instructions on
   the custom domain before launching the endpoint.

Publication is a separate deliberate action after these gates. A branch push or passing
unit suite does not create a release or prove recipient delivery.

## RC3 to first stable

Freeze the candidate API scope; fix defects and repeat affected qualification. Before stable,
retain repeatable clean-cache installation and upgrade evidence for the selected immutable
package set, participating-application feedback, recovery evidence and complete exact-commit
CI. Resolve critical defects and state the supported API/platform compatibility commitment.
Remove the suffix at the eligible numeric floor only after those checks and release review.

Calendar scheduling, S3, batches/chains, in-memory queues, passkeys, richer starters, extra
notification transports and broader AI orchestration remain later work. Laravel guides useful
conventions; parity with its entire feature set is not a gate for 26.1 stable. Container
images are in release scope with their own runtime/provider qualification; Kubernetes is
out of scope. Host-based checks do not substitute for container qualification.
