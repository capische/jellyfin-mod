using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Import;

/// <summary>
/// Where an episode of a series lives on disk, by the import's own rules (PHASE5 <i>Destination naming</i>): a library's
/// readable roots, the series folder its bound episodes are in, and an existing season folder. Shared by the import and the
/// placeholder writer (P1.FU24), so a placeholder sits exactly where the import will put the episode's file.
/// </summary>
internal static class SeriesFolders
{
    /// <summary>The canonical, existing folders of a movie or TV library; empty when none can be read.</summary>
    public static IReadOnlyList<string> LibraryRoots(ILibraryManager library, UnixFileInspector files, Guid libraryId)
    {
        try
        {
            var folder = library.GetVirtualFolders()?.FirstOrDefault(value =>
                Guid.TryParse(value.ItemId, out var id) && id == libraryId && value.CollectionType is
                    CollectionTypeOptions.movies or CollectionTypeOptions.tvshows);
            return (folder?.Locations ?? []).Select(location => files.TryCanonicalize(location, out var canonical) ? canonical : null)
                .OfType<string>().Where(Directory.Exists).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// The canonical folder of the series an entry's bound episodes are in, inside one of <paramref name="roots"/> and never a
    /// root itself; null when no bound episode leads to one.
    /// </summary>
    public static async Task<string?> BoundSeriesFolderAsync(ModDbContext database, ILibraryManager library, UnixFileInspector files,
        Guid entryId, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var bindings = await (from binding in database.EpisodeBindings.AsNoTracking()
                join tracked in database.Episodes.AsNoTracking() on binding.EpisodeId equals tracked.Id
                where tracked.EntryId == entryId
                select binding).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var binding in bindings.OrderBy(binding => binding.SeriesItemId))
        {
            string? folder = null;
            try
            {
                if (library.GetItemById(binding.SeriesItemId) is { Path: { Length: > 0 } seriesPath }) folder = seriesPath;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                folder = null;
            }

            if (folder is null && binding.MediaPath is { } media)
            {
                var parent = Path.GetDirectoryName(media);
                var name = parent is null ? null : Path.GetFileName(parent);
                folder = name is not null && (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase)) ? Path.GetDirectoryName(parent) : parent;
            }

            if (InsideRoots(files, folder, roots) is { } canonical) return canonical;
        }

        // No episode of it is bound because every file was reclaimed (P1.FU25): the series item Jellyfin still holds for it, kept
        // by the placeholders retention left in its folder, so its episodes return to the same folder.
        if (bindings.Count == 0)
        {
            var seriesItemId = await database.Entries.AsNoTracking().Where(entry => entry.Id == entryId && entry.State == FileState.Reclaimed)
                .Select(entry => entry.JellyfinItemId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (seriesItemId is { } id && library.GetItemById(id) is MediaBrowser.Controller.Entities.TV.Series { Path: { Length: > 0 } path } &&
                    InsideRoots(files, path, roots) is { } reclaimed)
                    return reclaimed;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return null;
            }

            // A series Jellyfin holds with no file of its own (P1.FU26): its placeholders' folder, or an empty series folder the
            // user made, bound to the entry as its series item. Only a folder that can be written takes new files; a read-only one
            // keeps the import's own new folder, as before.
            var seriesItems = await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entryId)
                .Select(binding => binding.JellyfinItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in seriesItems.Order())
            {
                try
                {
                    if (library.GetItemById(id) is MediaBrowser.Controller.Entities.TV.Series { Path: { Length: > 0 } path } &&
                        InsideRoots(files, path, roots) is { } held && files.CanCreateIn(held))
                        return held;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // An item that cannot be read gives no folder.
                }
            }

            // A title folder of the entry's, made by the import or for its placeholders before Jellyfin bound it (P1.FU26).
            var tmdbId = await database.Entries.AsNoTracking().Where(entry => entry.Id == entryId).Select(entry => entry.TmdbId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (tmdbId > 0 && TitleFolderByTmdbId(files, roots, tmdbId) is { } titled) return titled;
        }

        return null;
    }

    /// <summary>
    /// The first folder directly in one of <paramref name="roots"/> whose name carries the title's TMDB tag
    /// (<c>[tmdbid-N]</c>, the shape of every folder the import and the placeholders make), and that is a real folder, not a
    /// link; null when there is none (P1.FU26).
    /// </summary>
    public static string? TitleFolderByTmdbId(UnixFileInspector files, IReadOnlyList<string> roots, int tmdbId)
    {
        var tag = "[tmdbid-" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
        foreach (var root in roots)
        {
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root, "*" + tag + "*"))
                {
                    if (!Path.GetFileName(folder).Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (files.TryCanonicalize(folder, out var canonical) && canonical == folder) return folder;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An unreadable root has no folder to offer.
            }
        }

        return null;
    }

    /// <summary>Whether a folder's name carries a TMDB tag (<c>[tmdbid-N]</c>): a title folder of the import's or the placeholders'.</summary>
    public static bool IsTitleFolderName(string name) => name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical folder when it lies inside one of the roots and is not a root itself; otherwise null.</summary>
    private static string? InsideRoots(UnixFileInspector files, string? folder, IReadOnlyList<string> roots) =>
        folder is not null && files.TryCanonicalize(folder, out var canonical) &&
        roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
            !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal))
            ? canonical
            : null;

    /// <summary>The series' existing folder for a season (<c>Season 3</c>, <c>Season 03</c>, <c>Specials</c>), or null.</summary>
    public static string? ExistingSeasonFolder(string seriesFolder, int season)
    {
        try
        {
            if (!Directory.Exists(seriesFolder)) return null;
            return Directory.EnumerateDirectories(seriesFolder).FirstOrDefault(directory =>
            {
                var name = Path.GetFileName(directory);
                return season == 0
                    ? string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(name, "Season 0", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(name, "Season 00", StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) &&
                      int.TryParse(name[7..].Trim(), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var number) && number == season;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
