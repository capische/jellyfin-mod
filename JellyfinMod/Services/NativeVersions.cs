using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace JellyfinMod.Services;

/// <summary>One file Jellyfin plays as a version of a movie or episode (V1).</summary>
/// <param name="Item">The version's own native item.</param>
/// <param name="IsMain">Whether this is the main item every other version belongs to.</param>
/// <param name="IsLinked">Whether an administrator merged it in (Group versions) rather than Jellyfin finding it in the folder.</param>
/// <param name="SeasonNumber">An episode version's own season number.</param>
/// <param name="FirstEpisode">An episode version's own first episode number.</param>
/// <param name="LastEpisode">An episode version's own last episode number: larger than the first for a multi-episode file.</param>
internal sealed record NativeVersion(Video Item, bool IsMain, bool IsLinked, int? SeasonNumber, int? FirstEpisode, int? LastEpisode)
{
    /// <summary>Gets the file this version plays.</summary>
    public string Path => Item.Path;

    /// <summary>Gets whether this version is a multi-part (stacked) file, which is never unlinked (C17).</summary>
    public bool IsStacked => Item.AdditionalParts is { Length: > 0 };

    /// <summary>Gets whether this version holds more than one episode (<c>S01E01-E02</c>).</summary>
    public bool IsMultiEpisode => LastEpisode > FirstEpisode;
}

/// <summary>Every version of one main item, and why the group is refused when its members disagree (V1).</summary>
/// <param name="Main">The main (primary) item.</param>
/// <param name="Versions">Every version, the main first.</param>
/// <param name="Conflict">Null, or why the versions are not the same title or episode.</param>
internal sealed record NativeVersionSet(Video Main, IReadOnlyList<NativeVersion> Versions, string? Conflict)
{
    /// <summary>Gets the versions other than the main item.</summary>
    public IEnumerable<NativeVersion> Extras => Versions.Where(version => !version.IsMain);
}

/// <summary>
/// Lists the versions Jellyfin 12 plays for a movie or an episode and checks that they are one title (V1, analysis C2–C4,
/// C17). The list is the one <c>GetMediaSources(false)</c> builds (v12.0 <c>Video.GetAllItemsForMediaSources</c>): the
/// main item, the versions merged into it (<c>GetLinkedAlternateVersions</c>) and the file versions of each
/// (<c>GetLocalAlternateVersionIds</c>). It is assembled from those library-manager members, so reading it does not probe
/// media streams, attachments or segments for every source.
/// </summary>
internal static class NativeVersions
{
    /// <summary>The reason a version group is refused.</summary>
    public const string IdentityConflict = "version_identity_conflict";

    /// <summary>Whether the item has, or is, a version of something, from what the loaded item itself carries.</summary>
    public static bool HasVersions(Video video) =>
        video.LocalAlternateVersions is { Length: > 0 } || video.LinkedAlternateVersions is { Length: > 0 } ||
        JellyfinNativeTitleSource.PrimaryVersionId(video).HasValue;

    /// <summary>The main item a version belongs to; the item itself when it is its own main item or the main cannot be read.</summary>
    public static Video MainOf(ILibraryManager library, Video video) =>
        JellyfinNativeTitleSource.PrimaryVersionId(video) is { } id && library.GetItemById(id) is Video { Path.Length: > 0 } main
            ? main
            : video;

    /// <summary>Reads every version of <paramref name="main"/>; <paramref name="libraryId"/>, when given, must hold each merged one.</summary>
    public static NativeVersionSet Read(ILibraryManager library, Video main, Guid? libraryId)
    {
        var grouped = new List<(Video Item, bool Linked)> { (main, false) };
        grouped.AddRange(library.GetLinkedAlternateVersions(main).Where(version => version is not null).Select(version => (version, true)));
        var all = new List<(Video Item, bool Linked)>(grouped);
        foreach (var (video, linked) in grouped)
        {
            foreach (var id in library.GetLocalAlternateVersionIds(video))
            {
                // Jellyfin drops a source whose item it has not written yet; so does this list.
                if (library.GetItemById(id) is Video local) all.Add((local, linked));
            }
        }

        var versions = all.Where(version => !string.IsNullOrWhiteSpace(version.Item.Path))
            .DistinctBy(version => version.Item.Id)
            .Select(version => Describe(version.Item, version.Item.Id.Equals(main.Id), version.Linked))
            .OrderBy(version => version.IsMain ? 0 : 1).ThenBy(version => version.Path, StringComparer.Ordinal)
            .ToArray();
        return new(main, versions, Check(library, versions, libraryId));
    }

