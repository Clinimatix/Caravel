using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace Caravel.Storage;

public interface IStorageDisk
{
    /// <summary>Creates a new file. Existing files are never overwritten; the input stream remains open.</summary>
    ValueTask PutAsync(string path, Stream content, CancellationToken cancellationToken = default);
    /// <summary>Opens a file for streaming reads. The caller owns and disposes the returned stream.</summary>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Deletes a single file if present. Directories are never deleted.</summary>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>A disk rooted in a trusted, application-owned directory. Filesystem access checks are not race-safe against hostile local users.</summary>
public sealed class LocalStorageDisk : IStorageDisk
{
    private readonly string root;

    public LocalStorageDisk(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The disk root must be an absolute path.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RejectLinks(this.root);
        RejectDirectoryFile(this.root);
    }

    public async ValueTask PutAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Resolve(path);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The storage path already exists.");
        var parent = Path.GetDirectoryName(destination)!;
        // Resolve has checked every existing ancestor before recursive creation.
        Directory.CreateDirectory(parent);
        RejectLinks(destination);
        var temporary = Path.Combine(parent, $".caravel-write-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            Publish(temporary, destination);
        }
        finally { if (created) File.Delete(temporary); }
    }

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = Resolve(path);
        Stream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = Resolve(path);
        if (Directory.Exists(file)) throw new IOException("A storage file path cannot refer to a directory.");
        try { File.Delete(file); }
        catch (DirectoryNotFoundException) { /* An absent parent also means the requested file is absent. */ }
        return ValueTask.CompletedTask;
    }

    private string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Portable ASCII names exclude device paths, alternate streams and OS-dependent normalization.
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ' ')) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9'))
                throw new ArgumentException("Use relative file paths with portable names; traversal, rooted paths and device names are not allowed.", nameof(path));
        }
        var file = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The file must remain within the disk root.", nameof(path));
        RejectLinks(file);
        return file;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Storage paths must not contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RejectDirectoryFile(string path)
    {
        if (File.Exists(path)) throw new IOException("The disk root must be a directory.");
    }

    private static void Publish(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temporary, destination, overwrite: false);
            return;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Local disk publication supports Windows, Linux and macOS.");

        // Unix File.Move(false) can check then rename over a competing writer. link creates only a new name.
        // The existing finally removes the temporary name after successful publication or failure.
        if (Link(temporary, destination) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Storage publication without replacement failed (native error {error}).", new Win32Exception(error));
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}

public static class StorageServiceExtensions
{
    public static IServiceCollection AddCaravelLocalDisk(this IServiceCollection services, string name, string root)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddKeyedSingleton<IStorageDisk>(name, new LocalStorageDisk(root));
        return services;
    }
}
