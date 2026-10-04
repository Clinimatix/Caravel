using System.Net;
using System.Net.Http.Headers;
using global::Azure;
using global::Azure.Core.Pipeline;
using global::Azure.Storage.Blobs;
using global::Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Storage.Azure.Tests;

public class AzureBlobStorageTests
{
    [Fact]
    public async Task InvalidKeysAndConditionsFailBeforeNetworkAccess()
    {
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Unexpected request."));
        var disk = new AzureBlobStorageDisk(Container(handler), "archive");
        foreach (var key in new[] { "", "../item", "/item", "a//b", "a/../b", "a/CON.txt", "a:stream", "a%2fb", "a. ", "a?b", new string('a', 1024) })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => disk.PutAsync(key, Stream.Null).AsTask());
            await Assert.ThrowsAnyAsync<ArgumentException>(() => disk.OpenReadAsync(key).AsTask());
            await Assert.ThrowsAnyAsync<ArgumentException>(() => disk.DeleteAsync(key).AsTask());
        }
        foreach (var etag in new[] { default(ETag), ETag.All, new ETag(" * "), new ETag("W/\"weak\""), new ETag("\"one\", \"two\""), new ETag("\"a\r\nb\"") })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => disk.DownloadAsync("item", etag).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => disk.DeleteIfMatchAsync("item", etag).AsTask());
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.PutAsync("item", Stream.Null, canceled.Token).AsTask());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UploadIsCreateOnlyAndRegistrationSharesTheNativeCompanion()
    {
        using var handler = new RecordingHandler(_ => Response(HttpStatusCode.Created));
        var services = new ServiceCollection().AddCaravelAzureBlobDisk("objects", Container(handler), "archive");
        using var provider = services.BuildServiceProvider();
        var disk = provider.GetRequiredKeyedService<AzureBlobStorageDisk>("objects");
        Assert.Same(disk, provider.GetRequiredKeyedService<IStorageDisk>("objects"));
        using var input = new MemoryStream("synthetic"u8.ToArray());
        var receipt = await disk.CreateAsync("folder\\item.txt", input, new BlobHttpHeaders { ContentType = "text/plain" });
        Assert.True(input.CanRead);
        Assert.Equal(new ETag("\"revision\""), receipt.ETag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/objects/archive/folder/item.txt", request.Uri.AbsolutePath);
        Assert.Equal("*", request.Headers["If-None-Match"]);
        Assert.Equal("text/plain", request.Headers["x-ms-blob-content-type"]);
    }

    [Fact]
    public async Task GuardedReadPreservesRangeVersionAndETagWithoutASecondLookup()
    {
        using var handler = new RecordingHandler(_ => Response(HttpStatusCode.PartialContent, "abc"));
        var disk = new AzureBlobStorageDisk(Container(handler));
        var result = await disk.DownloadAsync("item", new ETag("\"revision\""), new HttpRange(3, 3), "opaque/version+1");
        using var content = result.Content;
        using var reader = new StreamReader(content);
        Assert.Equal("abc", await reader.ReadToEndAsync());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("\"revision\"", request.Headers["If-Match"]);
        Assert.Equal("bytes=3-5", request.Headers["x-ms-range"]);
        Assert.Equal("?versionid=opaque/version+1", Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact]
    public async Task GuardedDeleteDistinguishesAbsentFromChangedAndPreservesProviderFailure()
    {
        using var handler = new RecordingHandler(_ => Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet"));
        var disk = new AzureBlobStorageDisk(Container(handler));
        var error = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DeleteIfMatchAsync("item", new ETag("\"old\"")).AsTask());
        Assert.Equal(412, error.Status);
        Assert.Equal("ConditionNotMet", error.ErrorCode);
        Assert.Equal("\"old\"", Assert.Single(handler.Requests).Headers["If-Match"]);
        Assert.DoesNotContain("x-ms-delete-snapshots", handler.Requests[0].Headers.Keys);

        handler.Reply = _ => Error(HttpStatusCode.NotFound, "BlobNotFound");
        var absent = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DeleteIfMatchAsync("item", new ETag("\"old\"")).AsTask());
        Assert.Equal(404, absent.Status);
        await disk.DeleteAsync("item");
    }

    internal static BlobContainerClient Container(RecordingHandler handler)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return new BlobContainerClient(new Uri("https://storage.invalid/objects"), options);
    }

    internal static HttpResponseMessage Response(HttpStatusCode status, string body = "")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Headers.ETag = new EntityTagHeaderValue("\"revision\"");
        response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        response.Headers.Add("x-ms-blob-type", "BlockBlob");
        if (status == HttpStatusCode.PartialContent) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 10);
        return response;
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        { Content = new StringContent($"<Error><Code>{code}</Code><Message>Synthetic failure</Message></Error>", System.Text.Encoding.UTF8, "application/xml") };
        response.Headers.Add("x-ms-error-code", code);
        return response;
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers);

    internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = reply;
        public List<RecordedRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(Reply(request));
        }
    }
}
