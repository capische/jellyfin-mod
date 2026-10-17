using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>One user's watched mark that the restore would put back (or put back) on a placeholder item.</summary>
public sealed record WatchedRestoreItem(
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("seasonNumber")] int? SeasonNumber,
    [property: JsonPropertyName("episodeNumber")] int? EpisodeNumber,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("playedAt")] DateTime PlayedAt);

/// <summary>What <i>Restore Watched State From Retention History</i> would do (the dry run) or did.</summary>
public sealed record WatchedRestorePlan(
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("items")] IReadOnlyList<WatchedRestoreItem> Items,
    [property: JsonPropertyName("kept")] int Kept,
    [property: JsonPropertyName("waiting")] int Waiting,
    [property: JsonPropertyName("failures")] IReadOnlyList<string> Failures);

/// <summary>
/// Restores "watched" on the placeholder of a title that retention reclaimed (P1.FU27), for each user the plugin's own retention
/// history recorded as having finished it, when Jellyfin has no user data of that user for the placeholder at all: the data was
/// lost (a reclaim before placeholders existed, its parked rows cleaned up by Jellyfin's <i>Clean up user data</i> task, which
/// keeps detached data 90 days) rather than re-attached by key. Administrators only, one action with a dry run; never automatic.
/// </summary>
/// <remarks>
/// <para><b>The only source</b> is the plugin's completion record (<see cref="CompletionObservation"/>): played, a completion
/// date, not revoked by an unwatched state, for a movie or episode retention reclaimed (state reclaimed). Nothing is guessed: no
/// record, no mark. The mark carries the record's date (Jellyfin's last played date, else when the completion began).</para>
/// <para><b>Never over a user's own state:</b> a user who has any stored data for the placeholder (Jellyfin re-attached the old
/// rows, or the user marked or unmarked it since) is left alone, and so is a placeholder Jellyfin has not refreshed yet (its
/// first refresh re-attaches parked rows; a write before it would collide with them). That also makes it idempotent: a second run
/// finds the rows the first wrote and changes nothing.</para>
/// <para>The save uses <see cref="UserDataSaveReason.UpdateUserData"/> under the plugin's quiet-save flag: the plugin's retention
/// listener records no completion from it, the Trakt plugin (which acts on <c>TogglePlayed</c>) sends nothing.</para>
/// </remarks>
public sealed class PlaceholderWatchedRestore(
    ModDbContext database,
    ILibraryManager library,
    IUserManager users,
    IUserDataManager userData,
    PlaceholderService placeholders,
    TimeProvider time,
    ILogger<PlaceholderWatchedRestore> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The dry run: every mark the restore would put back, and how many it leaves alone or cannot place yet.</summary>
    public Task<WatchedRestorePlan> PlanAsync(CancellationToken cancellationToken) => RunAsync(false, cancellationToken);

    /// <summary>Puts back every mark the dry run lists, each checked again just before.</summary>
    public async Task<WatchedRestorePlan> ApplyAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Candidate(Guid EntryId, Guid? EpisodeId, Guid UserId, DateTime PlayedAt);

    private async Task<WatchedRestorePlan> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        var revoked = RetentionCompletionService.UnwatchedReason;
        var records = await database.CompletionObservations.AsNoTracking()
            .Where(observation => observation.Played && observation.CompletedAt != null && observation.SourceReason != revoked)
            .Select(observation => new
            {
                observation.EntryId, observation.EpisodeId, observation.UserId,
                PlayedAt = observation.LastPlayedAt ?? observation.CompletedAt!.Value
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var entryIds = records.Select(record => record.EntryId).Distinct().ToArray();
        var entries = await database.Entries.AsNoTracking().Where(entry => entryIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, cancellationToken).ConfigureAwait(false);
        var episodeIds = records.Where(record => record.EpisodeId != null).Select(record => record.EpisodeId!.Value).Distinct().ToArray();
        var episodes = await database.Episodes.AsNoTracking().Where(episode => episodeIds.Contains(episode.Id))
            .ToDictionaryAsync(episode => episode.Id, cancellationToken).ConfigureAwait(false);
        // Only what retention reclaimed: a reclaimed episode, or a reclaimed movie.
        var candidates = records.Where(record => entries.TryGetValue(record.EntryId, out var entry) && (record.EpisodeId is { } episodeId
                ? episodes.TryGetValue(episodeId, out var episode) && episode.State == FileState.Reclaimed && episode.SeasonNumber > 0
                : entry.MediaType == "movie" && entry.State == FileState.Reclaimed))
            .Select(record => new Candidate(record.EntryId, record.EpisodeId, record.UserId, DateTime.SpecifyKind(record.PlayedAt, DateTimeKind.Utc)))
            .ToArray();

        var items = new List<WatchedRestoreItem>();
        var failures = new List<string>();
        var kept = 0;
        var waiting = 0;
        var seriesStubs = new Dictionary<Guid, IReadOnlyList<JellyfinEpisode>>();
        var restoredByEntry = new Dictionary<Guid, List<string>>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[candidate.EntryId];
            var episode = candidate.EpisodeId is { } id ? episodes[id] : null;
            var stubId = episode is null
                ? await placeholders.TitleItemIdAsync(entry, cancellationToken).ConfigureAwait(false)
                : EpisodeStub(entry, episode, seriesStubs);
            if (users.GetUserById(candidate.UserId) is not { } user)
                continue;
            // The placeholder as stored, with its user data rows; one Jellyfin has not refreshed yet waits.
            if (stubId is not { } itemId || StoredUserData.TryItem(library, itemId, out var item, out _) != StoredRead.Found || item is null ||
                item is not Video { IsPlaceHolder: true } || !PlaceholderStub.IsStubFile(item.Path) || item.DateLastRefreshed == DateTime.MinValue)
            {
                waiting++;
                continue;
            }

            if ((item.UserData ?? []).Any(row => row.UserId == user.Id))
            {
                kept++;
                continue;
            }

            var restore = new WatchedRestoreItem(entry.Id, entry.Title, episode?.SeasonNumber, episode?.EpisodeNumber, user.Id, user.Username,
                itemId, candidate.PlayedAt);
            if (!apply)
            {
                items.Add(restore);
                continue;
            }

            try
            {
                // Saved through the instance Jellyfin serves the item from: SaveUserData writes every key to the database but
                // refreshes only the instance it is given, so a save through the stored copy read above would leave a cached item
                // answering unplayed until it is evicted (seen live on Jellyfin 12.0.0 for placeholders read just before).
                var target = library.GetItemById(itemId) ?? item;
                var data = userData.GetUserData(user, target);
                if (data is null)
                {
                    failures.Add($"{Label(restore)} for {user.Username}: Jellyfin returned no user data record.");
                    continue;
                }

                data.Played = true;
                data.PlayCount = Math.Max(data.PlayCount, 1);
                data.PlaybackPositionTicks = 0;
                data.LastPlayedDate = candidate.PlayedAt;
                PlaceholderUserData.SaveQuietly(userData, user, target, data, cancellationToken);
                items.Add(restore);
                if (!restoredByEntry.TryGetValue(entry.Id, out var labels)) restoredByEntry[entry.Id] = labels = [];
                labels.Add($"{Label(restore)} for {user.Username}");
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                failures.Add($"{Label(restore)} for {user.Username}: {error.Message}");
            }
        }

        if (apply && restoredByEntry.Count > 0)
        {
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var (entryId, labels) in restoredByEntry)
            {
                var summary = "Watched restored from retention history: " + string.Join(", ", labels);
                database.History.Add(new HistoryRecord
                {
                    EntryId = entryId, EventType = "watched_restored", CreatedAt = now,
                    Summary = summary.Length > 1024 ? summary[..1021] + "..." : summary
                });
            }

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("JellyfinMod restored {Count} watched mark(s) on placeholders from retention history ({Kept} left as Jellyfin holds them, {Waiting} waiting for a placeholder, {Failed} failed)",
                items.Count, kept, waiting, failures.Count);
        }

        return new(!apply, items, kept, waiting, failures);
    }

    private static string Label(WatchedRestoreItem item) =>
        item.SeasonNumber is { } season && item.EpisodeNumber is { } number ? $"{item.Title} S{season:00}E{number:00}" : item.Title;

    /// <summary>The plugin's placeholder episode Jellyfin holds for a reclaimed episode, by its numbers.</summary>
    private Guid? EpisodeStub(Entry entry, Data.Episode episode, Dictionary<Guid, IReadOnlyList<JellyfinEpisode>> cache)
    {
        if (!cache.TryGetValue(entry.Id, out var stubs)) cache[entry.Id] = stubs = placeholders.StubEpisodesOf(entry);
        return stubs.FirstOrDefault(item => item.ParentIndexNumber == episode.SeasonNumber && item.IndexNumber == episode.EpisodeNumber)?.Id;
    }
}
