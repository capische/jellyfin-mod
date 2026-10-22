using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>One stub the plugin would write (or wrote): where, and for which episode.</summary>
public sealed record PlaceholderWrite(
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("episodeNumber")] int EpisodeNumber,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("newFolder")] bool NewFolder,
    [property: JsonPropertyName("kind")] string Kind = PlaceholderKinds.Episode);

/// <summary>What a placeholder stands for.</summary>
public static class PlaceholderKinds
{
    /// <summary>A missing episode of a series (P1.FU24), or of a series with no file at all (P1.FU26).</summary>
    public const string Episode = "episode";

    /// <summary>A movie with no file (P1.FU26): <c>Title (Year) [tmdbid-N]/Title (Year) [tmdbid-N].disc</c>.</summary>
    public const string Movie = "movie";
}

/// <summary>One stub the plugin would remove (or removed), and why.</summary>
public sealed record PlaceholderRemoval(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Missing episodes the plugin leaves without a stub, and why (the fallback: the mod's own cards and rows).</summary>
public sealed record PlaceholderSkip(
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("seasonNumber")] int? SeasonNumber,
    [property: JsonPropertyName("episodes")] int Episodes,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>What a sync of one library does, or did: the dry run and the run share this shape.</summary>
public sealed record PlaceholderPlan(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("libraryName")] string LibraryName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("existing")] int Existing,
    [property: JsonPropertyName("writes")] IReadOnlyList<PlaceholderWrite> Writes,
    [property: JsonPropertyName("removals")] IReadOnlyList<PlaceholderRemoval> Removals,
    [property: JsonPropertyName("skipped")] IReadOnlyList<PlaceholderSkip> Skipped)
{
    /// <summary>Gets the paths the run could not write or remove, with the error; empty for a dry run.</summary>
    [JsonPropertyName("failures")]
    public IReadOnlyList<PlaceholderRemoval> Failures { get; init; } = [];

    /// <summary>
    /// Gets how many placeholders a run left for the next one: a run writes at most <see cref="PlaceholderService.SeriesPerRun"/>
    /// series. A dry run lists all of its writes and counts them all here.
    /// </summary>
    [JsonPropertyName("pending")]
    public int Pending { get; init; }

    /// <summary>
    /// Gets how many titles (series and movies) stand for missing items with placeholders: those that have one and those still
    /// waiting for theirs (P1.FU27, the backfill's progress). A title the plugin cannot write for (a read-only folder) is not
    /// counted: the mod's own cards and rows stay its surface.
    /// </summary>
    [JsonPropertyName("titles")]
    public int Titles { get; init; }

    /// <summary>Gets how many of <see cref="Titles"/> have every placeholder written.</summary>
    [JsonPropertyName("titlesDone")]
    public int TitlesDone { get; init; }

    /// <summary>
    /// Gets why a run wrote less than its batch allows, or null: <c>library_scan</c> while Jellyfin scans a library (the batch
    /// waits for the next run, P1.FU27).
    /// </summary>
    [JsonPropertyName("deferred")]
    public string? Deferred { get; init; }
}

/// <summary>What the delete repair's sync of one title did: the stubs written, how many were removed, what failed, the folders changed.</summary>
public sealed record EntryPlaceholders(IReadOnlyList<PlaceholderWrite> Written, int Removed, IReadOnlyList<PlaceholderRemoval> Failures,
    IReadOnlyCollection<string> ChangedFolders);

/// <summary>A TV or movie library and its placeholder switch.</summary>
public sealed record PlaceholderLibrary(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("stubs")] int Stubs,
    [property: JsonPropertyName("writable")] bool Writable,
    [property: JsonPropertyName("mediaType")] string MediaType = "series");

