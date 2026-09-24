using System.Text.Json;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Automation;

/// <summary>
/// Advances upgrades (P6.M5): the better version is imported by Phase 5 as an additional version; only once it is bound,
/// playable and its seeding copy is owned does a Phase 3 operation with provenance <c>upgrade_replaced</c> remove the
/// superseded version, through the same executor and protections as any reclaim. Keep blocks the replacement; the added
/// version stays.
/// </summary>
public sealed class UpgradeService(
    ModDbContext database,
    IServiceScopeFactory scopes,
    ILibraryManager library,
    TimeProvider time,
    ILogger<UpgradeService> logger)
{
    /// <summary>How long a blocked replacement waits before it is tried again.</summary>
    public static readonly TimeSpan BlockedRetry = TimeSpan.FromMinutes(10);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Advances every open upgrade once.</summary>
    public async Task<int> AdvanceAllAsync(CancellationToken cancellationToken)
    {
        var ids = await database.UpgradeOperations.AsNoTracking().Where(upgrade => UpgradeStates.Open.Contains(upgrade.State))
            .OrderBy(upgrade => upgrade.CreatedAt).Select(upgrade => upgrade.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            try
            {
                await AdvanceAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Upgrade {Upgrade} could not advance; it is retried on the next tick", id);
            }
        }

        return ids.Count;
    }

    private async Task AdvanceAsync(Guid upgradeId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var upgrade = await database.UpgradeOperations.SingleAsync(value => value.Id == upgradeId, cancellationToken).ConfigureAwait(false);
        var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == upgrade.NewGrabId, cancellationToken)
            .ConfigureAwait(false);
        var import = await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == upgrade.NewGrabId)
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        switch (upgrade.State)
        {
            case UpgradeStates.Planned:
            case UpgradeStates.Importing:
                if (grab is null || grab.State is GrabStates.Failed or GrabStates.Cancelled)
                {
                    await EndAsync(upgrade, UpgradeStates.Failed, "grab_" + (grab?.State ?? "missing")).ConfigureAwait(false);
                    return;
                }

                if (import is null) return;
                upgrade.NewImportOperationId = import.Id;
                if (import.State is ImportStates.Failed or ImportStates.Cancelled)
                {
                    await EndAsync(upgrade, UpgradeStates.Failed, "import_" + import.State).ConfigureAwait(false);
                    return;
                }

                if (import.State != ImportStates.Completed)
                {
                    if (upgrade.State != UpgradeStates.Importing)
                    {
                        upgrade.State = UpgradeStates.Importing;
                        upgrade.UpdatedAt = Now;
                        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    return;
                }

                upgrade.State = UpgradeStates.Imported;
                upgrade.UpdatedAt = Now;
                await AddHistoryAsync(upgrade, "upgrade_added",
                    $"Added {upgrade.NewQuality ?? "a better version"} beside {upgrade.SupersededQuality ?? "the existing version"}",
                    upgrade.Id, cancellationToken).ConfigureAwait(false);
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await ReplaceAsync(upgrade, import, cancellationToken).ConfigureAwait(false);
                return;
            case UpgradeStates.Imported:
            case UpgradeStates.Replacing:
                if (import is not null) await ReplaceAsync(upgrade, import, cancellationToken).ConfigureAwait(false);
                return;
            case UpgradeStates.Blocked:
                if (import is not null && Now - upgrade.UpdatedAt >= BlockedRetry)
                    await ReplaceAsync(upgrade, import, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Replaces the superseded version once the new one is bound, playable and seeded by the plugin.</summary>
    private async Task ReplaceAsync(UpgradeOperation upgrade, ImportOperation import, CancellationToken cancellationToken)
    {
        if (upgrade.Mode == "add")
        {
            await EndAsync(upgrade, UpgradeStates.Completed, "added").ConfigureAwait(false);
            return;
        }

        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == upgrade.EntryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            await EndAsync(upgrade, UpgradeStates.Failed, "entry_missing").ConfigureAwait(false);
            return;
        }

        // Keep protects the whole title: a kept title only gains versions (PHASE6 open question 2). A kept episode is
        // protected the same way (P10.E2).
        if (entry.RetentionPolicy == RetentionPolicy.Never || upgrade.EpisodeId is { } upgradeEpisodeId &&
            await database.Episodes.AsNoTracking().AnyAsync(episode => episode.Id == upgradeEpisodeId &&
                episode.RetentionPolicy == RetentionPolicy.Never, cancellationToken).ConfigureAwait(false))
        {
            database.AutomationDecisions.Add(new AutomationDecision
            {
                TargetId = upgrade.EpisodeId ?? upgrade.EntryId, EntryId = upgrade.EntryId, EpisodeId = upgrade.EpisodeId,
                Kind = AutomationDecisionKinds.UpgradeCompleted, Reason = AutomationReasons.KeptEntry,
                Detail = "Keep protects the title; the new version was added and the old one kept.", GrabId = upgrade.NewGrabId, CreatedAt = Now
            });
            await EndAsync(upgrade, UpgradeStates.Completed, AutomationReasons.KeptEntry).ConfigureAwait(false);
            return;
        }

        var superseded = upgrade.EpisodeId is null
            ? await database.EntryBindings.AsNoTracking().AnyAsync(binding => binding.Id == upgrade.SupersededBindingId, cancellationToken)
                .ConfigureAwait(false)
            : await database.EpisodeBindings.AsNoTracking().AnyAsync(binding => binding.Id == upgrade.SupersededBindingId, cancellationToken)
                .ConfigureAwait(false);
        if (!superseded)
        {
            await EndAsync(upgrade, UpgradeStates.Completed, "superseded_missing").ConfigureAwait(false);
            return;
        }

        // The old version goes only after the new one is bound, has a playable native item and its seeding copy is owned.
        var playable = import.NativeItemId is { } itemId && library.GetItemById(itemId) is { Path: { Length: > 0 } };
        var seeded = await database.SeedReleaseOperations.AsNoTracking().AnyAsync(seed => seed.ImportOperationId == import.Id, cancellationToken)
            .ConfigureAwait(false);
        if (import.State != ImportStates.Completed || import.BindingId is null || !playable || !seeded)
        {
            await BlockAsync(upgrade, "new_version_not_playable").ConfigureAwait(false);
            return;
        }

        upgrade.State = UpgradeStates.Replacing;
        upgrade.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        RetentionExecutionResult result;
        using (var scope = scopes.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<RetentionExecutor>()
                .ReplaceAsync(upgrade.SupersededBindingId, upgrade.Id, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        upgrade = await database.UpgradeOperations.SingleAsync(value => value.Id == upgrade.Id, cancellationToken).ConfigureAwait(false);
        upgrade.ReplacementRetentionOperationId = result.OperationId ?? upgrade.ReplacementRetentionOperationId;
        if (result.State == "completed")
        {
            database.AutomationDecisions.Add(new AutomationDecision
            {
                TargetId = upgrade.EpisodeId ?? upgrade.EntryId, EntryId = upgrade.EntryId, EpisodeId = upgrade.EpisodeId,
                Kind = AutomationDecisionKinds.UpgradeCompleted, Reason = "upgrade_replaced",
                Detail = $"{upgrade.SupersededQuality} replaced by {upgrade.NewQuality}.", GrabId = upgrade.NewGrabId, CreatedAt = Now
            });
            await EndAsync(upgrade, UpgradeStates.Completed, "replaced").ConfigureAwait(false);
            return;
        }

        if (result.State is "prepared" or "unlinked") return;
        await BlockAsync(upgrade, result.Reason).ConfigureAwait(false);
    }

    private async Task BlockAsync(UpgradeOperation upgrade, string reason)
    {
        upgrade.State = UpgradeStates.Blocked;
        upgrade.Reason = reason;
        upgrade.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EndAsync(UpgradeOperation upgrade, string state, string reason)
    {
        upgrade.State = state;
        upgrade.Reason = reason;
        upgrade.OpenTargetKey = null;
        upgrade.CompletedAt = Now;
        upgrade.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task AddHistoryAsync(UpgradeOperation upgrade, string eventType, string summary, Guid id, CancellationToken cancellationToken)
    {
        // Keyed by the upgrade, so a retried step cannot add a second event.
        if (await database.History.AnyAsync(history => history.Id == id, cancellationToken).ConfigureAwait(false)) return;
        database.History.Add(new HistoryRecord
        {
            Id = id, EntryId = upgrade.EntryId, EventType = eventType, CreatedAt = Now, Summary = summary.Length <= 1024 ? summary : summary[..1024],
            Data = JsonSerializer.Serialize(new
            {
                upgradeOperationId = upgrade.Id, upgrade.EpisodeId, upgrade.SupersededQuality, upgrade.NewQuality, upgrade.Mode,
                upgrade.NewGrabId, upgrade.NewImportOperationId
            })
        });
    }
}

/// <summary>
/// The native automation task (P6.M3). Its default hourly trigger checks <c>AutomationIntervalHours</c>, so the
/// administrator's interval decides the cadence; a manual run from the API always runs.
/// </summary>
public sealed class AutomationSearchTask(IServiceScopeFactory scopes) : IScheduledTask, IConfigurableScheduledTask
{
    private static int _manualPending;

    /// <summary>Marks the next execution as manual; the API sets it right before queueing the task.</summary>
    internal static void RequestManual() => Interlocked.Exchange(ref _manualPending, 1);

    /// <inheritdoc />
    public string Name => "Search for monitored JellyfinMod titles";

    /// <inheritdoc />
    public string Key => "JellyfinModAutomationSearch";

    /// <inheritdoc />
    public string Description => "Searches monitored titles and new episodes within budgets and grabs or upgrades them when automation is on.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var trigger = Interlocked.Exchange(ref _manualPending, 0) == 1 ? "manual" : "scheduled";
        using var scope = scopes.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<AutomationRunner>().RunAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (AutomationRunAlreadyActiveException)
        {
            // Another run is active; this trigger has nothing to add.
        }

        progress.Report(100);
    }
}
