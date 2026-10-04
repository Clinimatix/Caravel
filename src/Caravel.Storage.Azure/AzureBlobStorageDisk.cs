using global::Azure;
using global::Azure.Storage;
using global::Azure.Storage.Blobs;
using global::Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Storage.Azure;

/// <summary>A create-only disk using an application-supplied client and an existing container.</summary>
public sealed class AzureBlobStorageDisk : IStorageDisk
{
    private const int BufferSize = 4 * 1024 * 1024;
    private readonly BlobContainerClient container;
    private readonly string prefix;

    public AzureBlobStorageDisk(BlobContainerClient container, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.container = container;
        this.prefix = prefix is null ? "" : StorageKey.Normalize(prefix) + "/";
        if (this.prefix.Length >= 1024) throw new ArgumentException("The prefix must leave room for an object name.", nameof(prefix));
    }

    public async ValueTask PutAsync(string path, Stream content, CancellationToken cancellationToken = default) =>
        _ = await CreateAsync(path, content, cancellationToken: cancellationToken);

    /// <summary>Creates without replacement and returns the service ETag/version. The caller retains the input stream.</summary>
    public async ValueTask<BlobContentInfo> CreateAsync(string path, Stream content,
        BlobHttpHeaders? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        return (await Blob(path).UploadAsync(content, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = headers,
            TransferOptions = new StorageTransferOptions
            {
                InitialTransferSize = BufferSize, MaximumTransferSize = BufferSize, MaximumConcurrency = 1
            }
        }, cancellationToken)).Value;
    }

    /// <summary>Streams the current object with SDK modification detection. Dispose the returned stream.</summary>
    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Blob(path).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false)
        { BufferSize = BufferSize }, cancellationToken);
    }

    public async ValueTask<BlobProperties> GetPropertiesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await Blob(path).GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
    }

    /// <summary>Streams only the specified ETag, optionally a range or historical version. Dispose result.Content.</summary>
    public async ValueTask<BlobDownloadStreamingResult> DownloadAsync(string path, ETag expectedETag,
        HttpRange range = default, string? versionId = null, CancellationToken cancellationToken = default)
    {
        RequireETag(expectedETag);
        cancellationToken.ThrowIfCancellationRequested();
        var blob = Blob(path);
        if (versionId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
            blob = blob.WithVersion(versionId);
        }
        return (await blob.DownloadStreamingAsync(new BlobDownloadOptions
        { Range = range, Conditions = new BlobRequestConditions { IfMatch = expectedETag } }, cancellationToken)).Value;
    }

    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Deliberately do not request snapshot deletion or erase historical versions.
        await Blob(path).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Deletes only the expected current revision. Missing objects and failed conditions throw native provider errors.</summary>
    public async ValueTask DeleteIfMatchAsync(string path, ETag expectedETag, CancellationToken cancellationToken = default)
    {
        RequireETag(expectedETag);
        cancellationToken.ThrowIfCancellationRequested();
        await Blob(path).DeleteAsync(conditions: new BlobRequestConditions { IfMatch = expectedETag },
            cancellationToken: cancellationToken);
    }

    private BlobClient Blob(string path)
    {
        var name = prefix + StorageKey.Normalize(path);
        if (name.Length > 1024) throw new ArgumentException("The prefixed object name exceeds Azure's 1024 character limit.", nameof(path));
        return container.GetBlobClient(name);
    }

    private static void RequireETag(ETag etag)
    {
        var value = etag.ToString();
        if (value is null || value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            value[1..^1].Any(character => character < '!' || character == '"' || character == '\u007f'))
            throw new ArgumentException("Supply the ETag of a specific object revision.", nameof(etag));
    }
}

public static class AzureStorageServiceExtensions
{
    public static IServiceCollection AddCaravelAzureBlobDisk(this IServiceCollection services, string name,
        BlobContainerClient container, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var disk = new AzureBlobStorageDisk(container, prefix);
        services.AddKeyedSingleton<AzureBlobStorageDisk>(name, disk);
        services.AddKeyedSingleton<IStorageDisk>(name, disk);
        return services;
    }
}
