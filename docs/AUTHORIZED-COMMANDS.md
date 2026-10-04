# Retry a command without repeating its effects

The authenticated backend sample includes a small work-item application. A member can complete an item in their workspace, recover the result after a lost response, and inspect a background notice. Its browser form handles expired access, validation, uncertain retries and conflicting edits.

This guide targets `26.1.0-rc2`. The extension is not included in the immutable `26.1.0-rc1` source snapshot; see [upgrading](UPGRADING.md) before updating an existing sample.

This is editable application code in `samples/Caravel.Identity`, using native ASP.NET Core authorization and EF transactions, Caravel's outbox/queue, and captured mail. Workspace membership, command receipts and completion rules belong to the application. Bosun's `--stack identity` profile generates a focused version using the same command/session/worker/browser sources; see [creating an app](GETTING-STARTED.md#creating-and-running-an-app). The basic Razor/API starters remain available.

## Run the sample

Follow [backend setup](BACKEND-SAMPLE.md#prepare-the-two-database-contexts) to apply both contexts' migrations, then [create a synthetic user](AUTHENTICATION.md#try-the-sample). Upgrading an existing sample also requires applying the new Identity migration deliberately. Runtime startup never applies migrations.

With the same Development environment, database setting and `CARAVEL_DEMO_USER`, create a workspace for that existing user:

```powershell
dotnet run --project samples/Caravel.Identity -- --seed-demo-workspace
$env:Caravel__RunWorker = 'true'
dotnet run --project samples/Caravel.Identity -- --urls https://localhost:7246
```

Open `https://localhost:7246/work-items`. Sign in, select the item, and enter a completion note. The Development-only seed command refuses a second workspace for that user. No accounts or workspaces are created at startup.

The worker setting runs the counter worker, application outbox relay and notice worker. Leave it false to inspect pending intent. Mail is captured in memory; nothing is sent externally. Capture is bounded, disappears with the process, and is not a delivery log.

The form supports password sign-in and sign-out. It reports when an account requires MFA but does not implement MFA screens; the existing [account endpoints](AUTHENTICATION.md) remain available. Local Identity is this sample's choice. The native [OIDC](OIDC-AUTHENTICATION.md) and [bearer](SERVICE-AUTHENTICATION.md) recipes remain alternative integrations; this UI does not qualify a combined cookie/bearer profile.

## API contract

Workspace APIs require authentication and return `Cache-Control: no-store`. Current active membership controls reads; `CanComplete` additionally controls commands. Supplied workspace/item IDs select records and never grant access. Receipts are private to their initiating actor.

| Request | Result |
| --- | --- |
| `GET /workspaces/` | Up to 100 active workspace memberships |
| `GET /workspaces/{workspace}/items` | First 50 items in ID order and current command permission |
| `GET /workspaces/{workspace}/items/{item}` | Item and revision ETag, such as `"1"` |
| `POST /workspaces/{workspace}/items/{item}/complete` | Committed result, receipt ID and current item |
| `GET /workspaces/{workspace}/receipts/{receipt}` | Current authorized item and notice state |

The bounded lists are sample projections, not complete pagination. Applications can extend them with paging, search and resource-specific policies.

Fetch `/auth/csrf` after sign-in. Send its token/header and retain both authentication and antiforgery cookies. JSON mutations explicitly validate antiforgery. Read the item first to obtain its revision:

```http
POST /workspaces/{workspace}/items/{item}/complete
Content-Type: application/json
If-Match: "1"
Idempotency-Key: complete-summary-001
RequestVerificationToken: <fresh token>

{"comment":"Reviewed and complete"}
```

**200 OK** means the completion and notice intent committed. It does not mean a notice was delivered. The initial response includes a Location for the receipt; successful responses include the current item ETag. The precondition convention accepts one quoted positive integer without leading zeros. Wildcards, weak tags and lists are rejected; this is an application convention, not every HTTP conditional-request form.

| Outcome | Status |
| --- | --- |
| Missing authentication / current permission | 401 / 403 |
| Foreign or missing resource/private receipt, after membership check | 404 |
| Invalid body/key/tag or antiforgery token | 400 |
| Missing `If-Match` | 428 |
| New command with an old revision | 412 |
| Same key with changed semantic input, or an already completed transition | 409 |
| Database outcome remains unconfirmed after bounded retries | 503; retry the original command/key |

## Transaction and replay

Each attempt opens a fresh scoped context and serializable transaction, then checks current membership and the scoped item. A matching receipt is checked **before** a new-command revision check: the original success already advanced the revision. Current access is checked **before** the receipt, so revoked callers cannot recover protected data. Replays return a fresh authorized projection, not cached response JSON.

The receipt scope hashes a JSON array of workspace, actor, versioned operation name, resource and client key. A separate fingerprint covers resource, expected revision and trimmed note. JSON encodes boundaries unambiguously. The key is one nonblank header value of at most 128 characters without controls. Trimming the note is an explicit semantic choice; arbitrary input must not be normalized silently.

The item update, attributed change, receipt and reference-only outbox intent share one save and commit. The revision is an EF concurrency token; receipt scope has a unique constraint. Database races/failures receive at most three attempts, each with a fresh context/transaction and no external effects. An uncertain commit can be resolved by its receipt. Persistent failures return 503 without inferring success.

Do not turn this into middleware that retries arbitrary endpoints: their external effects and transaction rules may differ. Authorization here is serialized with the command's database work. A later membership change does not undo committed completion. Read projections and workers have their own access checks. The change table illustrates attributed history; it is not tamper-proof storage or a legal audit ledger.

## Reference-only background work

`WorkItemNotice(ReceiptId)` carries no title, note or address. The handler verifies its queue/workspace envelope, reloads the receipt, membership, confirmed email and completed item, then constructs a generic notice. Lost eligibility records **Suppressed**; a successful sender call records **Submitted**. These and **Pending** are application states, separate from queue status.

Eligibility is checked before submission. A message already accepted cannot be recalled by a later membership change. A crash after provider acceptance but before saving Submitted can cause another submission. Once Submitted/Suppressed is saved, worker redelivery does nothing. This does not promise exactly-once external delivery or recipient reading.

The built-in `NotificationDeliveryJob` deliberately snapshots content and destination. Use that simpler job for fixed delivery data, or an application reference job when current recipients/policy must be resolved at execution time. Both can use the existing outbox and replaceable transports.

## Browser behavior and retention

An uncertain response preserves the original key, revision and note in memory and offers **Retry the same command**. Validation allows correction; stale/conflicting commands preserve the edit until a deliberate reload. Workspace/session changes clear protected state, and late responses from an older view are discarded. Returning focus refreshes receipt state/current access.

Reloading or closing the page loses the in-memory retry envelope. This sample's one-way completion rule still prevents a new key from completing the item twice. Durable offline retries need application-owned protected storage and reconciliation.

Retain receipts, changes and required outbox/queue history through the supported retry horizon. Deleting them changes duplicate protection; no pruning is supplied. See [queue retention](QUEUES.md).

Integration tests cover access, preconditions, concurrent commands, rollback after writes, application restart and worker acknowledgement loss. [Packaged backend checks](TESTING.md#test-against-sql-server-and-postgresql) exercise the sample with isolated packages and provider-native migrations. Identity-provider, RLS, cloud-storage and deployment qualification remain separate.
