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
/// <param name="Updated">Items that changed in place: one the clients were told of got its first cover.</param>
public sealed record LibraryChange(Guid LibraryId, bool Movies, IReadOnlyCollection<Guid> Added, IReadOnlyCollection<Guid> Removed,
    IReadOnlyCollection<Guid> Folders, string? Rating = null, IReadOnlyList<string>? Tags = null, IReadOnlyCollection<Guid>? Updated = null);

/// <summary>
/// Tells the clients about a change at once (P4.FU12) with Jellyfin's own <c>LibraryChanged</c> message, sent early through
/// <see cref="ISessionManager"/> instead of waiting for Jellyfin's 30 s batch (which still follows and is harmless): a repeated
/// refresh of the same ids costs a client one read. Changes that come within <see cref="Window"/> of the first are one message per
/// user, and each user is sent only the ids they may see: the library must be theirs, an item must be visible to them, and a
/// removed item must pass their rating and tag rules by what it was. A user with no session costs nothing.
/// An item named that has no cover yet (a placeholder title made again after a delete, a new title, an import) gets it from
/// Jellyfin's own metadata refresh a few seconds later; its first cover is told the same way (ItemsUpdated), <see cref="CoverSettle"/>
/// after it came, and the rest of that burst (seasons, episodes) in one more message once no cover came for as long.
/// </summary>
public sealed class LibraryNotifications(IServiceScopeFactory scopeFactory, ILibraryManager library, IUserManager users, ISessionManager sessions,
    TimeProvider time, ILogger<LibraryNotifications> logger) : IHostedService
{
    /// <summary>How long after the first change the message is sent; fixed, later changes wait for the next message.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    /// <summary>How long an item told of without a cover is watched for its first one.</summary>
    public static readonly TimeSpan CoverWatch = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after a first cover it is told, and how long no cover may come before the rest of the burst is. The refresh that
    /// saves an item's first cover is also the one after which the placeholder listener, 2 s later, puts back the user data
    /// Jellyfin parked for it (<see cref="Placeholders.PlaceholderItemListener"/>); a page that read the item in between kept
    /// it without its favourite, so the clients are told only once that is done.
    /// </summary>
    public static readonly TimeSpan CoverSettle = TimeSpan.FromSeconds(3);

    private readonly ConcurrentQueue<LibraryChange> pending = new();
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    // Items told of with no cover yet, and the ones whose first cover came and is still to be told.
    private readonly ConcurrentDictionary<Guid, (Guid LibraryId, bool Movies, DateTime Since)> coverless = new();
    private readonly ConcurrentDictionary<Guid, (Guid LibraryId, bool Movies, long At)> covered = new();
    private readonly Channel<bool> coverSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private long lastCover;
    private CancellationTokenSource? stopping;
    private Task? worker;
    private Task? coverWorker;

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
        library.ItemUpdated += OnItemUpdated;
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        coverWorker = Task.Run(() => ProcessCoversAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemUpdated -= OnItemUpdated;
        signal.Writer.TryComplete();
        coverSignal.Writer.TryComplete();
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null) await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (coverWorker is not null) await coverWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    WatchCovers(batch);
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

    /// <summary>
    /// Notes the items named (not the library), to tell their first cover: a removed item too, because Jellyfin gives an item made
    /// again at the same path (a placeholder title's series and seasons after a delete) the same id. Whether one has a cover now is
    /// not asked: the instance Jellyfin caches for a re-made id can still carry the old one's.
    /// </summary>
    private void WatchCovers(IReadOnlyList<LibraryChange> batch)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (coverless.Count > 1000)
            foreach (var stale in coverless.Where(pair => now - pair.Value.Since > CoverWatch).Select(pair => pair.Key).ToArray())
                coverless.TryRemove(stale, out _);
        foreach (var change in batch)
        {
            foreach (var id in change.Added.Concat(change.Removed).Concat(change.Folders).Where(id => id != change.LibraryId))
                coverless[id] = (change.LibraryId, change.Movies, now);
        }
    }

    // Cheap, on Jellyfin's event thread: only an item being watched that is saved with a cover is noted.
    private void OnItemUpdated(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (coverless.IsEmpty || eventArgs.Item is not { } item || !coverless.ContainsKey(item.Id) || !item.HasImage(ImageType.Primary)) return;
        if (!coverless.TryRemove(item.Id, out var watched) || time.GetUtcNow().UtcDateTime - watched.Since > CoverWatch) return;
        var now = time.GetTimestamp();
        covered[item.Id] = (watched.LibraryId, watched.Movies, now);
        Interlocked.Exchange(ref lastCover, now);
        coverSignal.Writer.TryWrite(true);
    }

    // The first cover of a burst is told once it has settled; the rest of the burst (a title's seasons and episodes) once it is quiet.
    private async Task ProcessCoversAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await coverSignal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                coverSignal.Reader.TryRead(out _);
                var first = time.GetTimestamp();
                await Task.Delay(CoverSettle, time, cancellationToken).ConfigureAwait(false);
                NotifyCovers(first);
                long last;
                do await Task.Delay(CoverSettle, time, cancellationToken).ConfigureAwait(false);
                while (time.GetElapsedTime(last = Interlocked.Read(ref lastCover)) < CoverSettle);
                coverSignal.Reader.TryRead(out _);
                NotifyCovers(last);
                // One that came at the last moment is still young: its own burst follows.
                if (!covered.IsEmpty) coverSignal.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Tells the covers that came by the timestamp given (they have settled); a later one waits for the burst's last message.
    private void NotifyCovers(long upTo)
    {
        var settled = covered.Where(pair => pair.Value.At <= upTo).Select(pair => pair.Key).ToArray();
        var told = settled.Select(id => covered.TryRemove(id, out var where) ? (Id: id, where.LibraryId, where.Movies) : default)
            .Where(cover => cover.Id != Guid.Empty);
        foreach (var group in told.GroupBy(cover => (cover.LibraryId, cover.Movies)))
            Notify(new(group.Key.LibraryId, group.Key.Movies, [], [], [], Updated: group.Select(cover => cover.Id).ToArray()));
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
            var visible = VisibleTo(user, allowed.SelectMany(change => change.Added.Concat(change.Updated ?? [])
                .Concat(change.Folders.Where(id => id != change.LibraryId))).Distinct().ToArray());
            var added = new List<string>();
            var updated = new List<string>();
            var removed = new List<string>();
            var folders = new List<string>();
            foreach (var change in allowed)
            {
                added.AddRange(change.Added.Where(visible.Contains).Select(Wire));
                updated.AddRange((change.Updated ?? []).Where(visible.Contains).Select(Wire));
                folders.AddRange(change.Folders.Where(id => id == change.LibraryId || visible.Contains(id)).Select(Wire));
                if (change.Removed.Count > 0 && access.CanReadNativeSnapshot(user, change.Movies ? "movie" : "series", change.Rating, change.Tags ?? []))
                    removed.AddRange(change.Removed.Select(Wire));
            }

            if (added.Count == 0 && removed.Count == 0 && updated.Count == 0 && folders.Count == 0) continue;
            var info = new LibraryUpdateInfo
            {
                ItemsAdded = added.Distinct().ToArray(), ItemsRemoved = removed.Distinct().ToArray(), ItemsUpdated = updated.Distinct().ToArray(),
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
