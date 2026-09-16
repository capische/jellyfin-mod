using System.Runtime.InteropServices;

namespace JellyfinMod.Services;

/// <summary>Reads stable Linux file identity, size and hardlink information without changing the file.</summary>
public sealed partial class UnixFileInspector
{
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const uint StatxBasicStats = 0x7ff;
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
                        StatxBasicStats, buffer) != 0)
                    return false;
                var mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                if ((mask & StatxRequiredStats) != StatxRequiredStats) return false;
                var linkCount = unchecked((uint)Marshal.ReadInt32(buffer, 16));
                var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
                var size = unchecked((ulong)Marshal.ReadInt64(buffer, 40));
                var deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
                var deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
                snapshot = new UnixFileSnapshot(resolved,
                    $"{deviceMajor:x8}:{deviceMinor:x8}:{inode:x16}", linkCount, size);
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
    }
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
