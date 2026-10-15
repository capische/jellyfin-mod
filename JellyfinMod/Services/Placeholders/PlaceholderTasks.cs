using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// Stamps a stub episode as soon as Jellyfin creates or saves its item (P1.FU24): its date added becomes its air date and its
/// runtime TMDB's, so the new placeholder never heads Latest. Jellyfin's first metadata refresh saves the item it loaded before
/// the stamp, so a save is stamped again; the stamp's own save changes nothing and ends it. Done off Jellyfin's event thread.
/// </summary>
public sealed class PlaceholderItemListener(ILibraryManager library, IServiceScopeFactory scopeFactory,
    ILogger<PlaceholderItemListener> logger) : IHostedService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> queued = new();
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        library.ItemAdded += OnItemAdded;
        library.ItemUpdated += OnItemAdded;
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= OnItemAdded;
        library.ItemUpdated -= OnItemAdded;
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
        // Cheap: only an episode Jellyfin calls a placeholder whose file is a .disc is queued; the marker is read later.
        if (eventArgs.Item is JellyfinEpisode { IsPlaceHolder: true } episode && PlaceholderStub.HasStubExtension(episode.Path) &&
            queued.TryAdd(episode.Id, 0))
            queue.Writer.TryWrite(episode.Id);
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
                        if (library.GetItemById(id) is { } item) await service.StampAsync(item, cancellationToken).ConfigureAwait(false);
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
    public string Description => "Adds placeholders for missing aired episodes in the TV libraries where they are on (all, unless turned off), a few series per run, and removes the ones whose episode has a file.";

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
        var libraries = library.GetVirtualFolders().Where(folder => folder.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows)
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
