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
/// The backfill (P1.FU27): one batch over every TV and movie library, never two at once. The scheduled sync and an administrator's
/// <i>Sync Now</i> both run it. Each library writes at most <see cref="PlaceholderService.SeriesPerRun"/> titles with
/// <see cref="PlaceholderService.PauseBetweenSeries"/> between them, gives way to Jellyfin's own library scans, and asks Jellyfin
/// to look at the folders it changed once, at the end. While a batch leaves placeholders pending the scheduled sync runs every
/// <see cref="BusyInterval"/>; once nothing is pending, every <see cref="IdleInterval"/>.
/// </summary>
public static class PlaceholderBackfill
{
    /// <summary>How often the scheduled sync runs while placeholders are pending: its trigger's interval.</summary>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(5);

    /// <summary>How often it runs once nothing is pending (it then skips the triggers in between).</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(15);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object StateLock = new();
    private static DateTime? _lastRunAt;
    private static int _lastPending;
    private static string? _lastDeferred;

    /// <summary>Gets a value indicating whether a batch runs now.</summary>
    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>Gets when the last batch ended, what it left pending and why it wrote less than it could, if it did.</summary>
    public static (DateTime? LastRunAt, int Pending, string? Deferred) State
    {
        get
        {
            lock (StateLock) return (_lastRunAt, _lastPending, _lastDeferred);
        }
    }

    /// <summary>
    /// Whether a scheduled trigger runs a batch: always while the last batch left work (pending placeholders, or a batch that gave
    /// way to a scan) or none ran since start, else once the idle interval has passed since the last one.
    /// </summary>
    public static bool Due(DateTime now)
    {
        var (last, pending, deferred) = State;
        // A trigger fires a little early or late: half a minute of slack keeps the idle pace at 15 minutes, not 20.
        return last is null || pending > 0 || deferred is not null || now - last.Value >= IdleInterval - TimeSpan.FromSeconds(30);
    }

    /// <summary>When the scheduled sync runs a batch next, as far as the plugin knows (its trigger fires every busy interval).</summary>
    public static TimeSpan NextRunIn(DateTime now)
    {
        var (last, pending, deferred) = State;
        if (last is null || pending > 0 || deferred is not null) return BusyInterval;
        var idle = last.Value + IdleInterval - now;
        return idle < BusyInterval ? BusyInterval : idle;
    }

    /// <summary>
    /// Runs one batch over every TV and movie library that needs it, unless one already runs (then null: never two at once).
    /// Libraries with the switch off only remove stale placeholders and stamp items, as before.
    /// </summary>
    public static async Task<IReadOnlyList<PlaceholderPlan>?> TryRunAsync(IServiceScopeFactory scopeFactory, ILibraryManager library,
        TimeProvider time, ILogger logger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            var libraries = library.GetVirtualFolders().Where(folder => folder.CollectionType is MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows or
                    MediaBrowser.Model.Entities.CollectionTypeOptions.movies)
                .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
            var plans = new List<PlaceholderPlan>();
            for (var index = 0; index < libraries.Length; index++)
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PlaceholderService>();
                try
                {
                    if (service.NeedsSync(libraries[index]))
                        plans.Add(await service.SyncBatchAsync(libraries[index], cancellationToken).ConfigureAwait(false));
                }
                catch (KeyNotFoundException)
                {
                    // The library went between the listing and its sync.
                }

                progress?.Report(100.0 * (index + 1) / libraries.Length);
            }

            var pending = plans.Sum(plan => plan.Pending);
            var deferred = plans.Select(plan => plan.Deferred).FirstOrDefault(reason => reason is not null);
            lock (StateLock)
            {
                _lastRunAt = time.GetUtcNow().UtcDateTime;
                _lastPending = pending;
                _lastDeferred = deferred;
            }

