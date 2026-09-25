using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMod.Services;

/// <summary>
/// Reads a user's played state as Jellyfin has stored it, not as its item cache last saw it (Q16 review P2-2).
/// </summary>
/// <remarks>
/// Jellyfin 12's <c>IUserDataManager.GetUserData(user, item)</c> reads only the <c>UserData</c> rows loaded into the item
/// instance it is given, and <c>SaveUserData</c> reloads only the instance it was given. <c>ILibraryManager.GetItemById</c>
/// returns the instance cached in the library manager, so a save made through another instance of the same item, which
/// is how the Trakt plugin's history sync and the NFO importer write (reason <c>Import</c>), leaves the cached instance
/// serving the old state until it is evicted or the server restarts. <c>ILibraryManager.RetrieveItem</c> loads the item
/// from the database with its user data and does not touch the cache, so every retention read of played, resume and
/// favourite state goes through it. Anything that cannot be read is null: the caller treats it as unavailable, which
/// never deletes.
/// </remarks>
internal static class StoredUserData
{
    /// <summary>The item as stored, with its current user data; null when it is gone or cannot be read.</summary>
    public static BaseItem? Item(ILibraryManager library, Guid itemId)
    {
        if (itemId == Guid.Empty) return null;
        try
        {
            return library.RetrieveItem(itemId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The user's stored state for an item already read with <see cref="Item"/>; null when it cannot be read.</summary>
    public static UserItemData? For(IUserDataManager userData, User user, BaseItem? item)
    {
        if (item is null) return null;
        try
        {
            return userData.GetUserData(user, item);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }
}
