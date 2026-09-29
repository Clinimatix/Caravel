using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caravel.Storage.Tests;

public sealed class LocalStorageTests : IDisposable
{
    private readonly DirectoryInfo directory = CreateRealTempDirectory();

    private static DirectoryInfo CreateRealTempDirectory()
    {
        var created = Directory.CreateTempSubdirectory("caravel-storage-");
        // macOS exposes /var through a link; fixtures must supply the real root just like an application.
        var current = Path.GetPathRoot(created.FullName)!;
        foreach (var part in Path.GetRelativePath(current, created.FullName).Split(Path.DirectorySeparatorChar))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return new DirectoryInfo(current);
    }

    [Fact]
    public async Task KeyedDisksStreamCreateReadAndDeleteWithoutOverwriting()
    {
        var root = Path.Combine(directory.FullName, "documents");
        using var services = new ServiceCollection().AddCaravelLocalDisk("documents", root).BuildServiceProvider();
        var disk = services.GetRequiredKeyedService<IStorageDisk>("documents");
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("document contents"));
        await disk.PutAsync("reports/first.txt", content);
        Assert.True(content.CanRead);
        await using (var read = await disk.OpenReadAsync("reports/first.txt"))
        using (var reader = new StreamReader(read))
            Assert.Equal("document contents", await reader.ReadToEndAsync());
        content.Position = 0;
        await Assert.ThrowsAsync<IOException>(() => disk.PutAsync("reports/first.txt", content).AsTask());
        Assert.Equal("document contents", await File.ReadAllTextAsync(Path.Combine(root, "reports", "first.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "reports")));
        await Assert.ThrowsAsync<IOException>(() => disk.DeleteAsync("reports").AsTask());
        await disk.DeleteAsync("reports/first.txt");
        await disk.DeleteAsync("reports/first.txt");
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "reports")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("nested\\..\\outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:\\rooted.txt")]
    [InlineData("\\\\host\\share\\file")]
    [InlineData("file.txt:stream")]
    [InlineData("nested//file")]
    [InlineData("./file")]
    [InlineData("con.txt")]
    [InlineData("CON .txt")]
    [InlineData("COM0.txt")]
    [InlineData("LPT1")]
    [InlineData("bad./file")]
    [InlineData("bad /file")]
    [InlineData("file\0.txt")]
    public async Task UnsafePathsAreRejectedBeforeCreatingDirectories(string path)
    {
        var root = Path.Combine(directory.FullName, "unused");
        var disk = new LocalStorageDisk(root);
        await Assert.ThrowsAsync<ArgumentException>(() => disk.PutAsync(path, Stream.Null).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => disk.OpenReadAsync(path).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => disk.DeleteAsync(path).AsTask());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DeletingAbsentFilesDoesNotRequireTheirParentDirectoriesToExist()
    {
        var missingRoot = Path.Combine(directory.FullName, "not-created");
        var newDisk = new LocalStorageDisk(missingRoot);
        await newDisk.DeleteAsync("reports/missing.txt");
        Assert.False(Directory.Exists(missingRoot));

        var existingDisk = new LocalStorageDisk(directory.FullName);
        await existingDisk.DeleteAsync("missing-parent/nested/missing.txt");
        Assert.Empty(Directory.GetFileSystemEntries(directory.FullName));
    }

    [Fact]
    public async Task CancellationAndInputFailuresDoNotPublishPartialFiles()
    {
        var disk = new LocalStorageDisk(directory.FullName);
        using var cancellation = new CancellationTokenSource();
        await using var source = new FailingStream(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.PutAsync("cancel.txt", source, cancellation.Token).AsTask());
        Assert.Empty(Directory.GetFiles(directory.FullName));
        await using var failed = new FailingStream(null);
        await Assert.ThrowsAsync<IOException>(() => disk.PutAsync("failure.txt", failed).AsTask());
        Assert.Empty(Directory.GetFiles(directory.FullName));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.OpenReadAsync("missing.txt", cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.DeleteAsync("missing.txt", cancellation.Token).AsTask());
    }

    [Fact]
    public async Task CompetingWritesPublishOneCompleteFile()
    {
        var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "données-测试")).FullName;
        var gate = new PublicationGate();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writes = new[] { Write("first"), Write("second") };
        try
        {
            await gate.BothReady.Task.WaitAsync(cancellation.Token);
            Assert.False(File.Exists(Path.Combine(root, "same.txt")));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            gate.Release.TrySetResult();
            var outcomes = await Task.WhenAll(writes);
            Assert.Single(outcomes, succeeded => succeeded);
            Assert.Contains(await File.ReadAllTextAsync(Path.Combine(root, "same.txt")), new[] { "first", "second" });
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            gate.Release.TrySetResult();
            await Task.WhenAll(writes);
        }

        async Task<bool> Write(string text)
        {
            // Separate disks must coordinate through atomic filesystem publication, not an instance lock.
            var disk = new LocalStorageDisk(root);
            await using var content = new CoordinatedStream(Encoding.UTF8.GetBytes(text), gate);
            try { await disk.PutAsync("same.txt", content, cancellation.Token); return true; }
            catch (IOException) { return false; }
        }
    }

