# Azure Blob storage

The `26.1.0-rc2` candidate adds `Clinimatix.Caravel.Storage.Azure`, an optional disk backed by the official Azure Blob SDK. It is not included in the published RC1 packages. Install it explicitly at the same exact version as your other Caravel packages, using a feed containing that version; see [candidate installation](RELEASE-POLICY.md#using-prerelease-packages). Core and local storage do not depend on Azure.

Use this adapter when an application needs private objects, streamed transfers and reads or deletes tied to a known object revision. The application supplies a `BlobContainerClient` for an existing container and owns its credentials, endpoint, retry policy and provisioning. Registration never creates containers, discovers credentials, changes public access or generates a SAS URL.

## Register and create objects

```csharp
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Caravel.Storage;
using Caravel.Storage.Azure;

// Configure this client through your application's normal Azure setup.
BlobContainerClient container = configuredContainer;
builder.Services.AddCaravelAzureBlobDisk("documents", container, prefix: "uploads");

var disk = services.GetRequiredKeyedService<AzureBlobStorageDisk>("documents");
await using var input = File.OpenRead(sourceFile);
var key = $"reports/{Guid.NewGuid():N}.bin";
var receipt = await disk.CreateAsync(key, input,
    new BlobHttpHeaders { ContentType = "application/octet-stream" }, cancellationToken);
// Store key, receipt.ETag and optional receipt.VersionId in your application record.
```

The same instance is registered as keyed `IStorageDisk`. `PutAsync` uses the same create-only operation without returning a receipt. A service-side `If-None-Match: *` condition protects both single-request uploads and multipart commits. Existing objects are never replaced by this disk, including when two writers race. SDK upload chunks are bounded to 4 MiB with one concurrent transfer; non-seekable input is supported. The input remains open. Pass cancellation to transfers and later stream reads.

The prefix and key use the [local disk's portable naming rules](STORAGE.md#storage-keys-and-access-boundaries), normalized to forward slashes by `StorageKey.Normalize`. Their combined Azure name cannot exceed 1024 characters. Service/account-specific restrictions can still reject a name. Keys and prefixes are case-sensitive object selectors, not access controls.

## Read the expected revision

```csharp
using Azure;

var properties = await disk.GetPropertiesAsync(key, cancellationToken);
var download = await disk.DownloadAsync(key, savedETag,
    range: new HttpRange(0, 1024), versionId: savedVersionId,
    cancellationToken: cancellationToken);
await using var content = download.Content;
await content.CopyToAsync(destination, cancellationToken);

await disk.DeleteIfMatchAsync(key, savedETag, cancellationToken);
```

`CreateAsync` returns the service's native `BlobContentInfo`; `GetPropertiesAsync` returns `BlobProperties`. An ETag identifies a revision for conditional access, not a content hash or malware verdict. `DownloadAsync` requires one strong, quoted ETag and carries `If-Match` into the SDK request. It accepts a native `HttpRange` and an optional service-issued VersionId. It never falls back to different bytes when the condition fails. Dispose `download.Content` after use.

`OpenReadAsync` streams the current object using the SDK's modification detection across chunk requests. It does not select the application's previously accepted revision; use `DownloadAsync` with the stored ETag for that purpose. The caller owns the returned stream.

`DeleteAsync` is harmless when an object is absent. `DeleteIfMatchAsync` is stricter: the expected current revision must exist and match. A missing object can return `404` or `412`, depending on service condition evaluation; a failed condition is never converted to success. Neither operation requests snapshot deletion or removes historical versions. Retention, soft deletion and permissions may prevent a delete or retain data after it succeeds.

Native `RequestFailedException` status and error codes remain available: for example `404` for missing objects, `409`/`412` for conflicts or failed conditions, and `416` for an unsatisfiable range. Do not expose provider exceptions, signed URLs or account details through HTTP responses.

## Authorize a download

The [compiled download example](examples/AuthorizedBlobDownload.cs) resolves an application record by ID after authenticating the caller. Its application-owned `IDownloadRecords` implementation must query current membership, permissions and file eligibility and return the key/ETag for the accepted bytes. A key, an old claim or a previously issued link is insufficient authority.

Map `AuthorizedBlobDownload.HandleAsync` behind your native authentication middleware and supply the policy store and disk. The example checks policy on every request before contacting storage. It serves a full attachment with a generated filename, `application/octet-stream`, `nosniff` and `no-store`, ignoring uploaded names, content types and client Range headers. A missing or replaced stored revision becomes a generic conflict response; provider failures receive a generic unavailable response. The adapter's separate ranged API remains available for applications that implement their own HTTP range policy.

This is a download boundary, not a scanner or document workflow. Persist scan/validation results against the exact object revision and decide what makes it eligible. Revocation is checked at request start; it does not retract bytes already delivered or continuously recheck a running stream. Handle failures after response streaming begins through the host's normal aborted-response behavior.

## Failure and deployment limits

A remote timeout or canceled response can occur after the service commits an upload. Do not infer absence from an exception, or delete the destination as generic failure cleanup: another writer may own it. Allocate an application-owned unique key before upload and reconcile an uncertain result through that record, the object's properties and any application content verification. A retry on the same key may report a conflict. Multipart staging can leave uncommitted blocks for service cleanup; this differs from local temporary-file cleanup.

The tests cover SDK request conditions, current download policy and loopback Azurite transfers, including multipart races, bounded non-seekable reads, cancellation/input failure, revision changes and ranges. Historical VersionId forwarding is tested at the HTTP transport boundary; emulator tests do not establish cloud version retention. Live Azure identity/RBAC, Defender scanning, WORM, private networking, cloud versioning and deployment behavior require separate qualification. See Microsoft's [conditional access guidance](https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage), [versioning overview](https://learn.microsoft.com/en-us/azure/storage/blobs/versioning-overview) and [Azurite scope](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite).
