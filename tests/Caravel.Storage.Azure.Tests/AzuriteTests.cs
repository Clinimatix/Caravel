using global::Azure;
using global::Azure.Storage;
using global::Azure.Storage.Blobs;
using global::Azure.Storage.Blobs.Models;
using Xunit;

namespace Caravel.Storage.Azure.Tests;

public class AzuriteTests
{
    [AzuriteTheory]
    [InlineData(17)]
    [InlineData(9 * 1024 * 1024 + 31)]
    public async Task ConcurrentNonSeekableCreatesHaveOneIntactWinner(int length)
    {
        await using var fixture = await Emulator.CreateAsync();
        var disk = new AzureBlobStorageDisk(fixture.Container, "objects");
        using var first = new GeneratedStream(length, 65);
        using var second = new GeneratedStream(length, 66);
        var results = await Task.WhenAll(Create(first), Create(second));
        Assert.Single(results, x => x.Error is null);
        var loser = Assert.Single(results, x => x.Error is not null);
        Assert.Contains(loser.Error!.Status, new[] { 409, 412 });
        Assert.True(first.CanRead && second.CanRead);
        Assert.InRange(Math.Max(first.LargestRead, second.LargestRead), 1, 4 * 1024 * 1024);
        var winner = results.Single(x => x.Error is null);
        var properties = await disk.GetPropertiesAsync("item.bin");
        Assert.Equal(length, properties.ContentLength);
        Assert.Equal(winner.Info!.ETag, properties.ETag);
        await using var stored = await disk.OpenReadAsync("item.bin");
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await stored.ReadAsync(buffer)) != 0)
        {
            Assert.Equal(-1, buffer.AsSpan(0, count).IndexOfAnyExcept(winner.Marker));
            total += count;
        }
        Assert.Equal(length, total);

        async Task<(byte Marker, BlobContentInfo? Info, RequestFailedException? Error)> Create(GeneratedStream input)
        {
            try { return (input.Marker, await disk.CreateAsync("item.bin", input), null); }
            catch (RequestFailedException exception) { return (input.Marker, null, exception); }
        }
    }

    [AzuriteFact]
    public async Task FailedAndCanceledMultipartInputsCannotReplaceAnExistingObject()
    {
        await using var fixture = await Emulator.CreateAsync();
        var disk = new AzureBlobStorageDisk(fixture.Container);
        using var original = new MemoryStream("original"u8.ToArray());
        var receipt = await disk.CreateAsync("item", original);
        using var broken = new GeneratedStream(12 * 1024 * 1024, 67, failAfter: 5 * 1024 * 1024);
        await Assert.ThrowsAsync<IOException>(() => disk.PutAsync("item", broken).AsTask());
        Assert.True(broken.CanRead);
        Assert.Equal(receipt.ETag, (await disk.GetPropertiesAsync("item")).ETag);

        using var cancel = new CancellationTokenSource();
        using var canceled = new GeneratedStream(12 * 1024 * 1024, 68, cancelAfter: cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.PutAsync("item", canceled, cancel.Token).AsTask());
        Assert.True(canceled.CanRead);
        Assert.Equal(receipt.ETag, (await disk.GetPropertiesAsync("item")).ETag);

        using var missing = new GeneratedStream(12 * 1024 * 1024, 69, failAfter: 5 * 1024 * 1024);
        await Assert.ThrowsAsync<IOException>(() => disk.PutAsync("missing", missing).AsTask());
        var absent = await Assert.ThrowsAsync<RequestFailedException>(() => disk.GetPropertiesAsync("missing").AsTask());
        Assert.Equal(404, absent.Status);
        await disk.DeleteAsync("missing");
    }

    [AzuriteFact]
    public async Task RangesAndDeletesArePinnedToTheExpectedRevision()
    {
        await using var fixture = await Emulator.CreateAsync();
        var disk = new AzureBlobStorageDisk(fixture.Container);
        using var input = new MemoryStream("0123456789"u8.ToArray());
        var receipt = await disk.CreateAsync("item", input, new BlobHttpHeaders { ContentType = "text/plain" });
        Assert.Equal("text/plain", (await disk.GetPropertiesAsync("item")).ContentType);
        var ranged = await disk.DownloadAsync("item", receipt.ETag, new HttpRange(3, 3));
        using (ranged.Content)
        using (var reader = new StreamReader(ranged.Content)) Assert.Equal("345", await reader.ReadToEndAsync());
        var invalidRange = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DownloadAsync("item", receipt.ETag, new HttpRange(100, 3)).AsTask());
        Assert.Equal(416, invalidRange.Status);

        // Simulate an independent writer outside the create-only disk contract.
        await fixture.Container.GetBlobClient("item").UploadAsync(BinaryData.FromString("replacement"), overwrite: true);
        var staleRead = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DownloadAsync("item", receipt.ETag).AsTask());
        var staleDelete = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DeleteIfMatchAsync("item", receipt.ETag).AsTask());
        Assert.Equal(412, staleRead.Status);
        Assert.Equal(412, staleDelete.Status);
        var current = await disk.GetPropertiesAsync("item");
        Assert.NotEqual(receipt.ETag, current.ETag);
        await disk.DeleteIfMatchAsync("item", current.ETag);
        var missingDelete = await Assert.ThrowsAsync<RequestFailedException>(() => disk.DeleteIfMatchAsync("item", current.ETag).AsTask());
        Assert.Contains(missingDelete.Status, new[] { 404, 412 });
    }

    [AzuriteFact]
    public async Task OpenReadDetectsReplacementBetweenStreamingRequests()
    {
        await using var fixture = await Emulator.CreateAsync();
        var disk = new AzureBlobStorageDisk(fixture.Container);
        using var source = new GeneratedStream(9 * 1024 * 1024, 70);
        await disk.PutAsync("item", source);
        await using var reader = await disk.OpenReadAsync("item");
        Assert.Equal(70, reader.ReadByte());
        using var replacement = new GeneratedStream(9 * 1024 * 1024, 71);
        await fixture.Container.GetBlobClient("item").UploadAsync(replacement, overwrite: true);
        var error = await Assert.ThrowsAsync<RequestFailedException>(() => reader.CopyToAsync(Stream.Null));
        Assert.Equal(412, error.Status);
    }

    private sealed class GeneratedStream(int length, byte marker, int? failAfter = null, CancellationTokenSource? cancelAfter = null) : Stream
    {
        private int position;
        private bool disposed;
        public byte Marker => marker;
        public int LargestRead { get; private set; }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            LargestRead = Math.Max(LargestRead, buffer.Length);
            if (failAfter is not null && position >= failAfter) throw new IOException("Synthetic input failure.");
            if (cancelAfter is not null && position >= 5 * 1024 * 1024) { cancelAfter.Cancel(); cancelAfter.Token.ThrowIfCancellationRequested(); }
            var count = Math.Min(buffer.Length, length - position);
            buffer[..count].Fill(marker);
            position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class Emulator(BlobContainerClient container) : IAsyncDisposable
{
    public BlobContainerClient Container { get; } = container;
    public static async Task<Emulator> CreateAsync()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("CARAVEL_TEST_AZURITE_ENDPOINT")!);
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/caraveltest" ||
            endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException("Only the explicitly configured loopback emulator is allowed.");
        var credential = new StorageSharedKeyCredential("caraveltest", Environment.GetEnvironmentVariable("CARAVEL_TEST_AZURITE_KEY")!);
        var options = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2026_06_06);
        options.Retry.MaxRetries = 0;
        var service = new BlobServiceClient(endpoint, credential, options);
        var container = service.GetBlobContainerClient("caravel-test-" + Guid.NewGuid().ToString("N"));
        await container.CreateAsync();
        return new(container);
    }
    public async ValueTask DisposeAsync() => await Container.DeleteIfExistsAsync();
}

internal sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute() { if (Environment.GetEnvironmentVariable("CARAVEL_TEST_AZURITE_ENDPOINT") is null) Skip = "Run Test-AzureStorage.ps1 for the owned emulator."; }
}
internal sealed class AzuriteTheoryAttribute : TheoryAttribute
{
    public AzuriteTheoryAttribute() { if (Environment.GetEnvironmentVariable("CARAVEL_TEST_AZURITE_ENDPOINT") is null) Skip = "Run Test-AzureStorage.ps1 for the owned emulator."; }
}
