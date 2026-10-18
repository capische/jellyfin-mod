using System.Collections.Concurrent;
using System.Threading.Channels;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// What Jellyfin announced as removed, captured on its event thread (P4.FU12): enough to find the bindings, the catalog entry
/// and the folder the title had, without asking Jellyfin for an item that is gone. <see cref="Kind"/> is <c>movie</c>,
/// <c>series</c>, <c>season</c>, <c>episode</c> or <c>missing</c> (a binding whose file is gone, found at start).
/// </summary>
internal sealed record RemovedItem(Guid Id, string Kind, string Path, Guid SeriesId, int? Tmdb, string? Rating = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// Brings a title's placeholders back at once after Jellyfin removes a movie, series, season or episode (P1.FU29, user 2026-10-17:
/// after <i>Delete media</i> on a season "I should see the season with no files on disk"; P4.FU12: within seconds). Jellyfin
/// announces the removals (every descendant, then the deleted item, or the item alone: both are handled) on its own thread; this
/// only records them. Within <see cref="Window"/> of the first one the batch is repaired, per library and per title, never by a
/// library-wide pass:
/// <list type="number">
/// <item>under the library lease, the bindings the removals name whose files are provably gone are cleared
/// (<see cref="ReconciliationService.ConfirmAbsenceInScopeUnderLeaseAsync"/>), and each affected title's placeholders are written
/// where the title was (<see cref="PlaceholderService.SyncEntryUnderLeaseAsync"/>);</item>
/// <item>once the lease is released, Jellyfin is made to hold the written folders (<see cref="IMediaDiscovery"/>).</item>
/// </list>
/// Removals the plugin made itself (the import's swap, a stale stub, Remove Placeholders, Remove Entry) are expected once each and
/// start nothing. At start, bindings whose files are gone (a delete made while the server was down) are repaired the same way.
/// A title that left the catalog, a library turned off, a read-only folder, a file already in place or storage that cannot be
/// proved gone gets nothing, as in every sync.
/// </summary>
public sealed class DeleteRepair(ILibraryManager library, IServiceScopeFactory scopeFactory, TimeProvider time,
    ILogger<DeleteRepair> logger, DatabaseInitializer? readiness = null, IMediaDiscovery? discovery = null,
    LibraryNotifications? notifications = null) : IHostedService
{
    /// <summary>
    /// How long after the first removal the batch is taken: a season, or a series with its seasons, is announced within
    /// milliseconds, so this makes it one repair. Fixed from the first removal; later ones wait for the next batch.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether start-up also runs one bounded placeholder sync (on in the plugin; a test that controls its syncs turns it off).</summary>
    public static bool SyncAtStart { get; set; } = true;

    private readonly ConcurrentQueue<RemovedItem> pending = new();
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        library.ItemRemoved += OnItemRemoved;
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        _ = Task.Run(() => StartupRepairAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemRemoved -= OnItemRemoved;
        signal.Writer.TryComplete();
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null) await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs eventArgs)
    {
        // Cheap and never throwing, on Jellyfin's thread: a stop of every later subscriber would follow a throw.
        try
        {
            if (eventArgs.Item is not { } item || item.IsVirtualItem || string.IsNullOrEmpty(item.Path)) return;
            (string? kind, Guid seriesId) = item switch
            {
                Movie => ((string?)"movie", Guid.Empty),
                Series => ("series", item.Id),
                Season season => ("season", season.SeriesId),
                JellyfinEpisode episode => ((string?)"episode", episode.SeriesId),
                _ => ((string?)null, Guid.Empty)
            };
            if (kind is null) return;
            // The plugin's own removals: by item id when it deleted the item itself, else by path (the import's swap). Both are taken.
            var byItem = PlaceholderStub.ConsumeExpectedItem(item.Id);
            var byPath = PlaceholderStub.ConsumeExpectedRemoval(item.Path);
            if (byItem || byPath)
            {
                logger.LogDebug("JellyfinMod expected the removal of {Path}", item.Path);
                return;
            }

            int? tmdb = item.ProviderIds.TryGetValue("Tmdb", out var raw) &&
                int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            pending.Enqueue(new(item.Id, kind, item.Path, seriesId, tmdb, item.CustomRating ?? item.OfficialRating, item.Tags));
            logger.LogDebug("JellyfinMod noted the removal of a {Kind}: {Path}", kind, item.Path);
            signal.Writer.TryWrite(true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug(error, "JellyfinMod could not note a removal");
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                signal.Reader.TryRead(out _);
                await Task.Delay(Window, time, cancellationToken).ConfigureAwait(false);
                // Nothing is read or written until the plugin database is migrated.
                while (readiness is { IsReady: false }) await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
                var batch = new List<RemovedItem>();
                while (pending.TryDequeue(out var item)) batch.Add(item);
                if (batch.Count == 0) continue;
                logger.LogInformation("JellyfinMod delete repair batch of {Count}: {Items}", batch.Count,
                    string.Join("; ", batch.Take(6).Select(item => item.Kind + " " + item.Path)));
                try
                {
                    await RepairAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning("JellyfinMod could not bring back placeholders after a delete; the next sync does: {Error}", error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Repairs what a batch of removals left, library by library.</summary>
    private async Task RepairAsync(IReadOnlyList<RemovedItem> batch, CancellationToken cancellationToken)
    {
        var scanning = ScanRunning();
        var libraries = library.GetVirtualFolders().Where(folder => folder.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
            .Select(folder => (Ok: Guid.TryParse(folder.ItemId, out var id), Id: id, Locations: (IReadOnlyList<string>)(folder.Locations ?? []),
                Movies: folder.CollectionType == CollectionTypeOptions.movies))
            .Where(folder => folder.Ok).ToArray();
        foreach (var folder in libraries)
        {
            var mine = batch.Where(item => MediaStorageIdentity.IsWithin(item.Path, folder.Locations)).ToArray();
            if (mine.Length == 0) continue;
            await RepairLibraryAsync(folder.Id, folder.Locations, folder.Movies, mine, cancellationToken).ConfigureAwait(false);
        }

        // A library scan runs beside the repair and may race it: once it ends the same scope is looked at again (the repair is
        // idempotent, so a second look finds nothing to do when the first converged).
        if (scanning)
            _ = Task.Run(async () =>
            {
                for (var waited = 0; waited < 900 && ScanRunning(); waited++) await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken).ConfigureAwait(false);
                foreach (var item in batch) pending.Enqueue(item);
                signal.Writer.TryWrite(true);
            }, CancellationToken.None);
    }

    private async Task RepairLibraryAsync(Guid libraryId, IReadOnlyList<string> locations, bool movies, IReadOnlyList<RemovedItem> items,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<ModDbContext>();
        var libraryLock = services.GetRequiredService<ReconciliationLibraryLock>();
        var reconciliation = services.GetRequiredService<ReconciliationService>();
        var placeholders = services.GetRequiredService<PlaceholderService>();
        // A root event subsumes the descendants Jellyfin announced before it: only the outermost paths are looked at, and an
        // ancestor's id names the same bindings by id too.
        var paths = items.Select(item => Path.TrimEndingDirectorySeparator(item.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var outermost = paths.Where(path => !paths.Any(other => other != path && path.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToList();
        var removed = new RemovedScope(items.Where(item => item.Kind is "movie" or "series" or "episode" or "missing").Select(item => item.Id).Distinct().ToList(), outermost);
        var started = time.GetTimestamp();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var written = new List<string>();
        var titles = 0;
        await using (await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false))
        {
            if (!LibraryStorageProbe.IsAvailable(locations, out var unavailable))
            {
                logger.LogInformation("JellyfinMod leaves a removal alone: {Detail}", unavailable);
                return;
            }

            var absence = await reconciliation.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations, removed, cancellationToken).ConfigureAwait(false);
            var entryIds = new HashSet<Guid>(absence.EntryIds);
            var mediaType = movies ? "movie" : "series";
            // A title with no binding (placeholders only) is found by the series the removal belongs to, or by its TMDB id.
            var nativeIds = items.Select(item => item.Id).Concat(items.Where(item => item.SeriesId != Guid.Empty).Select(item => item.SeriesId)).Distinct().ToList();
            entryIds.UnionWith(await database.EntryBindings.AsNoTracking().Where(binding => binding.TargetLibraryId == libraryId &&
                nativeIds.Contains(binding.JellyfinItemId)).Select(binding => binding.EntryId).ToListAsync(cancellationToken).ConfigureAwait(false));
            var tmdbIds = items.Where(item => item.Kind == mediaType && item.Tmdb.HasValue).Select(item => item.Tmdb!.Value).Distinct().ToList();
            if (tmdbIds.Count > 0)
                entryIds.UnionWith(await database.Entries.AsNoTracking().Where(entry => entry.TargetLibraryId == libraryId &&
                    entry.MediaType == mediaType && tmdbIds.Contains(entry.TmdbId)).Select(entry => entry.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
            var entries = await database.Entries.AsNoTracking().Where(entry => entryIds.Contains(entry.Id) && entry.TargetLibraryId == libraryId &&
                entry.MediaType == mediaType).Select(entry => new { entry.Id, entry.TmdbId }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                // The folder the title had, from the removal of the title itself (a series' folder, a movie's folder).
                var own = items.FirstOrDefault(item => item.Kind == mediaType && item.Tmdb == entry.TmdbId);
                var preferred = own is null ? null : movies ? Path.GetDirectoryName(own.Path) : own.Path;
                var result = await placeholders.SyncEntryUnderLeaseAsync(libraryId, entry.Id, preferred, cancellationToken).ConfigureAwait(false);
                changed.UnionWith(result.ChangedFolders);
                written.AddRange(result.Written.Select(write => write.Path));
                titles++;
            }

            logger.LogInformation("JellyfinMod repaired a delete in library {Library}: {Bindings} binding(s) cleared, {Titles} title(s) looked at, {Unverified} not provable, {Folders} folder(s) changed",
                libraryId, absence.RemovedBindings, titles, absence.Unverified.Count, changed.Count);
        }

        // The clients are told twice (P4.FU12): at once, that the items are gone and the catalog's own rows stand in, and once
        // Jellyfin holds the placeholders, that they are there. Each is one early LibraryChanged, sent only to who may see it.
        var (rating, tags) = RatingOf(items);
        var removedIds = items.Where(item => item.Kind != "missing").Select(item => item.Id).Distinct().ToArray();
        notifications?.Notify(new(libraryId, movies, [], removedIds, [libraryId], rating, tags));

        // Jellyfin is made to hold what was written only after the lease is free, so an import or an Add is never made to wait for it.
        if (changed.Count > 0 && discovery is not null)
        {
            await discovery.DiscoverAsync(libraryId, written.Concat(changed).ToArray(), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("JellyfinMod delete repair in library {Library} took {Seconds:F1} s until Jellyfin held the placeholders", libraryId,
                time.GetElapsedTime(started).TotalSeconds);
            var addedIds = written.Select(path => library.FindByPath(path, false)?.Id).OfType<Guid>().ToArray();
            var folderIds = changed.Select(path => library.FindByPath(path, true)?.Id).OfType<Guid>().Append(libraryId).Distinct().ToArray();
            notifications?.Notify(new(libraryId, movies, addedIds, [], folderIds, rating, tags));
        }
    }

    /// <summary>
    /// The rating and tags a removed title had, for deciding who is told its ids are gone: the removed series or movie itself, else the
    /// series a removed season or episode belongs to (while Jellyfin still holds it), else nothing.
    /// </summary>
    private (string? Rating, IReadOnlyList<string>? Tags) RatingOf(IReadOnlyList<RemovedItem> items)
    {
        var title = items.FirstOrDefault(item => item.Kind is "movie" or "series");
        if (title is not null) return (title.Rating, title.Tags);
        try
        {
            if (items.Select(item => item.SeriesId).FirstOrDefault(id => id != Guid.Empty) is { } seriesId && library.GetItemById(seriesId) is { } series)
                return (series.CustomRating ?? series.OfficialRating, series.Tags);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug(error, "JellyfinMod could not read the rating of a removed title");
        }

        return (null, null);
    }

    /// <summary>
    /// At start, once the database is ready: bindings whose files are gone (a delete made while the server was down, or a folder
    /// removed on disk) are repaired the same way as a removal. Their files are looked at for each library whose storage can be
    /// read; Jellyfin is also made to look at the nearest folder that is still there, so it drops the items it still lists.
    /// </summary>
    private async Task StartupRepairAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (readiness is { IsReady: false }) await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
            var libraries = library.GetVirtualFolders().Where(folder => folder.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
                .Select(folder => (Ok: Guid.TryParse(folder.ItemId, out var id), Id: id, Locations: (IReadOnlyList<string>)(folder.Locations ?? []),
                    Movies: folder.CollectionType == CollectionTypeOptions.movies))
                .Where(folder => folder.Ok).ToArray();
            foreach (var folder in libraries)
            {
                if (!LibraryStorageProbe.IsAvailable(folder.Locations, out _)) continue;
                var gone = new List<RemovedItem>();
                using (var scope = scopeFactory.CreateScope())
                {
                    var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
                    var bound = await database.EntryBindings.AsNoTracking().Where(binding => binding.TargetLibraryId == folder.Id)
                        .Select(binding => new { binding.JellyfinItemId, binding.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false);
                    bound.AddRange(await database.EpisodeBindings.AsNoTracking().Where(binding => binding.TargetLibraryId == folder.Id)
                        .Select(binding => new { binding.JellyfinItemId, binding.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false));
                    foreach (var binding in bound)
                        if (!string.IsNullOrEmpty(binding.MediaPath) && MediaStorageIdentity.IsWithin(binding.MediaPath, folder.Locations) &&
                            !File.Exists(binding.MediaPath) && !Directory.Exists(binding.MediaPath))
                            gone.Add(new(binding.JellyfinItemId, "missing", binding.MediaPath, Guid.Empty, null));
                }

                if (gone.Count == 0) continue;
                logger.LogInformation("JellyfinMod found {Count} bound file(s) gone from library {Library} at start; repairing them", gone.Count, folder.Id);
                await RepairLibraryAsync(folder.Id, folder.Locations, folder.Movies, gone, cancellationToken).ConfigureAwait(false);
                // Jellyfin may still list them: it looks at the nearest folder that is there and drops what is gone.
                var ancestors = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var item in gone)
                {
                    var ancestor = Path.GetDirectoryName(item.Path);
                    while (ancestor is not null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);
                    if (ancestor is not null && MediaStorageIdentity.IsWithin(ancestor, folder.Locations)) ancestors.Add(ancestor);
                }

                if (ancestors.Count > 0 && discovery is not null)
                    await discovery.DiscoverAsync(folder.Id, ancestors.Take(20).ToArray(), cancellationToken).ConfigureAwait(false);
            }

            // And the normal bounded sync once, for titles with no binding (placeholders only) whose stubs went while the server was
            // down; it yields to a library scan and writes a few titles per library, the schedule finishing the rest.
            if (SyncAtStart) await PlaceholderBackfill.TryRunAsync(scopeFactory, library, time, logger, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not repair deletes made while it was not running; the next scan does: {Error}", error.Message);
        }
    }

    private bool ScanRunning()
    {
        try
        {
            return library.IsScanRunning;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            return false;
        }
    }
}
