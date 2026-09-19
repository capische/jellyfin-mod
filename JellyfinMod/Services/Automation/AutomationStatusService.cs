using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>Reads what automation would do next and why it would not (P6.M3/M8).</summary>
public sealed class AutomationStatusService(
    ModDbContext database,
    AutomationRunGate runGate,
    ILibraryManager library,
    UnixFileInspector files,
    TimeProvider time)
{
    /// <summary>Builds the administrator status.</summary>
    public async Task<AutomationStatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var today = now.Date;
        var grabsToday = await database.GrabOperations.CountAsync(grab => grab.Automatic && grab.CreatedAt >= today, cancellationToken)
            .ConfigureAwait(false);
        var openImports = await database.ImportOperations.CountAsync(operation => ImportStates.Open.Contains(operation.State),
            cancellationToken).ConfigureAwait(false);
        var (free, floor) = TightestFreeSpace(settings);
        var lastRun = await database.AutomationRuns.AsNoTracking().OrderByDescending(run => run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var indexers = await database.AcquisitionIndexers.AsNoTracking().OrderBy(indexer => indexer.Priority).ThenBy(indexer => indexer.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var budgets = await database.IndexerBudgets.AsNoTracking().ToDictionaryAsync(budget => budget.IndexerId, cancellationToken)
            .ConfigureAwait(false);
        var paused = PausedReasons(settings, grabsToday, openImports, free, floor, lastRun);
        // The hourly native trigger starts a run once the interval has passed since the last one.
        DateTime? next = null;
        if (settings.AutomationEnabled)
        {
            next = lastRun is null ? now : lastRun.StartedAt.AddHours(Math.Max(1, settings.AutomationIntervalHours));
            if (next < now) next = now;
        }

        return new AutomationStatusDto(settings.AutomationEnabled, paused, runGate.Busy, next is { } due ? Utc(due) : null,
            lastRun is null ? null : Run(lastRun),
            new AutomationBudgetsDto(grabsToday, settings.DailyAutoGrabBudget, openImports, settings.MaxConcurrentImports,
                free is null ? null : (long)free.Value, floor is null ? null : (long)floor.Value),
            indexers.Select(indexer =>
            {
                var budget = budgets.GetValueOrDefault(indexer.Id);
                return new AutomationIndexerDto(indexer.Id, indexer.Name, budget?.Day == today ? budget.QueriesUsed : 0,
                    indexer.DailyQueryBudget, indexer.MinIntervalSeconds,
                    budget?.BreakerOpenUntil is { } open && open > now ? Utc(open) : null);
            }).ToArray());
    }

    /// <summary>The compact state the queue banner shows.</summary>
    public async Task<QueueAutomationDto> BannerAsync(CancellationToken cancellationToken)
    {
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        return new QueueAutomationDto(status.Enabled, status.PausedReasons);
    }

    private static List<string> PausedReasons(AcquisitionSettings settings, int grabsToday, int openImports, ulong? free, ulong? floor,
        AutomationRun? lastRun)
    {
        var reasons = new List<string>();
        if (!settings.AutomationEnabled) reasons.Add("disabled");
        if (grabsToday >= settings.DailyAutoGrabBudget) reasons.Add(AutomationReasons.BudgetGrabs);
        if (openImports >= settings.MaxConcurrentImports) reasons.Add(AutomationReasons.TooManyOpenImports);
        if (free is { } bytes && floor is { } limit && bytes < limit) reasons.Add(AutomationReasons.FreeSpaceFloor);
        if (lastRun is { Status: AutomationRunStatuses.Paused } && lastRun.Detail == AutomationReasons.ClientUnreachable)
            reasons.Add(AutomationReasons.ClientUnreachable);
        else if (lastRun is { Status: AutomationRunStatuses.Paused }) reasons.Add(AutomationReasons.AcquisitionNotReady);
        return reasons;
    }

    /// <summary>The library mount with the least room above its floor.</summary>
    private (ulong? Free, ulong? Floor) TightestFreeSpace(AcquisitionSettings settings)
    {
        (ulong? Free, ulong? Floor) tightest = (null, null);
        try
        {
            foreach (var folder in library.GetVirtualFolders() ?? [])
            {
                if (folder.CollectionType is not (CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)) continue;
                foreach (var location in folder.Locations ?? [])
                {
                    if (!files.TryGetFreeSpace(location, out var free, out var total)) continue;
                    var floor = Math.Max((ulong)Math.Max(0, settings.FreeSpaceFloorBytes),
                        (ulong)(total * (Math.Clamp(settings.FreeSpaceFloorPercent, 0, 100) / 100.0)));
                    if (tightest.Free is null || (long)free - (long)floor < (long)tightest.Free.Value - (long)tightest.Floor!.Value)
                        tightest = (free, floor);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return (null, null);
        }

        return tightest;
    }

    /// <summary>The public view of a run.</summary>
    public static AutomationRunDto Run(AutomationRun run) => new(run.Id, run.Trigger, Utc(run.StartedAt), run.CompletedAt is { } done ? Utc(done) : null,
        run.Status, run.TargetsConsidered, run.Searched, run.Skipped, run.Grabbed, run.UpgradesPlanned,
        JsonSerializer.Deserialize<Dictionary<string, int>>(run.QueriesByIndexerJson) ?? [], run.Detail);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
