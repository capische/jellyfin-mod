using System.Runtime.InteropServices;

namespace JellyfinMod.Services;

/// <summary>Reads stable Linux file identity, size and hardlink information without changing the file.</summary>
public sealed partial class UnixFileInspector
{
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const uint StatxBasicStats = 0x7ff;
    private const uint StatxBirthTime = 0x800;
    private const uint StatxRequiredStats = 0x304;

    /// <summary>Inspects an existing regular file and resolves its final canonical path.</summary>
    public bool TryInspect(string path, out UnixFileSnapshot snapshot)
    {
        snapshot = default;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null) return false;
            var resolved = RealPath(fullPath);
            if (resolved is null) return false;
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                for (var offset = 0; offset < 256; offset += sizeof(long)) Marshal.WriteInt64(buffer, offset, 0);
                if (NativeMethods.Statx(AtFileDescriptorCurrentWorkingDirectory, resolved, 0,
                        StatxBasicStats | StatxBirthTime, buffer) != 0)
                    return false;
                var mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                if ((mask & StatxRequiredStats) != StatxRequiredStats) return false;
                var linkCount = unchecked((uint)Marshal.ReadInt32(buffer, 16));
                var size = unchecked((ulong)Marshal.ReadInt64(buffer, 40));
                snapshot = new UnixFileSnapshot(resolved, Identity(buffer), linkCount, size);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the canonical path and filesystem device of an existing directory with <c>statx</c>, so acquisition can
    /// prove that downloads and the library share one filesystem before any import relies on hardlinks
    /// (user decision 6).
    /// </summary>
    public bool TryGetDirectoryDevice(string path, out string canonicalPath, out string device)
    {
        canonicalPath = string.Empty;
        device = string.Empty;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var resolved = RealPath(Path.GetFullPath(path));
            if (resolved is null || !Directory.Exists(resolved)) return false;
            var buffer = Marshal.AllocHGlobal(256);
            try
            {
                for (var offset = 0; offset < 256; offset += sizeof(long)) Marshal.WriteInt64(buffer, offset, 0);
                if (NativeMethods.Statx(AtFileDescriptorCurrentWorkingDirectory, resolved, 0, StatxBasicStats, buffer) != 0)
                    return false;
                var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
                if ((mode & 0xF000) != 0x4000) return false;
                var deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
                var deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
                canonicalPath = resolved;
                device = $"{deviceMajor:x8}:{deviceMinor:x8}";
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Resolves an existing path through every symbolic-link segment.</summary>
    public bool TryCanonicalize(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            canonicalPath = RealPath(Path.GetFullPath(path)) ?? string.Empty;
            return canonicalPath.Length > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports whether a path exists. Only a missing file or directory counts as absent; permission or
    /// I/O errors are unknown, so an unreadable mount is never mistaken for a removed file.
    /// </summary>
    public PathPresence Probe(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return PathPresence.Present;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathPresence.Absent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return PathPresence.Unknown;
        }
    }

    /// <summary>
    /// Reports whether this process may remove the file: its directory must be writable and searchable.
    /// A read-only mount fails this check even for root.
    /// </summary>
    public bool CanUnlink(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        return !string.IsNullOrEmpty(directory) && NativeMethods.Access(directory, AccessWrite | AccessExecute) == 0;
    }

    /// <summary>
    /// Creates a hardlink with <c>link(2)</c> (P5.I4). Never copies: a failure is reported with its errno and nothing
    /// else is attempted.
    /// </summary>
    public HardlinkResult Link(string source, string destination)
    {
        if (!OperatingSystem.IsLinux()) return new(false, -1);
        return NativeMethods.Link(source, destination) == 0
            ? new(true, 0)
            : new(false, Marshal.GetLastPInvokeError());
    }

    /// <summary>Reads free and total bytes of the filesystem holding <paramref name="path"/> with <c>statvfs</c> (P6.M1).</summary>
    public bool TryGetFreeSpace(string path, out ulong freeBytes, out ulong totalBytes)
    {
        freeBytes = totalBytes = 0;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path)) return false;
        // struct statvfs on 64-bit Linux: f_bsize, f_frsize, f_blocks, f_bfree, f_bavail, ... (all 8 bytes wide).
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var offset = 0; offset < 256; offset += sizeof(long)) Marshal.WriteInt64(buffer, offset, 0);
            if (NativeMethods.StatVfs(path, buffer) != 0) return false;
            var fragment = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            if (fragment == 0) fragment = unchecked((ulong)Marshal.ReadInt64(buffer, 0));
            var blocks = unchecked((ulong)Marshal.ReadInt64(buffer, 16));
            var available = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            totalBytes = blocks * fragment;
            freeBytes = available * fragment;
            return totalBytes > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Removes one empty directory the caller created; anything else is left alone.</summary>
    public bool RemoveEmptyDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        return NativeMethods.RemoveDirectory(path) == 0;
    }

    private const int AccessWrite = 2;
    private const int AccessExecute = 1;
    private const int AtSymlinkNoFollow = 0x100;
    private const int OpenPath = 0x200000;
    private const int ErrorNoEntry = 2;

    // O_DIRECTORY, O_NOFOLLOW and O_CLOEXEC differ between x86-64 and the generic (ARM) Linux ABI.
    private static int DirectoryFlags => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86
        ? OpenPath | 0x10000 | 0x20000 | 0x80000
        : OpenPath | 0x4000 | 0x8000 | 0x80000;

    /// <summary>
    /// Unlinks exactly the verified file (P3.T12). Every directory from <c>/</c> down is opened relative to its
    /// parent without following symbolic links, so a directory swapped for a symlink after the check cannot
    /// redirect the unlink; the final name must still be the same device and inode, so a same-name
    /// replacement is detected instead of deleted.
    /// </summary>
    public PinnedUnlinkResult UnlinkPinned(string canonicalPath, string expectedPhysicalIdentity)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(canonicalPath))
            return PinnedUnlinkResult.Failed("Pinned unlink needs Linux and a canonical path.");
        var segments = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            return PinnedUnlinkResult.Failed("The canonical path is not a file path.");
        var directory = NativeMethods.Open("/", DirectoryFlags, 0);
        if (directory < 0) return PinnedUnlinkResult.Failed($"Opening / failed ({Marshal.GetLastPInvokeError()}).");
        try
        {
            foreach (var segment in segments[..^1])
            {
                var next = NativeMethods.OpenAt(directory, segment, DirectoryFlags, 0);
                var error = Marshal.GetLastPInvokeError();
                NativeMethods.Close(directory);
                directory = next;
                if (directory < 0)
                    return error == ErrorNoEntry
                        ? PinnedUnlinkResult.Replaced("A parent directory of the media file is gone.")
                        : PinnedUnlinkResult.Replaced($"A parent directory changed or became a link ({error}).");
            }

            var name = segments[^1];
            if (!TryReadIdentity(directory, name, out var identity, out var isRegular))
                return PinnedUnlinkResult.Replaced("The media file is gone or unreadable at unlink time.");
            if (!isRegular || !string.Equals(identity, expectedPhysicalIdentity, StringComparison.Ordinal))
                return PinnedUnlinkResult.Replaced("A different file now has the media file's name.");
            if (NativeMethods.UnlinkAt(directory, name, 0) != 0)
                return PinnedUnlinkResult.Failed($"unlinkat failed ({Marshal.GetLastPInvokeError()}).");
            return PinnedUnlinkResult.Unlinked;
        }
        finally
        {
            if (directory >= 0) NativeMethods.Close(directory);
        }
    }

    /// <summary>
    /// Device and inode, plus the birth time where the filesystem records one (else the modification time):
    /// a freed inode number is often reused at once, so device and inode alone cannot tell a same-name
    /// replacement from the original (P3.T12).
    /// </summary>
    private static string Identity(IntPtr statx)
    {
        var mask = unchecked((uint)Marshal.ReadInt32(statx, 0));
        var inode = unchecked((ulong)Marshal.ReadInt64(statx, 32));
        var deviceMajor = unchecked((uint)Marshal.ReadInt32(statx, 136));
        var deviceMinor = unchecked((uint)Marshal.ReadInt32(statx, 140));
        var hasBirth = (mask & StatxBirthTime) != 0;
        var timeOffset = hasBirth ? 80 : 112;
        var seconds = Marshal.ReadInt64(statx, timeOffset);
        var nanoseconds = unchecked((uint)Marshal.ReadInt32(statx, timeOffset + 8));
        return $"{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}:{(hasBirth ? 'b' : 'm')}{seconds:x}.{nanoseconds:x8}";
    }

    private static bool TryReadIdentity(int directory, string name, out string identity, out bool isRegular)
    {
        identity = string.Empty;
        isRegular = false;
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var offset = 0; offset < 256; offset += sizeof(long)) Marshal.WriteInt64(buffer, offset, 0);
            if (NativeMethods.Statx(directory, name, AtSymlinkNoFollow, StatxBasicStats | StatxBirthTime, buffer) != 0)
                return false;
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            isRegular = (mode & 0xF000) == 0x8000;
            identity = Identity(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? RealPath(string path)
    {
        var pointer = NativeMethods.RealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            NativeMethods.Free(pointer);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Statx(int directoryFileDescriptor, string path, int flags, uint mask, IntPtr buffer);

        [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial IntPtr RealPath(string path, IntPtr resolvedPath);

        [LibraryImport("libc", EntryPoint = "free")]
        internal static partial void Free(IntPtr pointer);

        [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Access(string path, int mode);

        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Open(string path, int flags, uint mode);

        [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int OpenAt(int directoryFileDescriptor, string path, int flags, uint mode);

        [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int UnlinkAt(int directoryFileDescriptor, string path, int flags);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fileDescriptor);

        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Link(string existing, string created);

        [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int StatVfs(string path, IntPtr buffer);

        [LibraryImport("libc", EntryPoint = "rmdir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int RemoveDirectory(string path);
    }
}

/// <summary>The outcome of one <c>link(2)</c> call.</summary>
/// <param name="Linked">True when the new name was created.</param>
/// <param name="Errno">The errno when it was not.</param>
public readonly record struct HardlinkResult(bool Linked, int Errno)
{
    /// <summary>EXDEV: the two paths are on different mounts.</summary>
    public bool CrossDevice => Errno == 18;

    /// <summary>EEXIST: the destination name already exists.</summary>
    public bool Exists => Errno == 17;
}

/// <summary>The outcome of a pinned unlink.</summary>
/// <param name="Removed">True when exactly the verified file was unlinked.</param>
/// <param name="IsReplacement">True when the path now leads to a different file or directory chain.</param>
/// <param name="Detail">Why nothing was removed.</param>
public readonly record struct PinnedUnlinkResult(bool Removed, bool IsReplacement, string? Detail)
{
    /// <summary>The verified file was unlinked.</summary>
    public static PinnedUnlinkResult Unlinked => new(true, false, null);

    /// <summary>The path leads somewhere else now; nothing was removed.</summary>
    public static PinnedUnlinkResult Replaced(string detail) => new(false, true, detail);

    /// <summary>The unlink could not be attempted or failed; nothing was removed.</summary>
    public static PinnedUnlinkResult Failed(string detail) => new(false, false, detail);
}

/// <summary>Whether a path exists, is gone, or could not be determined.</summary>
public enum PathPresence
{
    /// <summary>The path exists.</summary>
    Present,

    /// <summary>The path does not exist (ENOENT).</summary>
    Absent,

    /// <summary>Existence could not be determined, for example because the mount is unreadable.</summary>
    Unknown
}

/// <summary>One immutable observation of a Linux regular file.</summary>
/// <param name="CanonicalPath">The path after resolving all symbolic-link segments.</param>
/// <param name="PhysicalIdentity">The filesystem device and inode identity.</param>
/// <param name="HardlinkCount">The inode's current hardlink count.</param>
/// <param name="LogicalBytes">The file's logical size.</param>
public readonly record struct UnixFileSnapshot(
    string CanonicalPath,
    string PhysicalIdentity,
    uint HardlinkCount,
    ulong LogicalBytes);
