using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Import;

/// <summary>
/// The downloads JellyfinMod keeps on disk for the administrator: the files a seed release or a queue removal
/// recorded, and the seeding file of every release, with or without a list of its files. Nothing automatic
/// may unlink one (user decision 2026-10-02). Retention removes a library file; when that library file is a separate hardlink
/// of a recorded download, removing it is fine, but when the library path is the very directory entry of a recorded
/// download, reached through a bind mount or another name for its folder, retention must not unlink it (Codex delta reviews
/// 7, P1 2 and 9, P1 2).
/// </summary>
public static class RetainedDownloads
{
    private const string Unknown = "Whether it is a download JellyfinMod keeps on disk could not be established.";

    /// <summary>
    /// Why the library file at <paramref name="mediaPath"/> must not be unlinked automatically, or null. It must not be a
    /// recorded download's path, nor the same directory entry under another name: the same file name in a folder with the
    /// same device and inode, or the same place on the same device according to the mount table. The comparison is by
    /// directory entry, not by the file's identity, so a download replaced at its path is still protected. Anything that
    /// cannot be read refuses.
    /// </summary>
    public static async Task<string?> ProtectionAsync(ModDbContext database, UnixFileInspector files, string mediaPath,
        CancellationToken cancellationToken)
    {
        var manifests = (await database.SeedReleaseOperations.AsNoTracking()
                .Where(seed => seed.CleanupManifest != null).Select(seed => seed.CleanupManifest!).ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Concat(await database.ImportOperations.AsNoTracking().Where(operation => operation.CleanupManifest != null)
                .Select(operation => operation.CleanupManifest!).ToListAsync(cancellationToken).ConfigureAwait(false));
        // Every release's seeding file, listed or not and whatever the release's state: an older build's release detached
        // for a manual cleanup has no list at all, and an older completed release's download can still be on disk (final
        // review, finding 1). One that is gone is skipped below.
        var seeding = await database.SeedReleaseOperations.AsNoTracking()
            .Where(seed => seed.SeedingPath != string.Empty)
            .Select(seed => seed.SeedingPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recorded = manifests.SelectMany(manifest => TorrentDataRemoval.Deserialize(manifest) ?? []).Select(file => file.Path)
            .Concat(seeding).Distinct(StringComparer.Ordinal).ToList();
        if (recorded.Count == 0) return null;
        if (recorded.Contains(mediaPath, StringComparer.Ordinal)) return "It is a download JellyfinMod keeps on disk.";

        var target = files.InspectEntry(mediaPath);
        var mounts = MountExposure.Read();
        if (target.Error is not null || target.FolderIds.Count == 0 || mounts?.Locate(mediaPath) is not { } targetPlace)
            return Unknown;
        var name = Path.GetFileName(mediaPath);
        foreach (var path in recorded)
        {
            if (mounts.Locate(path) is not { } place) return Unknown;
            if (place.Device == targetPlace.Device && place.SourcePath == targetPlace.SourcePath)
                return $"Its library path is another name for the download {path}, which JellyfinMod keeps on disk.";
            if (Path.GetFileName(path) != name || Path.GetDirectoryName(path) is not { } folder) continue;
            // A folder confirmed gone (no such entry) cannot be this entry's folder; anything else that cannot be read, a
            // permission or I/O error included, refuses (Codex delta review 11, P1 1).
            switch (files.Probe(folder))
            {
                case PathPresence.Absent:
                    continue;
                case PathPresence.Unknown:
                    return Unknown;
            }

            if (!files.TryGetDirectoryId(folder, out var folderId)) return Unknown;
            if (folderId == target.FolderIds[^1])
                return $"Its library path is another name for the download {path}, which JellyfinMod keeps on disk.";
        }

        return null;
    }
}
