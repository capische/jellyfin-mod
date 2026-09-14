namespace JellyfinMod.Services;

/// <summary>Captures the Linux mount that contains a native media path.</summary>
public sealed class MediaStorageIdentity(string mountInfoPath = "/proc/self/mountinfo")
{
    /// <summary>Returns a stable identity for the most specific mount containing the path.</summary>
    public string? Capture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path);
        try
        {
            var mount = ReadMounts().Where(candidate => Contains(candidate.MountPoint, fullPath))
                .OrderByDescending(candidate => candidate.MountPoint.Length).FirstOrDefault();
            return mount is null ? null : mount.Identity;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Proves that a previously observed path still belongs to the same readable library mount.</summary>
    public bool IsCurrent(
        string? path,
        string? expectedIdentity,
        IReadOnlyList<string> libraryLocations,
        out string detail)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedIdentity))
        {
            detail = "A missing binding has no recorded media-path mount identity; run a positive reconciliation first.";
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var libraryRoot = libraryLocations.Where(location => !string.IsNullOrWhiteSpace(location))
            .Select(Path.GetFullPath).Where(location => Contains(location, fullPath))
            .OrderByDescending(location => location.Length).FirstOrDefault();
        if (libraryRoot is null)
        {
            detail = $"The recorded media path is outside the library's configured locations: {path}";
            return false;
        }

        string? currentIdentity;
        try
        {
            currentIdentity = Capture(fullPath);
        }
        catch (Exception)
        {
            detail = $"The current media mount could not be identified: {path}";
            return false;
        }

        if (!string.Equals(currentIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            detail = $"The media mount changed or is unavailable: {path}";
            return false;
        }

        try
        {
            if (!Directory.Exists(libraryRoot))
            {
                detail = $"Library storage is unavailable: {libraryRoot}";
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(libraryRoot).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception)
        {
            detail = $"Library storage could not be enumerated: {libraryRoot}";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    private IReadOnlyList<MountInfo> ReadMounts()
    {
        var mounts = new List<MountInfo>();
        foreach (var line in File.ReadLines(mountInfoPath))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var before = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var after = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (before.Length < 5 || after.Length < 2) continue;
            var root = Unescape(before[3]);
            var mountPoint = Path.GetFullPath(Unescape(before[4]));
            var fileSystem = Unescape(after[0]);
            var source = Unescape(after[1]);
            mounts.Add(new(mountPoint, $"{before[2]}|{root}|{mountPoint}|{fileSystem}|{source}"));
        }

        return mounts;
    }

    private static bool Contains(string root, string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        if (trimmed.Length == 0) trimmed = Path.DirectorySeparatorChar.ToString();
        return string.Equals(trimmed, path, StringComparison.Ordinal) ||
            trimmed == Path.DirectorySeparatorChar.ToString() ||
            path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Unescape(string value) => value.Replace(@"\040", " ", StringComparison.Ordinal)
        .Replace(@"\011", "\t", StringComparison.Ordinal)
        .Replace(@"\012", "\n", StringComparison.Ordinal)
        .Replace(@"\134", "\\", StringComparison.Ordinal);

    private sealed record MountInfo(string MountPoint, string Identity);
}
