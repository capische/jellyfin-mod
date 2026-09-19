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
    public async Task<bool> RefreshAsync(Guid userId, Guid jellyfinItemId, string sourceReason, CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshCoreAsync(userId, jellyfinItemId, sourceReason, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent writer (listener, repair, evaluation) created the row first; re-read it once.
            database.ChangeTracker.Clear();
            return await RefreshCoreAsync(userId, jellyfinItemId, sourceReason, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshCoreAsync(Guid userId, Guid jellyfinItemId, string sourceReason, CancellationToken cancellationToken)
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

        // One observation covers every bound version of the target: a resume or favourite on any
        // version protects it, and finishing any version completes it (plugin-retention-policy#4).
        var boundItemIds = await BoundItemIdsAsync(target, cancellationToken).ConfigureAwait(false);
        if (!boundItemIds.Contains(jellyfinItemId)) boundItemIds = [.. boundItemIds, jellyfinItemId];
        var states = boundItemIds.Select(id => library.GetItemById(id) is { } item ? userData.GetUserData(user, item) : null)
            .ToArray();
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
        var wasCompleted = observation.Played && observation.PlaybackPositionTicks == 0 &&
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
        observation.LastPlayedAt = Utc(current.Max(state => state.LastPlayedDate));
        observation.CompletedAt = isCompleted
            ? wasCompleted ? observation.CompletedAt : CompletionTime(observation.LastPlayedAt, now)
            : null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

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
        foreach (var itemId in itemIds)
        {
            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshAsync(userId, itemId, "Repair", cancellationToken).ConfigureAwait(false);
                progress.Report(total == 0 ? 100 : 100d * ++completed / total);
            }
        }

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
