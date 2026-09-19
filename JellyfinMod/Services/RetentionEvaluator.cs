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
    TimeProvider clock)
{
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

    private async Task EvaluateAsync(Target target, CancellationToken cancellationToken)
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
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (target.Entry.RetentionPolicy == RetentionPolicy.Never)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Kept);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var fingerprint = Fingerprint(accessibleUsers.Select(user => user.Id));
        var accessChanged = result.AccessFingerprint.Length > 0 && result.AccessFingerprint != fingerprint;
        result.AccessFingerprint = fingerprint;
        if (accessibleUsers.Length == 0)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.NoAccessibleUsers);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.WatchedUserMode == WatchedUserMode.SelectedUser)
        {
            if (!policy.SelectedUserId.HasValue)
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserMissing);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (accessibleUsers.All(user => user.Id != policy.SelectedUserId.Value))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserInaccessible);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var userIds = accessibleUsers.Select(user => user.Id).ToArray();
        var boundItemIds = target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.Entry.Id)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Evidence read from a representation that is no longer bound says nothing about the current file.
        var observations = (await database.CompletionObservations.AsNoTracking()
                .Where(observation => observation.TargetId == targetId && userIds.Contains(observation.UserId))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Where(observation => boundItemIds.Contains(observation.JellyfinItemId))
            .ToDictionary(observation => observation.UserId);
        if (userIds.Any(userId => !observations.TryGetValue(userId, out var observation) || !observation.EvidenceAvailable))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.CompletionEvidenceMissing);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (observations.Values.Any(observation => observation.PlaybackPositionTicks > 0))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.ActiveResume);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.ExemptFavourites && observations.Values.Any(observation => observation.IsFavorite))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Favorite);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
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
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
