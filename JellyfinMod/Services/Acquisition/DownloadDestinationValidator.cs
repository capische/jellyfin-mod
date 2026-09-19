using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellyfinMod.Services.Acquisition;

/// <summary>The outcome of a download-destination check.</summary>
public sealed record DestinationCheck(bool Ok, string Code, string Message, IReadOnlyList<Guid> SameFilesystemLibraryIds);

/// <summary>
/// Proves that the download directory shares one filesystem and one mount with movie/TV library roots, and sits
/// outside every watched library folder, so Phase 5 can import by hardlink with no copy fallback (user decision 6).
/// </summary>
/// <remarks>
/// Two bind mounts of the same filesystem report the same device, but <c>link(2)</c> across them still fails, so
/// the mount is compared as well as the <c>statx</c> device.
/// </remarks>
public sealed class DownloadDestinationValidator(ILibraryManager library, UnixFileInspector files, MediaStorageIdentity mounts)
{
    /// <summary>Checks a plugin-visible download directory against the configured libraries.</summary>
    public DestinationCheck Check(string localDirectory)
    {
        if (!files.TryGetDirectoryDevice(localDirectory, out var directory, out var device))
            return new(false, "destination_missing", "The download folder does not exist or is not a readable directory.", []);
        var table = mounts.ReadMountTable();
        var mount = table.Capture(directory);
        var compatible = new List<Guid>();
        foreach (var folder in library.GetVirtualFolders() ?? [])
        {
            foreach (var location in folder.Locations ?? [])
            {
                if (!files.TryCanonicalize(location, out var root)) continue;
                if (MediaStorageIdentity.Contains(root, directory))
                    return new(false, "destination_inside_library",
                        "The download folder is inside a watched library folder; the library would scan partial downloads.", []);
                if (MediaStorageIdentity.Contains(directory, root))
                    return new(false, "library_inside_destination", "A library folder is inside the download folder.", []);
                if (folder.CollectionType is not (CollectionTypeOptions.movies or CollectionTypeOptions.tvshows) ||
                    !Guid.TryParse(folder.ItemId, out var libraryId)) continue;
                if (files.TryGetDirectoryDevice(root, out _, out var rootDevice) && rootDevice == device &&
                    mount is not null && table.Capture(root) == mount && !compatible.Contains(libraryId))
                    compatible.Add(libraryId);
            }
        }

        return compatible.Count == 0
            ? new(false, "destination_not_same_filesystem",
                "No movie or TV library root shares the download folder's filesystem and mount, so imports could not hardlink.", [])
            : new(true, "ok", "The download folder shares a filesystem with the listed libraries.", compatible);
    }

    /// <summary>Returns true when the target library still shares the download directory's filesystem and mount.</summary>
    public bool Supports(string localDirectory, Guid libraryId) => Check(localDirectory) is { Ok: true } check &&
        check.SameFilesystemLibraryIds.Contains(libraryId);
}
