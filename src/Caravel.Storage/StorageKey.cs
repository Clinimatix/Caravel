namespace Caravel.Storage;

/// <summary>Portable relative object names shared by storage disks; not an authorization boundary.</summary>
public static class StorageKey
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
        return string.Join('/', parts);
    }
}
