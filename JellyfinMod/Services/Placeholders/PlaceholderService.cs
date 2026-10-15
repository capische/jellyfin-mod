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
}

/// <summary>A TV library and its placeholder switch.</summary>
public sealed record PlaceholderLibrary(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("stubs")] int Stubs);

/// <summary>
/// Placeholders for missing episodes (P1.FU24, user-approved 2026-10-15: "Go and implement isPlaceHolder"). For a series
/// Jellyfin already holds, every aired episode the plugin lists without a file gets a Jellyfin media stub where the import would
/// put its file (<c>Season NN/Series (Year) SNNENN.disc</c>), so Jellyfin shows it as a real episode with ✓, ♥, its metadata and
/// counts, for every client, and nothing plays it. Off by default, per library; a dry run lists exactly what would be written.
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

    /// <summary>The TV libraries, each with its switch and the plugin's stubs Jellyfin holds in it.</summary>
    public IReadOnlyList<PlaceholderLibrary> Libraries()
    {
        var enabled = PlaceholderSettings.EnabledLibraries();
        return library.GetVirtualFolders().Where(folder => folder.CollectionType == CollectionTypeOptions.tvshows)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? new PlaceholderLibrary(id, folder.Name, enabled.Contains(id),
                StubItems(id).Count) : null)
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
    /// Brings the library's stubs up to date when its switch is on: writes the missing ones, removes the ones whose episode now
    /// has a file or is no longer missing, asks Jellyfin to look at the changed folders, and stamps the stub items Jellyfin
    /// already holds. With the switch off it only stamps and removes stale stubs, never writes.
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

        if (enabled)
        {
            var titles = await database.Episodes.AsNoTracking().Where(episode => writes.Select(write => write.EntryId).Contains(episode.EntryId))
                .Select(episode => new { episode.EntryId, episode.SeasonNumber, episode.EpisodeNumber, episode.Title })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var write in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = titles.FirstOrDefault(row => row.EntryId == write.EntryId && row.SeasonNumber == write.SeasonNumber &&
                    row.EpisodeNumber == write.EpisodeNumber)?.Title;
                var error = WriteOne(write, PlaceholderStub.Body(write.Title, write.SeasonNumber, write.EpisodeNumber, title), changed);
                if (error is null) done.Add(write);
                else failures.Add(new(write.Path, error));
            }
        }

        foreach (var folder in changed) Report(folder);
        await StampAsync(libraryId, cancellationToken).ConfigureAwait(false);
        if (done.Count > 0 || removals.Count > 0)
            logger.LogInformation("JellyfinMod placeholders in library {Library}: wrote {Written}, removed {Removed}, {Failed} failed",
                libraryId, done.Count, removals.Count - failures.Count(failure => removals.Any(removal => removal.Path == failure.Path)),
                failures.Count);
        return new(libraryId, name, enabled, false, existing, enabled ? done : [], removals, skipped) { Failures = failures };
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
    /// Removes the plugin's stubs of one episode from a folder, before the scan that finds the episode's new file there, so no
    /// placeholder stays beside it (P1.FU24's minimal swap; the full import swap is P1.FU25). Never throws.
    /// </summary>
    public static void RemoveStubsBeside(UnixFileInspector files, string folder, int season, int episode, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*." + PlaceholderStub.Extension))
            {
                if (!Covers(path, season, episode)) continue;
                PlaceholderStub.Forget(path);
                if (!PlaceholderStub.IsStubFile(path) || !files.TryInspect(path, out var stub)) continue;
                var result = files.UnlinkPinned(stub.CanonicalPath, stub.PhysicalIdentity);
                PlaceholderStub.Forget(path);
                if (result.Removed)
                    logger.LogInformation("JellyfinMod removed the placeholder of S{Season:00}E{Episode:00} before its file is scanned", season, episode);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("JellyfinMod could not remove a placeholder beside an imported episode: {Error}", error.Message);
        }
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
        var entryIds = database.EpisodeBindings.AsNoTracking().Where(binding => binding.SeriesItemId == stub.SeriesId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id, (binding, episode) => episode.EntryId);
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

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = episodes[entry.Id].ToArray();
            // Aired episodes the plugin lists without a file (specials are not offered, as on the mod's own surfaces); an
            // unaired one keeps the mod's card with its date. A reclaimed one is missing too.
            var wanted = own.Where(episode => episode.SeasonNumber > 0 && episode.AirDate is { } airDate && airDate.Date <= today &&
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

            // Stale stubs in this series' season folders: their episode has a file now, is no longer missing or listed, or a file
            // for it sits beside the stub.
            foreach (var folder in SeasonFolders(seriesFolder))
            {
                if (Listing(folder) is not { } listing) continue;
                foreach (var stub in listing.Where(file => file.IsStub))
                {
                    if (stub.Season is not { } season || stub.Episodes.Count != 1)
                    {
                        removals.Add(new(stub.Path, "unnumbered"));
                        continue;
                    }

                    var number = stub.Episodes[0];
                    var tracked = own.FirstOrDefault(episode => episode.SeasonNumber == season && episode.EpisodeNumber == number);
                    var reason = listing.Any(file => !file.IsStub && file.IsVideo && file.Covers(season, number)) ? "file_present"
                        : tracked is null ? "not_listed"
                        : tracked.State == FileState.OnDisk || tracked.JellyfinItemId is not null ? "on_disk"
                        : !wantedKeys.Contains((season, number)) ? "not_aired"
                        : null;
                    if (reason is not null) removals.Add(new(stub.Path, reason));
                }
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
                    if (listing.Any(file => file.IsStub && file.Covers(season, number)))
                    {
                        existing++;
                        continue;
                    }

                    if (listing.Any(file => !file.IsStub && file.IsVideo && file.Covers(season, number)))
                    {
                        skipped.Add(new(entry.Id, entry.Title, season, 1, "file_present",
                            $"A file for S{season:00}E{number:00} is already in {Path.GetFileName(folder)}; nothing is written beside it."));
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
            foreach (var folder in SeasonFolders(seriesFolder))
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
        string? temp = null;
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

            temp = Path.Combine(folder, ".jfmod-stub-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(body);
                stream.Flush(true);
            }

            var linked = files.Link(temp, write.Path);
            if (!linked.Linked)
                return linked.Exists ? "A file appeared with the placeholder's name; it was left untouched." : $"The placeholder could not be created (errno {linked.Errno}).";
            PlaceholderStub.Forget(write.Path);
            changed.Add(created ? seriesFolder : folder);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The placeholder could not be written: " + error.Message;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("JellyfinMod could not remove a temporary placeholder file: {Error}", error.Message);
                }
            }

            // A season folder made for a stub that was not written goes again, while it is still empty.
            if (created && !File.Exists(write.Path)) files.RemoveEmptyDirectory(folder);
        }
    }

    /// <summary>Removes one stub after checking the marker again, and its season folder when that left it empty.</summary>
    private string? RemoveOne(string path, HashSet<string> changed)
    {
        PlaceholderStub.Forget(path);
        if (!PlaceholderStub.IsStubFile(path)) return "Not a JellyfinMod placeholder any more; left untouched.";
        if (!files.TryInspect(path, out var stub)) return "The placeholder could not be read.";
        var result = files.UnlinkPinned(stub.CanonicalPath, stub.PhysicalIdentity);
        PlaceholderStub.Forget(path);
        if (!result.Removed) return result.Detail ?? "The placeholder could not be removed.";
        var folder = Path.GetDirectoryName(stub.CanonicalPath)!;
        var name = Path.GetFileName(folder);
        var isSeason = name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase);
        if (isSeason && IsEmpty(folder) && files.RemoveEmptyDirectory(folder)) changed.Add(Path.GetDirectoryName(folder)!);
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

    /// <summary>The series' season folders that are real folders inside it (no links out).</summary>
    private IEnumerable<string> SeasonFolders(string seriesFolder)
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
            var name = Path.GetFileName(folder);
            if (!name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase))
                continue;
            if (files.TryCanonicalize(folder, out var canonical) && canonical == folder) yield return folder;
        }
    }

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

    private static bool Covers(string path, int season, int episode)
    {
        var parsed = ReleaseParser.Parse(Path.GetFileNameWithoutExtension(path));
        return parsed.SeasonNumber == season && parsed.EpisodeNumbers.Contains(episode);
    }
}