/// <summary>
/// Placeholders for missing episodes (P1.FU24, user-approved 2026-10-15: "Go and implement isPlaceHolder"). For a series
/// Jellyfin already holds, every aired episode the plugin lists without a file gets a Jellyfin media stub where the import would
/// put its file (<c>Season NN/Series (Year) SNNENN.disc</c>), so Jellyfin shows it as a real episode with ✓, ♥, its metadata and
/// counts, for every client, and nothing plays it. On by default for every TV library, which an administrator can turn off one
/// by one (user, 2026-10-15: "Show missing episodes - should be enabled by default. That's the point."); a dry run lists exactly
/// what a sync would write. A sync writes a bounded batch (<see cref="SeriesPerRun"/> series, a pause between them) and leaves the
/// rest for the next run, so a large library never writes thousands of files, and sets off as many scans, at once.
/// </summary>
/// <remarks>
/// <para>Writes only inside a library root the import may write to, into the series folder the entry's files are in; a
/// read-only folder gets nothing (the mod's own cards and rows stay its surface). Never overwrites, never follows a link out of
/// the library, never writes beside a file that already is the episode. Each stub is complete before it appears (a hidden temp
/// file, then <c>link(2)</c>, which refuses an existing name).</para>
/// <para>Removes only its own stubs (the marker line, <see cref="PlaceholderStub"/>), each unlinked by its inode, and a season
/// folder only when removing a stub left it empty.</para>
/// </remarks>
public sealed class PlaceholderService(
    ModDbContext database,
    ILibraryManager library,
    ILibraryMonitor libraryMonitor,
    UnixFileInspector files,
    ReconciliationLibraryLock libraryLock,
    TimeProvider time,
    ILogger<PlaceholderService> logger,
    IMediaDiscovery? discovery = null,
    LibraryNotifications? notifications = null)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "ts", "m2ts", "webm", "mpg", "mpeg", "flv", "iso", "vob", "ogv", "3gp", "divx",
        "xvid", "rmvb", "strm", "disc", "dvd", "bluray", "hddvd"
    };

    /// <summary>The most series one sync writes placeholders for; the rest wait for the next run.</summary>
    public const int SeriesPerRun = 10;

    /// <summary>The pause between two series of one run, so Jellyfin scans each series' folders before the next.</summary>
    public static readonly TimeSpan PauseBetweenSeries = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The TV libraries, each with its switch, the plugin's stubs Jellyfin holds in it, and whether any of its folders can be
    /// written (a library whose every folder is read-only gets nothing).
    /// </summary>
    public IReadOnlyList<PlaceholderLibrary> Libraries()
    {
        var disabled = PlaceholderSettings.DisabledLibraries();
        return library.GetVirtualFolders().Where(folder => folder.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? new PlaceholderLibrary(id, folder.Name, !disabled.Contains(id),
                StubItems(id).Count, SeriesFolders.LibraryRoots(library, files, id).Any(files.CanCreateIn),
                folder.CollectionType == CollectionTypeOptions.movies ? "movie" : "series") : null)
            .OfType<PlaceholderLibrary>().OrderBy(item => item.MediaType == "movie" ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether a library has anything to sync: its switch is on, or Jellyfin holds stubs of the plugin's in it.</summary>
    public bool NeedsSync(Guid libraryId) => PlaceholderSettings.IsEnabled(libraryId) || StubItems(libraryId).Count > 0;

    /// <summary>What a sync of the library would write and remove; nothing is changed.</summary>
    public async Task<PlaceholderPlan> PlanAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var plan = await ComputeAsync(libraryId, null, null, cancellationToken).ConfigureAwait(false);
        var waiting = plan.Writes.Select(write => write.EntryId).ToHashSet();
        var titles = plan.Covered.Union(waiting).Count();
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), true, plan.Existing, plan.Writes, plan.Removals, plan.Skipped)
        {
            Pending = plan.Writes.Count, Titles = titles, TitlesDone = titles - waiting.Count
        };
    }

    /// <summary>
    /// Brings the library's stubs up to date when its switch is on: writes the missing ones of at most
    /// <see cref="SeriesPerRun"/> series, pausing between series, removes the ones whose episode now has a file or is no longer
    /// missing, asks Jellyfin to look at each series' changed folders, and stamps the stub items Jellyfin already holds. The rest
    /// is written by the next run (<see cref="PlaceholderPlan.Pending"/>). With the switch off it only stamps and removes stale
    /// stubs, never writes.
    /// </summary>
    public Task<PlaceholderPlan> SyncAsync(Guid libraryId, CancellationToken cancellationToken) =>
        SyncAsync(libraryId, null, false, cancellationToken);

    /// <summary>
    /// The backfill's batch for one library (P1.FU27): <see cref="SyncAsync(Guid, CancellationToken)"/>, but it gives way to
    /// Jellyfin's own library scans. While one runs it writes and removes nothing (<see cref="PlaceholderPlan.Deferred"/>
    /// <c>library_scan</c>), and a scan that starts during the batch ends it after the series in hand; the rest is pending for the
    /// next run.
    /// </summary>
    public Task<PlaceholderPlan> SyncBatchAsync(Guid libraryId, CancellationToken cancellationToken) =>
        SyncAsync(libraryId, null, true, cancellationToken);

    /// <summary>
    /// Syncs one entry's placeholders at once (P1.FU26): a title just added, or a series whose episode list just loaded, becomes
    /// a Jellyfin item without waiting for the next scheduled sync. Same rules as <see cref="SyncAsync(Guid, CancellationToken)"/>.
    /// </summary>
    public Task<PlaceholderPlan> SyncEntryAsync(Guid libraryId, Guid entryId, CancellationToken cancellationToken) =>
        SyncAsync(libraryId, entryId, false, cancellationToken);

    private bool ScanRunning()
    {
        try
        {
            return library.IsScanRunning;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Not knowing is not a reason to stop: the batch runs as before the guard.
            logger.LogDebug(error, "JellyfinMod could not read whether a library scan runs");
            return false;
        }
    }

    private async Task<PlaceholderPlan> SyncAsync(Guid libraryId, Guid? entryId, bool yieldToScans, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var enabled = PlaceholderSettings.IsEnabled(libraryId);
        // The library lease is held for the plan and the removals, and for each title's writes; never across the pause between
        // titles, so a delete's repair or an import is never made to wait for a batch's three seconds (P4.FU12).
        IAsyncDisposable? lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await ComputeAsync(libraryId, entryId, null, cancellationToken).ConfigureAwait(false);
            var (writes, removals, skipped, existing) = (plan.Writes, plan.Removals, plan.Skipped, plan.Existing);
            // With the switch off nothing is written, so only the titles that have placeholders count.
            var titleIds = enabled ? plan.Covered.Union(writes.Select(write => write.EntryId)).ToHashSet() : plan.Covered.ToHashSet();
            if (yieldToScans && ScanRunning())
            {
                // Jellyfin is scanning: nothing is written or removed now (each would ask for another scan); the next run does it.
                var waiting = writes.Select(write => write.EntryId).ToHashSet();
                logger.LogInformation("JellyfinMod placeholders in library {Library}: a library scan is running, so this batch waits for the next run ({Pending} placeholder(s) pending)",
                    libraryId, writes.Count);
                return new(libraryId, name, enabled, false, existing, [], [], skipped)
                {
                    Pending = enabled ? writes.Count : 0, Titles = titleIds.Count, TitlesDone = titleIds.Count - (enabled ? waiting.Count : 0),
                    Deferred = "library_scan"
                };
            }

            var failures = new List<PlaceholderRemoval>();
            // Every folder the run changed, asked of Jellyfin once at its end (P1.FU27): one look per folder, never one per file.
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var done = new List<PlaceholderWrite>();
            string? deferred = null;
            var removedStubs = new List<string>();
            var removedFolders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var removal in removals)
            {
                var error = RemoveOne(removal.Path, changed, removedFolders);
                if (error is not null) failures.Add(new(removal.Path, error));
                else removedStubs.Add(removal.Path);
            }

            // The stub items go from Jellyfin now, before this run's writes ask it to look at the same folders.
            if (removedStubs.Count > 0) DropItems(libraryId, removedStubs, removedFolders);

            var pending = 0;
            if (enabled)
            {
                var series = writes.GroupBy(write => write.EntryId).ToArray();
                var batch = series.Take(SeriesPerRun).ToArray();
                pending = series.Skip(SeriesPerRun).Sum(group => group.Count());
                for (var index = 0; index < batch.Length; index++)
                {
                    var group = batch[index];
                    var titleWrites = group.ToList();
                    if (index > 0)
                    {
                        // A pause between series, so the disk and Jellyfin's file monitor take one series at a time, outside the lease.
                        await lease.DisposeAsync().ConfigureAwait(false);
                        lease = null;
                        await Task.Delay(PauseBetweenSeries, time, cancellationToken).ConfigureAwait(false);
                        // A scan that started meanwhile ends the batch: the series not written yet wait for the next run.
                        if (yieldToScans && ScanRunning())
                        {
                            deferred = "library_scan";
                            pending += batch.Skip(index).Sum(rest => rest.Count());
                            logger.LogInformation("JellyfinMod placeholders in library {Library}: a library scan started, so the rest of this batch waits for the next run",
                                libraryId);
                            break;
                        }

                        lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
                        // What the title needs is read again under the lease: it may have left the catalog, gained a file or been
                        // turned off while the lease was free.
                        if (!PlaceholderSettings.IsEnabled(libraryId)) { pending += titleWrites.Count; continue; }
                        titleWrites = (await ComputeAsync(libraryId, group.Key, null, cancellationToken).ConfigureAwait(false)).Writes;
                    }

                    var written = await WriteTitleAsync(titleWrites, done, failures, changed, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "JellyfinMod placeholders in library {Library}: wrote {Written} for {Title} (series {Index} of {Count} this run, {Pending} placeholder(s) left for the next run)",
                        libraryId, written, group.First().Title, index + 1, batch.Length, pending);
                }
            }

            var reported = ReportOnce(libraryId, changed, done.Select(write => write.Path));
            await StampAsync(libraryId, cancellationToken).ConfigureAwait(false);
            if (done.Count > 0 || removals.Count > 0)
                logger.LogInformation("JellyfinMod placeholders in library {Library}: wrote {Written}, removed {Removed}, {Failed} failed, {Pending} left for the next run; asked Jellyfin to look at {Folders} folder(s) once",
                    libraryId, done.Count, removals.Count - failures.Count(failure => removals.Any(removal => removal.Path == failure.Path)),
                    failures.Count, pending, reported);
            // A title is waiting while a write of it is pending or failed.
            var writtenPaths = done.Select(write => write.Path).ToHashSet(StringComparer.Ordinal);
            var waitingTitles = enabled ? writes.Where(write => !writtenPaths.Contains(write.Path)).Select(write => write.EntryId).ToHashSet() : [];
            return new(libraryId, name, enabled, false, existing, enabled ? done : [], removals, skipped)
            {
                Failures = failures, Pending = pending, Titles = titleIds.Count, TitlesDone = titleIds.Count - waitingTitles.Count, Deferred = deferred
            };
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One title's placeholders at once, for the delete repair (P4.FU12), while the caller holds the library lease: its plan is read
    /// afresh, its stale stubs removed and its missing ones written, in the folder the title had before when that is still free
    /// (<paramref name="preferredFolder"/>). Nothing is reported to Jellyfin: the caller discovers the returned folders once its
    /// lease is released. A title that left the catalog, a library turned off, a read-only folder or a file already in place gets
    /// nothing, as in every sync.
    /// </summary>
    public async Task<EntryPlaceholders> SyncEntryUnderLeaseAsync(Guid libraryId, Guid entryId, string? preferredFolder,
        CancellationToken cancellationToken)
    {
        _ = LibraryOf(libraryId);
        var plan = await ComputeAsync(libraryId, entryId, preferredFolder, cancellationToken).ConfigureAwait(false);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<PlaceholderRemoval>();
        var done = new List<PlaceholderWrite>();
        foreach (var removal in plan.Removals)
        {
            var error = RemoveOne(removal.Path, changed);
            if (error is not null) failures.Add(new(removal.Path, error));
        }

        if (PlaceholderSettings.IsEnabled(libraryId))
            await WriteTitleAsync(plan.Writes, done, failures, changed, cancellationToken).ConfigureAwait(false);
        return new(done, plan.Removals.Count - failures.Count(failure => plan.Removals.Any(removal => removal.Path == failure.Path)),
            failures, changed);
    }

    /// <summary>Writes the stubs of one title (an entry's writes), recording what was written, what failed and the folders changed.</summary>
    private async Task<int> WriteTitleAsync(IReadOnlyList<PlaceholderWrite> writes, List<PlaceholderWrite> done,
        List<PlaceholderRemoval> failures, HashSet<string> changed, CancellationToken cancellationToken)
    {
        if (writes.Count == 0) return 0;
        var entryId = writes[0].EntryId;
        var titles = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId)
            .Select(episode => new { episode.SeasonNumber, episode.EpisodeNumber, episode.Title }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var year = await database.Entries.AsNoTracking().Where(entry => entry.Id == entryId).Select(entry => entry.Year)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var write in writes)
        {
            var title = titles.FirstOrDefault(row => row.SeasonNumber == write.SeasonNumber && row.EpisodeNumber == write.EpisodeNumber)?.Title;
            var body = write.Kind == PlaceholderKinds.Movie
                ? PlaceholderStub.MovieBody(write.Title, year)
                : PlaceholderStub.Body(write.Title, write.SeasonNumber, write.EpisodeNumber, title);
            var error = WriteOne(write, body, changed);
            if (error is null)
            {
                done.Add(write);
                written++;
            }
            else
            {
                failures.Add(new(write.Path, error));
            }
        }

        return written;
    }

    /// <summary>
    /// Asks Jellyfin to look at the folders a run changed, once each and only the outermost (a season folder inside a reported
    /// series folder is left out): Jellyfin's file monitor waits its library monitor delay, merges folders that sit side by side
    /// into one look at their parent, and refreshes each item found once. Returns how many folders were reported.
    /// </summary>
    private int ReportOnce(Guid libraryId, IEnumerable<string> folders, IEnumerable<string> writtenFiles)
    {
        var sorted = folders.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var outermost = new List<string>();
        foreach (var folder in sorted)
            if (!outermost.Any(parent => folder.StartsWith(parent + "/", StringComparison.Ordinal))) outermost.Add(folder);
        // And at once (P4.FU12): Jellyfin picks the changed folders and the files written in them up now, not after its library
        // monitor delay (a library folder it holds no item for is made by its discovery); the report below stays as the fallback.
        // The discovery never faults and nothing here waits for it.
        // The clients are told once Jellyfin holds them (P4.FU12), at once and not with Jellyfin's own 30 s batch.
        if (outermost.Count > 0 && discovery is not null)
        {
            var files = writtenFiles.ToArray();
            _ = discovery.DiscoverAsync(libraryId, outermost.Concat(files).ToArray())
                .ContinueWith(_ => notifications?.NotifyAdded(libraryId, files, outermost), TaskScheduler.Default);
        }

        foreach (var folder in outermost) Report(folder);
        return outermost.Count;
    }

    /// <summary>
    /// Deletes the Jellyfin items of the stubs the plugin has just removed, and of the season and series folders that left empty,
    /// itself and at once (P4.FU12), instead of waiting for Jellyfin's library monitor (60 s, and never for the last folder of an
    /// emptied library folder, which Jellyfin skips). Each item is marked by its id first, so the delete repair knows exactly these
    /// removals are the plugin's own however late Jellyfin announces them. Only a placeholder item at a removed stub's path, and a
    /// series or season of a folder the plugin removed (<paramref name="removedFolders"/>) that is still gone, is deleted (files are
    /// never touched). What cannot be deleted is reported to the library monitor as before. The clients are told at once.
    /// </summary>
    private void DropItems(Guid libraryId, IReadOnlyCollection<string> removedStubs, IReadOnlyCollection<string> removedFolders)
    {
        var doomed = new List<BaseItem>();
        try
        {
            foreach (var path in removedStubs)
                if (library.FindByPath(path, false) is Video { IsPlaceHolder: true } stub) doomed.Add(stub);
            // The folders the plugin removed, deepest first, so a season goes before its series.
            foreach (var folder in removedFolders.Where(path => !Directory.Exists(path)).OrderByDescending(path => path.Length))
                if (library.FindByPath(folder, true) is Folder and (Series or Season) and var item) doomed.Add(item);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug(error, "JellyfinMod could not look up the Jellyfin items of removed placeholders");
        }

        var removedIds = new List<Guid>();
        var folderIds = new List<Guid> { libraryId };
        string? rating = null;
        IReadOnlyList<string>? tags = null;
        var failed = new List<string>();
        foreach (var item in doomed)
        {
            var below = new List<Guid>();
            try
            {
                if (item is Folder folder) below.AddRange(folder.GetRecursiveChildren().Select(child => child.Id));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogDebug(error, "JellyfinMod could not list what lies below a removed folder item");
            }

            var ids = below.Append(item.Id).Distinct().ToArray();
            foreach (var id in ids) PlaceholderStub.ExpectItemRemoval(id);
            try
            {
                rating ??= item.CustomRating ?? item.OfficialRating;
                tags ??= item.Tags;
                if (item.ParentId != Guid.Empty) folderIds.Add(item.ParentId);
                library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, notifyParentItem: true);
                removedIds.AddRange(ids);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                foreach (var id in ids) PlaceholderStub.ForgetExpectedItem(id);
                failed.Add(item.Path ?? string.Empty);
                logger.LogWarning("JellyfinMod could not delete the Jellyfin item of a removed placeholder; its library monitor still will: {Error}", error.Message);
            }
        }

        // What could not be deleted is left to Jellyfin's library monitor, as before.
        foreach (var path in failed.Where(path => path.Length > 0).Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path).Distinct())
            Report(path);
        if (removedIds.Count > 0)
            notifications?.Notify(new(libraryId, notifications.IsMovieLibrary(libraryId), [], removedIds.Distinct().ToArray(), folderIds.Distinct().ToArray(), rating, tags));
    }

    /// <summary>
    /// Waits, under the caller's library lease, for the discoveries of the library asked for before it (P4.FU12): a sync's discovery
    /// still running could otherwise make Jellyfin items for stubs after the removal looked for them, and those items, gone with
    /// their stubs later, would read as a user's deletes and bring the placeholders back. No sync can ask for another while the
    /// lease is held. Bounded, so a stuck discovery delays a removal and never blocks it.
    /// </summary>
    private async Task DiscoveryIdleAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        if (discovery is null) return;
        try
        {
            await discovery.IdleAsync(libraryId).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("JellyfinMod removes placeholders of library {Library} while a discovery is still running", libraryId);
        }
    }

    /// <summary>Every stub of the plugin's in the library: what <see cref="RemoveAllAsync"/> would remove.</summary>
    public async Task<PlaceholderPlan> RemovalPlanAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var paths = await AllStubPathsAsync(libraryId, cancellationToken).ConfigureAwait(false);
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), true, paths.Count, [],
            paths.Select(path => new PlaceholderRemoval(path, "remove_all")).ToArray(), []);
    }

    /// <summary>
    /// Removes every stub of the plugin's in the library (each re-checked for the marker just before), and the season folders
    /// that removing them left empty. Nothing else is ever deleted. Does not change the switch.
    /// </summary>
    public async Task<PlaceholderPlan> RemoveAllAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        await using var lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        await DiscoveryIdleAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var paths = await AllStubPathsAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var removedFolders = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<PlaceholderRemoval>();
        var failures = new List<PlaceholderRemoval>();
        foreach (var path in paths)
        {
            var error = RemoveOne(path, changed, removedFolders);
            if (error is null) removed.Add(new(path, "remove_all"));
            else failures.Add(new(path, error));
        }

        DropItems(libraryId, removed.Select(removal => removal.Path).ToArray(), removedFolders);
        // The surviving parents still go to Jellyfin's library monitor, the fallback for whatever the drop above could not reach.
        foreach (var folder in changed) Report(folder);
        logger.LogInformation("JellyfinMod removed {Removed} placeholder(s) from library {Library}, {Failed} failed", removed.Count,
            libraryId, failures.Count);
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), false, paths.Count, [], removed, []) { Failures = failures };
    }

    /// <summary>
    /// The import's half of the swap (P1.FU25): once the episode's real file is linked and verified at
    /// <paramref name="importedFile"/>, removes the plugin's stubs of that episode from its series folder and every season
    /// folder in it (a stub can sit in another season folder than the one the import used, or beside the series' files), so
    /// Jellyfin sees the stub go and the file arrive in one refresh and carries the user data over by key. Only marked stubs are
    /// removed, each by its inode, and a season folder only when that left it empty. Never throws: a stub that cannot be removed
    /// (a permission) stays for the sync, which removes it once the episode is on disk. Returns the folders it changed besides
    /// the file's own, for the caller to report.
    /// </summary>
    public static IReadOnlyList<string> SwapForImport(UnixFileInspector files, string importedFile, int season, int episode, ILogger logger)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (season <= 0 || Path.GetDirectoryName(importedFile) is not { } fileFolder) return [];
            var seriesFolder = IsSeasonFolderName(Path.GetFileName(fileFolder)) ? Path.GetDirectoryName(fileFolder) : fileFolder;
            if (seriesFolder is null) return [];
            foreach (var folder in StubFolders(files, seriesFolder))
            {
                if (Listing(folder) is not { } listing) continue;
                foreach (var stub in listing.Where(file => file.IsStub && file.Covers(season, episode)))
                {
                    var error = RemoveStub(files, stub.Path, changed);
                    if (error is null)
                        logger.LogInformation("JellyfinMod removed the placeholder of S{Season:00}E{Episode:00} now that its file is in place", season, episode);
                    else
                        logger.LogWarning("JellyfinMod could not remove the placeholder {Path} beside an imported episode; the next placeholder sync removes it: {Error}",
                            stub.Path, error);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("JellyfinMod could not remove a placeholder beside an imported episode; the next placeholder sync removes it: {Error}", error.Message);
        }

        changed.Remove(Path.GetDirectoryName(importedFile)!);
        return changed.ToArray();
    }

    /// <summary>
    /// The import's half of a movie's swap (P1.FU26): once the movie's file is linked and verified at
    /// <paramref name="importedFile"/>, removes the plugin's stubs of the movie from the file's folder and from any title folder of
    /// its TMDB id in the library's folders (a stub written in another library folder than the one the import used), and such a
    /// folder when that left it empty. Only marked stubs, each by its inode. Never throws. Returns the folders it changed
    /// besides the file's own, for the caller to report.
    /// </summary>
    public static IReadOnlyList<string> SwapMovieForImport(UnixFileInspector files, IReadOnlyList<string> roots, string importedFile,
        int tmdbId, ILogger logger)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (Path.GetDirectoryName(importedFile) is not { } fileFolder) return [];
            var folders = new List<string> { fileFolder };
            if (tmdbId > 0 && SeriesFolders.TitleFolderByTmdbId(files, roots, tmdbId) is { } titled && titled != fileFolder) folders.Add(titled);
            foreach (var folder in folders)
            {
                // Never a library folder itself: a stub is only ever in a title's own folder.
                if (roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root), folder, StringComparison.Ordinal))) continue;
                foreach (var stub in Listing(folder)?.Where(file => file.IsStub) ?? [])
                {
                    var error = RemoveStub(files, stub.Path, changed);
                    if (error is null)
                        logger.LogInformation("JellyfinMod removed the movie's placeholder now that its file is in place");
                    else
                        logger.LogWarning("JellyfinMod could not remove the placeholder {Path} beside an imported movie; the next placeholder sync removes it: {Error}",
                            stub.Path, error);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("JellyfinMod could not remove a placeholder beside an imported movie; the next placeholder sync removes it: {Error}", error.Message);
        }

        changed.Remove(Path.GetDirectoryName(importedFile)!);
        return changed.ToArray();
    }

    /// <summary>
    /// Removes an entry's placeholders when an administrator removes the title from the catalog (P1.FU26): every marked stub in
    /// the series' folders or the movie's folders, and the season and title folders that left empty. Nothing else is ever
    /// deleted. The caller holds the library lock. Returns the stubs removed and the ones that could not be.
    /// </summary>
    public async Task<(IReadOnlyList<string> Removed, IReadOnlyList<PlaceholderRemoval> Failures)> RemoveForEntryAsync(Entry entry,
        CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        var failures = new List<PlaceholderRemoval>();
        if (entry.TargetLibraryId is not { } libraryId) return (removed, failures);
        await DiscoveryIdleAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        if (entry.MediaType == "movie")
        {
            foreach (var folder in MovieStubFolders(roots, entry.TmdbId, StubItems(libraryId, entry.TmdbId).OfType<Movie>().ToArray(), []))
                foreach (var stub in Listing(folder)?.Where(file => file.IsStub) ?? [])
                    paths.Add(stub.Path);
        }
        else if (await SeriesFolders.BoundSeriesFolderAsync(database, library, files, entry.Id, roots, cancellationToken).ConfigureAwait(false)
                 is { } seriesFolder)
        {
            foreach (var folder in StubFolders(files, seriesFolder))
                foreach (var stub in Listing(folder)?.Where(file => file.IsStub) ?? [])
                    paths.Add(stub.Path);
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        var removedFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var error = RemoveOne(path, changed, removedFolders);
            if (error is null) removed.Add(path);
            else failures.Add(new(path, error));
        }

        DropItems(libraryId, removed, removedFolders);
        // The surviving parents still go to Jellyfin's library monitor, the fallback for whatever the drop above could not reach.
        foreach (var folder in changed) Report(folder);
        if (paths.Count > 0)
            logger.LogInformation("JellyfinMod removed {Removed} placeholder(s) of a title removed from the catalog, {Failed} failed", removed.Count,
                failures.Count);
        return (removed, failures);
    }

    /// <summary>
    /// The retention half for a movie (P1.FU26): after a reclaim unlinked the movie's last file <paramref name="mediaPath"/> and
    /// recorded it, writes the movie's stub in that same folder (named after the folder), so the movie stays a Jellyfin item with
    /// its user data re-attached by key. Only in a movie library with placeholders on, in a folder inside one of its folders
    /// (never a library folder itself: a loose file gets none), that can be written and holds no other video. Never deletes,
    /// never throws. Returns the stub's path, or null.
    /// </summary>
    public static string? LeaveAfterMovieReclaim(ILibraryManager library, UnixFileInspector files, ILibraryMonitor? libraryMonitor,
        Guid libraryId, string mediaPath, string title, int? year, ILogger logger)
    {
        try
        {
            if (MovieReclaimStubPath(library, files, libraryId, mediaPath) is not { } path) return null;
            var canonical = Path.GetDirectoryName(path)!;
            var error = WriteStub(files, path, PlaceholderStub.MovieBody(title, year));
            if (error is not null)
            {
                logger.LogWarning("JellyfinMod could not leave a placeholder for a reclaimed movie at {Path}: {Error}", path, error);
                return null;
            }

            libraryMonitor?.ReportFileSystemChanged(canonical);
            logger.LogInformation("JellyfinMod left a placeholder for a reclaimed movie in {Folder}", canonical);
            return path;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not leave a placeholder for a reclaimed movie: {Error}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// Where a reclaim of a movie's last file <paramref name="mediaPath"/> leaves the movie's stub (P1.FU26), or null when it
    /// leaves none: a movie library with placeholders on, the file in a folder inside one of its folders (never a library folder
    /// itself), that can be written and holds no other video or stub besides the file itself. The stub is named after the folder.
    /// </summary>
    public static string? MovieReclaimStubPath(ILibraryManager library, UnixFileInspector files, Guid libraryId, string? mediaPath)
    {
        try
        {
            if (string.IsNullOrEmpty(mediaPath) || !PlaceholderSettings.IsEnabled(libraryId) ||
                library.GetVirtualFolders()?.Any(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId &&
                    folder.CollectionType == CollectionTypeOptions.movies) != true) return null;
            var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
            if (Path.GetDirectoryName(mediaPath) is not { } folder || !files.TryCanonicalize(folder, out var canonical)) return null;
            if (!roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal)) || !files.CanCreateIn(canonical))
                return null;
            // Another version or a stub there keeps the movie an item; nothing is written beside it.
            var own = Path.GetFileName(mediaPath);
            if (Listing(canonical) is not { } listing ||
                listing.Any(file => (file.IsStub || file.IsVideo) && !string.Equals(Path.GetFileName(file.Path), own, StringComparison.Ordinal)))
                return null;
            var name = Path.GetFileName(canonical) + "." + PlaceholderStub.Extension;
            var path = Path.Combine(canonical, name);
            return ImportNaming.FitsFileName(name) && files.Probe(path) == PathPresence.Absent ? path : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>One episode a reclaim leaves without a file: its numbers and title, for the stub's name and message.</summary>
    public sealed record ReclaimedEpisode(int SeasonNumber, int EpisodeNumber, string? Title);

    /// <summary>
    /// Where a reclaim of <paramref name="mediaPath"/> leaves an episode's stub (P1.FU25), or null when it leaves none: the
    /// library is a TV library with placeholders on, the episode is not a special, and the file's folder lies inside one of the
    /// library's folders (never a library folder itself) and can be written. The stub takes the import's name for the episode in
    /// that same folder.
    /// </summary>
    public static string? ReclaimStubPath(ILibraryManager library, UnixFileInspector files, Guid libraryId, string? mediaPath,
        int season, int episode) => ReclaimStubPath(files, ReclaimRoots(library, files, libraryId), mediaPath, season, episode);

    /// <summary>
    /// The folders of a library a reclaim may leave stubs in: its readable folders when it is a TV library with placeholders on,
    /// else none. A caller that asks for many files of one library reads this once.
    /// </summary>
    public static IReadOnlyList<string> ReclaimRoots(ILibraryManager library, UnixFileInspector files, Guid libraryId)
    {
        try
        {
            if (!PlaceholderSettings.IsEnabled(libraryId) || library.GetVirtualFolders()?.Any(folder => Guid.TryParse(folder.ItemId, out var id) &&
                    id == libraryId && folder.CollectionType == CollectionTypeOptions.tvshows) != true) return [];
            return SeriesFolders.LibraryRoots(library, files, libraryId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary><see cref="ReclaimStubPath(ILibraryManager, UnixFileInspector, Guid, string?, int, int)"/> with the library's
    /// <see cref="ReclaimRoots"/> already read.</summary>
    public static string? ReclaimStubPath(UnixFileInspector files, IReadOnlyList<string> roots, string? mediaPath, int season, int episode)
    {
        try
        {
            if (season <= 0 || string.IsNullOrEmpty(mediaPath) || roots.Count == 0) return null;
            if (Path.GetDirectoryName(mediaPath) is not { } folder || !files.TryCanonicalize(folder, out var canonical)) return null;
            if (!roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal))) return null;
            var seriesFolder = IsSeasonFolderName(Path.GetFileName(canonical)) ? Path.GetDirectoryName(canonical) : canonical;
            if (seriesFolder is null || roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root), seriesFolder, StringComparison.Ordinal)) ||
                !files.CanCreateIn(canonical)) return null;
            var name = ImportNaming.EpisodeFile(Path.GetFileName(seriesFolder), season, episode, PlaceholderStub.Extension);
            return ImportNaming.FitsFileName(name) ? Path.Combine(canonical, name) : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The retention half (P1.FU25): after a reclaim has unlinked <paramref name="mediaPath"/> and recorded it, writes a stub for
    /// each episode the file held, so the episode stays a Jellyfin item (its user data re-attached by key when Jellyfin finds the
    /// stub) instead of disappearing. Writes only where <c>ReclaimStubPath</c> allows, never over or beside a file that
    /// already is the episode, and never deletes anything. Never throws. Returns the stubs written.
    /// </summary>
    public static IReadOnlyList<string> LeaveAfterReclaim(ILibraryManager library, UnixFileInspector files, ILibraryMonitor? libraryMonitor,
        Guid libraryId, string mediaPath, string seriesTitle, IReadOnlyList<ReclaimedEpisode> episodes, ILogger logger)
    {
        var written = new List<string>();
        try
        {
            foreach (var episode in episodes.DistinctBy(item => (item.SeasonNumber, item.EpisodeNumber)))
            {
                if (ReclaimStubPath(library, files, libraryId, mediaPath, episode.SeasonNumber, episode.EpisodeNumber) is not { } path)
                    continue;
                var folder = Path.GetDirectoryName(path)!;
                // Nothing beside a file (or a stub) that already is this episode, and never over any file.
                if (Listing(folder) is not { } listing ||
                    listing.Any(file => (file.IsStub || file.IsVideo) && file.Covers(episode.SeasonNumber, episode.EpisodeNumber)) ||
                    files.Probe(path) != PathPresence.Absent) continue;
                var error = WriteStub(files, path, PlaceholderStub.Body(seriesTitle, episode.SeasonNumber, episode.EpisodeNumber, episode.Title));
                if (error is null) written.Add(path);
                else logger.LogWarning("JellyfinMod could not leave a placeholder for a reclaimed episode at {Path}: {Error}", path, error);
            }

            if (written.Count > 0)
            {
                libraryMonitor?.ReportFileSystemChanged(Path.GetDirectoryName(written[0])!);
                logger.LogInformation("JellyfinMod left {Count} placeholder(s) for reclaimed episode(s) in {Folder}", written.Count,
                    Path.GetDirectoryName(written[0]));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not leave a placeholder for a reclaimed episode: {Error}", error.Message);
        }

        return written;
    }

    /// <summary>
    /// Sets each stub item's date added to its air date (so Latest shows real arrivals, not placeholders) and its runtime to
    /// TMDB's (upstream's runtime line, and a stop report cannot call a runtime-less item played). Idempotent.
    /// </summary>
    public async Task<int> StampAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var stamped = 0;
        foreach (var item in StubItems(libraryId))
            if (await StampAsync(item, cancellationToken).ConfigureAwait(false)) stamped++;
        return stamped;
    }

    /// <summary>Stamps one item when it is one of the plugin's stubs; true when it changed.</summary>
    public async Task<bool> StampAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is Movie { IsPlaceHolder: true } movie && PlaceholderStub.IsStubFile(movie.Path))
            return await StampMovieAsync(movie, cancellationToken).ConfigureAwait(false);
        if (item is not JellyfinEpisode { IsPlaceHolder: true } stub || !PlaceholderStub.IsStubFile(stub.Path) ||
            stub.ParentIndexNumber is not { } season || stub.IndexNumber is not { } number) return false;
        // The series' entry by its bound episodes, or by the series item itself once a reclaim left it no file (P1.FU25).
        // A series with no file at all (P1.FU26): its series item bound to the entry, or the entry of the series' TMDB id in its
        // library, before reconciliation has bound it.
        var seriesTmdb = stub.Series is { } series ? TmdbIdOf(series) : null;
        var libraryIds = seriesTmdb is null ? [] : LibraryIdsOf(stub);
        var entryIds = database.EpisodeBindings.AsNoTracking().Where(binding => binding.SeriesItemId == stub.SeriesId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id, (binding, episode) => episode.EntryId)
            .Concat(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "series" && entry.JellyfinItemId == stub.SeriesId)
                .Select(entry => entry.Id))
            .Concat(database.EntryBindings.AsNoTracking().Where(binding => binding.JellyfinItemId == stub.SeriesId).Select(binding => binding.EntryId))
            .Concat(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "series" && seriesTmdb != null && entry.TmdbId == seriesTmdb &&
                entry.TargetLibraryId != null && libraryIds.Contains(entry.TargetLibraryId.Value)).Select(entry => entry.Id));
        var tracked = await database.Episodes.AsNoTracking()
            .Where(episode => entryIds.Contains(episode.EntryId) && episode.SeasonNumber == season && episode.EpisodeNumber == number)
            .Select(episode => new { episode.AirDate, episode.RuntimeMinutes }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var airDate = tracked?.AirDate ?? stub.PremiereDate;
        var created = airDate is { } date ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc) : (DateTime?)null;
        var runtime = tracked?.RuntimeMinutes is { } minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : (long?)null;
        var change = false;
        if (created is { } value && stub.DateCreated != value)
        {
            stub.DateCreated = value;
            change = true;
        }

        if (runtime is { } ticks && stub.RunTimeTicks != ticks)
        {
            stub.RunTimeTicks = ticks;
            change = true;
        }

        if (change) await stub.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return change;
    }

    /// <summary>
    /// A movie stub's date added becomes the movie's release date and its runtime TMDB's (P1.FU26), from the entry of that TMDB
    /// id in the movie's library; true when it changed.
    /// </summary>
    private async Task<bool> StampMovieAsync(Movie stub, CancellationToken cancellationToken)
    {
        if (TmdbIdOf(stub) is not { } tmdbId) return false;
        var libraryIds = LibraryIdsOf(stub);
        var metadata = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "movie" && entry.TmdbId == tmdbId && entry.TargetLibraryId != null &&
                libraryIds.Contains(entry.TargetLibraryId.Value))
            .Select(entry => entry.MetadataJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var parsed = Metadata(metadata);
        var date = parsed?.PremiereDate ?? stub.PremiereDate;
        var created = date is { } value ? DateTime.SpecifyKind(value.Date, DateTimeKind.Utc) : (DateTime?)null;
        var runtime = parsed?.RuntimeMinutes is { } minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : (long?)null;
        var change = false;
        if (created is { } day && stub.DateCreated != day)
        {
            stub.DateCreated = day;
            change = true;
        }

        if (runtime is { } ticks && stub.RunTimeTicks != ticks)
        {
            stub.RunTimeTicks = ticks;
            change = true;
        }

        if (change) await stub.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return change;
    }

    private static TmdbMetadata? Metadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<TmdbMetadata>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static int? TmdbIdOf(BaseItem item) =>
        item.ProviderIds.FirstOrDefault(pair => pair.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value is { } raw &&
        int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    private Guid[] LibraryIdsOf(BaseItem item)
    {
        try
        {
            return library.GetCollectionFolders(item).Select(folder => folder.Id).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// The Jellyfin item that stands for a title with no file (P1.FU26): the plugin's stub movie of the entry's TMDB id in its
    /// library, or the series item Jellyfin holds for a series entry with no file on disk (its placeholders' folder); null when
    /// the title has a file or no such item.
    /// </summary>
    public async Task<Guid?> TitleItemIdAsync(Entry entry, CancellationToken cancellationToken)
    {
        if (entry.State == FileState.OnDisk || entry.TargetLibraryId is not { } libraryId) return null;
        try
        {
            if (entry.MediaType == "movie")
                return StubItems(libraryId, entry.TmdbId).FirstOrDefault()?.Id;
            var bound = await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entry.Id)
                .Select(binding => binding.JellyfinItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return bound.Order().FirstOrDefault(id => library.GetItemById(id) is Series) is var id && id != Guid.Empty ? id : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning("JellyfinMod could not find the placeholder item of entry {Entry}: {Error}", entry.Id, error.Message);
            return null;
        }
    }

    /// <summary>
    /// The plugin's placeholder episodes Jellyfin holds for a series entry (P1.FU27, the watched restore): in its library, under
    /// the entry's series item or a series of its TMDB id (a series with no file, before reconciliation binds it).
    /// </summary>
    public IReadOnlyList<JellyfinEpisode> StubEpisodesOf(Entry entry)
    {
        if (entry.MediaType != "series" || entry.TargetLibraryId is not { } libraryId) return [];
        var series = new Dictionary<Guid, bool>();
        bool Belongs(JellyfinEpisode episode)
        {
            if (series.TryGetValue(episode.SeriesId, out var known)) return known;
            var item = library.GetItemById(episode.SeriesId);
            var belongs = episode.SeriesId == entry.JellyfinItemId || item is not null && TmdbIdOf(item) == entry.TmdbId;
            series[episode.SeriesId] = belongs;
            return belongs;
        }

        return StubItems(libraryId).OfType<JellyfinEpisode>().Where(Belongs).ToArray();
    }

    /// <summary>The plugin's stub episodes and movies Jellyfin holds in a library (one TMDB id's movies only, when given).</summary>
    private IReadOnlyList<Video> StubItems(Guid libraryId, int? movieTmdbId = null)
    {
        try
        {
            if (library.GetItemById(libraryId) is not Folder folder) return [];
            var query = new InternalItemsQuery
            {
                Parent = folder,
                Recursive = true,
                IncludeItemTypes = movieTmdbId is null ? [BaseItemKind.Episode, BaseItemKind.Movie] : [BaseItemKind.Movie],
                IsPlaceHolder = true,
                IsVirtualItem = false
            };
            if (movieTmdbId is { } tmdbId)
                query.HasAnyProviderId = new() { ["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            return library.GetItemList(query).OfType<Video>().Where(item => item is JellyfinEpisode or Movie && PlaceholderStub.IsStubFile(item.Path) &&
                (movieTmdbId is null || TmdbIdOf(item) == movieTmdbId)).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not list the placeholders of library {Library}: {Error}", libraryId, error.Message);
            return [];
        }
    }

    private (string Name, bool IsMovies) LibraryOf(Guid libraryId)
    {
        var folder = library.GetVirtualFolders().FirstOrDefault(value => Guid.TryParse(value.ItemId, out var id) && id == libraryId &&
            value.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
            ?? throw new KeyNotFoundException("No TV or movie library has this id.");
        return (folder.Name, folder.CollectionType == CollectionTypeOptions.movies);
    }

    /// <summary>
    /// The plan: stubs to write for missing aired episodes (and released movies), stale stubs to remove, and what is left
    /// without one. <paramref name="entryId"/> limits it to one entry.
    /// </summary>
    private async Task<Computed> ComputeAsync(Guid libraryId, Guid? entryId, string? preferredFolder, CancellationToken cancellationToken)
    {
        if (LibraryOf(libraryId).IsMovies) return await ComputeMoviesAsync(libraryId, entryId, preferredFolder, cancellationToken).ConfigureAwait(false);
        var writes = new List<PlaceholderWrite>();
        var removals = new List<PlaceholderRemoval>();
        var skipped = new List<PlaceholderSkip>();
        var existing = 0;
        var covered = new HashSet<Guid>();
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var today = time.GetUtcNow().UtcDateTime.Date;
        var entries = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "series" && entry.TargetLibraryId == libraryId && (entryId == null || entry.Id == entryId))
            .OrderBy(entry => entry.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Series with a file of their own (a bound episode): they get placeholders only in the folder their files are in.
        var listed = entries.Select(entry => entry.Id).ToArray();
        var held = (await database.Episodes.AsNoTracking().Where(episode => listed.Contains(episode.EntryId) &&
                database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id))
            .Select(episode => episode.EntryId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var entryIds = entries.Select(entry => entry.Id).ToArray();
        var episodes = (await database.Episodes.AsNoTracking().Where(episode => entryIds.Contains(episode.EntryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(episode => episode.EntryId);
        // Episodes being grabbed, downloaded or imported (an open import): their file is on its way, and the import removes their
        // stub once it is in place, so no new stub is written for them meanwhile (P1.FU25: a sync running while an import links
        // the file must not put a stub back beside it). A stub already there stays until the import swaps it.
        var importing = (await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId != null && entryIds.Contains(operation.EntryId.Value) && operation.EpisodeId != null &&
                ImportStates.Open.Contains(operation.State))
            .Select(operation => operation.EpisodeId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = episodes[entry.Id].ToArray();
            // Aired episodes the plugin lists without a file (specials are not offered, as on the mod's own surfaces); an
            // unaired one keeps the mod's card with its date. A reclaimed one is missing too, and aired whatever its date says (it
            // had a file).
            var wanted = own.Where(episode => episode.SeasonNumber > 0 &&
                (episode.AirDate is { } airDate && airDate.Date <= today || episode.State == FileState.Reclaimed) &&
                episode.State != FileState.OnDisk && episode.JellyfinItemId is null).ToArray();
            var wantedKeys = wanted.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToHashSet();
            if (roots.Count == 0)
            {
                if (wanted.Length > 0) skipped.Add(new(entry.Id, entry.Title, null, wanted.Length, "library_root_missing",
                    "The library has no readable folder."));
                continue;
            }

            var seriesFolder = await SeriesFolders.BoundSeriesFolderAsync(database, library, files, entry.Id, roots, cancellationToken)
                .ConfigureAwait(false);
            var newSeries = false;
            if (seriesFolder is null)
            {
                // A series with nothing on disk (P1.FU26): its own folder in the library root the import would use, made with its
                // first placeholders, so Jellyfin builds the series with its seasons and episodes. A series that has files Jellyfin
                // cannot place keeps the mod's own surfaces.
                if (wanted.Length == 0) continue;
                if (held.Contains(entry.Id) || entry.State == FileState.OnDisk)
                {
                    skipped.Add(new(entry.Id, entry.Title, null, wanted.Length, "no_series_folder",
                        "The series' files are in no folder of this library that the plugin can find."));
                    continue;
                }

                // The folder a deleted title had, when it is still free (P4.FU12): the title returns where it was, not under a new name.
                var preferredRoot = PreferredRoot(roots, preferredFolder);
                if ((preferredRoot ?? roots.FirstOrDefault(files.CanCreateIn)) is not { } writableRoot)
                {
                    skipped.Add(new(entry.Id, entry.Title, null, wanted.Length, "read_only",
                        "No folder of the library can be written (a read-only folder or mount)."));
                    continue;
                }

                var folderName = preferredRoot is not null ? Path.GetFileName(Path.TrimEndingDirectorySeparator(preferredFolder!))
                    : ImportNaming.TitleFolder(entry.Title, entry.Year, entry.TmdbId);
                seriesFolder = Path.Combine(writableRoot, folderName);
                newSeries = true;
                if (!ImportNaming.FitsFileName(folderName) || files.Probe(seriesFolder) != PathPresence.Absent)
                {
                    skipped.Add(new(entry.Id, entry.Title, null, wanted.Length, "name_taken",
                        $"{folderName} cannot be used (too long, or something already has the name)."));
                    continue;
                }
            }

            // Every file of the series: in its folder and its season folders (a reclaim leaves its stub where the file was).
            var seriesFiles = StubFolders(files, seriesFolder).Select(Listing).OfType<List<FolderFile>>().SelectMany(listing => listing)
                .ToArray();
            // Stale stubs of this series: their episode has a file now (anywhere in the series), is no longer missing or listed.
            foreach (var stub in seriesFiles.Where(file => file.IsStub))
            {
                if (stub.Season is not { } season || stub.Episodes.Count != 1)
                {
                    removals.Add(new(stub.Path, "unnumbered"));
                    continue;
                }

                var number = stub.Episodes[0];
                var tracked = own.FirstOrDefault(episode => episode.SeasonNumber == season && episode.EpisodeNumber == number);
                var reason = seriesFiles.Any(file => !file.IsStub && file.IsVideo && file.Covers(season, number)) ? "file_present"
                    : tracked is null ? "not_listed"
                    : tracked.State == FileState.OnDisk || tracked.JellyfinItemId is not null ? "on_disk"
                    : !wantedKeys.Contains((season, number)) ? "not_aired"
                    : null;
                if (reason is not null) removals.Add(new(stub.Path, reason));
            }

            var seriesWritable = newSeries || files.CanCreateIn(seriesFolder);
            var seriesName = Path.GetFileName(seriesFolder);
            foreach (var group in wanted.GroupBy(episode => episode.SeasonNumber).OrderBy(group => group.Key))
            {
                var season = group.Key;
                var existingFolder = newSeries ? null : SeriesFolders.ExistingSeasonFolder(seriesFolder, season);
                string folder;
                bool newFolder;
                if (existingFolder is not null)
                {
                    // An existing season folder must really be inside the series folder: a link out of the library gets nothing.
                    if (!files.TryCanonicalize(existingFolder, out var canonical) || !MediaStorageIdentity.Contains(seriesFolder, canonical) ||
                        canonical == seriesFolder)
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, group.Count(), "outside_library",
                            $"{Path.GetFileName(existingFolder)} leads outside the series folder."));
                        continue;
                    }

                    folder = canonical;
                    newFolder = false;
                    if (!files.CanCreateIn(folder))
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, group.Count(), "read_only",
                            $"{Path.GetFileName(folder)} cannot be written (a read-only folder or mount)."));
                        continue;
                    }
                }
                else
                {
                    folder = Path.Combine(seriesFolder, ImportNaming.SeasonFolder(season));
                    newFolder = true;
                    if (!seriesWritable)
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, group.Count(), "read_only",
                            "The series folder cannot be written (a read-only folder or mount)."));
                        continue;
                    }
                }

                if ((newFolder ? new List<FolderFile>() : Listing(folder)) is not { } listing)
                {
                    skipped.Add(new(entry.Id, entry.Title, season, group.Count(), "unreadable", $"{Path.GetFileName(folder)} cannot be read."));
                    continue;
                }

                foreach (var episode in group.OrderBy(episode => episode.EpisodeNumber))
                {
                    var number = episode.EpisodeNumber;
                    if (seriesFiles.Any(file => file.IsStub && file.Covers(season, number)))
                    {
                        existing++;
                        covered.Add(entry.Id);
                        continue;
                    }

                    if (seriesFiles.Concat(listing).Any(file => !file.IsStub && file.IsVideo && file.Covers(season, number)))
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, 1, "file_present",
                            $"A file for S{season:00}E{number:00} is already in the series' folders; nothing is written beside it."));
                        continue;
                    }

                    if (importing.Contains(episode.Id))
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, 1, "importing",
                            $"S{season:00}E{number:00} is being downloaded or imported; its file takes the place of a placeholder."));
                        continue;
                    }

                    var name = ImportNaming.EpisodeFile(seriesName, season, number, PlaceholderStub.Extension);
                    var path = Path.Combine(folder, name);
                    if (!ImportNaming.FitsFileName(name) || files.Probe(path) != PathPresence.Absent)
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, 1, "name_taken",
                            $"{name} cannot be used (too long, or a file already has the name)."));
                        continue;
                    }

                    writes.Add(new(entry.Id, entry.Title, season, number, path, newFolder));
                }
            }
        }

        return new(writes, removals, skipped, existing, covered);
    }

    /// <summary>
    /// The library folder that holds <paramref name="preferredFolder"/> when that folder is directly in a writable one, is free and
    /// has a name that fits: the place a deleted title goes back to (P4.FU12). Null otherwise (the title takes the import's name).
    /// </summary>
    private string? PreferredRoot(IReadOnlyList<string> roots, string? preferredFolder)
    {
        if (string.IsNullOrEmpty(preferredFolder) || Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(preferredFolder)) is not { } parent)
            return null;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(preferredFolder));
        var root = roots.FirstOrDefault(candidate => string.Equals(Path.TrimEndingDirectorySeparator(candidate), parent, StringComparison.Ordinal));
        return root is not null && files.CanCreateIn(root) && ImportNaming.FitsFileName(name) &&
            files.Probe(Path.Combine(root, name)) == PathPresence.Absent ? root : null;
    }

    /// <summary>What a plan found: the writes, removals and skips, and the titles that already have a placeholder.</summary>
    private sealed record Computed(List<PlaceholderWrite> Writes, List<PlaceholderRemoval> Removals, List<PlaceholderSkip> Skipped, int Existing,
        HashSet<Guid> Covered);

    /// <summary>
    /// The plan for a movie library (P1.FU26): a stub for every released movie of the catalog with no file, in the folder the
    /// import would put its file (<c>Title (Year) [tmdbid-N]/Title (Year) [tmdbid-N].disc</c> in the first writable library
    /// folder, or the title folder already there), and the stale stubs (the movie has a file, is not released, or left the
    /// catalog) to remove. An unreleased movie keeps the mod's own card with its date.
    /// </summary>
    private async Task<Computed> ComputeMoviesAsync(Guid libraryId, Guid? entryId, string? preferredFolder, CancellationToken cancellationToken)
    {
        var writes = new List<PlaceholderWrite>();
        var removals = new List<PlaceholderRemoval>();
        var skipped = new List<PlaceholderSkip>();
        var existing = 0;
        var covered = new HashSet<Guid>();
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var today = time.GetUtcNow().UtcDateTime.Date;
        var entries = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "movie" && entry.TargetLibraryId == libraryId && (entryId == null || entry.Id == entryId))
            .OrderBy(entry => entry.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = entries.Select(entry => entry.Id).ToArray();
        var bound = (await database.EntryBindings.AsNoTracking().Where(binding => ids.Contains(binding.EntryId))
            .Select(binding => binding.EntryId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var importing = (await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId != null && ids.Contains(operation.EntryId.Value) && ImportStates.Open.Contains(operation.State))
            .Select(operation => operation.EntryId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var reclaimed = (await database.RetentionOperations.AsNoTracking()
                .Where(operation => operation.EntryId != null && ids.Contains(operation.EntryId.Value) && operation.EpisodeId == null &&
                    operation.State == RetentionOperationStates.Completed)
                .Select(operation => new { EntryId = operation.EntryId!.Value, operation.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(operation => operation.EntryId, operation => operation.MediaPath);
        var stubItems = StubItems(libraryId).OfType<Movie>().ToArray();
        var listedTmdb = entryId is null
            ? (await database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie" && entry.TargetLibraryId == libraryId)
                .Select(entry => entry.TmdbId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet()
            : null;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = MovieStubFolders(roots, entry.TmdbId, stubItems, reclaimed[entry.Id]);
            var listing = folders.Select(folder => Listing(folder) ?? []).SelectMany(files => files).ToArray();
            var stubs = listing.Where(file => file.IsStub).Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
            var hasFile = entry.State == FileState.OnDisk || bound.Contains(entry.Id);
            var release = Metadata(entry.MetadataJson)?.PremiereDate;
            var released = entry.State == FileState.Reclaimed || release is { } date && date.Date <= today;
            if (hasFile || !released)
            {
                removals.AddRange(stubs.Select(path => new PlaceholderRemoval(path, hasFile ? "on_disk" : "not_released")));
                continue;
            }

            if (stubs.Length > 0)
            {
                existing++;
                covered.Add(entry.Id);
                continue;
            }

            if (listing.Any(file => !file.IsStub && file.IsVideo))
            {
                skipped.Add(new(entry.Id, entry.Title, null, 1, "file_present",
                    "A video is already in the movie's folder; nothing is written beside it."));
                continue;
            }

            if (importing.Contains(entry.Id))
            {
                skipped.Add(new(entry.Id, entry.Title, null, 1, "importing",
                    "The movie is being downloaded or imported; its file takes the place of a placeholder."));
                continue;
            }

            if (roots.Count == 0)
            {
                skipped.Add(new(entry.Id, entry.Title, null, 1, "library_root_missing", "The library has no readable folder."));
                continue;
            }

            var folder = SeriesFolders.TitleFolderByTmdbId(files, roots, entry.TmdbId);
            var newFolder = folder is null;
            if (folder is null)
            {
                var preferredRoot = PreferredRoot(roots, preferredFolder);
                if ((preferredRoot ?? roots.FirstOrDefault(files.CanCreateIn)) is not { } writableRoot)
                {
                    skipped.Add(new(entry.Id, entry.Title, null, 1, "read_only",
                        "No folder of the library can be written (a read-only folder or mount)."));
                    continue;
                }

                folder = Path.Combine(writableRoot, preferredRoot is not null ? Path.GetFileName(Path.TrimEndingDirectorySeparator(preferredFolder!))
                    : ImportNaming.TitleFolder(entry.Title, entry.Year, entry.TmdbId));
            }
            else if (!files.CanCreateIn(folder))
            {
                skipped.Add(new(entry.Id, entry.Title, null, 1, "read_only",
                    $"{Path.GetFileName(folder)} cannot be written (a read-only folder or mount)."));
                continue;
            }

            var name = Path.GetFileName(folder) + "." + PlaceholderStub.Extension;
            var path = Path.Combine(folder, name);
            if (!ImportNaming.FitsFileName(name) || newFolder && files.Probe(folder) != PathPresence.Absent ||
                files.Probe(path) != PathPresence.Absent)
            {
                skipped.Add(new(entry.Id, entry.Title, null, 1, "name_taken",
                    $"{name} cannot be used (too long, or something already has the name)."));
                continue;
            }

            writes.Add(new(entry.Id, entry.Title, 0, 0, path, newFolder, PlaceholderKinds.Movie));
        }

        // A stub of a movie that left the catalog: no entry of its TMDB id in this library any more.
        if (listedTmdb is not null)
            foreach (var orphan in stubItems.Where(item => TmdbIdOf(item) is not { } tmdb || !listedTmdb.Contains(tmdb)))
                if (files.TryCanonicalize(orphan.Path, out var canonical) && roots.Any(root => MediaStorageIdentity.Contains(root, canonical)) &&
                    removals.All(removal => removal.Path != canonical))
                    removals.Add(new(canonical, "not_listed"));

        return new(writes, removals, skipped, existing, covered);
    }

    /// <summary>
    /// The folders a movie's stub can be in: its title folder (<c>[tmdbid-N]</c>), the folders of the stub items Jellyfin holds
    /// for its TMDB id, and the folder its last reclaimed file was in (the stub a reclaim left). Never a library folder itself.
    /// </summary>
    private IReadOnlyList<string> MovieStubFolders(IReadOnlyList<string> roots, int tmdbId, IReadOnlyList<Movie> stubItems,
        IEnumerable<string> reclaimedPaths)
    {
        var candidates = new List<string?> { SeriesFolders.TitleFolderByTmdbId(files, roots, tmdbId) };
        candidates.AddRange(stubItems.Where(item => TmdbIdOf(item) == tmdbId).Select(item => Path.GetDirectoryName(item.Path)));
        candidates.AddRange(reclaimedPaths.Select(Path.GetDirectoryName));
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (candidate is null || !Directory.Exists(candidate) || !files.TryCanonicalize(candidate, out var canonical)) continue;
            if (!roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal))) continue;
            if (!result.Contains(canonical, StringComparer.Ordinal)) result.Add(canonical);
        }

        return result;
    }

    /// <summary>Every path of a stub of the plugin's in the library: Jellyfin's stub items and the stubs in each series' folders.</summary>
    private async Task<IReadOnlyList<string>> AllStubPathsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in StubItems(libraryId))
            if (files.TryCanonicalize(item.Path, out var canonical) && roots.Any(root => MediaStorageIdentity.Contains(root, canonical)))
                paths.Add(canonical);
        var movieTmdbIds = await database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie" && entry.TargetLibraryId == libraryId)
            .Select(entry => entry.TmdbId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var tmdbId in movieTmdbIds)
            if (SeriesFolders.TitleFolderByTmdbId(files, roots, tmdbId) is { } folder)
                foreach (var stub in Listing(folder)?.Where(file => file.IsStub) ?? [])
                    paths.Add(stub.Path);
        var entryIds = await database.Entries.AsNoTracking().Where(entry => entry.MediaType == "series" && entry.TargetLibraryId == libraryId)
            .Select(entry => entry.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entryId in entryIds)
        {
            if (await SeriesFolders.BoundSeriesFolderAsync(database, library, files, entryId, roots, cancellationToken).ConfigureAwait(false)
                is not { } seriesFolder) continue;
            foreach (var folder in StubFolders(files, seriesFolder))
                foreach (var stub in Listing(folder)?.Where(file => file.IsStub) ?? [])
                    paths.Add(stub.Path);
        }

        return paths.ToArray();
    }

    /// <summary>Writes one stub: its season folder if needed, a hidden temp file, then a link to the final name (never over a file).</summary>
    private string? WriteOne(PlaceholderWrite write, byte[] body, HashSet<string> changed)
    {
        var folder = Path.GetDirectoryName(write.Path)!;
        var seriesFolder = Path.GetDirectoryName(folder)!;
        var created = false;
        var createdParent = false;
        try
        {
            if (!Directory.Exists(folder))
            {
                if (!Directory.Exists(seriesFolder))
                {
                    // A new series' own folder (P1.FU26): only directly in a library folder that is still a real folder.
                    var root = Path.GetDirectoryName(seriesFolder)!;
                    if (!files.TryCanonicalize(root, out var rootCanonical) || rootCanonical != root) return "The library folder changed.";
                    Directory.CreateDirectory(seriesFolder);
                    createdParent = true;
                }

                // The parent folder must still be what the plan saw: a real folder, not a link put in its place since.
                if (!files.TryCanonicalize(seriesFolder, out var canonical) || canonical != seriesFolder) return "The series folder changed.";
                Directory.CreateDirectory(folder);
                created = true;
            }
            else if (!files.TryCanonicalize(folder, out var canonical) || canonical != folder)
            {
                return "The season folder changed.";
            }

            var error = WriteStub(files, write.Path, body);
            if (error is not null) return error;
            changed.Add(createdParent ? Path.GetDirectoryName(seriesFolder)! : created ? seriesFolder : folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The placeholder could not be written: " + error.Message;
        }
        finally
        {
            // A folder made for a stub that was not written goes again, while it is still empty.
            if (created && !File.Exists(write.Path)) files.RemoveEmptyDirectory(folder);
            if (createdParent && !File.Exists(write.Path) && IsEmpty(seriesFolder)) files.RemoveEmptyDirectory(seriesFolder);
        }
    }

    /// <summary>
    /// Writes one stub into an existing folder: a hidden temp file, then <c>link(2)</c> to the final name, which refuses an
    /// existing name, so the stub is complete before it appears and never replaces a file. Null when written, else why not.
    /// </summary>
    private static string? WriteStub(UnixFileInspector files, string path, byte[] body)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".jfmod-stub-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(body);
                stream.Flush(true);
            }

            var linked = files.Link(temp, path);
            if (!linked.Linked)
                return linked.Exists ? "A file appeared with the placeholder's name; it was left untouched." : $"The placeholder could not be created (errno {linked.Errno}).";
            PlaceholderStub.Forget(path);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The placeholder could not be written: " + error.Message;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A hidden temp file left behind is skipped by every listing and Jellyfin; nothing else to do.
                _ = error;
            }
        }
    }

    /// <summary>Removes one stub after checking the marker again, and its season folder when that left it empty.</summary>
    private string? RemoveOne(string path, HashSet<string> changed, HashSet<string>? removedFolders = null) =>
        RemoveStub(files, path, changed, removedFolders);

    /// <summary>
    /// Removes one stub after checking the marker again, and its season and title folders when that left them empty. The folder
    /// that remains goes to <paramref name="changed"/>; the folders removed go to <paramref name="removedFolders"/>.
    /// </summary>
    private static string? RemoveStub(UnixFileInspector files, string path, HashSet<string> changed, HashSet<string>? removedFolders = null)
    {
        PlaceholderStub.Forget(path);
        if (!PlaceholderStub.IsStubFile(path)) return "Not a JellyfinMod placeholder any more; left untouched.";
        if (!files.TryInspect(path, out var stub)) return "The placeholder could not be read.";
        // Jellyfin's removal of the stub's item (and of a folder's, below) is the plugin's own, never a delete to repair (P1.FU29).
        PlaceholderStub.ExpectRemoval(path);
        PlaceholderStub.ExpectRemoval(stub.CanonicalPath);
        var result = files.UnlinkPinned(stub.CanonicalPath, stub.PhysicalIdentity);
        PlaceholderStub.Forget(path);
        if (!result.Removed) return result.Detail ?? "The placeholder could not be removed.";
        var folder = Path.GetDirectoryName(stub.CanonicalPath)!;
        if (IsSeasonFolderName(Path.GetFileName(folder)) && IsEmpty(folder))
        {
            PlaceholderStub.ExpectRemoval(folder);
            if (files.RemoveEmptyDirectory(folder))
            {
                removedFolders?.Add(folder);
                folder = Path.GetDirectoryName(folder)!;
            }
        }

        // A title folder (the import's or the placeholders' own shape, [tmdbid-N]) that removing the stub left empty goes too
        // (P1.FU26): a movie's folder, or a series' folder with no season left. Anything else in it keeps it.
        if (SeriesFolders.IsTitleFolderName(Path.GetFileName(folder)) && IsEmpty(folder))
        {
            PlaceholderStub.ExpectRemoval(folder);
            if (files.RemoveEmptyDirectory(folder))
            {
                removedFolders?.Add(folder);
                folder = Path.GetDirectoryName(folder)!;
            }
        }
        changed.Add(folder);
        return null;
    }

    private static bool IsEmpty(string folder)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Report(string path)
    {
        try
        {
            libraryMonitor.ReportFileSystemChanged(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not ask Jellyfin to look at a placeholder folder: {Error}", error.Message);
        }
    }

    /// <summary>Whether a folder's name makes it a season folder (<c>Season NN</c>, <c>Specials</c>).</summary>
    private static bool IsSeasonFolderName(string name) =>
        name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase);

    /// <summary>The series' season folders that are real folders inside it (no links out).</summary>
    private static IEnumerable<string> SeasonFolders(UnixFileInspector files, string seriesFolder)
    {
        string[] folders;
        try
        {
            folders = Directory.EnumerateDirectories(seriesFolder).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var folder in folders)
        {
            if (!IsSeasonFolderName(Path.GetFileName(folder))) continue;
            if (files.TryCanonicalize(folder, out var canonical) && canonical == folder) yield return folder;
        }
    }

    /// <summary>
    /// Every folder of a series a stub of the plugin's can be in: the series folder itself (a reclaim leaves its stub where the
    /// file was, P1.FU25) and its season folders.
    /// </summary>
    private static IEnumerable<string> StubFolders(UnixFileInspector files, string seriesFolder) =>
        Directory.Exists(seriesFolder) ? SeasonFolders(files, seriesFolder).Prepend(seriesFolder) : [];

    /// <summary>One file in a season folder: the plugin's stub or not, a video or not, and the episodes its name reads as.</summary>
    private sealed record FolderFile(string Path, bool IsStub, bool IsVideo, int? Season, IReadOnlyList<int> Episodes)
    {
        public bool Covers(int season, int episode) => Season == season && Episodes.Contains(episode);
    }

    /// <summary>The folder's files; null when it cannot be read, so nothing is written beside or removed from it.</summary>
    private static List<FolderFile>? Listing(string folder)
    {
        var result = new List<FolderFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith('.')) continue;
                var parsed = ReleaseParser.Parse(Path.GetFileNameWithoutExtension(name));
                result.Add(new(path, PlaceholderStub.IsStubFile(path), VideoExtensions.Contains(ImportFileSelector.Extension(name)),
                    parsed.SeasonNumber, parsed.EpisodeNumbers));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return result;
    }
}
