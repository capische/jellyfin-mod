using JellyfinMod.Services.Acquisition;

namespace JellyfinMod.Services.Import;

/// <summary>One torrent file checked when its torrent was detached: where it was, which file it was, and its torrent's folder.</summary>
/// <param name="Path">The canonical local path, outside every library when it was checked.</param>
/// <param name="PhysicalIdentity">The file's device and inode identity when it was checked.</param>
/// <param name="DownloadFolder">The torrent's download folder, which must still hold the file when it is cleaned up.</param>
/// <param name="Size">The file's size when it was checked; null in a list written before sizes were recorded.</param>
public sealed record VerifiedTorrentFile(string Path, string PhysicalIdentity, string DownloadFolder, long? Size = null);

/// <summary>
/// The files a torrent leaves behind when the client is asked to forget it. Nothing here deletes anything: the client only
/// forgets the torrent, and the files stay on disk, recorded on their operation, for the administrator to remove (Codex
/// delta review 5; user decisions 2026-10-02: 0.1.0.0 never deletes a download; a cleanup tool is planned for a later version).
/// </summary>
public static class TorrentDataRemoval
{
    /// <summary>
    /// Inspects every resolved data path of a torrent now. Returns null when one exists but cannot be inspected; a wanted
    /// file not yet on disk is left out.
    /// </summary>
    public static IReadOnlyList<VerifiedTorrentFile>? Inspect(ClientTorrentStatus torrent, IReadOnlyList<string> resolved,
        UnixFileInspector files)
    {
        var result = new List<VerifiedTorrentFile>(resolved.Count);
        for (var index = 0; index < resolved.Count; index++)
        {
            var path = resolved[index];
            if (!files.TryInspect(path, out var snapshot))
            {
                if (files.Probe(path) == PathPresence.Absent) continue;
                return null;
            }

            // The torrent's own relative name, removed from the end of the path, leaves its download folder.
            var depth = index < torrent.Files.Count
                ? torrent.Files[index].Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Length
                : 1;
            var folder = snapshot.CanonicalPath;
            for (var level = 0; level < depth; level++) folder = System.IO.Path.GetDirectoryName(folder) ?? "/";
            result.Add(new VerifiedTorrentFile(snapshot.CanonicalPath, snapshot.PhysicalIdentity, folder, (long)snapshot.LogicalBytes));
        }

        return result;
    }

    /// <summary>A list of kept files as it is stored on its operation, so a restart keeps it.</summary>
    public static string Serialize(IReadOnlyList<VerifiedTorrentFile> files) => System.Text.Json.JsonSerializer.Serialize(files);

    /// <summary>A stored list of kept files, or null when there is none or it cannot be read.</summary>
    public static IReadOnlyList<VerifiedTorrentFile>? Deserialize(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<VerifiedTorrentFile>>(manifest);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
