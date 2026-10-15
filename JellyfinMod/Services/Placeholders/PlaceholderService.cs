using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Entities;
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
    [property: JsonPropertyName("newFolder")] bool NewFolder);

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
    /// series (all of a dry run's writes are listed; its count is 0).
    /// </summary>
    [JsonPropertyName("pending")]
    public int Pending { get; init; }
}

/// <summary>A TV library and its placeholder switch.</summary>
public sealed record PlaceholderLibrary(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("stubs")] int Stubs,
    [property: JsonPropertyName("writable")] bool Writable);

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
    ILogger<PlaceholderService> logger)
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
        return library.GetVirtualFolders().Where(folder => folder.CollectionType == CollectionTypeOptions.tvshows)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? new PlaceholderLibrary(id, folder.Name, !disabled.Contains(id),
                StubItems(id).Count, SeriesFolders.LibraryRoots(library, files, id).Any(files.CanCreateIn)) : null)
            .OfType<PlaceholderLibrary>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Whether a library has anything to sync: its switch is on, or Jellyfin holds stubs of the plugin's in it.</summary>
    public bool NeedsSync(Guid libraryId) => PlaceholderSettings.IsEnabled(libraryId) || StubItems(libraryId).Count > 0;

    /// <summary>What a sync of the library would write and remove; nothing is changed.</summary>
    public async Task<PlaceholderPlan> PlanAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var (writes, removals, skipped, existing) = await ComputeAsync(libraryId, cancellationToken).ConfigureAwait(false);
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), true, existing, writes, removals, skipped);
    }

    /// <summary>
    /// Brings the library's stubs up to date when its switch is on: writes the missing ones of at most
    /// <see cref="SeriesPerRun"/> series, pausing between series, removes the ones whose episode now has a file or is no longer
    /// missing, asks Jellyfin to look at each series' changed folders, and stamps the stub items Jellyfin already holds. The rest
    /// is written by the next run (<see cref="PlaceholderPlan.Pending"/>). With the switch off it only stamps and removes stale
    /// stubs, never writes.
    /// </summary>
    public async Task<PlaceholderPlan> SyncAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var enabled = PlaceholderSettings.IsEnabled(libraryId);
        await using var lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var (writes, removals, skipped, existing) = await ComputeAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var failures = new List<PlaceholderRemoval>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var done = new List<PlaceholderWrite>();
        foreach (var removal in removals)
        {
            var error = RemoveOne(removal.Path, changed);
            if (error is not null) failures.Add(new(removal.Path, error));
        }

        foreach (var folder in changed) Report(folder);
        changed.Clear();
        var pending = 0;
        if (enabled)
        {
            var series = writes.GroupBy(write => write.EntryId).ToArray();
            var batch = series.Take(SeriesPerRun).ToArray();
            pending = series.Skip(SeriesPerRun).Sum(group => group.Count());
            var entryIds = batch.Select(group => group.Key).ToArray();
            var titles = await database.Episodes.AsNoTracking().Where(episode => entryIds.Contains(episode.EntryId))
                .Select(episode => new { episode.EntryId, episode.SeasonNumber, episode.EpisodeNumber, episode.Title })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < batch.Length; index++)
            {
                // A pause between series, so Jellyfin scans one series' folders before the next arrives.
                if (index > 0) await Task.Delay(PauseBetweenSeries, time, cancellationToken).ConfigureAwait(false);
                var written = 0;
                foreach (var write in batch[index])
                {
                    var title = titles.FirstOrDefault(row => row.EntryId == write.EntryId && row.SeasonNumber == write.SeasonNumber &&
                        row.EpisodeNumber == write.EpisodeNumber)?.Title;
                    var error = WriteOne(write, PlaceholderStub.Body(write.Title, write.SeasonNumber, write.EpisodeNumber, title), changed);
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

                foreach (var folder in changed) Report(folder);
                changed.Clear();
                logger.LogInformation(
                    "JellyfinMod placeholders in library {Library}: wrote {Written} for {Title} (series {Index} of {Count} this run, {Pending} placeholder(s) left for the next run)",
                    libraryId, written, batch[index].First().Title, index + 1, batch.Length, pending);
            }
        }

        await StampAsync(libraryId, cancellationToken).ConfigureAwait(false);
        if (done.Count > 0 || removals.Count > 0)
            logger.LogInformation("JellyfinMod placeholders in library {Library}: wrote {Written}, removed {Removed}, {Failed} failed, {Pending} left for the next run",
                libraryId, done.Count, removals.Count - failures.Count(failure => removals.Any(removal => removal.Path == failure.Path)),
                failures.Count, pending);
        return new(libraryId, name, enabled, false, existing, enabled ? done : [], removals, skipped) { Failures = failures, Pending = pending };
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
        var paths = await AllStubPathsAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<PlaceholderRemoval>();
        var failures = new List<PlaceholderRemoval>();
        foreach (var path in paths)
        {
            var error = RemoveOne(path, changed);
            if (error is null) removed.Add(new(path, "remove_all"));
            else failures.Add(new(path, error));
        }

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
        if (item is not JellyfinEpisode { IsPlaceHolder: true } stub || !PlaceholderStub.IsStubFile(stub.Path) ||
            stub.ParentIndexNumber is not { } season || stub.IndexNumber is not { } number) return false;
        // The series' entry by its bound episodes, or by the series item itself once a reclaim left it no file (P1.FU25).
        var entryIds = database.EpisodeBindings.AsNoTracking().Where(binding => binding.SeriesItemId == stub.SeriesId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id, (binding, episode) => episode.EntryId)
            .Concat(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "series" && entry.JellyfinItemId == stub.SeriesId)
                .Select(entry => entry.Id));
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

    /// <summary>The plugin's stub episodes Jellyfin holds in a library.</summary>
    private IReadOnlyList<JellyfinEpisode> StubItems(Guid libraryId)
    {
        try
        {
            if (library.GetItemById(libraryId) is not Folder folder) return [];
            return library.GetItemList(new InternalItemsQuery
            {
                Parent = folder,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Episode],
                IsPlaceHolder = true,
                IsVirtualItem = false
            }).OfType<JellyfinEpisode>().Where(item => PlaceholderStub.IsStubFile(item.Path)).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not list the placeholders of library {Library}: {Error}", libraryId, error.Message);
            return [];
        }
    }

    private (string Name, IReadOnlyList<string> Roots) LibraryOf(Guid libraryId)
    {
        var folder = library.GetVirtualFolders().FirstOrDefault(value => Guid.TryParse(value.ItemId, out var id) && id == libraryId &&
            value.CollectionType == CollectionTypeOptions.tvshows)
            ?? throw new KeyNotFoundException("No TV library has this id.");
        return (folder.Name, SeriesFolders.LibraryRoots(library, files, libraryId));
    }

    /// <summary>The plan: stubs to write for missing aired episodes, stale stubs to remove, and what is left without one.</summary>
    private async Task<(List<PlaceholderWrite> Writes, List<PlaceholderRemoval> Removals, List<PlaceholderSkip> Skipped, int Existing)>
        ComputeAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var writes = new List<PlaceholderWrite>();
        var removals = new List<PlaceholderRemoval>();
        var skipped = new List<PlaceholderSkip>();
        var existing = 0;
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var today = time.GetUtcNow().UtcDateTime.Date;
        var entries = await database.Entries.AsNoTracking()
            .Where(entry => entry.MediaType == "series" && entry.TargetLibraryId == libraryId)
            .OrderBy(entry => entry.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
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
            if (seriesFolder is null)
            {
                if (wanted.Length > 0) skipped.Add(new(entry.Id, entry.Title, null, wanted.Length, "no_series_folder",
                    "Jellyfin holds no file of this series yet, so there is no series folder to add placeholders to."));
                continue;
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

            var seriesWritable = files.CanCreateIn(seriesFolder);
            var seriesName = Path.GetFileName(seriesFolder);
            foreach (var group in wanted.GroupBy(episode => episode.SeasonNumber).OrderBy(group => group.Key))
            {
                var season = group.Key;
                var existingFolder = SeriesFolders.ExistingSeasonFolder(seriesFolder, season);
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

        return (writes, removals, skipped, existing);
    }

    /// <summary>Every path of a stub of the plugin's in the library: Jellyfin's stub items and the stubs in each series' folders.</summary>
    private async Task<IReadOnlyList<string>> AllStubPathsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var roots = SeriesFolders.LibraryRoots(library, files, libraryId);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in StubItems(libraryId))
            if (files.TryCanonicalize(item.Path, out var canonical) && roots.Any(root => MediaStorageIdentity.Contains(root, canonical)))
                paths.Add(canonical);
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
        try
        {
            if (!Directory.Exists(folder))
            {
                // The series folder must still be what the plan saw: a real folder, not a link put in its place since.
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
            changed.Add(created ? seriesFolder : folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The placeholder could not be written: " + error.Message;
        }
        finally
        {
            // A season folder made for a stub that was not written goes again, while it is still empty.
            if (created && !File.Exists(write.Path)) files.RemoveEmptyDirectory(folder);
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
    private string? RemoveOne(string path, HashSet<string> changed) => RemoveStub(files, path, changed);

    /// <summary>Removes one stub after checking the marker again, and its season folder when that left it empty.</summary>
    private static string? RemoveStub(UnixFileInspector files, string path, HashSet<string> changed)
    {
        PlaceholderStub.Forget(path);
        if (!PlaceholderStub.IsStubFile(path)) return "Not a JellyfinMod placeholder any more; left untouched.";
        if (!files.TryInspect(path, out var stub)) return "The placeholder could not be read.";
        var result = files.UnlinkPinned(stub.CanonicalPath, stub.PhysicalIdentity);
        PlaceholderStub.Forget(path);
        if (!result.Removed) return result.Detail ?? "The placeholder could not be removed.";
        var folder = Path.GetDirectoryName(stub.CanonicalPath)!;
        if (IsSeasonFolderName(Path.GetFileName(folder)) && IsEmpty(folder) && files.RemoveEmptyDirectory(folder)) changed.Add(Path.GetDirectoryName(folder)!);
        else changed.Add(folder);
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
