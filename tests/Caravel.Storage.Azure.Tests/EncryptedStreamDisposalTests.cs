using System.Net;
using global::Azure.Core.Cryptography;
using global::Azure.Core.Pipeline;
using global::Azure.Storage;
using global::Azure.Storage.Blobs;
using global::Azure.Storage.Blobs.Specialized;
using Xunit;

namespace Caravel.Storage.Azure.Tests;

public class EncryptedStreamDisposalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedDisposalPreservesIndependentEncryptedDownloads(bool concurrentDisposal)
    {
        using var handler = new EncryptedBlobHandler();
        var key = new SyntheticKey();
        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler))
        };
        options.Retry.MaxRetries = 0;
        var encryption = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V2_0)
        { KeyEncryptionKey = key, KeyResolver = key, KeyWrapAlgorithm = "synthetic-test-only" };
        var disk = new AzureBlobStorageDisk(new EncryptedContainer(options, encryption));
        var first = Enumerable.Repeat((byte)65, 32 * 1024).ToArray();
        var second = Enumerable.Repeat((byte)66, 32 * 1024).ToArray();
        using (var source = new MemoryStream(first)) await disk.PutAsync("first", source);
        using (var source = new MemoryStream(second)) await disk.PutAsync("second", source);
        Assert.NotEqual(first, handler.Blobs["/objects/first"].Content);

        var initial = await disk.DownloadAsync("first", new global::Azure.ETag("\"revision\""));
        Assert.Equal(65, initial.Content.ReadByte());
        if (concurrentDisposal)
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(initial.Content.Dispose)));
        // Exercise repeated cleanup on the continuation thread as well as concurrent cleanup.
        initial.Content.Dispose();
        await initial.Content.DisposeAsync();
        initial.Content.Dispose();

        var one = await disk.DownloadAsync("first", new global::Azure.ETag("\"revision\""));
        var two = await disk.DownloadAsync("second", new global::Azure.ETag("\"revision\""));
        await using var oneContent = one.Content;
        await using var twoContent = two.Content;
        Assert.Equal(65, oneContent.ReadByte());
        Assert.Equal(66, twoContent.ReadByte());
        using var oneRemainder = new MemoryStream();
        using var twoRemainder = new MemoryStream();
        await oneContent.CopyToAsync(oneRemainder);
        await twoContent.CopyToAsync(twoRemainder);
        Assert.Equal(first[1..], oneRemainder.ToArray());
        Assert.Equal(second[1..], twoRemainder.ToArray());
    }

    // A made-up key wrapper is sufficient to exercise the SDK's real AES-GCM stream.
    // It is deliberately not a cryptographic implementation or a service credential.
    private sealed class SyntheticKey : IKeyEncryptionKey, IKeyEncryptionKeyResolver
    {
        public string KeyId => "https://keys.invalid/synthetic";
        public byte[] WrapKey(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) => key.ToArray();
        public Task<byte[]> WrapKeyAsync(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Task.FromResult(WrapKey(algorithm, key, cancellationToken));
        public byte[] UnwrapKey(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) => key.ToArray();
        public Task<byte[]> UnwrapKeyAsync(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnwrapKey(algorithm, key, cancellationToken));
        public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) => this;
        public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IKeyEncryptionKey>(this);
    }

    private sealed record StoredBlob(byte[] Content, string EncryptionData);

    private sealed class EncryptedContainer : BlobContainerClient
    {
        private readonly BlobClientOptions options;
        private readonly ClientSideEncryptionOptions encryption;
        public EncryptedContainer(BlobClientOptions options, ClientSideEncryptionOptions encryption)
            : base(new Uri("https://storage.invalid/objects"), options)
        {
            this.options = options;
            this.encryption = encryption;
        }
        public override BlobClient GetBlobClient(string blobName) =>
            new BlobClient(new Uri(Uri.AbsoluteUri + "/" + blobName), options).WithClientSideEncryptionOptions(encryption);
    }

    private sealed class EncryptedBlobHandler : HttpMessageHandler
    {
        public Dictionary<string, StoredBlob> Blobs { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                Blobs[path] = new(await request.Content!.ReadAsByteArrayAsync(cancellationToken),
                    request.Headers.GetValues("x-ms-meta-encryptiondata").Single());
                return AzureBlobStorageTests.Response(HttpStatusCode.Created);
            }
            var blob = Blobs[path];
            var response = AzureBlobStorageTests.Response(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : blob.Content);
            response.Content.Headers.ContentLength = blob.Content.Length;
            response.Headers.Add("x-ms-meta-encryptiondata", blob.EncryptionData);
            return response;
        }
    }
}
