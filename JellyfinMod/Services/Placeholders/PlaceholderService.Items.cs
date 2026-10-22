using Jellyfin.Data.Enums;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// The placeholders' plan and sync (P1.FU30, <see cref="PlaceholderItems"/>): aired episodes and released movies the catalog lists
/// with nothing to play, per library switch, batched, read against what Jellyfin holds: an episode or movie is missing while
/// Jellyfin has no real item for it, whatever the catalog's file state says (a file deleted a second ago, or one Jellyfin found before the catalog did). So the same sync is
/// also the repair after a delete in Jellyfin and the clean-up after a file arrives from anywhere.
/// </summary>
public sealed partial class PlaceholderService
{
    /// <summary>One title's placeholder items to make: a file-less series' own item when it has none, and its episodes; or a movie.</summary>
    private sealed record ItemWork(Guid EntryId, string Title, string Kind, PlaceholderTitleInfo Info, Guid SeriesId, bool NewSeries,
        IReadOnlyList<PlaceholderEpisodeInfo> Episodes);

    /// <summary>One of the plugin's items to remove, why, and the real item that takes its place (its user data goes there).</summary>
    private sealed record ItemRemoval(BaseItem Item, string Reason, BaseItem? Twin);

    /// <summary>A placeholder episode to move into the real season folder that arrived for its season number.</summary>
    private sealed record ItemMove(JellyfinEpisode Episode, Season Target);

    private sealed class ItemsComputed
    {
        public List<ItemWork> Works { get; } = [];

        public List<ItemRemoval> Removals { get; set; } = [];

        public List<ItemMove> Moves { get; } = [];

        public List<PlaceholderSkip> Skipped { get; } = [];

        public HashSet<Guid> Covered { get; } = [];

        public int Existing { get; set; }

        public IReadOnlyList<PlaceholderWrite> Writes(Guid libraryId) => Works.SelectMany(work => work.Kind == PlaceholderKinds.Movie
            ? [new PlaceholderWrite(work.EntryId, work.Title, 0, 0, PlaceholderItems.MoviePath(libraryId, work.Info.TmdbId), false, PlaceholderKinds.Movie)]
            : work.Episodes.Select(episode => new PlaceholderWrite(work.EntryId, work.Title, episode.Season, episode.Episode,
                PlaceholderItems.EpisodePath(work.SeriesId, episode.Season, episode.Episode), work.NewSeries))).ToArray();
    }

    /// <summary>The plugin's own episode and movie items in a library (P1.FU30).</summary>
    private IReadOnlyList<BaseItem> OwnVideos(Guid libraryId)
    {
        try
        {
            return PlaceholderItems.List(library, libraryId, BaseItemKind.Episode, BaseItemKind.Movie);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not list its placeholder items in library {Library}: {Error}", libraryId, error.Message);
            return [];
        }
    }

    /// <summary>The plugin's own items that stand for an entry: its series or movie item, or its seasons and episodes under the real series.</summary>
    private IReadOnlyList<BaseItem> OwnItemsOfEntry(Entry entry, Guid libraryId)
    {
        try
        {
            if (entry.MediaType == "movie")
                return PlaceholderItems.List(library, libraryId, BaseItemKind.Movie)
                    .Where(item => item.Path == PlaceholderItems.MoviePath(libraryId, entry.TmdbId)).ToArray();
            var own = PlaceholderItems.List(library, libraryId, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode);
            var seriesIds = RealSeriesIds(entry, libraryId);
            seriesIds.Add(library.GetNewItemId(PlaceholderItems.SeriesPath(libraryId, entry.TmdbId), typeof(Series)));
            return own.Where(item => item is Series && seriesIds.Contains(item.Id) || item is Season season && seriesIds.Contains(season.ParentId) ||
                item is JellyfinEpisode episode && seriesIds.Contains(episode.SeriesId)).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not list the placeholder items of entry {Entry}: {Error}", entry.Id, error.Message);
            return [];
        }
    }

