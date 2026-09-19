using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
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
    ILogger<RetentionEventListener> logger,
    TimeSpan? accessPollInterval = null) : IHostedService
{
    private readonly TimeSpan _accessPollInterval = accessPollInterval ?? TimeSpan.FromMinutes(5);
    private string? _accessFingerprint;
    private Task? accessPoller;
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
        _accessFingerprint = AccessFingerprint();
        accessPoller = PollAccessAsync(stopping.Token);
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
        // Only the first stop owns the cancellation source; a repeated stop is a no-op instead of touching a disposed one.
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { worker, accessPoller })
            {
                if (task is null) continue;
                try
                {
                    await task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration) =>
        Enqueue(new RetentionWork(PolicyKey, null, null, null));

    private void OnUserUpdated(object? sender, GenericEventArgs<User> eventArgs) =>
        Enqueue(new RetentionWork(AccessKey, null, null, null));

    /// <summary>
    /// Users can be created, deleted, disabled or given other libraries without any event on 10.11.11, so a
    /// fingerprint of active users and their access is compared periodically (P3.T11).
    /// </summary>
    private async Task PollAccessAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_accessPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var fingerprint = AccessFingerprint();
                    if (fingerprint == _accessFingerprint) continue;
                    _accessFingerprint = fingerprint;
                    Enqueue(new RetentionWork(AccessKey, null, null, null));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "JellyfinMod could not read users for retention access changes");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private string AccessFingerprint() => string.Join(';', users.GetUsers().OrderBy(user => user.Id).Select(user =>
        string.Join('|', user.Id.ToString("N"), user.HasPermission(PermissionKind.IsDisabled),
            user.HasPermission(PermissionKind.EnableAllFolders),
            string.Join(',', user.GetPreference(PreferenceKind.EnabledFolders).Order(StringComparer.Ordinal)),
            user.MaxParentalRatingScore, user.MaxParentalRatingSubScore,
            string.Join(',', user.GetPreference(PreferenceKind.BlockedTags).Order(StringComparer.Ordinal)),
            string.Join(',', user.GetPreference(PreferenceKind.AllowedTags).Order(StringComparer.Ordinal)))));

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        // A series favourite protects its episodes (P3.T11).
        if (eventArgs.Item is Series series)
        {
            Enqueue(new RetentionWork(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"series:{series.Id:N}"), null, series.Id, null));
            return;
        }

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
                    else if (work.Key.StartsWith("series:", StringComparison.Ordinal) && work.JellyfinItemId is { } seriesId)
                    {
                        await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
                            .EvaluateSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false);
                    }
                    else if (work.UserId is { } userId && work.JellyfinItemId is { } itemId)
                    {
                        var changed = await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                            .RefreshAsync(userId, itemId, work.SourceReason!, cancellationToken).ConfigureAwait(false);
                        if (changed)
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
