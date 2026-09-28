using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>
/// Re-reads live Jellyfin state for one retention target immediately before an irreversible unlink.
/// Stored observations can miss a favourite, unwatched or resume event; this check does not.
/// </summary>
public sealed class RetentionLiveCheck(
    ModDbContext database,
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    ISessionManager sessions,
    LibraryAccess access,
    TimeProvider clock)
{
    /// <summary>Returns a stable blocking reason, or null when live state still permits reclamation.</summary>
    /// <param name="operation">The prepared operation.</param>
    /// <param name="policy">The live policy.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <param name="requireCompletion">
    /// False for an upgrade replacement (P6.M5, PHASE10 Q10): the watched rule does not apply, every other live protection does.
    /// </param>
    public async Task<string?> BlockReasonAsync(
        RetentionOperation operation,
        RetentionPolicySnapshot policy,
        CancellationToken cancellationToken,
        bool requireCompletion = true)
    {
        var unwatched = new HashSet<Guid>();
        var reason = await BlockReasonAsync(operation, policy, unwatched, requireCompletion, cancellationToken).ConfigureAwait(false);
        // A user seen here with no copy played has not finished the target, whatever completion was recorded or carried over
        // from a removed copy: it is revoked durably, so a later played flag without a date (an NFO or Trakt import) cannot
        // bring the old date and deadline back when the unwatched event itself is late or coalesced away (review P1-10).
        if (unwatched.Count > 0 && (operation.EpisodeId ?? operation.EntryId) is { } targetId)
            await RevokeCompletionAsync(database, targetId, unwatched, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return reason;
    }

    /// <summary>
    /// Revokes the recorded completion of these users for one target, carried or not (review P1-10, re-review P-1b): the
    /// observation reads as not finished, and marked unwatched-seen, until the next read of Jellyfin's state records what is
    /// there now, dated or not. Nothing carries a revoked completion to a remaining copy
    /// (<see cref="ReconciliationService.RepointRepresentationEvidenceAsync"/>), so the revocation also holds when it is
    /// written before a removal that is still carrying the evidence.
    /// </summary>
    internal static Task<int> RevokeCompletionAsync(ModDbContext database, Guid targetId, IReadOnlyCollection<Guid> userIds, DateTime now,
        CancellationToken cancellationToken) =>
        database.CompletionObservations
            .Where(observation => observation.TargetId == targetId && userIds.Contains(observation.UserId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(observation => observation.SourceReason, RetentionCompletionService.UnwatchedReason)
                .SetProperty(observation => observation.Played, false)
                .SetProperty(observation => observation.CompletedAt, (DateTime?)null)
                .SetProperty(observation => observation.LastPlayedAt, (DateTime?)null)
                .SetProperty(observation => observation.ObservedAt, now), cancellationToken);

    private async Task<string?> BlockReasonAsync(
        RetentionOperation operation,
        RetentionPolicySnapshot policy,
        HashSet<Guid> unwatched,
        bool requireCompletion,
        CancellationToken cancellationToken)
    {
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == operation.EntryId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return RetentionLiveReasons.BindingUnavailable;
        if (entry.RetentionPolicy == RetentionPolicy.Never) return RetentionLiveReasons.Kept;
        // An episode's own Keep is read again here, the last check before the unlink (P10.E2).
        if (operation.EpisodeId is { } keptEpisodeId && await database.Episodes.AsNoTracking()
                .AnyAsync(episode => episode.Id == keptEpisodeId && episode.RetentionPolicy == RetentionPolicy.Never,
                    cancellationToken).ConfigureAwait(false))
            return RetentionLiveReasons.Kept;
        // A kept file is read again too (PHASE10 Q3), by the path and identity the operation will unlink.
        var bindingPath = operation.EpisodeId.HasValue
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.Id == operation.BindingId)
                .Select(binding => binding.MediaPath).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.Id == operation.BindingId)
                .Select(binding => binding.MediaPath).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (await database.VersionKeeps.AsNoTracking().AnyAsync(keep => keep.MediaPath == operation.MediaPath ||
                keep.MediaPath == bindingPath || keep.PhysicalIdentity == operation.PhysicalIdentity, cancellationToken)
                .ConfigureAwait(false))
            return RetentionLiveReasons.VersionKept;

        Guid[] versionItemIds;
        Guid? seriesItemId = null;
        if (operation.EpisodeId is { } episodeId)
        {
            var bindings = await database.EpisodeBindings.AsNoTracking()
                .Where(binding => binding.EpisodeId == episodeId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            versionItemIds = bindings.Select(binding => binding.JellyfinItemId).ToArray();
            seriesItemId = bindings.FirstOrDefault(binding => binding.Id == operation.BindingId)?.SeriesItemId;
        }
        else
        {
            var bindings = await database.EntryBindings.AsNoTracking()
                .Where(binding => binding.EntryId == operation.EntryId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            versionItemIds = bindings.Select(binding => binding.JellyfinItemId)
                .Concat(bindings.Select(binding => binding.VersionGroupId)).ToArray();
        }

        versionItemIds = versionItemIds.Append(operation.JellyfinItemId).Distinct().ToArray();
        if (IsPlaying(versionItemIds) is not { } playing) return RetentionLiveReasons.ActiveSessionUnknown;
        if (playing) return RetentionLiveReasons.ActiveSession;

        User[] eligibleUsers;
        try
        {
            eligibleUsers = users.GetUsers().Where(IsActive)
                .Where(user => access.CanUseLibrary(user, entry.MediaType, entry.TargetLibraryId)).ToArray();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return RetentionLiveReasons.LiveStateUnavailable;
        }

        if (eligibleUsers.Length == 0) return RetentionLiveReasons.NoAccessibleUsers;
        // Played, resume and favourite state is read as stored, never from Jellyfin's cached items: a Trakt or NFO import
        // (for example a title marked unwatched) saves through another instance and leaves the cached one stale (Q16
        // review P2-2).
        // A version Jellyfin no longer has is skipped, as before; one that cannot be read is unknown state, and a version
        // with a resume or a favourite could be among them, so nothing is unlinked (review P2-1).
        var items = new List<BaseItem>();
        foreach (var id in versionItemIds)
        {
            var read = StoredUserData.TryItem(library, id, out var stored, out _);
            if (read == StoredRead.Error) return RetentionLiveReasons.LiveStateUnavailable;
            if (read == StoredRead.Found) items.Add(stored!);
        }

        if (items.Count == 0) return RetentionLiveReasons.LiveStateUnavailable;
        BaseItem? series = null;
        if (seriesItemId is { } seriesId &&
            StoredUserData.TryItem(library, seriesId, out series, out _) == StoredRead.Error)
            return RetentionLiveReasons.LiveStateUnavailable;
        if (operation.EpisodeId.HasValue && policy.ExemptFavourites && series is null)
            return RetentionLiveReasons.LiveStateUnavailable;

        // A file holding several episodes: no episode it covers may be kept, and the file itself must be finished, because
        // for an episode whose only copy it is, it is that episode (PHASE10 Q5).
        var multi = items.FirstOrDefault(item => item.Id == operation.JellyfinItemId) as MediaBrowser.Controller.Entities.TV.Episode;
        var coversSeveral = multi is { IndexNumberEnd: { } lastCovered, IndexNumber: { } firstCovered } && lastCovered > firstCovered;
        if (coversSeveral && operation.EpisodeId.HasValue)
        {
            var season = multi!.ParentIndexNumber;
            var (first, last) = (multi.IndexNumber!.Value, multi.IndexNumberEnd!.Value);
            if (season is null || await database.Episodes.AsNoTracking().AnyAsync(episode => episode.EntryId == operation.EntryId &&
                    episode.SeasonNumber == season && episode.EpisodeNumber >= first && episode.EpisodeNumber <= last &&
                    episode.RetentionPolicy == RetentionPolicy.Never, cancellationToken).ConfigureAwait(false))
                return RetentionLiveReasons.Kept;
        }

        // A watch counts only when Jellyfin dates it at or after the target's floor, exactly as the evaluator counts it
        // (RET3-R4): a played flag with no date (an imported state, or Jellyfin 12 marking the other versions of a
        // finished one) or an old date reattached to a re-acquired file is not a completion here either.
        DateTime? floor = null;
        if (requireCompletion)
        {
            var targetId = operation.EpisodeId ?? operation.EntryId;
            var evaluation = targetId is { } id
                ? await database.RetentionEvaluations.AsNoTracking().SingleOrDefaultAsync(item => item.TargetId == id, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (evaluation is null) return RetentionLiveReasons.NotCompleted;
            floor = RetentionEvaluator.FreshFloor(evaluation, policy);
        }

        bool Counts(UserItemData? state) => state is { Played: true, PlaybackPositionTicks: 0, LastPlayedDate: { } played } &&
            floor is { } since && DateTime.SpecifyKind(played, DateTimeKind.Utc) >= since;
        // A dated completion recorded through a version that has since been removed on purpose (a lower copy reclaimed
        // first, an upgrade, Remove this version) still stands while the remaining copies read played with no resume:
        // Jellyfin 12 copies the played flag to them without a date, and the copies expire together (decision 1, review
        // P2-4). Only while no remaining copy carries a date of its own: a dated live state is Jellyfin's word and is judged
        // against the floor as before (RET3-R4). Marking the title unwatched clears the flag and so the completion.
        // The carried completion counts exactly when the evaluator counts it (review P2-11): its own instant at or after the
        // floor, or, when that predates the floor, a Jellyfin last-played date at or after it (RetentionEvaluator.CompletionInstant).
        var now = clock.GetUtcNow().UtcDateTime;
        var recorded = requireCompletion && (operation.EpisodeId ?? operation.EntryId) is { } recordedTarget
            ? (await database.CompletionObservations.AsNoTracking()
                    .Where(observation => observation.TargetId == recordedTarget && observation.CompletedAt != null &&
                        observation.SourceReason == RetentionCompletionService.CarriedReason)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(observation => observation.UserId)
            : [];
        bool Recorded(Guid userId, UserItemData[] states) => floor is { } since &&
            recorded.TryGetValue(userId, out var observation) &&
            RetentionEvaluator.CompletionInstant(observation, true, since, now).HasValue &&
            states.Length > 0 && states.All(state => state.PlaybackPositionTicks == 0 && state.LastPlayedDate is null) &&
            states.Any(state => state.Played);

        var completedBy = new HashSet<Guid>();
        var fileCompletedBy = new HashSet<Guid>();
        foreach (var user in eligibleUsers)
        {
            UserItemData?[] states;
            UserItemData? seriesState = null, fileState = null;
            try
            {
                states = items.Select(item => userData.GetUserData(user, item)).ToArray();
                if (series is not null) seriesState = userData.GetUserData(user, series);
                if (coversSeveral) fileState = userData.GetUserData(user, multi!);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return RetentionLiveReasons.LiveStateUnavailable;
            }

            if (states.Any(state => state is null)) return RetentionLiveReasons.LiveStateUnavailable;
            var current = states.OfType<UserItemData>().ToArray();
            if (!current.Any(state => state.Played)) unwatched.Add(user.Id);
            if (current.Any(state => state.PlaybackPositionTicks > 0)) return RetentionLiveReasons.ActiveResume;
            if (policy.ExemptFavourites && current.Any(state => state.IsFavorite)) return RetentionLiveReasons.Favorite;
            if (policy.ExemptFavourites && seriesState?.IsFavorite == true)
                return RetentionLiveReasons.FavoriteSeries;
            if (current.Any(Counts) || Recorded(user.Id, current)) completedBy.Add(user.Id);
            if (coversSeveral && Counts(fileState)) fileCompletedBy.Add(user.Id);
        }

        if (!requireCompletion) return null;
        bool Satisfied(HashSet<Guid> done) => policy.WatchedUserMode switch
        {
            WatchedUserMode.AllUsers => eligibleUsers.All(user => done.Contains(user.Id)),
            WatchedUserMode.SelectedUser => policy.SelectedUserId is { } selected &&
                eligibleUsers.Any(user => user.Id == selected) && done.Contains(selected),
            WatchedUserMode.AnyUser => done.Count > 0,
            _ => false
        };
        if (!Satisfied(completedBy)) return RetentionLiveReasons.NotCompleted;
        return coversSeveral && !Satisfied(fileCompletedBy) ? RetentionLiveReasons.NotCompleted : null;
    }

    /// <summary>
    /// Whether a session plays exactly this file, or null when sessions cannot be read (V1, Remove this version). Jellyfin
    /// names the version through the media source; without one, the item itself is what plays.
    /// </summary>
    internal bool? IsFilePlaying(Guid itemId)
    {
        try
        {
            return sessions.Sessions.Any(session => Guid.TryParse(session.PlayState?.MediaSourceId, out var source)
                ? source == itemId
                : session.NowPlayingItem?.Id == itemId || session.FullNowPlayingItem?.Id == itemId);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Returns whether any session plays one of the items, or null when sessions cannot be read.</summary>
    private bool? IsPlaying(IReadOnlyCollection<Guid> itemIds)
    {
        try
        {
            // An alternate version is reported through the media source; the item may be the primary.
            return sessions.Sessions.Any(session =>
                session.NowPlayingItem?.Id is { } nowPlaying && itemIds.Contains(nowPlaying) ||
                session.FullNowPlayingItem?.Id is { } full && itemIds.Contains(full) ||
                Guid.TryParse(session.PlayState?.MediaSourceId, out var source) && itemIds.Contains(source));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsActive(User user) =>
        !user.Permissions.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value);
}

/// <summary>Stable reasons recorded when live state blocks an unlink.</summary>
internal static class RetentionLiveReasons
{
    public const string BindingUnavailable = "binding_unavailable";
    public const string Kept = "kept";
    public const string VersionKept = "version_kept";
    public const string ActiveSession = "live_active_session";
    public const string ActiveSessionUnknown = "live_session_unknown";
    public const string NoAccessibleUsers = "live_no_accessible_users";
    public const string LiveStateUnavailable = "live_state_unavailable";
    public const string ActiveResume = "live_active_resume";
    public const string Favorite = "live_favorite";
    public const string FavoriteSeries = "live_favorite_series";
    public const string NotCompleted = "live_not_completed";
}
