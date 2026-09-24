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
    LibraryAccess access)
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
        var items = versionItemIds.Select(id => library.GetItemById(id)).OfType<BaseItem>().ToArray();
        if (items.Length == 0) return RetentionLiveReasons.LiveStateUnavailable;
        var series = seriesItemId is { } seriesId ? library.GetItemById(seriesId) : null;
        if (operation.EpisodeId.HasValue && policy.ExemptFavourites && series is null)
            return RetentionLiveReasons.LiveStateUnavailable;

        // A file holding several episodes: no episode it covers may be kept, and the file itself must be finished, because
        // for an episode whose only copy it is, it is that episode (PHASE10 Q5).
        var multi = library.GetItemById(operation.JellyfinItemId) as MediaBrowser.Controller.Entities.TV.Episode;
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

        var completedBy = new HashSet<Guid>();
        var fileCompletedBy = new HashSet<Guid>();
        foreach (var user in eligibleUsers)
        {
            var states = items.Select(item => userData.GetUserData(user, item)).ToArray();
            if (states.Any(state => state is null)) return RetentionLiveReasons.LiveStateUnavailable;
            var current = states.OfType<UserItemData>().ToArray();
            if (current.Any(state => state.PlaybackPositionTicks > 0)) return RetentionLiveReasons.ActiveResume;
            if (policy.ExemptFavourites && current.Any(state => state.IsFavorite)) return RetentionLiveReasons.Favorite;
            if (policy.ExemptFavourites && series is not null && userData.GetUserData(user, series)?.IsFavorite == true)
                return RetentionLiveReasons.FavoriteSeries;
            if (current.Any(state => state.Played)) completedBy.Add(user.Id);
            if (coversSeveral && userData.GetUserData(user, multi!) is { Played: true, PlaybackPositionTicks: 0 }) fileCompletedBy.Add(user.Id);
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
