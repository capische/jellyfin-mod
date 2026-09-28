using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellyfinMod.Services;

/// <summary>Builds current library-scoped observations using bounded native queries.</summary>
public sealed class JellyfinNativeTitleSource(ILibraryManager library, MediaStorageIdentity? mediaStorage = null)
{
    private const int PageSize = 50;
    private readonly MediaStorageIdentity _mediaStorage = mediaStorage ?? new();

    /// <summary>Counts native title representations for scheduled-task progress without loading their metadata.</summary>
    public int GetNativeTitleCount(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var folderInfo in library.GetVirtualFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folderInfo.CollectionType is not (MediaBrowser.Model.Entities.CollectionTypeOptions.movies or
                    MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows)) continue;
            if (Guid.TryParse(folderInfo.ItemId, out var id) && library.GetItemById<CollectionFolder>(id) is { } folder)
                total += library.GetCount(TitleQuery(folder,
                    folderInfo.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.movies ? "movie" : "series"));
        }

        return total;
    }

    /// <summary>Enumerates title identities without retaining server-wide episode snapshots.</summary>
    public IEnumerable<NativeTitleWorkItem> GetWorkItems(CancellationToken cancellationToken) =>
        GetWorkItems(null, cancellationToken);

    /// <summary>Re-enumerates one library while its reconciliation lease is held.</summary>
    internal IEnumerable<NativeTitleWorkItem> GetLibraryWorkItems(Guid libraryId, CancellationToken cancellationToken) =>
        GetWorkItems(libraryId, cancellationToken);

    private IEnumerable<NativeTitleWorkItem> GetWorkItems(Guid? requiredLibraryId, CancellationToken cancellationToken)
    {
        foreach (var folderInfo in library.GetVirtualFolders()
                     .Where(folder => folder.CollectionType is MediaBrowser.Model.Entities.CollectionTypeOptions.movies or
                         MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows)
                     .Where(folder => !requiredLibraryId.HasValue ||
                         Guid.TryParse(folder.ItemId, out var id) && id == requiredLibraryId.Value)
                     .OrderBy(folder => folder.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mediaType = folderInfo.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.movies ? "movie" : "series";
            if (!Guid.TryParse(folderInfo.ItemId, out var libraryId) ||
                library.GetItemById<CollectionFolder>(libraryId) is not { } folder)
            {
                yield return new(Guid.Empty, libraryId, mediaType, null, folderInfo.Name);
                continue;
            }

            var seen = new HashSet<(int? TmdbId, Guid NativeId)>();
            foreach (var item in ReadPages(TitleQuery(folder, mediaType), cancellationToken))
            {
                var tmdbId = ProviderTmdbId(item);
                if (seen.Add((tmdbId, tmdbId.HasValue ? Guid.Empty : item.Id)))
                    yield return new(item.Id, libraryId, mediaType, tmdbId, item.Name);
            }
        }
    }

    /// <summary>Finds only the current title and collection folders affected by a notification.</summary>
    public IEnumerable<NativeTitleWorkItem> GetItemWorkItems(Guid itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = library.GetItemById(itemId);
        if (item is Episode episode)
            item = episode.Series;
        if (item is not (Movie or Series)) yield break;
        var mediaType = item is Movie ? "movie" : "series";
        foreach (var folder in library.GetCollectionFolders(item).OfType<CollectionFolder>())
        {
            if (folder.CollectionType != (mediaType == "movie" ? CollectionType.movies : CollectionType.tvshows)) continue;
            yield return new(item.Id, folder.Id, mediaType, ProviderTmdbId(item), item.Name);
        }
    }

    /// <summary>Reads a title afresh; callers hold the library lease until its database write finishes.</summary>
    public NativeCatalogObservation GetObservation(NativeTitleWorkItem work, CancellationToken cancellationToken)
    {
        if (library.GetItemById<CollectionFolder>(work.TargetLibraryId) is not { } folder)
            return new(work.NativeItemId, work.TargetLibraryId, work.Title, null, false,
                "The native library root could not be resolved.");
        var query = TitleQuery(folder, work.MediaType);
        if (work.TmdbId is { } tmdbId)
            query.HasAnyProviderId = new() { ["Tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture) };
        else
            query.ItemIds = [work.NativeItemId];
        var copies = ReadPages(query, cancellationToken)
            .Where(item => item is Movie or Series)
            .Where(item => library.GetCollectionFolders(item).Any(candidate => candidate.Id == work.TargetLibraryId))
            .DistinctBy(item => item.Id).ToArray();
        if (copies.Length == 0)
            return new(work.NativeItemId, work.TargetLibraryId, work.Title, null, false,
                "The native title changed during enumeration; the next event or repair run can retry it.");
        var representative = copies.OrderBy(item => item.Id).First();
        var providerId = ProviderTmdbId(representative);
        // An unidentified series is unmatched regardless of incomplete episode metadata.
        if (providerId is null)
            return new(representative.Id, folder.Id, representative.Name,
                Snapshot(work.MediaType, null, folder.Id, representative, copies,
                    copies.Select(item => item.Id).ToHashSet(), [], _mediaStorage.ReadMountTable()), false, null);

        // Every version Jellyfin plays for each copy, merged ones included, must be this title; a group whose members
        // disagree is refused as a whole and keeps its bindings and state (V1, analysis C2).
        var versionSets = new Dictionary<Guid, NativeVersionSet>();
        if (work.MediaType == "movie")
        {
            foreach (var main in copies.Cast<Movie>().Where(NativeVersions.HasVersions)
                         .Select(movie => NativeVersions.MainOf(library, movie)).DistinctBy(main => main.Id))
            {
                var set = NativeVersions.Read(library, main, folder.Id);
                if (set.Conflict is null && set.Versions.Select(version => ProviderTmdbId(version.Item)).OfType<int>()
                        .FirstOrDefault(id => id != providerId) is var other && other != 0)
                    set = set with { Conflict = $"{NativeVersions.IdentityConflict}: a version has TMDB id {other}, the title {providerId}" };
                if (set.Conflict is not null)
                    return new(main.Id, folder.Id, representative.Name, null, true, set.Conflict);
                versionSets[main.Id] = set;
            }
        }

        try
        {
            var mounts = _mediaStorage.ReadMountTable();
            var skipped = new List<SkippedNativeEpisode>();
            var episodes = work.MediaType == "series"
                ? GetEpisodes(copies.Cast<Series>().ToArray(), folder.Id, skipped, mounts, cancellationToken) : [];
            return new(representative.Id, folder.Id, representative.Name,
                Snapshot(work.MediaType, providerId, folder.Id, representative, copies,
                    copies.Select(item => item.Id).ToHashSet(), episodes, mounts, versionSets) with { SkippedEpisodes = skipped },
                false, null);
        }
        catch (InvalidOperationException error)
        {
            return new(representative.Id, folder.Id, representative.Name, null, false, error.Message);
        }
    }

    private static InternalItemsQuery TitleQuery(CollectionFolder folder, string mediaType) => new()
    {
        Parent = folder,
        IncludeItemTypes = [mediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
        Recursive = true,
        IsVirtualItem = false,
        GroupByPresentationUniqueKey = false
    };

    private IEnumerable<BaseItem> ReadPages(InternalItemsQuery query, CancellationToken cancellationToken)
    {
        query.Limit = PageSize;
        query.EnableTotalRecordCount = false;
        query.OrderBy = [(ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)];
        for (var start = 0; ; start += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            query.StartIndex = start;
            var page = library.GetItemList(query);
            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            if (page.Count < PageSize) yield break;
        }
    }

    /// <summary>Gets configured movie and series libraries with the paths needed to prove storage availability.</summary>
    internal IReadOnlyList<NativeLibraryStorage> GetLibraryStorage(CancellationToken cancellationToken)
    {
        var result = new List<NativeLibraryStorage>();
        foreach (var folder in library.GetVirtualFolders().Where(folder => folder.CollectionType is
                     MediaBrowser.Model.Entities.CollectionTypeOptions.movies or
                     MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParse(folder.ItemId, out var id))
                result.Add(new(id, folder.Name, folder.Locations ?? []));
        }

        return result;
    }

    private NativeTitleSnapshot Snapshot(
        string mediaType,
        int? tmdbId,
        Guid libraryId,
        BaseItem representative,
        IReadOnlyCollection<BaseItem> copies,
        IReadOnlySet<Guid>? movieIds,
        IReadOnlyList<NativeEpisodeSnapshot> episodes,
        MountTable mounts,
        IReadOnlyDictionary<Guid, NativeVersionSet>? versionSets = null)
    {
        var metadata = new TmdbMetadata(mediaType, tmdbId ?? 0, representative.Name,
            representative.PremiereDate, representative.Overview, null, null, ProviderId(representative, "Imdb"),
            ParseProviderId(representative, "Tvdb"), false, null, RuntimeMinutes(representative), [], [], []);
        var representations = new List<NativeRepresentation>();
        foreach (var copy in copies)
        {
            var versionGroupId = mediaType == "movie" ? VersionGroup((Movie)copy, movieIds!) : copy.Id;
            representations.Add(new(copy.Id, libraryId, IsPlayable(copy), versionGroupId, copy.Path,
                mounts.Capture(copy.Path)));
            if (mediaType != "movie" || versionSets?.GetValueOrDefault(copy.Id) is not { } versions) continue;
            // Each further version is a native item of its own with its own path and streams, observed as a representation
            // owned by the title a user opens (P6.M6, V1). A multi-part version is bound like any other; retention and
            // Remove this version refuse it on its own (C17). A version that is itself listed as a copy is bound as one.
            // Versions of a main item outside this observation are not this title's to bind; retention blocks such a copy.
            foreach (var version in versions.Extras.Where(version => !movieIds!.Contains(version.Item.Id)))
                representations.Add(new(version.Item.Id, libraryId, IsPlayable(version.Item), versionGroupId, version.Path,
                    mounts.Capture(version.Path), copy.Id));
        }

        return new(mediaType, tmdbId, libraryId, representative.Name, representative.ProductionYear,
            metadata.ImdbId, representative.Overview, null, JsonSerializer.Serialize(metadata),
            representations.DistinctBy(representation => representation.JellyfinItemId).ToArray(), episodes,
            copies.Min(copy => copy.DateCreated))
        {
            NativeRating = representative.CustomRating ?? representative.OfficialRating,
            NativeTags = representative.Tags ?? []
        };
    }

    private NativeEpisodeSnapshot[] GetEpisodes(
        IReadOnlyCollection<Series> seriesCopies,
        Guid libraryId,
        ICollection<SkippedNativeEpisode> skipped,
        MountTable mounts,
        CancellationToken cancellationToken)
    {
        var episodes = new List<NativeEpisodeSnapshot>();
        foreach (var series in seriesCopies)
        {
            // Series.GetItemList rewrites recursive queries to a presentation key (Series.cs, 12.0.0).
            // Query physical ancestry directly so grouped copies cannot borrow each other's episodes.
            var query = new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                GroupByPresentationUniqueKey = false
            };
            foreach (var episode in ReadPages(query, cancellationToken).OfType<Episode>())
            {
                if (episode.SeriesId != series.Id)
                    throw new InvalidOperationException($"Native episode {episode.Id} has conflicting series provenance.");
                // Date-named, unparsed or "Season Unknown" episodes cannot be matched by position. They are
                // diagnosed one at a time instead of failing the whole series (P2.R6).
                if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
                {
                    skipped.Add(new(episode.Id, episode.SeriesId, IsPlayable(episode), episode.Name ?? string.Empty,
                        "The native episode has no season and episode number."));
                    continue;
                }

                // Jellyfin 12 groups the files of one episode in one folder as versions of one main episode and hides the
                // others from queries (analysis C4); each is observed here as a version owned by the main episode (V1).
                var versions = NativeVersions.HasVersions(episode) ? NativeVersions.Read(library, episode, libraryId) : null;
                if (versions?.Conflict is { } conflict)
                {
                    // Two different episodes grouped (C2): refused as a whole, so every file keeps its binding and state.
                    foreach (var version in versions.Versions)
                        skipped.Add(new(version.Item.Id, episode.SeriesId, IsPlayable(version.Item), version.Item.Name ?? string.Empty,
                            conflict));
                    continue;
                }

                var episodeTmdbId = ProviderTmdbId(episode);
                episodes.Add(new(episode.Id, episode.SeriesId, episodeTmdbId, episode.ParentIndexNumber.Value,
                    episode.IndexNumber.Value, IsPlayable(episode), episode.Name, episode.Overview, null,
                    episode.PremiereDate, RuntimeMinutes(episode), episode.Path, mounts.Capture(episode.Path),
                    episode.IndexNumberEnd));
                // A version carries the main episode's identity (the check above proved they agree) and its own last
                // episode: S01E01-E02 grouped under S01E01 covers E02 like any multi-episode file (decision 2, C1).
                foreach (var version in versions?.Extras ?? [])
                    episodes.Add(new(version.Item.Id, episode.SeriesId, episodeTmdbId, episode.ParentIndexNumber.Value,
                        episode.IndexNumber.Value, IsPlayable(version.Item), episode.Name, episode.Overview, null,
                        episode.PremiereDate, RuntimeMinutes(episode), version.Path, mounts.Capture(version.Path),
                        version.IsMultiEpisode ? version.LastEpisode : null, episode.Id));
            }
        }

        return episodes.DistinctBy(episode => episode.JellyfinItemId).OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber).ThenBy(episode => episode.JellyfinItemId).ToArray();
    }

    private static Guid VersionGroup(Movie movie, IReadOnlySet<Guid> movieIds)
    {
        var primaryId = PrimaryVersionId(movie);
        return primaryId.HasValue && movieIds.Contains(primaryId.Value) ? primaryId.Value : movie.Id;
    }

    /// <summary>The main item of a version group, or null for a video that is its own main item.</summary>
    internal static Guid? PrimaryVersionId(Video video) =>
        video.PrimaryVersionId is { } id && !id.Equals(Guid.Empty) ? id : null;

    private static bool IsPlayable(BaseItem item) => !string.IsNullOrWhiteSpace(item.Path);

    private static int? ProviderTmdbId(BaseItem item) => ParseProviderId(item, "Tmdb");

    private static int? ParseProviderId(BaseItem item, string provider) =>
        int.TryParse(ProviderId(item, provider), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    private static string? ProviderId(BaseItem item, string provider) => item.ProviderIds
        .FirstOrDefault(value => value.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? RuntimeMinutes(BaseItem item) => item.RunTimeTicks is { } ticks
        ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes)
        : null;
}

/// <summary>One native title work item or a bounded diagnostic produced while inspecting it.</summary>
public sealed record NativeCatalogObservation(Guid NativeItemId, Guid TargetLibraryId, string Title,
    NativeTitleSnapshot? Snapshot, bool IsConflict, string? Detail);

/// <summary>A native episode left out of matching because it has no usable season and episode number.</summary>
public sealed record SkippedNativeEpisode(Guid JellyfinItemId, Guid SeriesItemId, bool IsPlayable, string Title, string Detail);

/// <summary>A native media library and its configured storage locations.</summary>
internal sealed record NativeLibraryStorage(Guid LibraryId, string Name, IReadOnlyList<string> Locations);

/// <summary>A lightweight identity to re-read immediately before reconciliation.</summary>
public sealed record NativeTitleWorkItem(Guid NativeItemId, Guid TargetLibraryId, string MediaType, int? TmdbId, string Title);
