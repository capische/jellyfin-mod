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
    ILogger<LibraryEventListener> logger) : IHostedService
{
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, byte> pending = new();
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        library.ItemAdded += OnItemChanged;
        library.ItemUpdated += OnItemChanged;
        library.ItemRemoved += OnItemChanged;
        worker = ProcessAsync(stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= OnItemChanged;
        library.ItemUpdated -= OnItemChanged;
        library.ItemRemoved -= OnItemChanged;
        queue.Writer.TryComplete();
        if (stopping is not null) await stopping.CancelAsync().ConfigureAwait(false);
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

        stopping?.Dispose();
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs.Item is not (Movie or Series or Episode)) return;
        var itemId = eventArgs.Item.Id;
        if (pending.TryAdd(itemId, 0)) queue.Writer.TryWrite(itemId);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var itemId in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
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
