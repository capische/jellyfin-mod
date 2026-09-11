using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace JellyfinMod.Services;

/// <summary>Builds complete, library-scoped title observations from Jellyfin's native database.</summary>
public sealed class JellyfinNativeTitleSource(ILibraryManager library)
{
    /// <summary>Gets deterministic observations for every movie and series library.</summary>
    public IReadOnlyList<NativeCatalogObservation> GetObservations(CancellationToken cancellationToken)
    {
        var observations = new List<NativeCatalogObservation>();
        foreach (var virtualFolder in library.GetVirtualFolders()
                     .Where(folder => folder.CollectionType is MediaBrowser.Model.Entities.CollectionTypeOptions.movies or
                         MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows)
                     .OrderBy(folder => folder.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(virtualFolder.ItemId, out var libraryId) ||
                library.GetItemById<CollectionFolder>(libraryId) is not { } folder)
            {
                observations.Add(new(Guid.Empty, libraryId, virtualFolder.Name, null, false,
                    "The native library root could not be resolved."));
                continue;
            }

            if (virtualFolder.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.movies)
                observations.AddRange(GetMovies(folder, libraryId, cancellationToken));
            else
                observations.AddRange(GetSeries(folder, libraryId, cancellationToken));
        }

        return observations;
    }

    private static IEnumerable<NativeCatalogObservation> GetMovies(
        CollectionFolder folder,
        Guid libraryId,
        CancellationToken cancellationToken)
    {
        var movies = folder.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            GroupByPresentationUniqueKey = false
        }).OfType<Movie>().OrderBy(item => item.Id).ToArray();
        var movieIds = movies.Select(item => item.Id).ToHashSet();
        var versionConflictGroups = movies.GroupBy(item => VersionGroup(item, movieIds))
            .Where(group => group.Select(ProviderTmdbId).Where(id => id.HasValue).Distinct().Skip(1).Any())
            .ToArray();
        var versionConflicts = versionConflictGroups.SelectMany(group => group.Select(item => item.Id)).ToHashSet();
        foreach (var conflictGroup in versionConflictGroups)
        {
            var conflict = conflictGroup.OrderBy(item => item.Id).First();
            yield return new(conflict.Id, libraryId, conflict.Name, null, true,
                "One native version group contains conflicting TMDB identities.");
        }

        foreach (var movie in movies.Where(item => ProviderTmdbId(item) is null && !versionConflicts.Contains(item.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(movie.Id, libraryId, movie.Name,
                Snapshot("movie", null, libraryId, movie, [movie], movieIds, []), false, null);
        }

        foreach (var providerGroup in movies.Where(item => ProviderTmdbId(item).HasValue && !versionConflicts.Contains(item.Id))
                     .GroupBy(item => ProviderTmdbId(item)!.Value).OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representative = providerGroup.OrderBy(item => item.Id).First();
            yield return new(representative.Id, libraryId, representative.Name,
                Snapshot("movie", providerGroup.Key, libraryId, representative, providerGroup.ToArray(), movieIds, []), false, null);
        }
    }

    private static IEnumerable<NativeCatalogObservation> GetSeries(
        CollectionFolder folder,
        Guid libraryId,
        CancellationToken cancellationToken)
    {
        var seriesItems = folder.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
            GroupByPresentationUniqueKey = false
        }).OfType<Series>().OrderBy(item => item.Id).ToArray();
        foreach (var series in seriesItems.Where(item => ProviderTmdbId(item) is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(series.Id, libraryId, series.Name,
                Snapshot("series", null, libraryId, series, [series], null,
                    GetEpisodes([series], libraryId, cancellationToken)), false, null);
        }

        foreach (var providerGroup in seriesItems.Where(item => ProviderTmdbId(item).HasValue)
                     .GroupBy(item => ProviderTmdbId(item)!.Value).OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copies = providerGroup.ToArray();
            var representative = copies.OrderBy(item => item.Id).First();
            NativeEpisodeSnapshot[] episodes;
            NativeCatalogObservation? failure = null;
            try
            {
                episodes = GetEpisodes(copies, libraryId, cancellationToken);
            }
            catch (InvalidOperationException error)
            {
                episodes = [];
                failure = new(representative.Id, libraryId, representative.Name, null, false, error.Message);
            }

            if (failure is not null)
            {
                yield return failure;
                continue;
            }

            yield return new(representative.Id, libraryId, representative.Name,
                Snapshot("series", providerGroup.Key, libraryId, representative, copies, null, episodes), false, null);
        }
    }

    private static NativeTitleSnapshot Snapshot(
        string mediaType,
        int? tmdbId,
        Guid libraryId,
        BaseItem representative,
        IReadOnlyCollection<BaseItem> copies,
        IReadOnlySet<Guid>? movieIds,
        IReadOnlyList<NativeEpisodeSnapshot> episodes)
    {
        var metadata = new TmdbMetadata(mediaType, tmdbId ?? 0, representative.Name,
            representative.PremiereDate, representative.Overview, null, null, ProviderId(representative, "Imdb"),
            ParseProviderId(representative, "Tvdb"), false, null, RuntimeMinutes(representative), [], [], []);
        return new(mediaType, tmdbId, libraryId, representative.Name, representative.ProductionYear,
            metadata.ImdbId, representative.Overview, null, JsonSerializer.Serialize(metadata),
            copies.Select(copy => new NativeRepresentation(copy.Id, libraryId, IsPlayable(copy),
                mediaType == "movie" ? VersionGroup((Movie)copy, movieIds!) : copy.Id)).ToArray(), episodes);
    }

    private static NativeEpisodeSnapshot[] GetEpisodes(
        IReadOnlyCollection<Series> seriesCopies,
        Guid libraryId,
        CancellationToken cancellationToken)
    {
        var episodes = new List<NativeEpisodeSnapshot>();
        foreach (var series in seriesCopies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var episode in series.GetItemList(new InternalItemsQuery
                         {
                             IncludeItemTypes = [BaseItemKind.Episode],
                             Recursive = true,
                             IsVirtualItem = false,
                             GroupByPresentationUniqueKey = false
                         }).OfType<MediaBrowser.Controller.Entities.TV.Episode>())
            {
                if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
                    throw new InvalidOperationException($"Native episode {episode.Id} has no season/episode identity.");
                episodes.Add(new(episode.Id, series.Id, ProviderTmdbId(episode), episode.ParentIndexNumber.Value,
                    episode.IndexNumber.Value, IsPlayable(episode), episode.Name, episode.Overview, null,
                    episode.PremiereDate, RuntimeMinutes(episode)));
            }
        }

        return episodes.OrderBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber)
            .ThenBy(episode => episode.JellyfinItemId).ToArray();
    }

    private static Guid VersionGroup(Movie movie, IReadOnlySet<Guid> movieIds) =>
        Guid.TryParse(movie.PrimaryVersionId, out var primaryId) && movieIds.Contains(primaryId) ? primaryId : movie.Id;

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
