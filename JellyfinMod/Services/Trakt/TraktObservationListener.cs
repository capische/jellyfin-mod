using System.Threading.Channels;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TvEpisode = MediaBrowser.Controller.Entities.TV.Episode;
using TvSeason = MediaBrowser.Controller.Entities.TV.Season;
using TvSeries = MediaBrowser.Controller.Entities.TV.Series;

namespace JellyfinMod.Services.Trakt;

/// <summary>
/// Records which titles received watch history from Trakt (P7.Q16). The stock Trakt plugin's <c>SyncFromTraktTask</c>
/// writes Trakt's history into Jellyfin through <c>IUserDataManager.SaveUserData</c> with
/// <see cref="UserDataSaveReason.Import"/>; this listener observes that save while the Trakt plugin is installed and its
/// import task is running (Jellyfin's NFO parser saves with <c>Import</c> too). Titles synced before JellyfinMod started
/// observing are recorded the next time a sync touches them.
/// </summary>
/// <remarks>
/// Separate from <see cref="RetentionEventListener"/> on purpose: the indicator is not retention evidence. The event
/// handlers only queue identities; the database is written by one reader off the host's thread, in a scope of its own.
/// Rows go when their item leaves the library and when their user no longer exists (checked at start and then
/// periodically, as <see cref="RetentionEventListener"/> does, because the host raises no event a plugin can rely on
/// for a deleted user). An observation is written only for an item and a user that still exist, and rows whose item
/// has gone are also looked for periodically: an import handler that queued its observation after the item's removal
/// was processed would otherwise leave a row behind. Logs carry item and user ids at most, never user-data values.
/// </remarks>
public sealed class TraktObservationListener(
    IUserDataManager userData,
    ILibraryManager library,
    IUserManager users,
    TraktPluginState trakt,
    DatabaseInitializer readiness,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<TraktObservationListener> logger,
    TimeSpan? userPollInterval = null) : IHostedService
{
    private const int BatchSize = 200;
    // Rows of items that no longer exist are looked for on every twelfth user check (hourly by default).
    private const int ItemCheckEvery = 12;
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _userPollInterval = userPollInterval ?? TimeSpan.FromMinutes(5);
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private CancellationTokenSource? stopping;
    private Task? worker;
    private Task? userPoller;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = ProcessAsync(stopping.Token);
        userData.UserDataSaved += OnUserDataSaved;
        library.ItemRemoved += OnItemRemoved;
        // Users deleted while the server was down are pruned once the database is ready.
        queue.Writer.TryWrite(new PruneUsers());
        userPoller = PollUsersAsync(stopping.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops taking events, then lets the reader write what is already queued, for as long as the host's stop token
    /// allows; only then is the reader cancelled. Waiting for the cancelled tasks is bounded by the same token, so the
    /// listener never takes longer than the host allows.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        userData.UserDataSaved -= OnUserDataSaved;
        library.ItemRemoved -= OnItemRemoved;
        queue.Writer.TryComplete();
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        try
        {
            if (worker is not null)
            {
                try
                {
                    await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("JellyfinMod stopped before every queued Trakt observation was written");
                }
            }

            await source.CancelAsync().ConfigureAwait(false);
            var running = new[] { worker, userPoller }.OfType<Task>().ToArray();
            try
            {
                await Task.WhenAll(running).WaitAsync(StopWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
            }
        }
        finally
        {
            // A task still unwinding keeps its token source until it has finished.
            var pending = new[] { worker, userPoller }.OfType<Task>().Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) source.Dispose();
            else _ = Task.WhenAll(pending).ContinueWith(_ => source.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs eventArgs)
    {
        try
        {
            if (eventArgs.SaveReason != UserDataSaveReason.Import || eventArgs.Item is not (Movie or TvEpisode)) return;
            // Only the Trakt plugin's own import, never an NFO import or anything else that saves with Import.
            if (!trakt.IsImporting() || !trakt.Describe().Installed) return;
            var data = eventArgs.UserData;
            // Played or a resume point is history; an import that leaves neither is Trakt reporting the title unwatched
            // (its "unwatched" import clears Played and leaves Jellyfin's own play count alone, so the count proves nothing).
            var hasHistory = data is not null && (data.Played || data.PlaybackPositionTicks > 0);
            var episode = eventArgs.Item as TvEpisode;
            queue.Writer.TryWrite(new Observation(eventArgs.UserId, eventArgs.Item.Id,
                NonEmpty(episode?.SeriesId), NonEmpty(episode?.SeasonId), hasHistory, clock.GetUtcNow().UtcDateTime));
        }
        catch (Exception error)
        {
            // Never let the mod break the host's save, whatever it is saving.
            logger.LogWarning(error, "JellyfinMod could not queue a Trakt import observation for item {ItemId}", eventArgs.Item?.Id);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs eventArgs)
    {
        try
        {
            switch (eventArgs.Item)
            {
                case Movie or TvEpisode:
                    queue.Writer.TryWrite(new RemoveItem(eventArgs.Item.Id, RemovedKind.Item));
                    break;
                case TvSeason:
                    queue.Writer.TryWrite(new RemoveItem(eventArgs.Item.Id, RemovedKind.Season));
                    break;
                case TvSeries:
                    queue.Writer.TryWrite(new RemoveItem(eventArgs.Item.Id, RemovedKind.Series));
                    break;
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "JellyfinMod could not queue pruning Trakt observations for removed item {ItemId}", eventArgs.Item?.Id);
        }
    }

    private async Task PollUsersAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_userPollInterval);
        try
        {
            var ticks = 0;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                queue.Writer.TryWrite(new PruneUsers());
                if (++ticks % ItemCheckEvery == 0) queue.Writer.TryWrite(new PruneItems());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Guid? NonEmpty(Guid? id) => id is { } value && value != Guid.Empty ? value : null;

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var batch = new List<Work>(BatchSize);
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < BatchSize && queue.Reader.TryRead(out var work)) batch.Add(work);
                if (batch.Count == 0) continue;
                if (!await WaitForDatabaseAsync(cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("JellyfinMod dropped {Count} Trakt observation changes because its database is not ready", batch.Count);
                    continue;
                }

                try
                {
                    await SaveAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "JellyfinMod could not write {Count} Trakt observation changes", batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> WaitForDatabaseAsync(CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + ReadyWait;
        while (!readiness.IsReady)
        {
            if (clock.GetUtcNow() >= deadline) return false;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task SaveAsync(IReadOnlyList<Work> batch, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
        // In queue order: an observation, then a removal of the same item, leaves nothing, and the reverse leaves the row.
        // Observations accumulate in the context; pending ones are saved before a removal queries the table.
        foreach (var work in batch)
        {
            if (work is not Observation) await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            switch (work)
            {
                case Observation observation:
                    await ApplyAsync(database, observation, cancellationToken).ConfigureAwait(false);
                    break;
                case PruneItems:
                    var itemIds = await database.TraktObservations.Select(row => row.JellyfinItemId).Distinct()
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    var missing = itemIds.Where(id => library.GetItemById(id) is null).ToArray();
                    if (missing.Length > 0)
                        database.TraktObservations.RemoveRange(await database.TraktObservations
                            .Where(row => missing.Contains(row.JellyfinItemId)).ToListAsync(cancellationToken).ConfigureAwait(false));
                    break;
                case RemoveItem removal:
                    var rows = removal.Kind switch
                    {
                        RemovedKind.Season => database.TraktObservations.Where(row => row.SeasonId == removal.ItemId),
                        RemovedKind.Series => database.TraktObservations.Where(row => row.SeriesId == removal.ItemId),
                        _ => database.TraktObservations.Where(row => row.JellyfinItemId == removal.ItemId)
                    };
                    database.TraktObservations.RemoveRange(await rows.ToListAsync(cancellationToken).ConfigureAwait(false));
                    break;
                case PruneUsers:
                    var known = users.GetUsers().Select(user => user.Id).ToHashSet();
                    var orphans = (await database.TraktObservations.Select(row => row.UserId).Distinct()
                        .ToListAsync(cancellationToken).ConfigureAwait(false)).Where(id => !known.Contains(id)).ToArray();
                    if (orphans.Length > 0)
                        database.TraktObservations.RemoveRange(await database.TraktObservations
                            .Where(row => orphans.Contains(row.UserId)).ToListAsync(cancellationToken).ConfigureAwait(false));
                    break;
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(ModDbContext database, Observation observation, CancellationToken cancellationToken)
    {
        // A row added, changed or deleted earlier in this batch is tracked by the context and not yet in the table; a
        // deleted one is not in Local, and a query would hand back that same tracked, still-deleted instance.
        var entry = database.ChangeTracker.Entries<TraktObservation>().FirstOrDefault(candidate =>
            candidate.Entity.UserId == observation.UserId && candidate.Entity.JellyfinItemId == observation.ItemId);
        var row = entry?.Entity ?? await database.TraktObservations.SingleOrDefaultAsync(candidate =>
            candidate.UserId == observation.UserId && candidate.JellyfinItemId == observation.ItemId, cancellationToken).ConfigureAwait(false);
        var deleted = entry?.State == EntityState.Deleted;
        if (!observation.HasHistory)
        {
            if (row is not null && !deleted) database.TraktObservations.Remove(row);
            return;
        }

        // An import handled after its item's removal (or its user's) must not bring the row back.
        if (library.GetItemById(observation.ItemId) is null || users.GetUserById(observation.UserId) is null) return;
        if (deleted)
        {
            // Removed earlier in this batch and imported again: the row stays, as a new observation from now.
            entry!.State = EntityState.Modified;
            row!.FirstSyncedAt = observation.At;
        }

        if (row is null)
        {
            database.TraktObservations.Add(new TraktObservation
            {
                UserId = observation.UserId, JellyfinItemId = observation.ItemId, SeriesId = observation.SeriesId,
                SeasonId = observation.SeasonId, FirstSyncedAt = observation.At, LastSyncedAt = observation.At
            });
            return;
        }

        row.SeriesId = observation.SeriesId;
        row.SeasonId = observation.SeasonId;
        row.LastSyncedAt = observation.At;
    }

    private enum RemovedKind
    {
        Item,
        Season,
        Series
    }

    private abstract record Work;

    private sealed record Observation(Guid UserId, Guid ItemId, Guid? SeriesId, Guid? SeasonId, bool HasHistory, DateTime At) : Work;

    private sealed record RemoveItem(Guid ItemId, RemovedKind Kind) : Work;

    private sealed record PruneUsers : Work;

    private sealed record PruneItems : Work;
}
