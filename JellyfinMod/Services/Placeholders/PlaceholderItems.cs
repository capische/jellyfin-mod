using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>What a placeholder episode item is made with: its numbers and what the catalog knows of it.</summary>
public sealed record PlaceholderEpisodeInfo(int Season, int Episode, string? Title, string? Overview, DateTime? AirDate, int? RuntimeMinutes,
    int TmdbId);

/// <summary>What a placeholder series or movie item is made with: the catalog entry's identity and metadata.</summary>
public sealed record PlaceholderTitleInfo(int TmdbId, string Title, int? Year, string? Overview, DateTime? PremiereDate, int? RuntimeMinutes,
    string? ImdbId, int? TvdbId);

/// <summary>
/// The plugin's file-less placeholders (P1.FU30, the virtual-items spike; user, 2026-10-18): movies, series, seasons and episodes
/// that exist only as Jellyfin items, made with <see cref="Folder.AddChild"/> as Jellyfin's own TMDb missing-episode provider makes
/// its items, with two differences that keep them shown and kept: a path in the plugin's own scheme (<see cref="Scheme"/>), so
/// Jellyfin's folder validation never reaps them (it removes only file-protocol children that are gone from disk) and no user can
/// delete them (<c>CanDelete</c> is <c>IsFileProtocol</c>), and <c>IsVirtualItem = false</c>, so Latest, Next Up, search and the
/// counts treat them as present. Videos carry <c>IsPlaceHolder</c>, so every client and the response filter treat them as not
/// playable. Ids come from <c>GetNewItemId(path)</c>, so an item made again has the same id and takes back its parked user data.
/// </summary>
/// <remarks>
/// <para>The plugin recognises its own items by the scheme alone and never touches any other item. Nothing in Jellyfin removes
/// these items except the removal of their parent folder or library; the plugin owns their whole life (the sync, the import's
/// swap, Remove Placeholders, Remove Entry, the uninstall clean-up).</para>
/// <para>The recipe and its measurements: the 2026-10-18 experiment on stock Jellyfin 12.2 (scratch report
/// <c>nonfile-placeholder-experiment.md</c>, summarised in <c>design/missing-media-user-actions.md</c> §17).</para>
/// </remarks>
public static class PlaceholderItems
{
    /// <summary>The path scheme of every placeholder item the plugin makes; nothing else ever has it.</summary>
    public const string Scheme = "jellyfinmod://";

    /// <summary>Whether a path is one of the plugin's placeholder items (no I/O).</summary>
    public static bool IsOwn(string? path) => path is not null && path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Whether an item is one of the plugin's placeholder items.</summary>
    public static bool IsOwn(BaseItem? item) => IsOwn(item?.Path);

