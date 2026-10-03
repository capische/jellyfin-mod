using System.Globalization;
using System.Text;

namespace JellyfinMod.Services.Import;

/// <summary>
/// The mounts this process sees, read from <c>/proc/self/mountinfo</c>, so retention can tell when the
/// same files are visible both in a download folder and inside a library through a bind mount: canonical paths and descriptor
/// paths do not show that two mounts expose one directory tree (Codex delta review 5, P1).
/// </summary>
public sealed class MountExposure
{
    private readonly IReadOnlyList<Mount> _mounts;

    private MountExposure(IReadOnlyList<Mount> mounts) => _mounts = mounts;

    /// <summary>One mount: its device, the directory of that device it exposes, and where.</summary>
    private sealed record Mount(string Device, string Root, string MountPoint, string FileSystem);

    /// <summary>Reads the table now; null when it cannot be read, which keeps every file.</summary>
    public static MountExposure? Read()
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) return null;
            var mounts = new List<Mount>();
            foreach (var line in File.ReadAllLines("/proc/self/mountinfo"))
            {
                // id parent major:minor root mount-point options [optional fields] - fstype source super-options
                var fields = line.Split(' ');
                var separator = Array.IndexOf(fields, "-");
                if (fields.Length < 5 || separator < 6 || separator + 1 >= fields.Length) return null;
                mounts.Add(new Mount(fields[2], Unescape(fields[3]), Unescape(fields[4]), fields[separator + 1]));
            }

            return mounts.Count == 0 ? null : new MountExposure(mounts);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a canonical path lives: the device and the path within that device's filesystem, through the mount that is
    /// visible at it (the last one mounted on the longest matching mount point). Null when no mount matches.
    /// </summary>
    public (string Device, string SourcePath, string FileSystem)? Locate(string canonicalPath)
    {
        Mount? best = null;
        foreach (var mount in _mounts)
            if (Within(mount.MountPoint, canonicalPath) && (best is null || mount.MountPoint.Length >= best.MountPoint.Length))
                best = mount;
        if (best is null) return null;
        var relative = best.MountPoint == "/" ? canonicalPath : canonicalPath[best.MountPoint.Length..];
        return (best.Device, Join(best.Root, relative), best.FileSystem);
    }

    private static bool Within(string folder, string path) =>
        folder == "/" || path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static string Join(string root, string relative) =>
        relative.Length == 0 ? root : root == "/" ? relative : root.TrimEnd('/') + relative;

    /// <summary>Undoes the octal escapes mountinfo uses for spaces, tabs, newlines and backslashes.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;
        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 3 < value.Length &&
                int.TryParse(value.AsSpan(index + 1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                bytes.Add(Convert.ToByte(value.Substring(index + 1, 3), 8));
                index += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[index].ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
