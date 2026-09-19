using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellyfinMod.Services.Import;

/// <summary>
/// Validates path mappings and proves, without adding a torrent, that a mapped download folder can hardlink into the
/// libraries (P5.I2). The probe writes one dot-prefixed file into the download folder and one link of it into each
/// library root on the same mount, and removes both immediately.
/// </summary>
public sealed class ImportPathProbe(ILibraryManager library, UnixFileInspector files, MediaStorageIdentity mounts)
{
    /// <summary>The largest number of mappings one client may hold.</summary>
    public const int MaxMappings = 16;

    /// <summary>Validates an ordered mapping list; returns a stable code and a message naming the rule, or null.</summary>
    public (string Code, string Message)? Validate(IReadOnlyList<PathMappingRequest> mappings)
    {
        if (mappings.Count > MaxMappings) return ("too_many_mappings", $"A client can have at most {MaxMappings} path mappings.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var client = ImportPaths.Normalize(mapping.ClientPathPrefix);
            var local = ImportPaths.Normalize(mapping.LocalPathPrefix);
            if (client is null || local is null)
                return ("invalid_mapping", "Both prefixes must be absolute paths without '..' segments.");
            if (!seen.Add(client)) return ("duplicate_mapping", "Two mappings use the same client prefix.");
            foreach (var root in Roots().Select(root => root.Root))
            {
                if (ImportPaths.Within(root, local))
                    return ("mapping_inside_library",
                        "A mapped download folder must not be inside a library folder; the library would scan partial downloads.");
                if (ImportPaths.Within(local, root))
                    return ("mapping_contains_library", "A mapped download folder must not contain a library folder.");
            }
        }

        return null;
    }

    /// <summary>Probes one local folder: existence, mount identity, and a real <c>link(2)</c> into each same-mount root.</summary>
    public ImportPathTestDto Probe(string? localPath)
    {
        if (localPath is null)
            return new(false, ImportReasons.PathUnmapped, "No mapping turns this client path into a local path.", null, false, null, []);
        var folder = localPath;
        if (files.TryInspect(localPath, out var asFile)) folder = Path.GetDirectoryName(asFile.CanonicalPath) ?? localPath;
        if (!files.TryGetDirectoryDevice(folder, out var canonical, out var device))
            return new(false, "local_path_missing", "The mapped folder does not exist or cannot be read.", localPath, false, null, []);
        var table = mounts.ReadMountTable();
        var mount = table.Capture(canonical);
        var results = new List<ImportPathLibraryDto>();
        string? probe = null;
        try
        {
            var probeName = ".jfmod-probe-" + Guid.NewGuid().ToString("N");
            try
            {
                using (new FileStream(Path.Combine(canonical, probeName), FileMode.CreateNew, FileAccess.Write)) { }
                probe = Path.Combine(canonical, probeName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                probe = null;
            }

            foreach (var (libraryId, name, root) in Roots())
            {
                var sameMount = files.TryGetDirectoryDevice(root, out var rootCanonical, out var rootDevice) &&
                    rootDevice == device && mount is not null && table.Capture(rootCanonical) == mount;
                string outcome;
                if (!sameMount) outcome = "not_same_mount";
                else if (probe is null) outcome = "source_not_writable";
                else
                {
                    var target = Path.Combine(rootCanonical, Path.GetFileName(probe));
                    var link = files.Link(probe, target);
                    outcome = link.Linked ? "linked" : link.CrossDevice ? "cross_device" : "failed_" + link.Errno;
                    if (link.Linked) TryDelete(target);
                }

                results.Add(new(libraryId, name, rootCanonical.Length > 0 ? rootCanonical : root, sameMount, outcome));
            }
        }
        finally
        {
            if (probe is not null) TryDelete(probe);
        }

        var linked = results.Any(result => result.LinkProbe == "linked");
        var sameMountOnly = !linked && probe is null && results.Any(result => result.SameMount);
        var ok = linked || sameMountOnly;
        return new(ok, ok ? "ok" : results.Count == 0 ? ImportReasons.LibraryRootMissing : ImportReasons.CrossFilesystem,
            linked ? "A hardlink from this folder into the listed libraries succeeded; the probe files were removed."
                : sameMountOnly ? "The folder shares a mount with the listed libraries. It is not writable, so no link was tried."
                : "No movie or TV library folder can receive a hardlink from this folder.",
            canonical, true, mount, results);
    }

    private IEnumerable<(Guid LibraryId, string Name, string Root)> Roots()
    {
        foreach (var folder in library.GetVirtualFolders() ?? [])
        {
            if (folder.CollectionType is not (CollectionTypeOptions.movies or CollectionTypeOptions.tvshows) ||
                !Guid.TryParse(folder.ItemId, out var id)) continue;
            foreach (var location in folder.Locations ?? [])
                if (ImportPaths.Normalize(files.TryCanonicalize(location, out var canonical) ? canonical : location) is { } root)
                    yield return (id, folder.Name ?? string.Empty, root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A probe file that cannot be removed is reported by its dot-prefixed name on the next probe.
        }
    }
}
