using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>
/// One change to tell the clients about (P4.FU12): what a library gained, lost or changed. Item ids only, never a path, a torrent
/// or user data; who is told is decided from the library, the item's visibility and, for an item that is gone, the rating and tags
/// it had (<see cref="Rating"/>, <see cref="Tags"/>).
/// </summary>
/// <param name="LibraryId">The library the change is in.</param>
/// <param name="Movies">Whether it is a movie library (else a TV library).</param>
/// <param name="Added">Items that now exist (placeholders, files).</param>
/// <param name="Removed">Items Jellyfin no longer has.</param>
/// <param name="Folders">Folders that gained or lost children: a series, a season, the library folder.</param>
/// <param name="Rating">The title's rating, for the removed items.</param>
/// <param name="Tags">The title's tags, for the removed items.</param>
public sealed record LibraryChange(Guid LibraryId, bool Movies, IReadOnlyCollection<Guid> Added, IReadOnlyCollection<Guid> Removed,
    IReadOnlyCollection<Guid> Folders, string? Rating = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// Tells the clients about a change at once (P4.FU12) with Jellyfin's own <c>LibraryChanged</c> message, sent early through
/// <see cref="ISessionManager"/> instead of waiting for Jellyfin's 30 s batch (which still follows and is harmless): a repeated
/// refresh of the same ids costs a client one read. Changes that come within <see cref="Window"/> of the first are one message per
/// user, and each user is sent only the ids they may see: the library must be theirs, an item must be visible to them, and a
/// removed item must pass their rating and tag rules by what it was. A user with no session costs nothing.
/// </summary>
public sealed class LibraryNotifications(IServiceScopeFactory scopeFactory, ILibraryManager library, IUserManager users, ISessionManager sessions,
    TimeProvider time, ILogger<LibraryNotifications> logger) : IHostedService
{
    /// <summary>How long after the first change the message is sent; fixed, later changes wait for the next message.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentQueue<LibraryChange> pending = new();
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <summary>Queues a change; never waits and never throws.</summary>
    public void Notify(LibraryChange change)
    {
        pending.Enqueue(change);
        signal.Writer.TryWrite(true);
    }

    /// <summary>Whether the library is a movie library (else a TV library).</summary>
    public bool IsMovieLibrary(Guid libraryId) => library.GetVirtualFolders().Any(folder =>
        Guid.TryParse(folder.ItemId, out var id) && id == libraryId && folder.CollectionType == CollectionTypeOptions.movies);

    /// <summary>
    /// Queues the change for items Jellyfin now holds at <paramref name="files"/> (found by path) and the folders they and
    /// <paramref name="folders"/> sit in; never throws.
    /// </summary>
    public void NotifyAdded(Guid libraryId, IEnumerable<string> files, IEnumerable<string> folders)
    {
        try
        {
            var added = files.Select(path => library.FindByPath(path, false)?.Id).OfType<Guid>().Distinct().ToArray();
            var folderIds = folders.Select(path => library.FindByPath(path, true)?.Id).OfType<Guid>().Append(libraryId).Distinct().ToArray();
            Notify(new(libraryId, IsMovieLibrary(libraryId), added, [], folderIds));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug(error, "JellyfinMod could not queue a library change for the clients; Jellyfin's own message still follows");
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        signal.Writer.TryComplete();
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null) await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                signal.Reader.TryRead(out _);
                await Task.Delay(Window, time, cancellationToken).ConfigureAwait(false);
                var batch = new List<LibraryChange>();
                while (pending.TryDequeue(out var change)) batch.Add(change);
                if (batch.Count == 0) continue;
                try
                {
                    await SendAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning("JellyfinMod could not tell the clients about a library change; Jellyfin's own message still follows: {Error}", error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(IReadOnlyList<LibraryChange> batch, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<LibraryAccess>();
        // Only users who have a session now are worked out and sent to, as Jellyfin's own notifier does.
        var userIds = sessions.Sessions.Select(session => session.UserId).Where(id => id != Guid.Empty).Distinct().ToArray();
        foreach (var user in userIds.Select(users.GetUserById).OfType<Jellyfin.Database.Implementations.Entities.User>())
        {
            var allowed = batch.Where(change => access.CanUseLibrary(user, change.Movies ? "movie" : "series", change.LibraryId)).ToArray();
            // One read for what this user may see among the items named, by Jellyfin's own rules (library, rating, tags).
            var visible = VisibleTo(user, allowed.SelectMany(change => change.Added.Concat(change.Folders.Where(id => id != change.LibraryId))).Distinct().ToArray());
            var added = new List<string>();
            var removed = new List<string>();
            var folders = new List<string>();
            foreach (var change in allowed)
            {
                added.AddRange(change.Added.Where(visible.Contains).Select(Wire));
                folders.AddRange(change.Folders.Where(id => id == change.LibraryId || visible.Contains(id)).Select(Wire));
                if (change.Removed.Count > 0 && access.CanReadNativeSnapshot(user, change.Movies ? "movie" : "series", change.Rating, change.Tags ?? []))
                    removed.AddRange(change.Removed.Select(Wire));
            }

            if (added.Count == 0 && removed.Count == 0 && folders.Count == 0) continue;
            var info = new LibraryUpdateInfo
            {
                ItemsAdded = added.Distinct().ToArray(), ItemsRemoved = removed.Distinct().ToArray(), ItemsUpdated = [],
                FoldersAddedTo = folders.Distinct().ToArray(), FoldersRemovedFrom = folders.Distinct().ToArray(), CollectionFolders = []
            };
            await sessions.SendMessageToUserSessions([user.Id], SessionMessageType.LibraryChanged, info, cancellationToken).ConfigureAwait(false);
        }
    }

    private HashSet<Guid> VisibleTo(Jellyfin.Database.Implementations.Entities.User user, Guid[] ids)
    {
        if (ids.Length == 0) return [];
        try
        {
            return library.GetItemList(new InternalItemsQuery(user) { ItemIds = ids }).Select(item => item.Id).ToHashSet();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Not knowing is not seeing: an id is never sent on a guess.
            logger.LogDebug(error, "JellyfinMod could not tell which items a user may see");
            return [];
        }
    }

    private static string Wire(Guid id) => id.ToString("N");
}
