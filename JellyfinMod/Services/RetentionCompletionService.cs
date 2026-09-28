using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Re-reads and persists authoritative per-user completion evidence.</summary>
public sealed class RetentionCompletionService(
    ModDbContext database,
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    TimeProvider clock,
    ILogger<RetentionCompletionService> logger)
{
    /// <summary>
    /// Refreshes one native movie or episode for one user. Returns false when a playback-progress update
    /// changed nothing but the position, which is then not written (P3.T11).
    /// </summary>
    /// <remarks>
    /// An item that cannot be read (as opposed to one Jellyfin no longer has) throws and leaves the stored observation as
    /// it is: a passing read error must not turn recorded evidence into "unavailable", which would clear a running
    /// deadline and later make an elapsed window due with no new warning (review P3-1).
    /// </remarks>
    public Task<bool> RefreshAsync(Guid userId, Guid jellyfinItemId, string sourceReason, CancellationToken cancellationToken) =>
        RefreshAsync(userId, jellyfinItemId, sourceReason, false, null, cancellationToken);

    /// <summary>
    /// Refreshes one native movie or episode for one user after events of which at least one reported it unwatched
    /// (<paramref name="unwatchedSeen"/>). The read sees only the newest state, so an unwatched event coalesced with a later
    /// one is not visible in it; it still ends any completion recorded before it, a carried one included (review P1-10).
    /// </summary>
    public Task<bool> RefreshAsync(Guid userId, Guid jellyfinItemId, string sourceReason, bool unwatchedSeen,
        CancellationToken cancellationToken) =>
        RefreshAsync(userId, jellyfinItemId, sourceReason, unwatchedSeen, null, cancellationToken);

    private async Task<bool> RefreshAsync(Guid userId, Guid jellyfinItemId, string sourceReason, bool unwatchedSeen,
        Dictionary<Guid, BaseItem?>? loaded, CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(userId, jellyfinItemId, sourceReason, unwatchedSeen, loaded, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent writer (listener, repair, evaluation) created the row first; re-read it once.
            database.ChangeTracker.Clear();
            return await RefreshCoreAsync(userId, jellyfinItemId, sourceReason, unwatchedSeen, loaded, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The stored item, or null when Jellyfin has no such item; throws when it cannot be read. A repair pass shares the
    /// items it loaded between users (review P3-2).
    /// </summary>
    private BaseItem? StoredItem(Guid itemId, Dictionary<Guid, BaseItem?>? loaded)
    {
        if (loaded is not null && loaded.TryGetValue(itemId, out var known)) return known;
        var read = StoredUserData.TryItem(library, itemId, out var item, out var error);
        if (read == StoredRead.Error)
        {
            logger.LogWarning(error, "JellyfinMod could not read item {ItemId} for retention evidence; the recorded evidence is kept",
                itemId);
            throw new InvalidOperationException($"Item {itemId} could not be read for retention evidence", error);
        }

        if (loaded is not null) loaded[itemId] = item;
        return item;
    }

    private async Task<bool> RefreshCoreAsync(Guid userId, Guid jellyfinItemId, string sourceReason, bool unwatchedSeen,
        Dictionary<Guid, BaseItem?>? loaded, CancellationToken cancellationToken)
    {
        var user = users.GetUserById(userId);
        if (user is null)
        {
            logger.LogDebug("Retention evidence skipped unknown user {UserId}", userId);
            return false;
        }

        var target = await ResolveTargetAsync(jellyfinItemId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            logger.LogDebug("Retention evidence skipped unbound native item {ItemId}", jellyfinItemId);
            return false;
        }

        // One observation covers every bound version of the target: a resume or favourite on any
        // version protects it, and finishing any version completes it (plugin-retention-policy#4).
        // The state is read as stored, never from Jellyfin's cached item, which a Trakt or NFO import does not update
        // (Q16 review P2-2). The event that queued this read is not used for the state: the listener coalesces events per
        // user and item, so the stored state is the newest one, at least as new as any event.
        var boundItemIds = await BoundItemIdsAsync(target, cancellationToken).ConfigureAwait(false);
        if (!boundItemIds.Contains(jellyfinItemId)) boundItemIds = [.. boundItemIds, jellyfinItemId];
        // An unwatched event ends the recorded completion, carried or not, whatever the read below finds, so that revocation is
        // written first: the listener has already taken this work off its queue, and a read that fails now must not drop the
        // unwatched state and leave the old date and deadline to a later undated played flag (re-review P-1). An ordinary
        // completion is revoked too, because a removal still in flight may carry it to a remaining copy next (re-review P-1b).
        if (unwatchedSeen)
            await RetentionLiveCheck.RevokeCompletionAsync(database, target.TargetId, [userId], clock.GetUtcNow().UtcDateTime,
                cancellationToken).ConfigureAwait(false);
        // Read everything before changing the tracked observation, so an item that cannot be read leaves it as it was.
        var states = boundItemIds.Select(id => StoredItem(id, loaded) is { } item ? userData.GetUserData(user, item) : null)
            .ToArray();
        var now = clock.GetUtcNow().UtcDateTime;
        var observation = await database.CompletionObservations.SingleOrDefaultAsync(
            candidate => candidate.TargetId == target.TargetId && candidate.UserId == userId,
            cancellationToken).ConfigureAwait(false);
        var existing = observation is not null;
        if (observation is null)
        {
            observation = new CompletionObservation
            {
                EntryId = target.EntryId,
                EpisodeId = target.EpisodeId,
                TargetId = target.TargetId,
                UserId = userId,
                JellyfinItemId = jellyfinItemId
            };
            database.CompletionObservations.Add(observation);
        }

        var previousReason = observation.SourceReason;
        observation.JellyfinItemId = jellyfinItemId;
        observation.ObservedAt = now;
        observation.SourceReason = sourceReason;
        observation.EvidenceAvailable = states.All(state => state is not null);
        if (!observation.EvidenceAvailable)
        {
            observation.CompletedAt = null;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var current = states.OfType<UserItemData>().ToArray();
        // An unwatched state seen since the last read ended whatever completion was recorded: one read now starts anew.
        var wasCompleted = !unwatchedSeen && observation.Played && observation.PlaybackPositionTicks == 0 &&
            observation.CompletedAt.HasValue;
        var finished = current.Any(state => state.Played && state.PlaybackPositionTicks == 0);
        var resume = current.Max(state => state.PlaybackPositionTicks);
        var isCompleted = finished && resume == 0;
        // Sustained playback only moves the position: once resume protection exists, nothing that
        // retention reads changes, so the row is left as it is (P3.T11).
        if (existing && sourceReason == "PlaybackProgress" && observation.PlaybackPositionTicks > 0 && resume > 0 &&
            observation.Played == finished && observation.IsFavorite == current.Any(state => state.IsFavorite) &&
            !observation.CompletedAt.HasValue)
        {
            database.ChangeTracker.Clear();
            return false;
        }

        observation.Played = finished;
        observation.IsFavorite = current.Any(state => state.IsFavorite);
        observation.PlaybackPositionTicks = resume;
        // The dated completion of a version removed on purpose stands while the remaining copies still read played with no
        // resume and no date of their own (their played flag was copied without one, review P2-4); unwatched, resumed or a
        // dated state replaces it as before.
        var carried = !unwatchedSeen && previousReason == CarriedReason && isCompleted && wasCompleted &&
            current.All(state => state.LastPlayedDate is null);
        if (carried) observation.SourceReason = CarriedReason;
        observation.LastPlayedAt = Utc(current.Max(state => state.LastPlayedDate)) ?? (carried ? observation.LastPlayedAt : null);
        observation.CompletedAt = isCompleted
            ? wasCompleted ? observation.CompletedAt : CompletionTime(observation.LastPlayedAt, now)
            : null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The source of a completion carried over from a version the plugin removed on purpose to a version that stays
    /// (review P2-4); it counts only while the remaining copies read played with no resume and no date of their own.
    /// </summary>
    internal const string CarriedReason = "CarriedFromRemovedVersion";

    /// <summary>
    /// The source of an observation whose completion was revoked because an unwatched state was seen, by the last check
    /// before an unlink or by an unwatched event (review P1-10, re-review P-1, P-1b); the next read of Jellyfin's state
    /// replaces it, and a revoked completion is never carried to a remaining copy.
    /// </summary>
    internal const string UnwatchedReason = "UnwatchedSeenLive";

    private async Task<Guid[]> BoundItemIdsAsync(RetentionTarget target, CancellationToken cancellationToken) =>
        target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.EntryId)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Repairs missed completion notifications for every bound movie and episode.</summary>
    public async Task RefreshAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var itemIds = await database.EntryBindings.AsNoTracking()
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id, (binding, _) => binding.JellyfinItemId)
            .Concat(database.EpisodeBindings.AsNoTracking().Select(binding => binding.JellyfinItemId))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var userIds = users.GetUsers().Select(user => user.Id).ToArray();
        var total = itemIds.Length * userIds.Length;
        logger.LogInformation("Refreshing retention evidence for {ItemCount} bound items and {UserCount} users",
            itemIds.Length, userIds.Length);
        var completed = 0;
        var skipped = 0;
        foreach (var itemId in itemIds)
        {
            // Each item, with the other versions of its target, is loaded once for every user.
            var loaded = new Dictionary<Guid, BaseItem?>();
            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await RefreshAsync(userId, itemId, "Repair", false, loaded, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Unreadable now, or not written: the recorded evidence stays, and the next repair or event reads it
                    // again. A write failure is not a passing read error, so neither is left at Debug (P3-B).
                    skipped++;
                    logger.LogWarning(error, "Retention evidence repair skipped item {ItemId} for user {UserId}", itemId, userId);
                    database.ChangeTracker.Clear();
                }

                progress.Report(total == 0 ? 100 : 100d * ++completed / total);
            }
        }

        if (skipped > 0)
            logger.LogWarning("Retention evidence repair skipped {Skipped} of {Total} item and user pairs; they are read again next time",
                skipped, total);
        if (total == 0) progress.Report(100);
    }

    private async Task<RetentionTarget?> ResolveTargetAsync(Guid jellyfinItemId, CancellationToken cancellationToken)
    {
        var episode = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, item => item.Id,
                (binding, item) => new RetentionTarget(item.EntryId, item.Id, item.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is not null) return episode;

        return await database.EntryBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id,
                (binding, entry) => new RetentionTarget(entry.Id, null, entry.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTime? Utc(DateTime? value) => value.HasValue
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        : null;

    private static DateTime CompletionTime(DateTime? lastPlayedAt, DateTime observedAt) =>
        lastPlayedAt is { } value && value <= observedAt ? value : observedAt;

    private sealed record RetentionTarget(Guid EntryId, Guid? EpisodeId, Guid TargetId);
}
