using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Computes access-aware completion deadlines without deleting media.</summary>
public sealed class RetentionEvaluator(
    ModDbContext database,
    IUserManager users,
    LibraryAccess access,
    TimeProvider clock,
    RetentionCompletionService? completion = null,
    ILibraryManager? library = null,
    IUserDataManager? userData = null)
{
    /// <summary>Re-evaluates every episode target that belongs to one native series (P3.T11).</summary>
    public async Task EvaluateSeriesAsync(Guid seriesItemId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var targets = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.SeriesItemId == seriesItemId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var target in targets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates every bound movie and episode.</summary>
    public async Task EvaluateAllAsync(CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var movieTargets = await database.EntryBindings.AsNoTracking()
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id, (_, entry) => entry)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var episodeTargets = await database.EpisodeBindings.AsNoTracking()
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in movieTargets)
            await EvaluateAsync(new Target(entry, null), cancellationToken).ConfigureAwait(false);
        foreach (var target in episodeTargets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates the stable target represented by a native movie or episode.</summary>
    public async Task EvaluateNativeItemAsync(Guid jellyfinItemId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var episode = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, item => item.Id,
                (_, item) => item)
            .Join(database.Entries.AsNoTracking(), item => item.EntryId, entry => entry.Id,
                (item, entry) => new Target(entry, item.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is not null)
        {
            await EvaluateAsync(episode, cancellationToken).ConfigureAwait(false);
            return;
        }

        var movie = await database.EntryBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id, (_, entry) => entry)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (movie is not null) await EvaluateAsync(new Target(movie, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates one target. Listener, repair and Keep can evaluate the same new target at once; the loser of
    /// the insert re-reads the winner's row instead of failing (P3.T11).
    /// </summary>
    private async Task EvaluateAsync(Target target, CancellationToken cancellationToken)
    {
        try
        {
            await EvaluateCoreAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            database.ChangeTracker.Clear();
            await EvaluateCoreAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluateCoreAsync(Target target, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var targetId = target.EpisodeId ?? target.Entry.Id;
        var result = await database.RetentionEvaluations.SingleOrDefaultAsync(
            candidate => candidate.TargetId == targetId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // A target's grace never starts before it was first evaluated, which is at or after its
            // binding. Historical native play dates must not make newly bound media due at once.
            result = new RetentionEvaluation
            {
                EntryId = target.Entry.Id,
                EpisodeId = target.EpisodeId,
                TargetId = targetId,
                BaselineAt = now
            };
            database.RetentionEvaluations.Add(result);
        }

        var priorPolicyVersion = result.PolicyVersion;
        var priorState = result.State;
        var hadPriorEvaluation = result.EvaluatedAt != default;
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        result.EvaluatedAt = now;
        result.PolicyVersion = policy?.Version ?? 0;
        if (policy is null || !policy.Enabled)
        {
            Set(result, RetentionEvaluationStates.Disabled, RetentionEvaluationReasons.RetentionDisabled);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (target.Entry.RetentionPolicy == RetentionPolicy.Never)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Kept);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        User[] accessibleUsers;
        try
        {
            accessibleUsers = users.GetUsers().Where(IsActive)
                .Where(user => access.CanUseLibrary(user, target.Entry.MediaType, target.Entry.TargetLibraryId))
                .OrderBy(user => user.Id)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.AccessUnavailable);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fingerprint = Fingerprint(accessibleUsers.Select(user => user.Id));
        var accessChanged = result.AccessFingerprint.Length > 0 && result.AccessFingerprint != fingerprint;
        result.AccessFingerprint = fingerprint;
        if (accessibleUsers.Length == 0)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.NoAccessibleUsers);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.WatchedUserMode == WatchedUserMode.SelectedUser)
        {
            if (!policy.SelectedUserId.HasValue)
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserMissing);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (accessibleUsers.All(user => user.Id != policy.SelectedUserId.Value))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserInaccessible);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var userIds = accessibleUsers.Select(user => user.Id).ToArray();
        var boundItemIds = target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.Entry.Id)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var observations = await LoadCurrentObservationsAsync(targetId, userIds, boundItemIds, cancellationToken)
            .ConfigureAwait(false);
        var missingUserIds = userIds.Where(userId => !observations.ContainsKey(userId)).ToArray();
        if (missingUserIds.Length > 0 && boundItemIds.Length > 0 && completion is not null)
        {
            // A user who never touched the item has no event to record; read their live state now
            // instead of blocking Any and Selected mode until a manual repair (prior-H1).
            foreach (var userId in missingUserIds)
                await completion.RefreshAsync(userId, boundItemIds[0], "Evaluate", cancellationToken).ConfigureAwait(false);
            observations = await LoadCurrentObservationsAsync(targetId, userIds, boundItemIds, cancellationToken)
                .ConfigureAwait(false);
        }
        if (userIds.Any(userId => !observations.TryGetValue(userId, out var observation) || !observation.EvidenceAvailable))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.CompletionEvidenceMissing);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (observations.Values.Any(observation => observation.PlaybackPositionTicks > 0))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.ActiveResume);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.ExemptFavourites && observations.Values.Any(observation => observation.IsFavorite))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Favorite);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A favourite series protects its episodes. The state is persisted, so Details and "Due within 7 days"
        // agree with the executor instead of learning it only at unlink time (P3.T11).
        if (policy.ExemptFavourites && target.EpisodeId is { } favouriteEpisodeId && library is not null && userData is not null)
        {
            var seriesIds = await database.EpisodeBindings.AsNoTracking()
                .Where(binding => binding.EpisodeId == favouriteEpisodeId).Select(binding => binding.SeriesItemId)
                .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var seriesItems = seriesIds.Select(id => library.GetItemById(id)).ToArray();
            if (seriesItems.Any(series => series is null))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SeriesUnavailable);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (seriesItems.Any(series => accessibleUsers.Any(user => userData.GetUserData(user, series!)?.IsFavorite == true)))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.FavoriteSeries);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var completed = observations.Values
            .Select(observation => (observation.UserId, CompletedAt: CompletionInstant(observation, result, now)))
            .Where(item => item.CompletedAt.HasValue)
            .ToDictionary(item => item.UserId, item => item.CompletedAt!.Value);
        DateTime? completionBasis = policy.WatchedUserMode switch
        {
            WatchedUserMode.AllUsers when userIds.All(completed.ContainsKey) => completed.Values.Max(),
            WatchedUserMode.SelectedUser when completed.TryGetValue(policy.SelectedUserId!.Value, out var selected) => selected,
            WatchedUserMode.AnyUser when completed.Count > 0 => completed.Values.Min(),
            _ => null
        };
        if (!completionBasis.HasValue)
        {
            Set(result, RetentionEvaluationStates.Waiting, RetentionEvaluationReasons.WaitingForCompletion);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        var priorDeadline = result.State == RetentionEvaluationStates.Scheduled ? result.Deadline : null;
        var policyChanged = priorPolicyVersion != 0 && priorPolicyVersion != policy.Version;
        var eligibleAt = Latest(Latest(completionBasis.Value, policy.EnabledAt ?? now), result.BaselineAt);
        if (!priorDeadline.HasValue && (accessChanged || policyChanged ||
            (hadPriorEvaluation && priorState != RetentionEvaluationStates.Disabled)))
            eligibleAt = Latest(eligibleAt, now);
        var days = target.Entry.RetentionPolicy == RetentionPolicy.Days && target.Entry.ReclaimAfterDays is > 0
            ? Math.Clamp(target.Entry.ReclaimAfterDays.Value, 1, 3650)
            : policy.ReclaimAfterDays;
        var deadline = eligibleAt.AddDays(days);
        if (priorDeadline > deadline) deadline = priorDeadline.Value;

        result.State = RetentionEvaluationStates.Scheduled;
        result.Reason = RetentionEvaluationReasons.CompletionPolicySatisfied;
        result.CompletionBasisAt = completionBasis;
        result.EligibleAt = eligibleAt;
        result.Deadline = deadline;
        await SaveAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an evaluation only when something other than its evaluation time changed (P3.T11).</summary>
    private async Task SaveAsync(RetentionEvaluation result, CancellationToken cancellationToken)
    {
        var tracked = database.Entry(result);
        if (tracked.State == EntityState.Modified && tracked.Properties
                .Where(property => property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                .All(property => property.Metadata.Name == nameof(RetentionEvaluation.EvaluatedAt)))
            tracked.State = EntityState.Unchanged;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsActive(User user) =>
        !user.Permissions.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value);

    private static string Fingerprint(IEnumerable<Guid> userIds)
    {
        var value = string.Join(',', userIds.Order().Select(id => id.ToString("N")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static DateTime Latest(DateTime left, DateTime right) => left > right ? left : right;

    /// <summary>Loads each user's observation, ignoring evidence read from a representation that is no longer bound.</summary>
    private async Task<Dictionary<Guid, CompletionObservation>> LoadCurrentObservationsAsync(
        Guid targetId, Guid[] userIds, Guid[] boundItemIds, CancellationToken cancellationToken) =>
        (await database.CompletionObservations.AsNoTracking()
            .Where(observation => observation.TargetId == targetId && userIds.Contains(observation.UserId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        .Where(observation => boundItemIds.Contains(observation.JellyfinItemId))
        .ToDictionary(observation => observation.UserId);

    /// <summary>
    /// Returns when this user's completion counts. After a reset only a completion at or after the
    /// baseline counts; the stored start of the completed state may predate it when Jellyfin
    /// reattached old user data, so a later native last-played value is accepted as the fresh one.
    /// </summary>
    private static DateTime? CompletionInstant(CompletionObservation observation, RetentionEvaluation result, DateTime now)
    {
        if (!observation.Played || observation.PlaybackPositionTicks != 0 || !observation.CompletedAt.HasValue) return null;
        if (!result.RequiresFreshCompletion) return observation.CompletedAt.Value;
        if (observation.CompletedAt.Value >= result.BaselineAt) return observation.CompletedAt.Value;
        return observation.LastPlayedAt is { } lastPlayed && lastPlayed >= result.BaselineAt && lastPlayed <= now
            ? lastPlayed
            : null;
    }

    private static void Set(RetentionEvaluation result, string state, string reason)
    {
        result.State = state;
        result.Reason = reason;
        result.CompletionBasisAt = null;
        result.EligibleAt = null;
        result.Deadline = null;
    }

    private sealed record Target(Entry Entry, Guid? EpisodeId);
}
