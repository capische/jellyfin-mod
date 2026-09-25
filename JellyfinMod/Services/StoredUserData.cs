using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMod.Services;

/// <summary>
/// Reads an item as Jellyfin has stored it, with its current user data, not as its item cache last saw it (Q16 review
/// P2-2).
/// </summary>
/// <remarks>
/// Jellyfin 12's <c>IUserDataManager.GetUserData(user, item)</c> reads only the <c>UserData</c> rows loaded into the item
/// instance it is given, and <c>SaveUserData</c> reloads only the instance it was given. <c>ILibraryManager.GetItemById</c>
/// returns the instance cached in the library manager, so a save made through another instance of the same item leaves
/// the cached instance serving the old state until it is evicted or the server restarts. The Trakt plugin's history sync
/// and the NFO importer save that way (reason <c>Import</c>), and so does stock Jellyfin's mark season or series played
/// and unplayed. <c>ILibraryManager.RetrieveItem</c> loads the item from the database with its user data and does not
/// touch the cache, so every retention read of played, resume and favourite state goes through it.
/// </remarks>
internal static class StoredUserData
{
    /// <summary>
    /// Loads the item as stored. Returns <see cref="StoredRead.Found"/> with the item, <see cref="StoredRead.Missing"/>
    /// when Jellyfin has no such item, or <see cref="StoredRead.Error"/> with the exception when it could not be read. A
    /// caller that protects media treats an error as unreadable state, never as an absent version (review P2-1).
    /// </summary>
    public static StoredRead TryItem(ILibraryManager library, Guid itemId, out BaseItem? item, out Exception? error)
    {
        item = null;
        error = null;
        if (itemId == Guid.Empty) return StoredRead.Missing;
        try
        {
            item = library.RetrieveItem(itemId);
            return item is null ? StoredRead.Missing : StoredRead.Found;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not OperationCanceledException)
        {
            error = failure;
            return StoredRead.Error;
        }
    }
}

/// <summary>The outcome of reading one stored item.</summary>
internal enum StoredRead
{
    /// <summary>The item was read.</summary>
    Found,

    /// <summary>Jellyfin has no item with that id.</summary>
    Missing,

    /// <summary>The item could not be read; its state is unknown.</summary>
    Error
}
