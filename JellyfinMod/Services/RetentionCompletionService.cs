using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Re-reads and persists authoritative per-user completion evidence.</summary>
public sealed class RetentionCompletionService(
    ModDbContext database,
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    TimeProvider clock)
{
    /// <summary>Refreshes one native movie or episode for one user.</summary>
    public async Task RefreshAsync(Guid userId, Guid jellyfinItemId, string sourceReason, CancellationToken cancellationToken)
    {
        var user = users.GetUserById(userId);
        if (user is null) return;

        var target = await ResolveTargetAsync(jellyfinItemId, cancellationToken).ConfigureAwait(false);
        if (target is null) return;

        var now = clock.GetUtcNow().UtcDateTime;
        var observation = await database.CompletionObservations.SingleOrDefaultAsync(
            candidate => candidate.TargetId == target.TargetId && candidate.UserId == userId,
            cancellationToken).ConfigureAwait(false);
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

        var item = library.GetItemById(jellyfinItemId);
        var current = item is null ? null : userData.GetUserData(user, item);
        observation.JellyfinItemId = jellyfinItemId;
        observation.ObservedAt = now;
        observation.SourceReason = sourceReason;
        observation.EvidenceAvailable = current is not null;
        if (current is null)
        {
            observation.CompletedAt = null;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var wasCompleted = observation.Played && observation.PlaybackPositionTicks == 0 &&
            observation.CompletedAt.HasValue;
        var isCompleted = current.Played && current.PlaybackPositionTicks == 0;
        observation.Played = current.Played;
        observation.IsFavorite = current.IsFavorite;
        observation.PlaybackPositionTicks = current.PlaybackPositionTicks;
        observation.LastPlayedAt = Utc(current.LastPlayedDate);
        observation.CompletedAt = isCompleted
            ? wasCompleted ? observation.CompletedAt : CompletionTime(observation.LastPlayedAt, now)
            : null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

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
