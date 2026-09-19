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
    public async Task<string?> BlockReasonAsync(
        RetentionOperation operation,
        RetentionPolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == operation.EntryId, cancellationToken).ConfigureAwait(false);
        if (entry is null) return RetentionLiveReasons.BindingUnavailable;
        if (entry.RetentionPolicy == RetentionPolicy.Never) return RetentionLiveReasons.Kept;

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

        var completedBy = new HashSet<Guid>();
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
        }

        var satisfied = policy.WatchedUserMode switch
        {
            WatchedUserMode.AllUsers => eligibleUsers.All(user => completedBy.Contains(user.Id)),
            WatchedUserMode.SelectedUser => policy.SelectedUserId is { } selected &&
                eligibleUsers.Any(user => user.Id == selected) && completedBy.Contains(selected),
            WatchedUserMode.AnyUser => completedBy.Count > 0,
            _ => false
        };
        return satisfied ? null : RetentionLiveReasons.NotCompleted;
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
    public const string ActiveSession = "live_active_session";
    public const string ActiveSessionUnknown = "live_session_unknown";
    public const string NoAccessibleUsers = "live_no_accessible_users";
    public const string LiveStateUnavailable = "live_state_unavailable";
    public const string ActiveResume = "live_active_resume";
    public const string Favorite = "live_favorite";
    public const string FavoriteSeries = "live_favorite_series";
    public const string NotCompleted = "live_not_completed";
}
