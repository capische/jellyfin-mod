using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
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
    private const string PrimaryVersionIdPropertyName = "PrimaryVersionId";
    private static readonly ConcurrentDictionary<Type, Func<Video, Guid?>> PrimaryVersionIdReaders = new();
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
        if (work.MediaType == "movie")
        {
            foreach (var movie in copies.Cast<Movie>())
            {
                var groupId = PrimaryVersionId(movie) ?? movie.Id;
                var versionsQuery = TitleQuery(folder, "movie");
                versionsQuery.PresentationUniqueKey = groupId.ToString("N");
                var versions = ReadPages(versionsQuery, cancellationToken).OfType<Movie>().ToList();
                if (library.GetItemById(groupId) is Movie primary &&
                    library.GetCollectionFolders(primary).Any(candidate => candidate.Id == folder.Id))
                    versions.Add(primary);
                if (versions.Any(version => ProviderTmdbId(version) is { } id && id != providerId))
                    return new(groupId, folder.Id, representative.Name, null, true,
                        "One native version group contains conflicting TMDB identities.");
            }
        }

        try
        {
            var mounts = _mediaStorage.ReadMountTable();
            var skipped = new List<SkippedNativeEpisode>();
            var episodes = work.MediaType == "series"
                ? GetEpisodes(copies.Cast<Series>().ToArray(), skipped, mounts, cancellationToken) : [];
            return new(representative.Id, folder.Id, representative.Name,
                Snapshot(work.MediaType, providerId, folder.Id, representative, copies,
                    copies.Select(item => item.Id).ToHashSet(), episodes, mounts) with { SkippedEpisodes = skipped },
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
        MountTable mounts)
    {
        var metadata = new TmdbMetadata(mediaType, tmdbId ?? 0, representative.Name,
            representative.PremiereDate, representative.Overview, null, null, ProviderId(representative, "Imdb"),
            ParseProviderId(representative, "Tvdb"), false, null, RuntimeMinutes(representative), [], [], []);
        return new(mediaType, tmdbId, libraryId, representative.Name, representative.ProductionYear,
            metadata.ImdbId, representative.Overview, null, JsonSerializer.Serialize(metadata),
            copies.Select(copy => new NativeRepresentation(copy.Id, libraryId, IsPlayable(copy),
                mediaType == "movie" ? VersionGroup((Movie)copy, movieIds!) : copy.Id, copy.Path,
                mounts.Capture(copy.Path))).ToArray(), episodes,
            copies.Min(copy => copy.DateCreated));
    }

    private NativeEpisodeSnapshot[] GetEpisodes(
        IReadOnlyCollection<Series> seriesCopies,
        ICollection<SkippedNativeEpisode> skipped,
        MountTable mounts,
        CancellationToken cancellationToken)
    {
        var episodes = new List<NativeEpisodeSnapshot>();
        foreach (var series in seriesCopies)
        {
            // Series.GetItemList rewrites recursive queries to a presentation key in 10.11.11.
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

                episodes.Add(new(episode.Id, episode.SeriesId, ProviderTmdbId(episode), episode.ParentIndexNumber.Value,
                    episode.IndexNumber.Value, IsPlayable(episode), episode.Name, episode.Overview, null,
                    episode.PremiereDate, RuntimeMinutes(episode), episode.Path, mounts.Capture(episode.Path),
                    episode.IndexNumberEnd));
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

    private static Guid? PrimaryVersionId(Video video) =>
        PrimaryVersionIdReaders.GetOrAdd(video.GetType(), CreatePrimaryVersionIdReader)(video);

    private static Func<Video, Guid?> CreatePrimaryVersionIdReader(Type videoType)
    {
        for (var type = videoType; type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(PrimaryVersionIdPropertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property is null) continue;
            if (property.PropertyType == typeof(string))
                return video => Guid.TryParse(property.GetValue(video) as string, out var id) && id != Guid.Empty ? id : null;
            if (property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?))
                return video => property.GetValue(video) is Guid id && id != Guid.Empty ? id : null;
            throw new NotSupportedException(
                $"Unsupported Jellyfin {PrimaryVersionIdPropertyName} type {property.PropertyType.FullName}.");
        }

        throw new MissingMemberException(videoType.FullName, PrimaryVersionIdPropertyName);
    }

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
