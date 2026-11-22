using System.Text;

namespace SecureSync.Core.Storage;

/// <summary>
/// Names come out of decrypted records, which may have been written on another device. These helpers enforce
/// Windows naming rules and make sure a name can never escape the destination folder when decrypted to disk.
/// </summary>
public static class PathSafety
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidChars = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':'];

    public const int MaxNameLength = 200;

    /// <summary>Returns why a name can't be used for a file or folder, or null if it's fine.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
        if (name.Length > MaxNameLength) return "That name is too long.";
        if (name.Any(c => Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c)))
            return "A name can't contain any of these characters:  \\ / : * ? \" < > |";
        if (name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.'))
            return "A name can't start or end with a space, or end with a period.";
        if (ReservedNames.Contains(name.Split('.')[0])) return $"\"{name}\" is reserved by Windows.";
        return null;
    }

    /// <summary>Turns any name into one that is safe as a single path segment on Windows.</summary>
    public static string SanitizeSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c) ? '_' : c);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result == "." || result == "..") return "unnamed";
        if (ReservedNames.Contains(result.Split('.')[0])) result = "_" + result;
        return result.Length > MaxNameLength ? result[..MaxNameLength] : result;
    }

    /// <summary>Maps a chain of folder names plus a file name to a safe path under <paramref name="root"/>.</summary>
    public static string ToSafeLocalPath(string root, IEnumerable<string> folders, string name)
    {
        var segments = folders.Select(SanitizeSegment).Append(SanitizeSegment(name));
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        var rootWithSep = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new VaultException("Refusing to write outside the destination folder.");
        return combined;
    }

    /// <summary>Returns "name (2).ext", "name (3).ext", ... until <paramref name="exists"/> is false.</summary>
    public static string MakeUnique(string name, Func<string, bool> exists, bool isFolder = false)
    {
        if (!exists(name)) return name;
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isFolder ? "" : Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }
}
