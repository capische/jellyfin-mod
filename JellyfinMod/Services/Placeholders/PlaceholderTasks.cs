using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// Stamps a stub episode as soon as Jellyfin creates or saves its item (P1.FU24): its date added becomes its air date and its
/// runtime TMDB's, so the new placeholder never heads Latest. Jellyfin's first metadata refresh saves the item it loaded before
/// the stamp, so a save is stamped again; the stamp's own save changes nothing and ends it. Done off Jellyfin's event thread.
/// Also refreshes Jellyfin's cached user data of an item that took a placeholder's place, or a placeholder that took a file's
/// place, once its first refresh has re-attached the data (<see cref="PlaceholderUserData"/>, P1.FU25 follow-up).
/// </summary>
public sealed class PlaceholderItemListener(ILibraryManager library, IServiceScopeFactory scopeFactory,
    ILogger<PlaceholderItemListener> logger, IUserDataManager? userData = null, IUserManager? users = null) : IHostedService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> queued = new();
    // Items created in place of another whose first refresh has not been handled yet, with when they were created.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> fresh = new();
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        library.ItemAdded += OnItemAdded;
        library.ItemUpdated += OnItemUpdated;
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= OnItemAdded;
        library.ItemUpdated -= OnItemUpdated;
        queue.Writer.TryComplete();
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

    private void OnItemAdded(object? sender, ItemChangeEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.Item is { } item && PlaceholderUserData.IsCandidate(item))
            {
                var now = DateTime.UtcNow;
                if (fresh.Count > 1000)
                    foreach (var stale in fresh.Where(pair => now - pair.Value > TimeSpan.FromMinutes(30)).Select(pair => pair.Key).ToArray())
                        fresh.TryRemove(stale, out _);
                fresh[item.Id] = now;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug(error, "JellyfinMod could not note a new item");
        }

        OnItemUpdated(sender, eventArgs);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs eventArgs)
    {
        // Cheap: only a placeholder whose file is a .disc (the marker is read later), or an item created in place of another
        // whose first metadata refresh has now saved it (Jellyfin re-attaches the user data right after that save), is queued.
        if (eventArgs.Item is not { } item) return;
        var stub = item is Video { IsPlaceHolder: true } && PlaceholderStub.HasStubExtension(item.Path);
        var refreshed = fresh.ContainsKey(item.Id) && item.DateLastRefreshed != DateTime.MinValue;
        if ((stub || refreshed) && queued.TryAdd(item.Id, 0))
            queue.Writer.TryWrite(item.Id);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Jellyfin saves the item, then refreshes its metadata: a short pause lets those saves land, then the whole
                // batch that arrived meanwhile is stamped.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var batch = new List<Guid>();
                while (queue.Reader.TryRead(out var id))
                {
                    queued.TryRemove(id, out _);
                    batch.Add(id);
                }

                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PlaceholderService>();
                foreach (var id in batch)
                {
                    try
                    {
                        if (library.GetItemById(id) is not { } item) continue;
                        await service.StampAsync(item, cancellationToken).ConfigureAwait(false);
                        // The first refresh has re-attached the user data by now (the pause above): put it in Jellyfin's cache.
                        if (item.DateLastRefreshed != DateTime.MinValue && fresh.TryRemove(id, out _) && userData is not null && users is not null)
                            PlaceholderUserData.Resave(userData, users, item, logger);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        logger.LogWarning("JellyfinMod could not stamp a placeholder item: {Error}", error.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// Keeps every library's placeholders up to date (P1.FU24): writes stubs for missing aired episodes where the switch is on (every
/// TV library unless turned off), at most <see cref="PlaceholderService.SeriesPerRun"/> series per library and run, removes stubs
/// whose episode got a file, and stamps new stub items. Every 15 minutes, so a large library is filled in batches over a few
/// hours (the first run comes one interval after the plugin starts, never in the start-up rush), and on demand from the settings.
/// </summary>
public sealed class PlaceholderSyncTask(IServiceScopeFactory scopeFactory, ILibraryManager library) : IScheduledTask
{
    /// <inheritdoc />
    public string Name => "Sync JellyfinMod placeholders";

    /// <inheritdoc />
    public string Key => "JellyfinModPlaceholderSync";

    /// <inheritdoc />
    public string Description => "Adds placeholders for missing aired episodes and released movies with no file in the libraries where they are on (all, unless turned off), a few titles per run, and removes the ones whose title has a file.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(15).Ticks }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Every TV library with a switch on, and every other one too: stale stubs go and stub items are stamped even after the
        // switch is turned off (stubs written before stay until an administrator removes them).
        var libraries = library.GetVirtualFolders().Where(folder => folder.CollectionType is MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows or
                MediaBrowser.Model.Entities.CollectionTypeOptions.movies)
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
        for (var index = 0; index < libraries.Length; index++)
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<PlaceholderService>();
            if (service.NeedsSync(libraries[index]))
                await service.SyncAsync(libraries[index], cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (index + 1) / libraries.Length);
        }
    }
}

/// <summary>
/// Writes a title's placeholders right after it is added from TMDb or its episode list loads (P1.FU26), so it becomes a Jellyfin
/// item within seconds instead of at the next scheduled sync. One entry per request, off the request, one at a time.
/// </summary>
public sealed class PlaceholderSyncQueue(IServiceScopeFactory scopeFactory, ILogger<PlaceholderSyncQueue> logger) : IHostedService
{
    private readonly Channel<(Guid LibraryId, Guid EntryId)> queue =
        Channel.CreateUnbounded<(Guid, Guid)>(new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <summary>Asks for one entry's placeholders to be written now.</summary>
    public void Enqueue(Guid? libraryId, Guid entryId)
    {
        if (libraryId is { } id && id != Guid.Empty) queue.Writer.TryWrite((id, entryId));
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
        queue.Writer.TryComplete();
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
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = new HashSet<(Guid, Guid)>();
                while (queue.Reader.TryRead(out var item)) batch.Add(item);
                foreach (var (libraryId, entryId) in batch)
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<PlaceholderService>().SyncEntryAsync(libraryId, entryId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (KeyNotFoundException)
                    {
                        // Not a TV or movie library: nothing to write.
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        logger.LogWarning("JellyfinMod could not write the placeholders of a new title; the next sync does: {Error}", error.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
