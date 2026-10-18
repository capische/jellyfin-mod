using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>One placeholder item the plugin would make (or made): its path, and for which episode.</summary>
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

    /// <summary>A movie with no file (P1.FU26).</summary>
    public const string Movie = "movie";
}

/// <summary>One placeholder item the plugin would remove (or removed), and why.</summary>
public sealed record PlaceholderRemoval(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Missing episodes the plugin leaves without a placeholder, and why (the fallback: the mod's own cards and rows).</summary>
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
    /// <summary>Gets the items the run could not make or remove, with the error; empty for a dry run.</summary>
    [JsonPropertyName("failures")]
    public IReadOnlyList<PlaceholderRemoval> Failures { get; init; } = [];

    /// <summary>
    /// Gets how many placeholders a run left for the next one: a run makes at most <see cref="PlaceholderService.SeriesPerRun"/>
    /// titles. A dry run lists all of them and counts them all here.
    /// </summary>
    [JsonPropertyName("pending")]
    public int Pending { get; init; }

    /// <summary>
    /// Gets how many titles (series and movies) stand for missing items with placeholders: those that have one and those still
    /// waiting for theirs (P1.FU27, the backfill's progress).
    /// </summary>
    [JsonPropertyName("titles")]
    public int Titles { get; init; }

    /// <summary>Gets how many of <see cref="Titles"/> have every placeholder made.</summary>
    [JsonPropertyName("titlesDone")]
    public int TitlesDone { get; init; }

    /// <summary>
    /// Gets why a run made less than its batch allows, or null: <c>library_scan</c> while Jellyfin scans a library (the batch
    /// waits for the next run, P1.FU27).
    /// </summary>
    [JsonPropertyName("deferred")]
    public string? Deferred { get; init; }
}

/// <summary>A TV or movie library, its placeholder switch and how many placeholder items Jellyfin holds in it.</summary>
public sealed record PlaceholderLibrary(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("items")] int Items,
    [property: JsonPropertyName("mediaType")] string MediaType = "series");