            var written = plans.Sum(plan => plan.Writes.Count);
            if (deferred is not null)
                logger.LogInformation("JellyfinMod placeholder backfill gave way to a library scan: wrote {Written} placeholder(s), {Done} of {Titles} title(s) done, {Pending} placeholder(s) pending; next run in {Minutes} minutes",
                    written, plans.Sum(plan => plan.TitlesDone), plans.Sum(plan => plan.Titles), pending, (int)BusyInterval.TotalMinutes);
            else if (written > 0 || pending > 0)
                logger.LogInformation("JellyfinMod placeholder backfill: wrote {Written} placeholder(s), {Done} of {Titles} title(s) done, {Pending} placeholder(s) pending; next run in {Minutes} minutes",
                    written, plans.Sum(plan => plan.TitlesDone), plans.Sum(plan => plan.Titles), pending,
                    (int)(pending > 0 ? BusyInterval : IdleInterval).TotalMinutes);
            return plans;
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>
/// Keeps every library's placeholders up to date (P1.FU24, P1.FU27): writes stubs for missing aired episodes and released movies
/// where the switch is on (every library unless turned off), at most <see cref="PlaceholderService.SeriesPerRun"/> titles per
/// library and run, removes stubs whose title got a file, and stamps new stub items. Its trigger fires every 5 minutes; a run
/// does the work only while placeholders are pending (a first backfill of a large catalogue) or every 15 minutes once nothing
/// is (<see cref="PlaceholderBackfill"/>). The first run comes one trigger interval after the plugin starts, never in the start-up
/// rush. An administrator's <i>Sync Now</i> runs the same batch at once; the two never run together.
/// </summary>
public sealed class PlaceholderSyncTask(IServiceScopeFactory scopeFactory, ILibraryManager library, TimeProvider time,
    ILogger<PlaceholderSyncTask> logger) : IScheduledTask
{
    /// <inheritdoc />
    public string Name => "Sync JellyfinMod placeholders";

    /// <inheritdoc />
    public string Key => "JellyfinModPlaceholderSync";

    /// <inheritdoc />
    public string Description => "Adds placeholders for missing aired episodes and released movies with no file in the libraries where they are on (all, unless turned off), a few titles per run (every 5 minutes while some are pending, else every 15), and removes the ones whose title has a file.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = PlaceholderBackfill.BusyInterval.Ticks }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Every library with a switch on, and every other one too: stale stubs go and stub items are stamped even after the
        // switch is turned off (stubs written before stay until an administrator removes them). Nothing pending: the idle pace.
        if (PlaceholderBackfill.Due(time.GetUtcNow().UtcDateTime))
            await PlaceholderBackfill.TryRunAsync(scopeFactory, library, time, logger, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}

/// <summary>
/// Starts the first backfill 5 minutes after the plugin starts on a server where the placeholder sync has never completed (P1.FU27).
/// Jellyfin 12 starts an interval task that has never completed only one hour after start (<c>IntervalTrigger</c>), which would
/// leave an existing catalogue without placeholders for that hour after the plugin is installed or updated. The task is queued
/// through Jellyfin's task manager, so it shows and runs as the task, never beside it; once that run has completed its triggers are
/// armed again from its result (Jellyfin re-arms them only when one fires), so the task's own trigger paces it from then on, and
/// on a server where it has completed before this does nothing.
/// </summary>
public sealed class PlaceholderBackfillStarter(ITaskManager taskManager, ILogger<PlaceholderBackfillStarter> logger) : IHostedService
{
    /// <summary>How long after start the first batch waits: past the start-up rush, one busy interval.</summary>
    public static readonly TimeSpan FirstRunDelay = PlaceholderBackfill.BusyInterval;

    private CancellationTokenSource? stopping;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = new CancellationTokenSource();
        var token = stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstRunDelay, token).ConfigureAwait(false);
                var worker = taskManager.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask is PlaceholderSyncTask);
                if (worker is null || worker.LastExecutionResult is not null) return;
                logger.LogInformation("JellyfinMod starts the first placeholder sync now rather than an hour after start");
                taskManager.QueueIfNotRunning<PlaceholderSyncTask>();
                // Jellyfin re-arms a task's triggers only when a trigger fires, so the hour set at start would stand after this
                // queued run: once it has completed, the triggers are armed again from its result (the busy pace follows).
                for (var waited = TimeSpan.Zero; waited < TimeSpan.FromHours(1); waited += TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    if (worker.LastExecutionResult is null || worker.State != TaskState.Idle) continue;
                    worker.ReloadTriggerEvents();
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning("JellyfinMod could not start the first placeholder sync; its schedule starts it: {Error}", error.Message);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping?.Cancel();
        stopping?.Dispose();
        stopping = null;
        return Task.CompletedTask;
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
