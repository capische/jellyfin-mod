using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Coalesces retention policy and user-data notifications outside host callbacks.</summary>
public sealed class RetentionEventListener(
    IUserDataManager userData,
    IUserManager users,
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionEventListener> logger) : IHostedService
{
    private const string PolicyKey = "policy";
    private const string AccessKey = "access";
    private readonly Channel<RetentionWork> queue = Channel.CreateUnbounded<RetentionWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = ProcessAsync(stopping.Token);
        userData.UserDataSaved += OnUserDataSaved;
        users.OnUserUpdated += OnUserUpdated;
        Plugin.Instance!.ConfigurationChanged += OnConfigurationChanged;
        Enqueue(new RetentionWork(PolicyKey, null, null, null));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        userData.UserDataSaved -= OnUserDataSaved;
        users.OnUserUpdated -= OnUserUpdated;
        if (Plugin.Instance is not null) Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
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

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration) =>
        Enqueue(new RetentionWork(PolicyKey, null, null, null));

    private void OnUserUpdated(object? sender, GenericEventArgs<User> eventArgs) =>
        Enqueue(new RetentionWork(AccessKey, null, null, null));

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        if (eventArgs.Item is not (Movie or Episode)) return;
        var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"user:{eventArgs.UserId:N}:{eventArgs.Item.Id:N}");
        Enqueue(new RetentionWork(key, eventArgs.UserId, eventArgs.Item.Id, eventArgs.SaveReason.ToString()));
    }

    private void Enqueue(RetentionWork work)
    {
        if (pending.TryAdd(work.Key, 0)) queue.Writer.TryWrite(work);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.TryRemove(work.Key, out _);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    if (work.Key == PolicyKey)
                    {
                        await scope.ServiceProvider.GetRequiredService<RetentionPolicyService>()
                            .SyncAsync(Plugin.Instance!.Configuration, cancellationToken).ConfigureAwait(false);
                        await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
                            .EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (work.Key == AccessKey)
                    {
                        await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
                            .EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else if (work.UserId is { } userId && work.JellyfinItemId is { } itemId)
                    {
                        await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                            .RefreshAsync(userId, itemId, work.SourceReason!, cancellationToken).ConfigureAwait(false);
                        await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
                            .EvaluateNativeItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "JellyfinMod retention evidence refresh failed for {WorkKey}", work.Key);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed record RetentionWork(string Key, Guid? UserId, Guid? JellyfinItemId, string? SourceReason);
}
