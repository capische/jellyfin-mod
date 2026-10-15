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

            if (folder is not null && files.TryCanonicalize(folder, out var canonical) &&
                roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal)))
                return canonical;
        }

        return null;
    }

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
