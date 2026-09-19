using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Coalesces native library notifications and reconciles them outside Jellyfin's event callbacks.</summary>
public sealed class LibraryEventListener(
    ILibraryManager library,
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryEventListener> logger,
    TimeSpan? debounce = null,
    JellyfinMod.Data.DatabaseInitializer? readiness = null) : IHostedService
{
    private readonly TimeSpan _debounce = debounce ?? TimeSpan.FromSeconds(1);
    // The last identity, path and playability seen per native item; an update that changes none of them
    // cannot change a binding (P2.R8).
    private readonly ConcurrentDictionary<Guid, string> fingerprints = new();
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, long> pending = new();
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        library.ItemAdded += OnItemChanged;
        library.ItemUpdated += OnItemUpdated;
        library.ItemRemoved += OnItemChanged;
        worker = ProcessAsync(stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= OnItemChanged;
        library.ItemUpdated -= OnItemUpdated;
        library.ItemRemoved -= OnItemChanged;
        queue.Writer.TryComplete();
        // The host may stop a service more than once (a failed start, then shutdown); only the first call owns the
        // cancellation source, so a later call neither cancels nor disposes it again.
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
            if (worker is not null)
            {
                try
                {
                    await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            source.Dispose();
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not (Movie or Series or Episode)) return;
        fingerprints[eventArgs.Item.Id] = Fingerprint(eventArgs.Item);
        Enqueue(eventArgs.Item);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not (Movie or Series or Episode)) return;
        var fingerprint = Fingerprint(eventArgs.Item);
        if (fingerprints.TryGetValue(eventArgs.Item.Id, out var previous) && previous == fingerprint) return;
        fingerprints[eventArgs.Item.Id] = fingerprint;
        Enqueue(eventArgs.Item);
    }

    /// <summary>
    /// Queues one work key per title: an episode event reconciles its series once, however many of its
    /// episodes changed within the debounce window (P2.R8).
    /// </summary>
    private void Enqueue(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var key = item is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty ? seriesId : item.Id;
        var now = Environment.TickCount64;
        if (pending.TryAdd(key, now)) queue.Writer.TryWrite(key);
        else pending[key] = now;
    }

    private static string Fingerprint(MediaBrowser.Controller.Entities.BaseItem item) => string.Join('|',
        item.Path, item.IsVirtualItem, item.ProviderIds.GetValueOrDefault("Tmdb"), item.ParentIndexNumber,
        item.IndexNumber, (item as Episode)?.IndexNumberEnd, (item as Episode)?.SeriesId);

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var itemId in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Wait until the title has been quiet for the debounce window, so a season import or a
                // metadata refresh becomes one observation.
                while (pending.TryGetValue(itemId, out var lastEvent) &&
                       Environment.TickCount64 - lastEvent < (long)_debounce.TotalMilliseconds)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,
                        _debounce.TotalMilliseconds - (Environment.TickCount64 - lastEvent))), cancellationToken).ConfigureAwait(false);
                // Nothing is written until the plugin database is migrated (P2.R10).
                while (readiness is { IsReady: false })
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                pending.TryRemove(itemId, out _);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<JellyfinItemReconciliationRunner>()
                        .ReconcileAsync(itemId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "JellyfinMod event reconciliation failed for native item {ItemId}; the repair task can retry it", itemId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
