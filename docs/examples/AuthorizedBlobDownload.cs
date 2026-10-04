using System.Security.Claims;
using Azure;
using Caravel.Storage.Azure;
using Microsoft.AspNetCore.Http;

namespace StorageExample;

// Application-owned policy and persistence. Resolve by record ID, never by a caller-supplied object key.
public interface IDownloadRecords
{
    // Return a reference only after checking current membership, permission and download eligibility.
    // The stored ETag must identify the same bytes accepted by the application's validation policy.
    Task<DownloadReference?> FindAllowedAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken);
}

public sealed record DownloadReference(string Key, ETag ETag, string? VersionId = null);

public static class AuthorizedBlobDownload
{
    // Map this handler behind the application's native authentication middleware.
    public static async Task<IResult> HandleAsync(Guid id, HttpContext context,
        IDownloadRecords records, AzureBlobStorageDisk disk, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        var reference = await records.FindAllowedAsync(id, context.User, cancellationToken);
        if (reference is null) return Results.NotFound();

        try
        {
            var download = await disk.DownloadAsync(reference.Key, reference.ETag,
                versionId: reference.VersionId, cancellationToken: cancellationToken);
            // Serve full attachments. Uploaded names/content types and client Range headers are not used.
            return Results.Stream(download.Content, "application/octet-stream", $"download-{id:N}.bin",
                enableRangeProcessing: false);
        }
        catch (RequestFailedException error) when (error.Status is 404 or 412)
        {
            return Results.Problem(statusCode: 409, title: "The stored file revision is unavailable.");
        }
        catch (RequestFailedException)
        {
            // Never include a provider exception, signed URL or account detail in the response.
            return Results.Problem(statusCode: 503, title: "File storage is temporarily unavailable.");
        }
    }
}
