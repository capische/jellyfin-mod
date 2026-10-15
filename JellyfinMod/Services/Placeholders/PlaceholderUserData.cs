using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// Keeps Jellyfin's per-user data cache right for a new item that took another one's user data (P1.FU25 follow-up): a
/// placeholder swapped for the imported file, a file swapped for a placeholder by a reclaim, the same for movies (P1.FU26).
/// </summary>
/// <remarks>
/// <para>The cause, in Jellyfin 12.0 (<c>Emby.Server.Implementations/Library/UserDataManager.cs</c>): list answers
/// (<c>/Items</c>, <c>/Shows/{id}/Episodes</c>) read user data through <c>GetUserDataBatch</c>, which keeps each user's data
/// per item id in an in-memory LRU cache and, on a miss, caches what the database holds, or an empty "unplayed" record when it
/// holds nothing. A new item's data is re-attached by key (the removed item's parked rows move to the new id) only at its first
/// metadata refresh (<c>MetadataService.SaveItemAsync</c> → <c>ReattachUserDataAsync</c>), which never touches that cache. A
/// client that lists the item in the second or two between its creation and that refresh caches "unplayed", and list answers
/// keep saying so until the server restarts. <c>/Items/{id}</c> (<c>GetUserData</c>, no cache) and the played and favourite
/// filters (SQL) are right throughout.</para>
/// <para>The remedy: once a new movie or episode's first refresh has saved it, and only when it then holds stored user data
/// (which a brand-new item never does: such rows exist only when Jellyfin re-attached them), each such user's data is saved
/// again, unchanged, through <c>IUserDataManager.SaveUserData</c>, the one public call that replaces the cache entry. It
/// writes the same rows. The save is marked <see cref="IsResaving"/> on its thread and uses
/// <see cref="UserDataSaveReason.UpdateUserData"/>: the plugin's retention listener skips it (no completion is ever recorded
/// from it), the Trakt plugin acts only on <c>TogglePlayed</c>, the plugin's Trakt indicator only on <c>Import</c>; InfuseSync
/// records the item as changed, which only makes Infuse read its data again.</para>
/// </remarks>
public static class PlaceholderUserData
{
    [ThreadStatic]
    private static bool _resaving;

    /// <summary>Gets a value indicating whether the current thread is re-saving re-attached data (listeners skip it).</summary>
    public static bool IsResaving => _resaving;

    /// <summary>Whether a new item is one whose user data Jellyfin may re-attach from a removed item: a movie or an episode.</summary>
    public static bool IsCandidate(BaseItem item) => item is Movie or Episode && item.Path is { Length: > 0 };

    /// <summary>
    /// Saves each user's stored data of <paramref name="item"/> again, unchanged, so Jellyfin's per-user cache holds it. Only
    /// users with stored data are touched. Returns how many were saved; never throws.
    /// </summary>
    public static int Resave(IUserDataManager userData, IUserManager users, BaseItem item, ILogger logger)
    {
        var saved = 0;
        try
        {
            var owners = (item.UserData ?? []).Where(row => row.ItemId == item.Id).Select(row => row.UserId).Distinct().ToArray();
            foreach (var userId in owners)
            {
                if (users.GetUserById(userId) is not { } user || userData.GetUserData(user, item) is not { } data) continue;
                _resaving = true;
                try
                {
                    userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
                    saved++;
                }
                finally
                {
                    _resaving = false;
                }
            }

            if (saved > 0)
                logger.LogInformation("JellyfinMod refreshed Jellyfin's cached user data of new item {Item} for {Count} user(s) after Jellyfin re-attached it",
                    item.Id, saved);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not refresh the cached user data of item {Item}: {Error}", item.Id, error.Message);
        }

        return saved;
    }
}