    [LinkFact]
    public async Task DirectoryFileAndDanglingLinksCannotEscapeTheRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "root"));
        var outside = Directory.CreateDirectory(Path.Combine(directory.FullName, "outside"));
        var secret = Path.Combine(outside.FullName, "secret.txt");
        await File.WriteAllTextAsync(secret, "outside data");
        Directory.CreateSymbolicLink(Path.Combine(root.FullName, "linked"), outside.FullName);
        File.CreateSymbolicLink(Path.Combine(root.FullName, "linked.txt"), secret);
        File.CreateSymbolicLink(Path.Combine(root.FullName, "dangling.txt"), Path.Combine(outside.FullName, "missing.txt"));
        var disk = new LocalStorageDisk(root.FullName);
        foreach (var path in new[] { "linked/secret.txt", "linked/new/file.txt", "linked.txt", "dangling.txt" })
        {
            await Assert.ThrowsAsync<IOException>(() => disk.OpenReadAsync(path).AsTask());
            await Assert.ThrowsAsync<IOException>(() => disk.PutAsync(path, Stream.Null).AsTask());
            await Assert.ThrowsAsync<IOException>(() => disk.DeleteAsync(path).AsTask());
        }
        Assert.Throws<IOException>(() => new LocalStorageDisk(Path.Combine(root.FullName, "linked")));
        Assert.Throws<IOException>(() => new LocalStorageDisk(Path.Combine(root.FullName, "linked", "nested")));
        Assert.False(Directory.Exists(Path.Combine(outside.FullName, "new")));
        Assert.Equal("outside data", await File.ReadAllTextAsync(secret));
    }

    public void Dispose() => directory.Delete(recursive: true);

    private sealed class FailingStream(CancellationTokenSource? cancellation) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            if (cancellation is null) throw new IOException("Synthetic source failure.");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class PublicationGate
    {
        public int Arrived;
        public TaskCompletionSource BothReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CoordinatedStream(byte[] bytes, PublicationGate gate) : MemoryStream(bytes)
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            if (Interlocked.Increment(ref gate.Arrived) == 2) gate.BothReady.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }
}

public sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute()
    {
        var probe = Directory.CreateTempSubdirectory("caravel-storage-link-probe-");
        try
        {
            File.CreateSymbolicLink(Path.Combine(probe.FullName, "link"), Path.Combine(probe.FullName, "missing"));
        }
        catch (UnauthorizedAccessException) { Skip = "Symbolic-link creation requires privileges unavailable on this machine."; }
        catch (PlatformNotSupportedException) { Skip = "Symbolic links are unavailable on this platform."; }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
        { Skip = "Windows symbolic-link creation privilege is unavailable."; }
        finally { probe.Delete(recursive: true); }
    }
}