/// <summary>
/// Placeholders for missing episodes and movies (P1.FU24, P1.FU26, P1.FU30; user-approved 2026-10-15: "Go and implement
/// isPlaceHolder"; user, 2026-10-18: items only, no stub files). Every aired episode and released movie the plugin lists with
/// nothing to play gets a Jellyfin item with no file (<see cref="PlaceholderItems"/>), so Jellyfin shows it as a real episode or
/// movie with ✓, ♥, its metadata and counts, for every client, and nothing plays it. On by default for every TV and movie
/// library, which an administrator can turn off one by one; a dry run lists exactly what a sync would make. A sync makes a
/// bounded batch (<see cref="SeriesPerRun"/> titles, a pause between them) and leaves the rest for the next run. Nothing is
/// ever written to or removed from the library's folders.
/// </summary>
public sealed partial class PlaceholderService(
    ModDbContext database,
    ILibraryManager library,
    ReconciliationLibraryLock libraryLock,
    TimeProvider time,
    ILogger<PlaceholderService> logger,
    IUserDataManager? userData = null,
    IUserManager? users = null,
    LibraryNotifications? notifications = null)
{
    /// <summary>The most titles one sync makes placeholders for; the rest wait for the next run.</summary>
    public const int SeriesPerRun = 10;

    /// <summary>The pause between two titles of one run, so Jellyfin's metadata queue takes one title at a time.</summary>
    public static readonly TimeSpan PauseBetweenSeries = TimeSpan.FromSeconds(3);

    /// <summary>The TV and movie libraries, each with its switch and the plugin's placeholder items Jellyfin holds in it.</summary>
    public IReadOnlyList<PlaceholderLibrary> Libraries()
    {
        var disabled = PlaceholderSettings.DisabledLibraries();
        return library.GetVirtualFolders().Where(folder => folder.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? new PlaceholderLibrary(id, folder.Name, !disabled.Contains(id),
                OwnVideos(id).Count, folder.CollectionType == CollectionTypeOptions.movies ? "movie" : "series") : null)
            .OfType<PlaceholderLibrary>().OrderBy(item => item.MediaType == "movie" ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether a library has anything to sync: its switch is on, or Jellyfin holds placeholder items of the plugin's in it.</summary>
    public bool NeedsSync(Guid libraryId) => PlaceholderSettings.IsEnabled(libraryId) || OwnVideos(libraryId).Count > 0;

    /// <summary>What a sync of the library would make and remove; nothing is changed.</summary>
    public Task<PlaceholderPlan> PlanAsync(Guid libraryId, CancellationToken cancellationToken) =>
        PlanItemsAsync(libraryId, null, cancellationToken);

    /// <summary>
    /// Brings the library's placeholder items up to date: makes the missing ones of at most <see cref="SeriesPerRun"/> titles
    /// when its switch is on, and removes the ones a real item replaced or that stand for nothing any more. The rest is made by
    /// the next run (<see cref="PlaceholderPlan.Pending"/>). With the switch off it only removes, never makes.
    /// </summary>
    public Task<PlaceholderPlan> SyncAsync(Guid libraryId, CancellationToken cancellationToken) =>
        SyncItemsAsync(libraryId, null, false, SeriesPerRun, cancellationToken);

    /// <summary>
    /// The backfill's batch for one library (P1.FU27): <see cref="SyncAsync(Guid, CancellationToken)"/>, but it gives way to
    /// Jellyfin's own library scans (<see cref="PlaceholderPlan.Deferred"/> <c>library_scan</c>); the rest is pending for the
    /// next run.
    /// </summary>
    public Task<PlaceholderPlan> SyncBatchAsync(Guid libraryId, CancellationToken cancellationToken) =>
        SyncItemsAsync(libraryId, null, true, SeriesPerRun, cancellationToken);

    /// <summary>Syncs one entry's placeholders at once (P1.FU26): a title just added, or a series whose episode list just loaded.</summary>
    public Task<PlaceholderPlan> SyncEntryAsync(Guid libraryId, Guid entryId, CancellationToken cancellationToken) =>
        SyncItemsAsync(libraryId, entryId, false, SeriesPerRun, cancellationToken);

    /// <summary>
    /// One title's placeholders at once, for the delete repair (P1.FU29, P4.FU12), while the caller holds the library lease: the
    /// same sync as <see cref="SyncEntryAsync"/>. Its items exist when this returns; the clients are told at once.
    /// </summary>
    public Task<PlaceholderPlan> SyncEntryUnderLeaseAsync(Guid libraryId, Guid entryId, CancellationToken cancellationToken) =>
        SyncItemsAsync(libraryId, entryId, false, SeriesPerRun, cancellationToken, leased: true);

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

    /// <summary>Every placeholder item of the plugin's in the library: what <see cref="RemoveAllAsync"/> would remove.</summary>
    public Task<PlaceholderPlan> RemovalPlanAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        var paths = OwnVideos(libraryId).Select(item => item.Path).ToArray();
        return Task.FromResult(new PlaceholderPlan(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), true, paths.Length, [],
            paths.Select(path => new PlaceholderRemoval(path, "remove_all")).ToArray(), []));
    }

    /// <summary>Removes every placeholder item of the plugin's in the library; nothing else. Does not change the switch.</summary>
    public async Task<PlaceholderPlan> RemoveAllAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var (name, _) = LibraryOf(libraryId);
        await using var lease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var paths = OwnVideos(libraryId).Select(item => item.Path).ToArray();
        PlaceholderItems.RemoveAllIn(library, libraryId, logger);
        var left = OwnVideos(libraryId).Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var removed = paths.Where(path => !left.Contains(path)).Select(path => new PlaceholderRemoval(path, "remove_all")).ToArray();
        var failures = left.Select(path => new PlaceholderRemoval(path, "The placeholder item could not be removed.")).ToArray();
        logger.LogInformation("JellyfinMod removed {Removed} placeholder(s) from library {Library}, {Failed} failed", removed.Length,
            libraryId, failures.Length);
        return new(libraryId, name, PlaceholderSettings.IsEnabled(libraryId), false, paths.Length, [], removed, []) { Failures = failures };
    }

    /// <summary>
    /// Removes an entry's placeholder items when an administrator removes the title from the catalog (P1.FU26). The caller holds
    /// the library lock. Returns the items removed and the ones that could not be.
    /// </summary>
    public Task<(IReadOnlyList<string> Removed, IReadOnlyList<PlaceholderRemoval> Failures)> RemoveForEntryAsync(Entry entry,
        CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        var failures = new List<PlaceholderRemoval>();
        if (entry.TargetLibraryId is { } libraryId)
            foreach (var item in OwnItemsOfEntry(entry, libraryId))
            {
                if (library.GetItemById(item.Id) is not { } still) continue;
                if (PlaceholderItems.Remove(library, still, logger)) removed.Add(item.Path);
                else failures.Add(new(item.Path, "The placeholder item could not be removed."));
            }

        if (removed.Count > 0 || failures.Count > 0)
            logger.LogInformation("JellyfinMod removed {Removed} placeholder(s) of a title removed from the catalog, {Failed} failed", removed.Count,
                failures.Count);
        return Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<PlaceholderRemoval>)>((removed, failures));
    }

    /// <summary>One episode a reclaim leaves without a file: its numbers and title.</summary>
    public sealed record ReclaimedEpisode(int SeasonNumber, int EpisodeNumber, string? Title);

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

    /// <summary>
    /// The Jellyfin item that stands for a title with no file (P1.FU26): the plugin's movie item of the entry's TMDB id in its
    /// library, the plugin's series item, or the series item Jellyfin holds for a series entry with no file; null when the title
    /// has a file or no such item.
    /// </summary>
    public async Task<Guid?> TitleItemIdAsync(Entry entry, CancellationToken cancellationToken)
    {
        if (entry.State == FileState.OnDisk || entry.TargetLibraryId is not { } libraryId) return null;
        try
        {
            if (entry.MediaType == "movie")
                return library.GetItemById(library.GetNewItemId(PlaceholderItems.MoviePath(libraryId, entry.TmdbId), typeof(Movie))) is Movie movie
                    ? movie.Id
                    : null;
            if (library.GetItemById(library.GetNewItemId(PlaceholderItems.SeriesPath(libraryId, entry.TmdbId), typeof(Series))) is Series own)
                return own.Id;
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

    /// <summary>The plugin's placeholder episodes Jellyfin holds for a series entry (P1.FU27, the watched restore).</summary>
    public IReadOnlyList<JellyfinEpisode> PlaceholderEpisodesOf(Entry entry) =>
        entry.MediaType != "series" || entry.TargetLibraryId is not { } libraryId ? [] : OwnItemsOfEntry(entry, libraryId).OfType<JellyfinEpisode>().ToArray();

    private (string Name, bool IsMovies) LibraryOf(Guid libraryId)
    {
        var folder = library.GetVirtualFolders().FirstOrDefault(value => Guid.TryParse(value.ItemId, out var id) && id == libraryId &&
            value.CollectionType is CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)
            ?? throw new KeyNotFoundException("No TV or movie library has this id.");
        return (folder.Name, folder.CollectionType == CollectionTypeOptions.movies);
    }
}
