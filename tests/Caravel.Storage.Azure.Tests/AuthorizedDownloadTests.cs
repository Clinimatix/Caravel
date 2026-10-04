using System.Net;
using System.Security.Claims;
using global::Azure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StorageExample;
using Xunit;

namespace Caravel.Storage.Azure.Tests;

public class AuthorizedDownloadTests
{
    [Fact]
    public async Task CurrentPolicyAndStoredRevisionGateEveryDownloadWithoutExposingProviderDetails()
    {
        using var handler = new AzureBlobStorageTests.RecordingHandler(_ => AzureBlobStorageTests.Response(HttpStatusCode.OK, "abc"));
        var disk = new AzureBlobStorageDisk(AzureBlobStorageTests.Container(handler));
        var records = new DownloadRecords();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDownloadRecords>(records);
        builder.Services.AddSingleton(disk);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Test authentication only. Production uses its configured native authentication handler.
            if (context.Request.Headers.ContainsKey("Synthetic-User"))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "test"));
            await next(context);
        });
        app.MapGet("/downloads/{id:guid}", AuthorizedBlobDownload.HandleAsync);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var path = "/downloads/" + Guid.NewGuid();
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, records.Calls);
        client.DefaultRequestHeaders.Add("Synthetic-User", "reader");
        using var forbidden = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Empty(handler.Requests);

        records.Allowed = new("object-key", new ETag("\"revision\""));
        client.DefaultRequestHeaders.Range = new(0, 1);
        using var allowed = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("abc", await allowed.Content.ReadAsStringAsync());
        Assert.Equal("application/octet-stream", allowed.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", allowed.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(allowed.Headers.CacheControl!.NoStore);
        Assert.Contains("nosniff", allowed.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("\"revision\"", Assert.Single(handler.Requests).Headers["If-Match"]);

        records.Allowed = null; // Membership or eligibility revoked after the previous download.
        using var revoked = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Single(handler.Requests);
        records.Allowed = new("object-key", new ETag("\"revision\""));
        handler.Reply = _ => AzureBlobStorageTests.Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
        using var replaced = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);
        Assert.DoesNotContain("Synthetic failure", await replaced.Content.ReadAsStringAsync());
        Assert.DoesNotContain("storage.invalid", await replaced.Content.ReadAsStringAsync());
        handler.Reply = _ => AzureBlobStorageTests.Error(HttpStatusCode.Forbidden, "AuthorizationFailure");
        using var unavailable = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.DoesNotContain("AuthorizationFailure", await unavailable.Content.ReadAsStringAsync());
    }

    private sealed class DownloadRecords : IDownloadRecords
    {
        public DownloadReference? Allowed { get; set; }
        public int Calls { get; private set; }
        public Task<DownloadReference?> FindAllowedAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Allowed); }
    }
}
