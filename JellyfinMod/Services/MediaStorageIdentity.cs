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

    /// <summary>
    /// Returns the path's current identity when only the device number or mount source changed since it was
    /// recorded, as after a reboot or USB re-enumeration (P2.R6). Returns null when nothing changed or when
    /// the mount point, root or filesystem type differ.
    /// </summary>
    public string? Rebaseline(string? path, string? recordedIdentity)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(recordedIdentity)) return null;
        var current = Capture(path);
        if (current is null || string.Equals(current, recordedIdentity, StringComparison.Ordinal)) return null;
        var recorded = recordedIdentity.Split('|');
        var observed = current.Split('|');
        if (recorded.Length != 5 || observed.Length != 5) return null;
        return recorded[1] == observed[1] && recorded[2] == observed[2] && recorded[3] == observed[3]
            ? current
            : null;
    }

    /// <summary>Returns true when the path is inside one of the configured library locations.</summary>
    public static bool IsWithin(string? path, IReadOnlyList<string> locations)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        return locations.Where(location => !string.IsNullOrWhiteSpace(location))
            .Any(location => Contains(Path.GetFullPath(location), fullPath));
    }

    /// <summary>
    /// Proves absence without a trusted mount identity (P2.R6): the file is gone and its nearest existing
    /// ancestor directory is readable and not empty, so an empty unmounted mount point cannot pass.
    /// </summary>
    public static bool IsProvablyAbsent(string? path, out string detail)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "A missing binding has no recorded media path.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                detail = $"The media file still exists although Jellyfin no longer lists it: {path}";
                return false;
            }

            var ancestor = Path.GetDirectoryName(fullPath);
            while (ancestor is not null && !Directory.Exists(ancestor))
                ancestor = Path.GetDirectoryName(ancestor);
            if (ancestor is null || ancestor == Path.GetPathRoot(fullPath))
            {
                detail = $"No readable parent directory proves the media is gone: {path}";
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(ancestor).GetEnumerator();
            if (!entries.MoveNext())
            {
                detail = $"The nearest parent directory is empty and may be an unmounted mount point: {path}";
                return false;
            }
        }
        catch (Exception)
        {
            detail = $"The media path could not be checked: {path}";
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