    /// <summary>The path of a file-less series' item in a library.</summary>
    public static string SeriesPath(Guid libraryId, int tmdbId) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}series/{libraryId:N}/{tmdbId}");

    /// <summary>The path of a file-less movie's item in a library.</summary>
    public static string MoviePath(Guid libraryId, int tmdbId) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}movie/{libraryId:N}/{tmdbId}");

    /// <summary>The path of a placeholder season's item under a series item (the plugin's or a real one).</summary>
    public static string SeasonPath(Guid seriesId, int season) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}season/{seriesId:N}/{season}");

    /// <summary>The path of a placeholder episode's item under a series item (the plugin's or a real one).</summary>
    public static string EpisodePath(Guid seriesId, int season, int episode) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}episode/{seriesId:N}/{season}/{episode}");

    /// <summary>The TMDB id a series' or movie's placeholder path names, or null.</summary>
    public static int? TmdbIdOfPath(string? path)
    {
        if (!IsOwn(path) || !(path!.StartsWith(Scheme + "series/", StringComparison.Ordinal) || path.StartsWith(Scheme + "movie/", StringComparison.Ordinal)))
            return null;
        var last = path[(path.LastIndexOf('/') + 1)..];
        return int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>The TMDB id an item carries, or null.</summary>
    public static int? TmdbIdOf(BaseItem? item) =>
        item?.ProviderIds.FirstOrDefault(pair => pair.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value is { } raw &&
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    // Real items that took a placeholder's marks in the last minutes: each later save of theirs reloads their rows.
    private static readonly ConcurrentDictionary<Guid, DateTime> Twins = new();

    /// <summary>
    /// Notes a real item that just took a placeholder's marks. Jellyfin's own first refresh of a new item can replace its cached
    /// instance with one loaded before the marks were saved, and an item answered by id shows that instance's rows; the
    /// item listener reloads them after each such save for a few minutes.
    /// </summary>
    public static void NoteTwin(Guid id)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in Twins.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray())
            Twins.TryRemove(stale, out _);
        Twins[id] = now;
    }

    /// <summary>Whether an item took a placeholder's marks in the last ten minutes.</summary>
    public static bool IsRecentTwin(Guid id) => Twins.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10);

    /// <summary>
    /// The item Jellyfin's database holds under <paramref name="id"/>, or null. Not <c>GetItemById</c>: Jellyfin keeps a removed item
    /// in its memory cache, so an item that went with its library (a library removed and added again) would be taken for one still
    /// there and never made again.
    /// </summary>
    public static BaseItem? Stored(ILibraryManager library, Guid id) =>
        library.GetItemList(new InternalItemsQuery { ItemIds = [id], GroupByPresentationUniqueKey = false }).FirstOrDefault();

    /// <summary>Every item of the plugin's of the given kinds in a library (movies, series, seasons, episodes).</summary>
    public static IReadOnlyList<BaseItem> List(ILibraryManager library, Guid libraryId, params BaseItemKind[] kinds)
    {
        if (library.GetItemById(libraryId) is not Folder folder) return [];
        return library.GetItemList(new InternalItemsQuery
        {
            Parent = folder,
            Recursive = true,
            IncludeItemTypes = kinds,
            IsVirtualItem = false,
            GroupByPresentationUniqueKey = false
        }).Where(IsOwn).ToArray();
    }

    /// <summary>
    /// The folder a file-less series or movie is made under: the first of the library's physical folders (a real title's parent
    /// is one; never the library's collection folder) that the library itself lists, by the order of its locations, or null
    /// when it lists none yet. Only a folder in the collection folder's own list is visible to users: a folder that was empty at
    /// the last scan has an item but is not in that list until the next scan (seen on the local environments, 2026-10-18).
    /// </summary>
    public static Folder? LibraryFolder(ILibraryManager library, Guid libraryId)
    {
        if (library.GetItemById(libraryId) is not CollectionFolder collection) return null;
        var listed = collection.PhysicalFolderIds.Select(library.GetItemById).OfType<Folder>().ToArray();
        var info = library.GetVirtualFolders()?.FirstOrDefault(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId);
        var order = (info?.Locations ?? []).ToList();
        return listed.OrderBy(folder => order.IndexOf(folder.Path) is var index && index >= 0 ? index : int.MaxValue).FirstOrDefault();
    }

    /// <summary>Makes a file-less series' item under <paramref name="parent"/> (or returns the one already there).</summary>
    public static async Task<Series> CreateSeriesAsync(ILibraryManager library, Folder parent, Guid libraryId, PlaceholderTitleInfo info,
        CancellationToken cancellationToken)
    {
        var path = SeriesPath(libraryId, info.TmdbId);
        var id = library.GetNewItemId(path, typeof(Series));
        if (Stored(library, id) is Series existing) return existing;
        var series = new Series
        {
            Id = id, Path = path, Name = info.Title, ProductionYear = info.Year, Overview = info.Overview, PremiereDate = info.PremiereDate
        };
        SetIds(series, info);
        series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
        parent.AddChild(series);
        await FinishAsync(series, cancellationToken).ConfigureAwait(false);
        return series;
    }

    /// <summary>Makes a file-less movie's item under <paramref name="parent"/> (or returns the one already there).</summary>
    public static async Task<Movie> CreateMovieAsync(ILibraryManager library, Folder parent, Guid libraryId, PlaceholderTitleInfo info,
        CancellationToken cancellationToken)
    {
        var path = MoviePath(libraryId, info.TmdbId);
        var id = library.GetNewItemId(path, typeof(Movie));
        if (Stored(library, id) is Movie existing) return existing;
        var movie = new Movie
        {
            Id = id, Path = path, Name = info.Title, ProductionYear = info.Year, Overview = info.Overview, PremiereDate = info.PremiereDate,
            IsPlaceHolder = true, IsVirtualItem = false,
            RunTimeTicks = info.RuntimeMinutes is > 0 and var minutes ? TimeSpan.FromMinutes(minutes).Ticks : null
        };
        // Its date added is its release date, so it never heads Recently Added as new media.
        if (info.PremiereDate is { } released) movie.DateCreated = DateTime.SpecifyKind(released.Date, DateTimeKind.Utc);
        SetIds(movie, info);
        movie.PresentationUniqueKey = movie.CreatePresentationUniqueKey();
        parent.AddChild(movie);
        await FinishAsync(movie, cancellationToken).ConfigureAwait(false);
        return movie;
    }

    /// <summary>
    /// The season a placeholder episode goes in: the series' own season of that number (a real one first), else a new placeholder
    /// season made under the series.
    /// </summary>
    public static async Task<Season> EnsureSeasonAsync(ILibraryManager library, Series series, int number, CancellationToken cancellationToken)
    {
        var seasons = library.GetItemList(new InternalItemsQuery
        {
            Parent = series, IncludeItemTypes = [BaseItemKind.Season], IsVirtualItem = false, GroupByPresentationUniqueKey = false
        }).OfType<Season>().Where(season => season.IndexNumber == number).ToArray();
        if (seasons.FirstOrDefault(season => !IsOwn(season)) is { } real) return real;
        if (seasons.FirstOrDefault() is { } own) return own;
        var path = SeasonPath(series.Id, number);
        var id = library.GetNewItemId(path, typeof(Season));
        if (Stored(library, id) is Season existing) return existing;
        var made = new Season
        {
            Id = id, Path = path, IndexNumber = number,
            Name = number == 0 ? "Specials" : "Season " + number.ToString(CultureInfo.InvariantCulture),
            SeriesId = series.Id, SeriesName = series.Name, SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
        };
        made.PresentationUniqueKey = made.CreatePresentationUniqueKey();
        series.AddChild(made);
        await FinishAsync(made, cancellationToken).ConfigureAwait(false);
        return made;
    }

    /// <summary>Makes a placeholder episode's item in <paramref name="season"/> of <paramref name="series"/> (or returns the one there).</summary>
    public static async Task<JellyfinEpisode> CreateEpisodeAsync(ILibraryManager library, Series series, Season season, PlaceholderEpisodeInfo info,
        CancellationToken cancellationToken)
    {
        var path = EpisodePath(series.Id, info.Season, info.Episode);
        var id = library.GetNewItemId(path, typeof(JellyfinEpisode));
        if (Stored(library, id) is JellyfinEpisode existing) return existing;
        var episode = new JellyfinEpisode
        {
            Id = id, Path = path, Name = string.IsNullOrWhiteSpace(info.Title) ? null : info.Title, Overview = info.Overview,
            IndexNumber = info.Episode, ParentIndexNumber = info.Season, PremiereDate = info.AirDate,
            ProductionYear = info.AirDate?.Year,
            IsPlaceHolder = true, IsVirtualItem = false,
            SeasonId = season.Id, SeasonName = season.Name, SeriesId = series.Id, SeriesName = series.Name,
            SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
            RunTimeTicks = info.RuntimeMinutes is > 0 and var minutes ? TimeSpan.FromMinutes(minutes).Ticks : null
        };
        // Its date added is its air date, so a placeholder never heads Latest as newly added media.
        if (info.AirDate is { } aired) episode.DateCreated = DateTime.SpecifyKind(aired.Date, DateTimeKind.Utc);
        if (info.TmdbId > 0) episode.SetProviderId(MetadataProvider.Tmdb, info.TmdbId.ToString(CultureInfo.InvariantCulture));
        episode.PresentationUniqueKey = episode.CreatePresentationUniqueKey();
        season.AddChild(episode);
        await FinishAsync(episode, cancellationToken).ConfigureAwait(false);
        return episode;
    }

    /// <summary>
    /// Moves a placeholder episode into another season of its series (a real season folder arrived for its season number): its id,
    /// metadata, images and user data stay.
    /// </summary>
    public static async Task MoveAsync(JellyfinEpisode episode, Season target, CancellationToken cancellationToken)
    {
        var previous = episode.GetParent() as Folder;
        episode.SetParent(target);
        episode.SeasonId = target.Id;
        episode.SeasonName = target.Name;
        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        if (previous is not null) previous.Children = null;
        target.Children = null;
    }

    /// <summary>
    /// Removes one of the plugin's items (with everything under it). Jellyfin parks its user data by key, so the item that takes
    /// its place (the imported file's, or the same placeholder made again) takes it back. Never a real item: anything without the
    /// scheme is left alone. Returns whether it was removed.
    /// </summary>
    public static bool Remove(ILibraryManager library, BaseItem item, ILogger logger)
    {
        if (!IsOwn(item)) return false;
        try
        {
            library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not remove its placeholder item {Item}: {Error}", item.Id, error.Message);
            return false;
        }
    }

    /// <summary>
    /// The import's half of the swap for items (P1.FU30), once the file is linked and before its folder is refreshed: a movie's
    /// item goes; for an episode, a file-less series' own item goes whole (the file makes a real series; the sync makes the other
    /// placeholders again under it, with their user data), else the episode's item under the real series, and its placeholder
    /// season when that left it empty. Never throws; returns how many items went.
    /// </summary>
    public static int SwapForImport(ILibraryManager library, Guid libraryId, int tmdbId, int? season, int? episode, ILogger logger)
    {
        var removed = 0;
        try
        {
            if (tmdbId <= 0) return 0;
            if (season is null || episode is null)
                return library.GetItemById(library.GetNewItemId(MoviePath(libraryId, tmdbId), typeof(Movie))) is Movie movie &&
                    Remove(library, movie, logger) ? 1 : 0;
            if (library.GetItemById(library.GetNewItemId(SeriesPath(libraryId, tmdbId), typeof(Series))) is Series own)
                return Remove(library, own, logger) ? 1 : 0;
            if (library.GetItemById(libraryId) is not Folder folder) return 0;
            foreach (var series in library.GetItemList(new InternalItemsQuery
                     {
                         Parent = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.Series], IsVirtualItem = false,
                         HasAnyProviderId = new() { ["Tmdb"] = tmdbId.ToString(CultureInfo.InvariantCulture) }
                     }).OfType<Series>())
            {
                if (library.GetItemById(library.GetNewItemId(EpisodePath(series.Id, season.Value, episode.Value), typeof(JellyfinEpisode))) is not
                    JellyfinEpisode placeholder) continue;
                var parent = placeholder.GetParent();
                if (Remove(library, placeholder, logger)) removed++;
                if (parent is Season ownSeason && IsOwn(ownSeason) && !library.GetItemList(new InternalItemsQuery
                        {
                            Parent = ownSeason, IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false
                        }).Any())
                    Remove(library, ownSeason, logger);
            }

            if (removed > 0)
                logger.LogInformation("JellyfinMod removed the placeholder item of S{Season:00}E{Episode:00} now that its file is in place", season, episode);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not remove a placeholder item beside an imported file; the next sync removes it: {Error}", error.Message);
        }

        return removed;
    }

    /// <summary>
    /// The retention half for items (P1.FU30): after a reclaim unlinked an episode's last file and recorded it, makes the
    /// placeholder item of each episode the file held, under the series the file was in, where the library's switch is on. Its
    /// user data, parked when the file's item went, comes back by key. Never deletes, never throws; returns how many it made.
    /// </summary>
    public static async Task<int> LeaveAfterReclaimAsync(ILibraryManager library, Guid libraryId, string mediaPath, int seriesTmdbId,
        IReadOnlyList<PlaceholderService.ReclaimedEpisode> episodes, ILogger logger)
    {
        var made = 0;
        try
        {
            if (!PlaceholderSettings.IsEnabled(libraryId) || library.GetItemById(libraryId) is not CollectionFolder { CollectionType: CollectionType.tvshows } folder)
                return 0;
            var fileFolder = Path.GetDirectoryName(mediaPath);
            var seriesFolder = fileFolder is not null && Path.GetFileName(fileFolder).StartsWith("Season ", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(fileFolder) : fileFolder;
            var series = seriesFolder is null ? null : library.FindByPath(seriesFolder, true) as Series;
            series ??= library.GetItemList(new InternalItemsQuery
            {
                Parent = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.Series], IsVirtualItem = false,
                HasAnyProviderId = new() { ["Tmdb"] = seriesTmdbId.ToString(CultureInfo.InvariantCulture) }
            }).OfType<Series>().FirstOrDefault(item => !IsOwn(item));
            if (series is null) return 0;
            foreach (var episode in episodes.Where(item => item.SeasonNumber > 0).DistinctBy(item => (item.SeasonNumber, item.EpisodeNumber)))
            {
                var season = await EnsureSeasonAsync(library, series, episode.SeasonNumber, CancellationToken.None).ConfigureAwait(false);
                await CreateEpisodeAsync(library, series, season, new(episode.SeasonNumber, episode.EpisodeNumber, episode.Title, null, null, null, 0),
                    CancellationToken.None).ConfigureAwait(false);
                made++;
            }

            if (made > 0) logger.LogInformation("JellyfinMod left {Count} placeholder item(s) for reclaimed episode(s) of {Series}", made, series.Name);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not leave a placeholder item for a reclaimed episode: {Error}", error.Message);
        }

        return made;
    }

    /// <summary>
    /// The retention half for a movie as an item (P1.FU30): after a reclaim of the movie's last file, makes its placeholder item in
    /// the library, where the switch is on. Never deletes, never throws; true when made.
    /// </summary>
    public static async Task<bool> LeaveAfterMovieReclaimAsync(ILibraryManager library, Guid libraryId, int tmdbId, string title, int? year,
        ILogger logger)
    {
        try
        {
            if (tmdbId <= 0 || !PlaceholderSettings.IsEnabled(libraryId) ||
                library.GetItemById(libraryId) is not CollectionFolder { CollectionType: CollectionType.movies } ||
                LibraryFolder(library, libraryId) is not { } parent) return false;
            await CreateMovieAsync(library, parent, libraryId, new(tmdbId, title, year, null, null, null, null, null), CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogInformation("JellyfinMod left a placeholder item for the reclaimed movie {Title}", title);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not leave a placeholder item for a reclaimed movie: {Error}", error.Message);
            return false;
        }
    }

    /// <summary>
    /// Removes every item of the plugin's in every library (the uninstall clean-up and <i>Remove All Virtual Items</i>): titles
    /// first (with their seasons and episodes), then seasons, then episodes and movies left. Returns how many episodes and movies
    /// went.
    /// </summary>
    public static int RemoveEverywhere(ILibraryManager library, ILogger logger)
    {
        var removed = 0;
        foreach (var folder in library.GetVirtualFolders() ?? [])
        {
            if (!Guid.TryParse(folder.ItemId, out var libraryId)) continue;
            removed += RemoveAllIn(library, libraryId, logger);
        }

        return removed;
    }

    /// <summary>Removes every item of the plugin's in one library; returns how many episodes and movies went.</summary>
    public static int RemoveAllIn(ILibraryManager library, Guid libraryId, ILogger logger)
    {
        var videos = List(library, libraryId, BaseItemKind.Episode, BaseItemKind.Movie).Count;
        foreach (var kind in new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie })
            foreach (var item in List(library, libraryId, kind))
                if (library.GetItemById(item.Id) is { } still) Remove(library, still, logger);
        return videos;
    }

    /// <summary>
    /// After an item is made: Jellyfin's parked user data of the same keys comes back (a placeholder made again, or one that takes
    /// a reclaimed file's place), and its metadata (images, people, the rest of TMDb's) is refreshed through Jellyfin's own refresh
    /// queue, one item at a time, never as a burst of TMDb requests.
    /// </summary>
    private static async Task FinishAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await item.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
        BaseItem.ProviderManager?.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(BaseItem.FileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default, ImageRefreshMode = MetadataRefreshMode.Default
        }, RefreshPriority.Low);
    }

    // The provider ids of real series and movies Jellyfin just removed, by kind and TMDB id: a placeholder made for the same
    // title carries them all, so its user-data keys are the removed item's and Jellyfin's parked marks come back at once.
    private static readonly ConcurrentDictionary<(bool Movie, int TmdbId), (Dictionary<string, string> Ids, DateTime At)> RemovedIds = new();

    /// <summary>Remembers a removed real series' or movie's provider ids for a placeholder made for it soon after (P1.FU30).</summary>
    public static void RememberRemoved(BaseItem item)
    {
        if (IsOwn(item) || item is not (Series or Movie) || TmdbIdOf(item) is not { } tmdb) return;
        var now = DateTime.UtcNow;
        if (RemovedIds.Count > 500)
            foreach (var stale in RemovedIds.Where(pair => now - pair.Value.At > TimeSpan.FromHours(1)).Select(pair => pair.Key).ToArray())
                RemovedIds.TryRemove(stale, out _);
        RemovedIds[(item is Movie, tmdb)] = (new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase), now);
    }

    private static void SetIds(BaseItem item, PlaceholderTitleInfo info)
    {
        if (RemovedIds.TryGetValue((item is Movie, info.TmdbId), out var removed) && DateTime.UtcNow - removed.At < TimeSpan.FromHours(1))
            foreach (var (name, value) in removed.Ids)
                if (!string.IsNullOrWhiteSpace(value)) item.SetProviderId(name, value);
        item.SetProviderId(MetadataProvider.Tmdb, info.TmdbId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(info.ImdbId)) item.SetProviderId(MetadataProvider.Imdb, info.ImdbId);
        if (info.TvdbId is > 0) item.SetProviderId(MetadataProvider.Tvdb, info.TvdbId.Value.ToString(CultureInfo.InvariantCulture));
    }
}
