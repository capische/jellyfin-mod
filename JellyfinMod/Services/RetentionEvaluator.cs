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
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
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
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in movieTargets)
            await EvaluateAsync(new Target(entry, null), cancellationToken).ConfigureAwait(false);
        foreach (var target in episodeTargets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates one tracked episode, for example after its own Keep changed (P10.E2).</summary>
    public async Task EvaluateEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var target = await database.Episodes.AsNoTracking().Where(episode => episode.Id == episodeId &&
                database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id))
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is not null) await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
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
                (item, entry) => new Target(entry, item.Id, item.RetentionPolicy, item.ReclaimAfterDays))
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
            // An episode additionally needs a completion after that first evaluation and after retention was enabled
            // (P10.E1, PHASE10 question 1 answered 2026-09-24): only a new watch counts.
            result = new RetentionEvaluation
            {
                EntryId = target.Entry.Id,
                EpisodeId = target.EpisodeId,
                TargetId = targetId,
                BaselineAt = now,
                RequiresFreshCompletion = target.EpisodeId.HasValue
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

        // Keep on the series or on the episode itself protects an episode (P10.E2).
        if (target.IsKept)
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
        // Reclaiming the file a movie folder resolved from leaves its other media sources without a native item
        // until Jellyfin re-resolves the folder and reconciliation observes it (P6.M6). Unreadable is not the
        // same as unwatched: reading evidence now would report none and restart the title's window, so the
        // previous evaluation stands until the bindings are current again.
        if (library is not null && hadPriorEvaluation && boundItemIds.Length > 0 &&
            boundItemIds.All(itemId => ResolvesNatively(itemId) == false))
        {
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

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
            // Evidence read just now may carry a last-played instant after the start of this evaluation; it is not
            // from the future and must not be discarded as such.
            now = clock.GetUtcNow().UtcDateTime;
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

        // Only a new watch counts for an episode (PHASE10 Q1 and Q9, answered 2026-09-24): a completion must carry
        // Jellyfin's own last-played instant at or after the later of when the episode was first tracked and when
        // retention was first ever enabled, so nothing watched before per-episode retention existed becomes due. A later
        // switch-off and switch-on moves nothing.
        var freshFloor = target.EpisodeId.HasValue && policy.GraceStartAt is { } enabledAt
            ? Latest(result.BaselineAt, enabledAt)
            : result.BaselineAt;
        var requiresFresh = result.RequiresFreshCompletion || target.EpisodeId.HasValue;
        var completed = observations.Values
            .Select(observation => (observation.UserId,
                CompletedAt: CompletionInstant(observation, requiresFresh, freshFloor, now)))
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
        var priorBasis = result.State == RetentionEvaluationStates.Scheduled ? result.CompletionBasisAt : null;
        var policyChanged = priorPolicyVersion != 0 && priorPolicyVersion != policy.Version;
        // A target that stayed completed keeps the completion that scheduled it. Re-reading evidence from a remaining
        // version after a sibling version was reclaimed or removed can report a later instant, which must not restart the
        // title's window (P6.M7). A new completion always passes through waiting or blocked first; a policy or access
        // change recomputes as before and still never shortens the prior deadline.
        if (priorBasis is { } keptBasis && !policyChanged && !accessChanged && keptBasis < completionBasis.Value)
            completionBasis = keptBasis;
        // Grace starts no earlier than the first switch-on (PHASE10 Q9): switching retention off and on again does not
        // restart a countdown that was running, and neither does the policy revision that switch bumps.
        var eligibleAt = Latest(Latest(completionBasis.Value, policy.GraceStartAt ?? now), result.BaselineAt);
        if (priorState == RetentionEvaluationStates.Disabled) policyChanged = false;
        if (result.GraceNotBefore is { } restarted) eligibleAt = Latest(eligibleAt, restarted);
        if (!priorDeadline.HasValue && (accessChanged || policyChanged ||
            (hadPriorEvaluation && priorState != RetentionEvaluationStates.Disabled)))
            eligibleAt = Latest(eligibleAt, now);
        var days = target.WindowDays(policy.ReclaimAfterDays);
        // An isolated test instance can shorten every window to minutes; production leaves this at zero.
        var deadline = policy.TestWindowMinutes > 0 ? eligibleAt.AddMinutes(policy.TestWindowMinutes) : eligibleAt.AddDays(days);
        if (priorDeadline > deadline) deadline = priorDeadline.Value;

        if (priorState != RetentionEvaluationStates.Scheduled)
            await RecordWindowStartAsync(target, observations.Values, completionBasis.Value, deadline, cancellationToken)
                .ConfigureAwait(false);
        result.State = RetentionEvaluationStates.Scheduled;
        result.Reason = RetentionEvaluationReasons.CompletionPolicySatisfied;
        result.CompletionBasisAt = completionBasis;
        result.EligibleAt = eligibleAt;
        result.Deadline = deadline;
        await SaveAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that a target's retention window started, with its cause and its files (PHASE10 Q8, 2026-09-24), so the
    /// title's History says why and when its files will go. The cause is the save reason Jellyfin reported for the
    /// completing user's data: the Trakt plugin's sync saves with <c>Import</c>.
    /// </summary>
    private async Task RecordWindowStartAsync(Target target, IEnumerable<CompletionObservation> observations, DateTime basis,
        DateTime deadline, CancellationToken cancellationToken)
    {
        var source = observations.Where(observation => observation.Played)
            .OrderBy(observation => Math.Abs((observation.LastPlayedAt ?? observation.CompletedAt ?? basis).Ticks - basis.Ticks))
            .Select(observation => observation.SourceReason).FirstOrDefault() ?? string.Empty;
        var cause = WindowCause(source);
        var paths = target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.Entry.Id)
                .Select(binding => binding.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Kept files are not counting down; they stay out of the event (PHASE10 Q3).
        var kept = (await database.VersionKeeps.AsNoTracking().Where(keep => keep.EntryId == target.Entry.Id)
            .Select(keep => keep.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var files = RetentionFileNames.Distinct(paths.Where(path => !string.IsNullOrEmpty(path) && !kept.Contains(path!)).Select(path => path!));
        var label = target.EpisodeId.HasValue
            ? await database.Episodes.AsNoTracking().Where(episode => episode.Id == target.EpisodeId)
                .Select(episode => "S" + episode.SeasonNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture) +
                    "E" + episode.EpisodeNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var what = files.Length == 1 ? "this file" : $"these {files.Length} files";
        database.History.Add(new HistoryRecord
        {
            EntryId = target.Entry.Id,
            EventType = "retention_started",
            Summary = $"{(label is null ? string.Empty : label + ": ")}Added to retention ({cause}): {what} will be deleted on " +
                $"{deadline.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} unless kept",
            Data = System.Text.Json.JsonSerializer.Serialize(new { episodeId = target.EpisodeId, cause, sourceReason = source,
                deadline = DateTime.SpecifyKind(deadline, DateTimeKind.Utc), files })
        });
    }

    private static string WindowCause(string sourceReason) => sourceReason switch
    {
        "Import" => "watched on another device (Trakt)",
        // A client that syncs watched state from elsewhere writes user data directly (POST /UserItems/{id}/UserData).
        "UpdateUserData" => "watched on another device (synced)",
        "TogglePlayed" => "marked played",
        "PlaybackFinished" or "PlaybackProgress" or "PlaybackStart" => "watched on this server",
        _ => "watched"
    };

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

    /// <summary>Whether Jellyfin still has this native item, or null when the library cannot answer.</summary>
    private bool? ResolvesNatively(Guid itemId)
    {
        try
        {
            return library!.GetItemById(itemId) is not null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
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
    private static DateTime? CompletionInstant(CompletionObservation observation, bool requiresFresh, DateTime floor, DateTime now)
    {
        if (!observation.Played || observation.PlaybackPositionTicks != 0 || !observation.CompletedAt.HasValue) return null;
        if (!requiresFresh) return observation.CompletedAt.Value;
        // A fresh completion must carry Jellyfin's own last-played instant at or after the baseline. A played state with
        // no last-played date (an imported or synced watched flag) has no evidence of when it happened; the stored start
        // of the completed state is then only the time the plugin first read it, which is not a new completion (P10.E1).
        // Jellyfin's MarkPlayed keeps an older LastPlayedDate when one exists and MarkUnplayed clears it, so "mark played"
        // on an item played before the floor stays old, while mark unplayed then played, or a real playback, is fresh.
        if (observation.LastPlayedAt is not { } lastPlayed || lastPlayed < floor || lastPlayed > now) return null;
        return observation.CompletedAt.Value >= floor ? observation.CompletedAt.Value : lastPlayed;
    }

    private static void Set(RetentionEvaluation result, string state, string reason)
    {
        result.State = state;
        result.Reason = reason;
        result.CompletionBasisAt = null;
        result.EligibleAt = null;
        result.Deadline = null;
    }

    private sealed record Target(Entry Entry, Guid? EpisodeId, RetentionPolicy EpisodePolicy = RetentionPolicy.Inherit,
        int? EpisodeDays = null)
    {
        public bool IsKept => RetentionOverrides.IsKept(Entry, EpisodeId.HasValue ? EpisodePolicy : null);

        public int WindowDays(int globalDays) =>
            RetentionOverrides.WindowDays(Entry, EpisodeId.HasValue ? EpisodePolicy : null, EpisodeDays, globalDays);
    }
}