    private static NativeVersion Describe(Video item, bool isMain, bool linked)
    {
        if (item is not Episode episode) return new(item, isMain, linked, null, null, null);
        var last = episode.IndexNumberEnd is { } end && end > (episode.IndexNumber ?? end) ? end : episode.IndexNumber;
        return new(item, isMain, linked, episode.ParentIndexNumber, episode.IndexNumber, last);
    }

    /// <summary>
    /// Null when every version is the same title (or episode) as the main item; otherwise why not. The type, the library
    /// of a merged version, the TMDB, IMDb and TVDB ids wherever both carry one, and for episodes the series, season and
    /// first episode number must agree. A different last episode number is a multi-episode version, not a conflict.
    /// </summary>
    private static string? Check(ILibraryManager library, IReadOnlyList<NativeVersion> versions, Guid? libraryId)
    {
        var main = versions.FirstOrDefault(version => version.IsMain);
        if (main is null) return null;
        foreach (var version in versions.Where(version => !version.IsMain))
        {
            var name = System.IO.Path.GetFileName(version.Path);
            if (version.Item.GetType() != main.Item.GetType())
                return $"{IdentityConflict}: {name} is a {version.Item.GetType().Name}, grouped with a {main.Item.GetType().Name}";
            foreach (var provider in new[] { "Tmdb", "Imdb", "Tvdb" })
            {
                var mine = ProviderId(main.Item, provider);
                var theirs = ProviderId(version.Item, provider);
                if (mine is not null && theirs is not null && !string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase))
                    return $"{IdentityConflict}: {name} has {provider} id {theirs}, the title {mine}";
            }

            if (main.Item is not Episode mainEpisode || version.Item is not Episode episode) continue;
            if (!mainEpisode.SeriesId.Equals(Guid.Empty) && !episode.SeriesId.Equals(Guid.Empty) && !mainEpisode.SeriesId.Equals(episode.SeriesId))
                return $"{IdentityConflict}: {name} belongs to another series";
            if (version.SeasonNumber is null || version.FirstEpisode is null || main.SeasonNumber is null || main.FirstEpisode is null)
                return $"{IdentityConflict}: {name} has no season and episode number to compare";
            if (version.SeasonNumber != main.SeasonNumber || version.FirstEpisode != main.FirstEpisode)
                return string.Create(CultureInfo.InvariantCulture,
                    $"{IdentityConflict}: {name} is S{version.SeasonNumber:00}E{version.FirstEpisode:00}, grouped with S{main.SeasonNumber:00}E{main.FirstEpisode:00}");
        }

        // A merged version must be in the title's library; checked last, as it is the one check that asks the library.
        foreach (var version in versions.Where(version => !version.IsMain && version.IsLinked))
        {
            if (libraryId is { } required && !library.GetCollectionFolders(version.Item).Any(folder => folder.Id.Equals(required)))
                return $"{IdentityConflict}: {System.IO.Path.GetFileName(version.Path)} is in another library";
        }

        return null;
    }

    /// <summary>
    /// A provider id of the item. An episode that carries its series' TMDB id took it from a tag in its file name (Jellyfin 12
    /// reads <c>[tmdbid-]</c> from episode file names), which says nothing about the episode (analysis C18).
    /// </summary>
    private static string? ProviderId(BaseItem item, string provider)
    {
        var value = item.ProviderIds.FirstOrDefault(pair => pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (item is Episode { Series: { } series } && provider == "Tmdb" &&
            string.Equals(series.ProviderIds.FirstOrDefault(pair => pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value,
                value, StringComparison.OrdinalIgnoreCase))
            return null;
        return value;
    }
}
