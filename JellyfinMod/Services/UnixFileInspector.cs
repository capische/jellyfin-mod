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
            if (resolved is null || NativeMethods.Statx(AtFileDescriptorCurrentWorkingDirectory, resolved, 0,
                    StatxBasicStats, out var stat) != 0 || (stat.Mask & StatxRequiredStats) != StatxRequiredStats)
                return false;
            snapshot = new UnixFileSnapshot(resolved,
                $"{stat.DeviceMajor:x8}:{stat.DeviceMinor:x8}:{stat.Inode:x16}", stat.LinkCount, stat.Size);
            return true;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Reserved;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModifyTime;
        public uint RawDeviceMajor;
        public uint RawDeviceMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoAlignment;
        public uint DirectIoOffsetAlignment;
        public ulong Subvolume;
        public uint AtomicWriteUnitMin;
        public uint AtomicWriteUnitMax;
        public uint AtomicWriteSegmentsMax;
        public uint DirectIoReadOffsetAlignment;
        public uint AtomicWriteUnitMaxOptimal;
        public uint SpareOne;
        public fixed ulong Spare[8];
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Statx(int directoryFileDescriptor, string path, int flags, uint mask, out StatxBuffer buffer);

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
