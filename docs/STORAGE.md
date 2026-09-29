# Local storage disks

`Clinimatix.Caravel.Storage` gives your app named storage disks that stream files instead of loading them into memory. The first driver uses the local filesystem; cloud drivers such as Azure Blob Storage and S3 are on the [roadmap](ROADMAP.md).

## Register a disk

Choose an absolute directory that the application owns. Keep it outside any publicly served web directory.

```csharp
using Caravel.Storage;

builder.Services.AddCaravelLocalDisk("documents",
    builder.Configuration["Storage:DocumentsRoot"]
        ?? throw new InvalidOperationException("Set Storage:DocumentsRoot."));
```

Registration uses .NET's keyed dependency injection. Resolve a disk by name, or inject it with `[FromKeyedServices("documents")] IStorageDisk`.

```csharp
var disk = services.GetRequiredKeyedService<IStorageDisk>("documents");
await using var source = File.OpenRead(sourceFile);
await disk.PutAsync("reports/summary.txt", source, cancellationToken);

await using var stored = await disk.OpenReadAsync("reports/summary.txt", cancellationToken);
await stored.CopyToAsync(destinationStream, cancellationToken);

await disk.DeleteAsync("reports/summary.txt", cancellationToken);
```

`PutAsync` leaves the input stream open. `OpenReadAsync` returns a stream you must dispose; pass cancellation to subsequent reads or copies too. `DeleteAsync` removes one file and is harmless when that file is already absent, including when its parent directories or disk root haven't been created. It does not create those directories and refuses directory deletion.

## Files appear only after a successful write

Writes create new files. A name conflict raises `IOException` and preserves the existing file. Choose a new storage key to save another version; there is no overwrite switch in this first driver.

Under the hood, the driver writes to a temporary file next to the destination, then moves it into place in a way that can never replace an existing file, even if two writers race. On Linux and macOS this uses a hard link, so the disk's filesystem must support hard links.

If the write is canceled or the input stream fails, the temporary file is removed and nothing appears at the destination (though newly created empty folders may remain). A crash or power loss can leave stray temporary files behind; clean them up only while the app isn't writing to the disk.

## Storage keys and access boundaries

Keys are relative paths, such as `reports/summary.txt`. Each component accepts ASCII letters, digits, spaces, dots, underscores and hyphens, with no trailing space or dot. Both slash separators are accepted. Rooted paths, `.`/`..`, empty components, Windows device names and alternate data streams are rejected. Prefer an application-generated key and keep a person's original upload name in metadata.

The driver checks the root's ancestors and existing path components for symbolic links and reparse points before access and before creating directories. A linked root, linked subdirectory or linked file is rejected, including dangling symbolic links. Configure the real directory path if an operating system exposes a temporary folder through a symlink.

**The root must be trusted and application-owned.** These checks cannot prevent a hostile local process from swapping filesystem entries between validation and use. Hard links are not detected. Do not give untrusted users or other services write access to the root or its ancestors; this is not an OS sandbox for adversarial filesystem trees.

A storage key is not an authorization decision. Check the authenticated caller's access to the associated application record before reading, writing or deleting its file. Do not expose arbitrary filesystem paths as an HTTP API. Upload limits, content validation, malware scanning, retention, backups and download authorization belong to the consuming application.

## Testing

The storage tests cover registration, streaming reads and writes, name conflicts, rejected paths, cancellation cleanup and symbolic links, plus a race in which two writers try to create the same file at once and exactly one succeeds. They run on Windows and Linux. On Windows, the symbolic-link tests are skipped if the account can't create symbolic links.