    private HashSet<Guid> RealSeriesIds(Entry entry, Guid libraryId)
    {
        var ids = database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entry.Id).Select(binding => binding.JellyfinItemId)
            .Concat(database.EpisodeBindings.AsNoTracking().Join(database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entry.Id),
                binding => binding.EpisodeId, episode => episode.Id, (binding, _) => binding.SeriesItemId)).ToHashSet();
        if (entry.JellyfinItemId is { } bound) ids.Add(bound);
        if (library.GetItemById(libraryId) is Folder folder)
            foreach (var series in library.GetItemList(new InternalItemsQuery
                     {
                         Parent = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.Series], IsVirtualItem = false,
                         HasAnyProviderId = new() { ["Tmdb"] = entry.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                     }))
                ids.Add(series.Id);
        return ids;
    }

    private async Task<PlaceholderPlan> PlanItemsAsync(Guid libraryId, Guid? entryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var plan = await ComputeItemsAsync(libraryId, entryId, cancellationToken).ConfigureAwait(false);
        var writes = plan.Writes(libraryId);
        var waiting = writes.Select(write => write.EntryId).ToHashSet();
        var titles = plan.Covered.Union(waiting).Count();
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), true, plan.Existing, writes,
            plan.Removals.Select(removal => new PlaceholderRemoval(removal.Item.Path, removal.Reason)).ToArray(), plan.Skipped)
        {
            Pending = writes.Count, Titles = titles, TitlesDone = titles - waiting.Count
        };
    }

    /// <summary>
    /// The items' sync of one library (or one entry): removes the plugin's items that a real item replaced or that stand for
    /// nothing any more (each real twin then takes back the parked user data), moves placeholder episodes into a real season
    /// folder that arrived, and makes the missing items of at most <paramref name="limit"/> titles, pausing between titles in a
    /// batch. With the switch off it only removes and moves, never makes.
    /// </summary>
    private async Task<PlaceholderPlan> SyncItemsAsync(Guid libraryId, Guid? entryId, bool yieldToScans, int limit, CancellationToken cancellationToken,
        bool leased = false)
    {
        var (name, movies) = LibraryOf(libraryId);
        // No item is made beside a stub of the stubs line (review P1-1): its clean-up runs first, once, at start.
        var stubsGone = await PlaceholderStubCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        // The delete repair holds the library lease already (it cleared the bindings under it).
        var lease = leased ? null : await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = PlaceholderSettings.IsEnabled(libraryId);
            var plan = await ComputeItemsAsync(libraryId, entryId, cancellationToken).ConfigureAwait(false);
            var writes = plan.Writes(libraryId);
            var titleIds = enabled ? plan.Covered.Union(writes.Select(write => write.EntryId)).ToHashSet() : plan.Covered.ToHashSet();
            var removals = plan.Removals.Select(removal => new PlaceholderRemoval(removal.Item.Path, removal.Reason)).ToArray();
            if (yieldToScans && ScanRunning())
            {
                logger.LogInformation("JellyfinMod placeholder items in library {Library}: a library scan is running, so this batch waits for the next run ({Pending} pending)",
                    libraryId, writes.Count);
                return new(libraryId, name, enabled, false, plan.Existing, [], [], plan.Skipped)
                {
                    Pending = enabled ? writes.Count : 0, Titles = titleIds.Count,
                    TitlesDone = titleIds.Count - (enabled ? writes.Select(write => write.EntryId).Distinct().Count() : 0), Deferred = "library_scan"
                };
            }

            var failures = new List<PlaceholderRemoval>();
            // What the clients are told at once (P4.FU12), rather than at Jellyfin's 30 s LibraryChanged batch.
            var added = new List<Guid>();
            var gone = new List<Guid>();
            var folders = new HashSet<Guid> { libraryId };
            // Moves first (into a real season folder that arrived; the plugin's season then goes, empty), then removals: a
            // placeholder leaves before anything reads the real item that takes its place.
            foreach (var move in plan.Moves)
            {
                try
                {
                    await PlaceholderItems.MoveAsync(move.Episode, move.Target, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    failures.Add(new(move.Episode.Path, "The placeholder could not be moved into its season: " + error.Message));
                }
            }

            foreach (var removal in plan.Removals)
            {
                if (library.GetItemById(removal.Item.Id) is not { } still) continue;
                // The placeholder's marks go to the real item that takes its place now, by user, whatever keys either has yet
                // (a real series' TVDB id, which its episodes' keys carry, may arrive only with its first metadata refresh).
                logger.LogInformation("JellyfinMod removes its placeholder item {Path} ({Reason}){Twin}", still.Path, removal.Reason,
                    removal.Twin is null ? string.Empty : ", for " + removal.Twin.Path);
                // Through Jellyfin's cached instance of the twin: its user data is read from, and refreshed on, that instance.
                var twin = removal.Twin is null ? null : library.GetItemById(removal.Twin.Id) ?? removal.Twin;
                if (twin is not null) await CopyUserDataAsync(still, twin, cancellationToken).ConfigureAwait(false);
                if (!PlaceholderItems.Remove(library, still, logger))
                {
                    failures.Add(new(removal.Item.Path, "The placeholder item could not be removed."));
                    continue;
                }

                gone.Add(still.Id);
                folders.Add(still.ParentId);
                if (twin is not null) added.Add(twin.Id);

                if (twin is not null) await TakeBackUserDataAsync(twin, cancellationToken).ConfigureAwait(false);
            }

            var done = new List<PlaceholderWrite>();
            var pending = 0;
            string? deferred = null;
            if (enabled && !stubsGone)
            {
                deferred = "stub_cleanup";
                pending = plan.Works.Sum(work => Math.Max(1, work.Episodes.Count));
            }
            else if (enabled)
            {
                var batch = plan.Works.Take(limit).ToArray();
                pending = plan.Works.Skip(limit).Sum(work => Math.Max(1, work.Episodes.Count));
                for (var index = 0; index < batch.Length; index++)
                {
                    var work = batch[index];
                    if (index > 0)
                    {
                        // The lease is never held across the pause between titles (review P2-2): an import's swap or a delete repair
                        // takes it meanwhile, as on the stubs line.
                        if (lease is not null)
                        {
                            await lease.DisposeAsync().ConfigureAwait(false);
                            lease = null;
                        }

                        await Task.Delay(PauseBetweenSeries, time, cancellationToken).ConfigureAwait(false);
                        if (yieldToScans && ScanRunning())
                        {
                            deferred = "library_scan";
                            pending += batch.Skip(index).Sum(rest => Math.Max(1, rest.Episodes.Count));
                            break;
                        }

                        if (!leased) lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
                        // Read again under the lease (review P2-1): Remove All Virtual Items or the switch may have turned the library
                        // off meanwhile, and the title may have gained a file or left the catalog.
                        if (!PlaceholderSettings.IsEnabled(libraryId))
                        {
                            pending = 0;
                            break;
                        }

                        var again = (await ComputeItemsAsync(libraryId, work.EntryId, cancellationToken).ConfigureAwait(false)).Works
                            .FirstOrDefault(fresh => fresh.EntryId == work.EntryId);
                        if (again is null) continue;
                        work = again;
                    }

                    try
                    {
                        var made = await MakeAsync(libraryId, work, added, folders, cancellationToken).ConfigureAwait(false);
                        done.AddRange(made);
                        logger.LogInformation(
                            "JellyfinMod placeholder items in library {Library}: made {Made} for {Title} (title {Index} of {Count} this run, {Pending} left for the next run)",
                            libraryId, made.Count, work.Title, index + 1, batch.Length, pending);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        failures.Add(new(work.Title, "The placeholder items could not be made: " + error.Message));
                        logger.LogWarning("JellyfinMod could not make the placeholder items of {Title}: {Error}", work.Title, error.Message);
                    }
                }
            }

            if (done.Count > 0 || removals.Length > 0 || plan.Moves.Count > 0)
                logger.LogInformation("JellyfinMod placeholder items in library {Library}: made {Made}, removed {Removed}, moved {Moved}, {Failed} failed, {Pending} left for the next run",
                    libraryId, done.Count, removals.Length, plan.Moves.Count, failures.Count, pending);
            if (added.Count > 0 || gone.Count > 0)
                notifications?.Notify(new(libraryId, movies, added.Distinct().ToArray(), gone.Distinct().ToArray(), folders.Where(id => id != Guid.Empty).ToArray()));
            var waitingTitles = enabled
                ? plan.Works.Where(work => !done.Any(write => write.EntryId == work.EntryId)).Select(work => work.EntryId).ToHashSet()
                : [];
            return new(libraryId, name, enabled, false, plan.Existing, enabled ? done : [], removals, plan.Skipped)
            {
                Failures = failures, Pending = pending, Titles = titleIds.Count, TitlesDone = titleIds.Count - waitingTitles.Count, Deferred = deferred
            };
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How many of the plugin's episode and movie items with no file every library holds.</summary>
    public int ItemCount() => Libraries().Sum(item => OwnVideos(item.LibraryId).Count);

    /// <summary>
    /// Copies each user's marks (played with its date and count, favourite, position, rating) from a placeholder item to the real
    /// item taking its place, where that user has none on it yet. Quiet: no completion is recorded, no Trakt scrobble.
    /// </summary>
    private async Task CopyUserDataAsync(BaseItem from, BaseItem to, CancellationToken cancellationToken)
    {
        if (userData is null || users is null) return;
        try
        {
            // An item read by id carries no user data rows (Jellyfin's GetUserData reads them from the item); this loads them,
            // and on the placeholder also takes back any row still parked under its keys.
            await from.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            await to.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            foreach (var user in users.GetUsers())
            {
                var source = userData.GetUserData(user, from);
                if (source is null || !(source.Played || source.IsFavorite || source.PlaybackPositionTicks > 0 || source.PlayCount > 0 ||
                        source.Rating is not null))
                {
                    logger.LogDebug("JellyfinMod: {User} has no marks on {Item} ({Rows} rows)", user.Username, from.Id, from.UserData?.Count);
                    continue;
                }
                var target = userData.GetUserData(user, to);
                if (target is null) continue;
                // Merged, never lowered: the real item keeps what it has and gains the placeholder's marks. Saved through the
                // cached instance either way, so every answer (by id and in lists) shows them at once.
                target.Played = target.Played || source.Played;
                target.PlayCount = Math.Max(target.PlayCount, source.PlayCount);
                target.LastPlayedDate = new[] { target.LastPlayedDate, source.LastPlayedDate }.Max();
                target.IsFavorite = target.IsFavorite || source.IsFavorite;
                if (target.PlaybackPositionTicks <= 0) target.PlaybackPositionTicks = source.PlaybackPositionTicks;
                target.Rating ??= source.Rating;
                PlaceholderUserData.SaveQuietly(userData, user, to, target, CancellationToken.None);
                logger.LogInformation("JellyfinMod moved {User}'s marks from placeholder {Item} to {Twin}", user.Username, from.Id, to.Id);
            }

            // Jellyfin 12.2 answers an item by id from the rows its cached instance holds, which a save does not reload: read them
            // again, so the item's own page shows the marks now and not after a restart.
            await to.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            PlaceholderItems.NoteTwin(to.Id);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning("JellyfinMod could not move the marks of placeholder {Item} to {Twin}: {Error}", from.Id, to.Id, error.Message);
        }
    }

    private async Task TakeBackUserDataAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            await item.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            if (userData is not null && users is not null) PlaceholderUserData.Resave(userData, users, item, logger);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not move the user data to item {Item}: {Error}", item.Id, error.Message);
        }
    }

    /// <summary>Makes one title's items; returns what it made, and adds their ids and their folders' to the clients' message.</summary>
    private async Task<IReadOnlyList<PlaceholderWrite>> MakeAsync(Guid libraryId, ItemWork work, List<Guid> added, HashSet<Guid> folders,
        CancellationToken cancellationToken)
    {
        var made = new List<PlaceholderWrite>();
        if (work.Kind == PlaceholderKinds.Movie)
        {
            var parent = PlaceholderItems.LibraryFolder(library, libraryId) ?? throw new InvalidOperationException("The library has no folder item yet.");
            var movie = await PlaceholderItems.CreateMovieAsync(library, parent, libraryId, work.Info, cancellationToken).ConfigureAwait(false);
            added.Add(movie.Id);
            made.Add(new(work.EntryId, work.Title, 0, 0, movie.Path, false, PlaceholderKinds.Movie));
            return made;
        }

        var series = library.GetItemById(work.SeriesId) as Series;
        if (series is null)
        {
            var parent = PlaceholderItems.LibraryFolder(library, libraryId) ?? throw new InvalidOperationException("The library has no folder item yet.");
            series = await PlaceholderItems.CreateSeriesAsync(library, parent, libraryId, work.Info, cancellationToken).ConfigureAwait(false);
            added.Add(series.Id);
        }

        folders.Add(series.Id);

        foreach (var group in work.Episodes.GroupBy(episode => episode.Season).OrderBy(group => group.Key))
        {
            var season = await PlaceholderItems.EnsureSeasonAsync(library, series, group.Key, cancellationToken).ConfigureAwait(false);
            folders.Add(season.Id);
            foreach (var episode in group.OrderBy(episode => episode.Episode))
            {
                var item = await PlaceholderItems.CreateEpisodeAsync(library, series, season, episode, cancellationToken).ConfigureAwait(false);
                added.Add(item.Id);
                made.Add(new(work.EntryId, work.Title, episode.Season, episode.Episode, item.Path, work.NewSeries));
            }
        }

        return made;
    }

    private async Task<ItemsComputed> ComputeItemsAsync(Guid libraryId, Guid? entryId, CancellationToken cancellationToken)
    {
        var result = new ItemsComputed();
        if (library.GetItemById(libraryId) is not Folder folder) return result;
        if (LibraryOf(libraryId).IsMovies) return await ComputeMovieItemsAsync(libraryId, folder, entryId, result, cancellationToken).ConfigureAwait(false);
        var today = time.GetUtcNow().UtcDateTime.Date;
        var entries = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "series" && entry.TargetLibraryId == libraryId && (entryId == null || entry.Id == entryId))
            .OrderBy(entry => entry.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entryIds = entries.Select(entry => entry.Id).ToArray();
        var tracked = (await database.Episodes.AsNoTracking().Where(episode => entryIds.Contains(episode.EntryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(episode => episode.EntryId);
        var importing = (await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId != null && entryIds.Contains(operation.EntryId.Value) && operation.EpisodeId != null &&
                ImportStates.Open.Contains(operation.State))
            .Select(operation => operation.EpisodeId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var boundSeries = (await database.EntryBindings.AsNoTracking().Where(binding => entryIds.Contains(binding.EntryId))
                .Select(binding => new { binding.EntryId, Id = binding.JellyfinItemId }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Concat(await database.EpisodeBindings.AsNoTracking()
                .Join(database.Episodes.AsNoTracking().Where(episode => entryIds.Contains(episode.EntryId)), binding => binding.EpisodeId,
                    episode => episode.Id, (binding, episode) => new { episode.EntryId, Id = binding.SeriesItemId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Concat(entries.Where(entry => entry.JellyfinItemId != null).Select(entry => new { EntryId = entry.Id, Id = entry.JellyfinItemId!.Value }))
            .ToLookup(row => row.EntryId, row => row.Id);
        // What Jellyfin holds in the library, read once.
        var items = library.GetItemList(new InternalItemsQuery
        {
            Parent = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            IsVirtualItem = false, GroupByPresentationUniqueKey = false
        });
        var allSeries = items.OfType<Series>().ToArray();
        var seasonsOf = items.OfType<Season>().ToLookup(season => season.ParentId);
        // An episode Jellyfin has just made may not carry its series id yet: its season's (or its own) parent is the series.
        var seasonParent = items.OfType<Season>().ToDictionary(season => season.Id, season => season.ParentId);
        var episodesOf = items.OfType<JellyfinEpisode>().ToLookup(episode => episode.SeriesId != Guid.Empty ? episode.SeriesId
            : seasonParent.TryGetValue(episode.ParentId, out var series) ? series : episode.ParentId);
        var parent = PlaceholderItems.LibraryFolder(library, libraryId);
        // A library Jellyfin holds no real series of while the catalog has series on disk in it: just added again and not
        // scanned yet (Jellyfin 12.2 drops a library's scan request while another scan runs). Its titles get no file-less
        // series now, or every held series would be made as one and removed again after the scan.
        var unscanned = !allSeries.Any(series => !PlaceholderItems.IsOwn(series)) && entries.Any(entry => entry.State == FileState.OnDisk);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The plugin's file-less series by its id as well: the library listing misses it for a while when Jellyfin's scan has
            // just made the library's folder item again, and a real series arriving then would get the placeholders a second time.
            var own = allSeries.FirstOrDefault(series => series.Path == PlaceholderItems.SeriesPath(libraryId, entry.TmdbId))
                ?? PlaceholderItems.Stored(library, library.GetNewItemId(PlaceholderItems.SeriesPath(libraryId, entry.TmdbId), typeof(Series))) as Series;
            var bound = boundSeries[entry.Id].ToHashSet();
            // The real series of the entry.
            var reals = allSeries.Where(series => !PlaceholderItems.IsOwn(series) && (TmdbIdOf(series) == entry.TmdbId || bound.Contains(series.Id)))
                .OrderBy(series => bound.Contains(series.Id) ? 0 : 1).ThenBy(series => series.Id).ToArray();
            // Real episodes: every one keeps a placeholder from being made; only one Jellyfin has refreshed (its series id and
            // numbers certain, its own first refresh done) takes a placeholder's place, so nothing is swapped while Jellyfin is
            // still building the new items and the episode never vanishes in between.
            var twins = new Dictionary<(int, int), BaseItem>();
            var present = new HashSet<(int, int)>();
            foreach (var episode in reals.SelectMany(series => episodesOf[series.Id]).Where(episode => !episode.IsPlaceHolder))
                if (episode.ParentIndexNumber is { } s && episode.IndexNumber is { } first)
                    for (var number = first; number <= Math.Max(first, episode.IndexNumberEnd ?? first); number++)
                    {
                        present.Add((s, number));
                        if (Ready(episode)) twins.TryAdd((s, number), episode);
                    }
            var listed = tracked[entry.Id].ToArray();
            // Aired, or it had a file (reclaimed, or the catalog still says on disk for a file Jellyfin no longer holds): an
            // episode that had a file has aired, whatever date the catalog has for it.
            var wanted = listed.Where(episode => episode.SeasonNumber > 0 &&
                    (episode.AirDate is { } airDate && airDate.Date <= today || episode.State is FileState.Reclaimed or FileState.OnDisk))
                .ToDictionary(episode => (episode.SeasonNumber, episode.EpisodeNumber));
            // A real series that arrived for a file-less one takes over only once Jellyfin has refreshed it and every episode in
            // it; until then the file-less series stays as it is.
            if (own is not null && reals.Length > 0 && !reals.All(series => Ready(series) &&
                    episodesOf[series.Id].Where(episode => !PlaceholderItems.IsOwn(episode)).All(Ready)))
            {
                result.Existing += episodesOf[own.Id].Count(PlaceholderItems.IsOwn);
                result.Covered.Add(entry.Id);
                continue;
            }

            var target = reals.FirstOrDefault() ?? own;
            if (reals.Length > 0 && own is not null)
            {
                // A real series arrived for a file-less one: each placeholder episode with a real twin gives it its user data
                // (the twin's own first refresh may have run already), then the series goes with the rest, whose data the
                // placeholders made again under the real series take back by key.
                foreach (var episode in episodesOf[own.Id].Where(PlaceholderItems.IsOwn))
                    if (twins.GetValueOrDefault((episode.ParentIndexNumber ?? -1, episode.IndexNumber ?? -1)) is { } twin)
                        result.Removals.Add(new(episode, "file_present", twin));
                foreach (var ownSeason in seasonsOf[own.Id])
                    if (reals.SelectMany(series => seasonsOf[series.Id]).FirstOrDefault(season => !PlaceholderItems.IsOwn(season) &&
                            season.IndexNumber == ownSeason.IndexNumber) is { } realSeason)
                        result.Removals.Add(new(ownSeason, "season_present", realSeason));
                result.Removals.Add(new(own, "series_present", reals[0]));
            }
            // The plugin's episodes under another real copy of the series than the one it uses: they go.
            foreach (var other in reals.Skip(1))
                foreach (var stray in episodesOf[other.Id].Where(PlaceholderItems.IsOwn))
                    result.Removals.Add(new(stray, "duplicate", null));

            var kept = new HashSet<(int, int)>();
            var keptEpisodes = new List<JellyfinEpisode>();
            foreach (var episode in target is null ? [] : episodesOf[target.Id].Where(PlaceholderItems.IsOwn))
            {
                var key = (episode.ParentIndexNumber ?? -1, episode.IndexNumber ?? -1);
                var reason = twins.ContainsKey(key) ? "file_present"
                    : present.Contains(key) ? null
                    : listed.All(item => (item.SeasonNumber, item.EpisodeNumber) != key) ? "not_listed"
                    : !wanted.ContainsKey(key) ? "not_aired"
                    : null;
                if (reason is null)
                {
                    kept.Add(key);
                    keptEpisodes.Add(episode);
                }
                else
                {
                    result.Removals.Add(new(episode, reason, twins.GetValueOrDefault(key)));
                }
            }

            var make = new List<PlaceholderEpisodeInfo>();
            foreach (var ((season, number), episode) in wanted.OrderBy(pair => pair.Key))
            {
                if (present.Contains((season, number)) || kept.Contains((season, number))) continue;
                if (importing.Contains(episode.Id))
                {
                    result.Skipped.Add(new(entry.Id, entry.Title, season, 1, "importing",
                        $"S{season:00}E{number:00} is being downloaded or imported; its file takes the place of a placeholder."));
                    continue;
                }

                make.Add(new(season, number, episode.Title, episode.Overview, episode.AirDate, episode.RuntimeMinutes, episode.TmdbId));
            }

            if (kept.Count > 0)
            {
                result.Existing += kept.Count;
                result.Covered.Add(entry.Id);
            }

            if (target is not null)
            {
                // The plugin's seasons: one a real season folder of the same number arrived for gives its episodes to it and
                // goes; one left with no placeholder goes.
                var seasons = seasonsOf[target.Id].ToArray();
                foreach (var ownSeason in seasons.Where(PlaceholderItems.IsOwn))
                {
                    var real = seasons.FirstOrDefault(season => !PlaceholderItems.IsOwn(season) && season.IndexNumber == ownSeason.IndexNumber &&
                        Ready(season));
                    var inside = keptEpisodes.Where(episode => episode.ParentId == ownSeason.Id).ToArray();
                    if (real is not null)
                    {
                        result.Moves.AddRange(inside.Select(episode => new ItemMove(episode, real)));
                        result.Removals.Add(new(ownSeason, "season_present", real));
                    }
                    else if (inside.Length == 0 && make.All(episode => episode.Season != ownSeason.IndexNumber))
                    {
                        result.Removals.Add(new(ownSeason, "empty", null));
                    }
                }
            }

            if (target is null && own is null && make.Count > 0 && unscanned && entry.State == FileState.OnDisk)
            {
                result.Skipped.Add(new(entry.Id, entry.Title, null, make.Count, "library_not_scanned",
                    "Jellyfin holds none of this library's series yet; a library scan comes first."));
                continue;
            }

            if (target is null && own is null && make.Count > 0 && parent is null)
            {
                result.Skipped.Add(new(entry.Id, entry.Title, null, make.Count, "library_folder_missing",
                    "Jellyfin holds no folder of this library yet (a library scan makes it)."));
                continue;
            }

            if (target == own && own is not null && kept.Count == 0 && make.Count == 0)
            {
                // A file-less series with nothing aired left to stand for: its item goes (an empty series reads as watched).
                result.Removals.Add(new(own, "not_aired", null));
                continue;
            }

            if (make.Count > 0)
                result.Works.Add(new(entry.Id, entry.Title, PlaceholderKinds.Episode, TitleInfo(entry),
                    target?.Id ?? library.GetNewItemId(PlaceholderItems.SeriesPath(libraryId, entry.TmdbId), typeof(Series)), target is null, make));
        }

        if (entryId is null)
        {
            // Items of titles that left the catalog: a file-less series' item, and the plugin's episodes under a series no entry has.
            var listedTmdb = await database.Entries.AsNoTracking().Where(entry => entry.MediaType == "series" && entry.TargetLibraryId == libraryId)
                .Select(entry => entry.TmdbId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var allBound = boundSeries.SelectMany(group => group).ToHashSet();
            foreach (var series in allSeries)
            {
                var tmdb = PlaceholderItems.IsOwn(series) ? PlaceholderItems.TmdbIdOfPath(series.Path) : TmdbIdOf(series);
                if (tmdb is { } id && listedTmdb.Contains(id) || allBound.Contains(series.Id)) continue;
                if (PlaceholderItems.IsOwn(series)) result.Removals.Add(new(series, "not_listed", null));
                else
                    foreach (var stray in episodesOf[series.Id].Where(PlaceholderItems.IsOwn).Concat<BaseItem>(seasonsOf[series.Id].Where(PlaceholderItems.IsOwn)))
                        result.Removals.Add(new(stray, "not_listed", null));
            }
        }

        // One removal per item (a title that left the catalog can be listed twice); the sync re-checks each before it goes.
        result.Removals = result.Removals.DistinctBy(removal => removal.Item.Id).ToList();
        return result;
    }

    private async Task<ItemsComputed> ComputeMovieItemsAsync(Guid libraryId, Folder folder, Guid? entryId, ItemsComputed result,
        CancellationToken cancellationToken)
    {
        var today = time.GetUtcNow().UtcDateTime.Date;
        var entries = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "movie" && entry.TargetLibraryId == libraryId && (entryId == null || entry.Id == entryId))
            .OrderBy(entry => entry.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = entries.Select(entry => entry.Id).ToArray();
        var bound = (await database.EntryBindings.AsNoTracking().Where(binding => ids.Contains(binding.EntryId))
                .Select(binding => new { binding.EntryId, binding.JellyfinItemId }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(binding => binding.EntryId, binding => binding.JellyfinItemId);
        var importing = (await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId != null && ids.Contains(operation.EntryId.Value) && ImportStates.Open.Contains(operation.State))
            .Select(operation => operation.EntryId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var movies = library.GetItemList(new InternalItemsQuery
        {
            Parent = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.Movie], IsVirtualItem = false, GroupByPresentationUniqueKey = false
        }).OfType<Movie>().ToArray();
        var real = movies.Where(movie => !PlaceholderItems.IsOwn(movie)).ToArray();
        var own = movies.Where(PlaceholderItems.IsOwn).ToDictionary(movie => movie.Path, StringComparer.Ordinal);
        var parent = PlaceholderItems.LibraryFolder(library, libraryId);
        var unscanned = real.Length == 0 && entries.Any(entry => entry.State == FileState.OnDisk);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mine = own.GetValueOrDefault(PlaceholderItems.MoviePath(libraryId, entry.TmdbId));
            var twin = real.FirstOrDefault(movie => TmdbIdOf(movie) == entry.TmdbId) ?? real.FirstOrDefault(movie => bound[entry.Id].Contains(movie.Id));
            // A real movie Jellyfin has not refreshed yet keeps the placeholder until it has (see the episodes above).
            if (twin is not null && !Ready(twin))
            {
                if (mine is not null)
                {
                    result.Existing++;
                    result.Covered.Add(entry.Id);
                }

                continue;
            }
            var release = Metadata(entry.MetadataJson)?.PremiereDate;
            // Released, or it had a file (a title found in the library may have no release date in the catalog).
            var released = entry.State is FileState.Reclaimed or FileState.OnDisk || release is { } date && date.Date <= today ||
                release is null && entry.Year is { } year && year < today.Year;
            if (twin is not null || !released)
            {
                if (mine is not null) result.Removals.Add(new(mine, twin is not null ? "on_disk" : "not_released", twin));
                continue;
            }

            if (mine is not null)
            {
                result.Existing++;
                result.Covered.Add(entry.Id);
                continue;
            }

            if (importing.Contains(entry.Id))
            {
                result.Skipped.Add(new(entry.Id, entry.Title, null, 1, "importing",
                    "The movie is being downloaded or imported; its file takes the place of a placeholder."));
                continue;
            }

            if (unscanned && entry.State == FileState.OnDisk)
            {
                result.Skipped.Add(new(entry.Id, entry.Title, null, 1, "library_not_scanned",
                    "Jellyfin holds none of this library's movies yet; a library scan comes first."));
                continue;
            }

            if (parent is null)
            {
                result.Skipped.Add(new(entry.Id, entry.Title, null, 1, "library_folder_missing",
                    "Jellyfin holds no folder of this library yet (a library scan makes it)."));
                continue;
            }

            result.Works.Add(new(entry.Id, entry.Title, PlaceholderKinds.Movie, TitleInfo(entry), Guid.Empty, false, []));
        }

        if (entryId is null)
        {
            var listedTmdb = (await database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie" && entry.TargetLibraryId == libraryId)
                .Select(entry => entry.TmdbId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            foreach (var orphan in own.Values.Where(movie => PlaceholderItems.TmdbIdOfPath(movie.Path) is not { } tmdb || !listedTmdb.Contains(tmdb)))
                result.Removals.Add(new(orphan, "not_listed", null));
        }

        return result;
    }

    /// <summary>Whether Jellyfin has refreshed an item at least once (a new item's series id and numbers are certain only then).</summary>
    private static bool Ready(BaseItem item) => item.DateLastRefreshed != DateTime.MinValue;

    private static PlaceholderTitleInfo TitleInfo(Entry entry)
    {
        var metadata = Metadata(entry.MetadataJson);
        return new(entry.TmdbId, entry.Title, entry.Year, entry.Overview ?? metadata?.Overview, metadata?.PremiereDate, metadata?.RuntimeMinutes,
            entry.ImdbId ?? metadata?.ImdbId, metadata?.TvdbId);
    }
}
