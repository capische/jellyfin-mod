using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Automation;

/// <summary>Prevents two automation runs from overlapping.</summary>
public sealed class AutomationRunGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets a value indicating whether a run is active.</summary>
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>Acquires the run lease immediately when no run is active.</summary>
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Signals that a second automation run was refused without waiting.</summary>
public sealed class AutomationRunAlreadyActiveException : InvalidOperationException
{
    /// <summary>Initializes the stable overlap error.</summary>
    public AutomationRunAlreadyActiveException()
        : base("A JellyfinMod automation run is already active.")
    {
    }
}

/// <summary>One searchable target of a run.</summary>
internal sealed record AutomationTarget(Guid TargetId, Entry Entry, Episode? Episode, AcquisitionQualityProfile Profile, bool Inherited,
    UpgradeAssessment? Upgrade)
{
    public bool IsUpgrade => Upgrade is { Eligible: true };
}

/// <summary>
/// Searches monitored targets on a schedule, within budgets, and grabs the best eligible release through the Phase 4 grab
/// path (P6.M3/M4/M5). It decides and records; Phase 5 imports, Phase 3 deletes and Phase 2 binds. Nothing here writes an
/// entry's file state, a binding or a file.
/// </summary>
public sealed class AutomationRunner(
    ModDbContext database,
    IServiceScopeFactory scopes,
    AutomationRunGate runGate,
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    ILibraryManager library,
    UnixFileInspector files,
    TimeProvider time,
    ILogger<AutomationRunner> logger)
{
    /// <summary>The first backoff after an empty search; doubled per empty search up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromHours(12);

    /// <summary>The longest backoff.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(7);

    /// <summary>How long after an automatic grab its entry may not be grabbed again automatically.</summary>
    public static readonly TimeSpan PerEntryGrabWindow = TimeSpan.FromHours(24);

    /// <summary>The most series refreshed from TMDB in one run.</summary>
    public const int MaxRefreshesPerRun = 10;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Runs one batch. A scheduled run that is not due yet (see <c>AutomationIntervalHours</c>) returns null without
    /// recording anything; a manual run always runs.
    /// </summary>
    public async Task<AutomationRun?> RunAsync(string trigger, CancellationToken cancellationToken)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AutomationRunAlreadyActiveException();
        var now = Now;
        // This process holds the gate, so a "running" row belongs to a process that died (the P3.T9 pattern).
        foreach (var stale in await database.AutomationRuns.Where(run => run.Status == AutomationRunStatuses.Running)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            stale.Status = AutomationRunStatuses.Interrupted;
            stale.CompletedAt = now;
            stale.Detail = "The process stopped before this run finished.";
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        if (trigger == "scheduled")
        {
            var last = await database.AutomationRuns.AsNoTracking().Where(run => run.Status != AutomationRunStatuses.Disabled)
                .OrderByDescending(run => run.StartedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (last is not null && last.StartedAt > now - TimeSpan.FromHours(Math.Max(1, settings.AutomationIntervalHours)) &&
                settings.AutomationEnabled)
                return null;
        }

        var run = new AutomationRun { Trigger = trigger, StartedAt = now };
        database.AutomationRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var queries = new Dictionary<Guid, int>();
        try
        {
            if (!settings.AutomationEnabled)
            {
                await FinishAsync(run, AutomationRunStatuses.Disabled, "Automation is off; no indexer was contacted.", queries)
                    .ConfigureAwait(false);
                return run;
            }

            var state = await configuration.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
            if (!state.Settings.Enabled || !state.Ready)
            {
                await FinishAsync(run, AutomationRunStatuses.Paused,
                    "Acquisition is not ready: " + string.Join(", ", state.Settings.Enabled ? state.Blockers : ["acquisition_disabled"]) + ".",
                    queries).ConfigureAwait(false);
                return run;
            }

            // An unreachable client pauses automatic grabs until the next run; nothing is searched for nothing.
            if (!await ClientReachableAsync(state.Client!, cancellationToken).ConfigureAwait(false))
            {
                await FinishAsync(run, AutomationRunStatuses.Paused, AutomationReasons.ClientUnreachable, queries).ConfigureAwait(false);
                return run;
            }

            await RefreshAiringSeriesAsync(now, cancellationToken).ConfigureAwait(false);
            var targets = await TargetsAsync(settings, state, now, cancellationToken).ConfigureAwait(false);
            run.TargetsConsidered = targets.Count;
            var targetStates = await EnsureTargetStatesAsync(targets, now, cancellationToken).ConfigureAwait(false);
            var due = targets.Where(target => targetStates[target.TargetId] is var row &&
                    (row.SearchNowRequestedAt is not null || row.NextSearchAt <= now))
                .OrderBy(target => targetStates[target.TargetId].SearchNowRequestedAt is null)
                .ThenBy(target => targetStates[target.TargetId].NextSearchAt)
                .ThenBy(target => target.TargetId)
                .Take(Math.Max(1, settings.AutomationBatchSize)).ToArray();

            var today = now.Date;
            var grabsToday = await database.GrabOperations.CountAsync(grab => grab.Automatic && grab.CreatedAt >= today, cancellationToken)
                .ConfigureAwait(false);
            var openImports = await database.ImportOperations.CountAsync(operation => ImportStates.Open.Contains(operation.State),
                cancellationToken).ConfigureAwait(false);
            foreach (var target in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Turning automation off stops the run before the next target, not after the batch.
                database.ChangeTracker.Clear();
                var current = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
                if (!current.AutomationEnabled)
                {
                    run = await database.AutomationRuns.SingleAsync(value => value.Id == run.Id, cancellationToken).ConfigureAwait(false);
                    await FinishAsync(run, AutomationRunStatuses.Completed, "Automation was turned off during the run.", queries)
                        .ConfigureAwait(false);
                    return run;
                }

                run = await database.AutomationRuns.SingleAsync(value => value.Id == run.Id, cancellationToken).ConfigureAwait(false);
                var row = await database.AutomationTargets.SingleAsync(value => value.TargetId == target.TargetId, cancellationToken)
                    .ConfigureAwait(false);
                var outcome = await ProcessAsync(run, target, row, current, grabsToday, openImports, queries, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome == AutomationReasons.Grabbed || outcome == AutomationReasons.UpgradePlanned)
                {
                    grabsToday++;
                    openImports++;
                }

                run.QueriesByIndexerJson = JsonSerializer.Serialize(queries);
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            run = await database.AutomationRuns.SingleAsync(value => value.Id == run.Id, cancellationToken).ConfigureAwait(false);
            await PruneAsync(settings.DecisionLogCap, cancellationToken).ConfigureAwait(false);
            await FinishAsync(run, AutomationRunStatuses.Completed, null, queries).ConfigureAwait(false);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            database.ChangeTracker.Clear();
            run = await database.AutomationRuns.SingleAsync(value => value.Id == run.Id, CancellationToken.None).ConfigureAwait(false);
            await FinishAsync(run, AutomationRunStatuses.Interrupted, "The run was cancelled between targets.", queries).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "JellyfinMod automation run {Run} failed", run.Id);
            database.ChangeTracker.Clear();
            run = await database.AutomationRuns.SingleAsync(value => value.Id == run.Id, CancellationToken.None).ConfigureAwait(false);
            await FinishAsync(run, AutomationRunStatuses.Failed, error.Message, queries).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Checks budgets, searches, picks and grabs for one target, recording exactly one decision.</summary>
    private async Task<string> ProcessAsync(AutomationRun run, AutomationTarget target, AutomationTargetState row,
        AcquisitionSettings settings, int grabsToday, int openImports, Dictionary<Guid, int> queries, CancellationToken cancellationToken)
    {
        var now = Now;
        var targetKey = GrabService.TargetKey(target.Entry.Id, target.Episode?.Id);
        row.SearchNowRequestedAt = null;
        if (await database.GrabOperations.AnyAsync(grab => grab.ActiveTarget == targetKey || grab.ActiveTarget == targetKey + "+add",
                cancellationToken).ConfigureAwait(false) ||
            await database.UpgradeOperations.AnyAsync(upgrade => upgrade.OpenTargetKey == targetKey, cancellationToken).ConfigureAwait(false))
            return Skip(run, target, row, AutomationReasons.ActiveGrabExists, null, retryIn: TimeSpan.FromHours(1));
        if (grabsToday >= Math.Max(0, settings.DailyAutoGrabBudget))
            return Skip(run, target, row, AutomationReasons.BudgetGrabs, "The daily automatic grab budget is used up.",
                retryIn: now.Date.AddDays(1) - now, kind: AutomationDecisionKinds.BudgetExhausted);
        if (row.LastAutoGrabAt is { } lastGrab && now - lastGrab < PerEntryGrabWindow)
            return Skip(run, target, row, AutomationReasons.BudgetGrabs, "This title was grabbed automatically in the last 24 hours.",
                retryIn: PerEntryGrabWindow - (now - lastGrab));
        if (openImports >= Math.Max(0, settings.MaxConcurrentImports))
            return Skip(run, target, row, AutomationReasons.TooManyOpenImports, null, retryIn: TimeSpan.FromHours(1));
        if (FreeSpaceBelowFloor(target.Entry.TargetLibraryId, settings, out var freeDetail))
            return Skip(run, target, row, AutomationReasons.FreeSpaceFloor, freeDetail, retryIn: TimeSpan.FromHours(6));

        // Budgets and breakers are applied inside the Phase 4 fan-out; when every indexer is out the search is skipped.
        var indexers = await database.AcquisitionIndexers.AsNoTracking().Where(indexer => indexer.Enabled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var budgets = await database.IndexerBudgets.AsNoTracking().ToDictionaryAsync(budget => budget.IndexerId, cancellationToken)
            .ConfigureAwait(false);
        var usable = indexers.Where(indexer => budgets.GetValueOrDefault(indexer.Id) is not { } budget ||
            (budget.BreakerOpenUntil is null || budget.BreakerOpenUntil <= now) &&
            (budget.Day != now.Date || budget.QueriesUsed < indexer.DailyQueryBudget)).ToArray();
        if (usable.Length == 0)
        {
            var allBroken = indexers.Count > 0 && indexers.All(indexer => budgets.GetValueOrDefault(indexer.Id)?.BreakerOpenUntil > now);
            return Skip(run, target, row, allBroken ? AutomationReasons.BreakerOpen : AutomationReasons.BudgetIndexer, null,
                retryIn: allBroken ? ReleaseSearchService.BreakerDuration : now.Date.AddDays(1) - now,
                kind: allBroken ? AutomationDecisionKinds.Skipped : AutomationDecisionKinds.BudgetExhausted);
        }

        ReleaseSearchSnapshot snapshot;
        using (var scope = scopes.CreateScope())
        {
            var search = scope.ServiceProvider.GetRequiredService<ReleaseSearchService>();
            var held = target.IsUpgrade
                ? (await VersionQuality.HeldAsync(database, target.Entry.Id, target.Episode?.Id, cancellationToken).ConfigureAwait(false))
                    .Select(version => version.Quality).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            try
            {
                snapshot = await search.SearchAsync(GrabService.AutomationUserId, ReleaseTargets.For(target.Entry, target.Episode),
                    new EvaluationProfile(target.Profile.Id, target.Profile.Name, target.Profile.Revision,
                        AcquisitionConfiguration.Qualities(target.Profile), target.Profile.MinimumBytesPerHour,
                        target.Profile.MaximumBytesPerHour),
                    target.Inherited, settings.Revision, cancellationToken,
                    new ReleaseSearchOptions(target.IsUpgrade ? GrabIntents.AddVersion : GrabIntents.Acquire, held)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TorznabException or HttpRequestException or DbUpdateException)
            {
                return Skip(run, target, row, AutomationReasons.SearchFailed, error.GetType().Name, empty: true);
            }
        }

        foreach (var (indexerId, count) in snapshot.QueriesByIndexer) queries[indexerId] = queries.GetValueOrDefault(indexerId) + count;
        foreach (var opened in snapshot.BreakersOpened)
            Record(run, target, AutomationDecisionKinds.BreakerOpened, AutomationReasons.BreakerOpen,
                "The indexer failed five times in a row and is paused for an hour.", indexerId: opened);
        run.Searched++;
        row.LastSearchedAt = now;

        var qualities = AcquisitionConfiguration.Qualities(target.Profile);
        var heldIndex = target.IsUpgrade ? VersionQuality.ProfileIndex(qualities, target.Upgrade!.HeldBest) : int.MaxValue;
        var eligible = snapshot.Candidates.Where(candidate => candidate.Evaluation.Eligible &&
                (!target.IsUpgrade || VersionQuality.ProfileIndex(qualities, candidate.Parsed.Quality) < heldIndex))
            .ToArray();
        if (eligible.Length == 0)
        {
            var blocklistedOnly = snapshot.Candidates.Any(candidate => candidate.Evaluation.Rejections.Any(rejection => rejection.Code == "blocklisted"));
            return Skip(run, target, row, blocklistedOnly ? AutomationReasons.Blocklisted : AutomationReasons.NoEligibleCandidate,
                $"{snapshot.Candidates.Count} candidates, none eligible.", empty: true);
        }

        var best = eligible[0];
        if (target.Profile.MinimumAutoScore is { } minimumScore && best.Evaluation.Score < minimumScore)
            return Skip(run, target, row, AutomationReasons.BelowMinimumScore, $"Best score {best.Evaluation.Score} < {minimumScore}.", empty: true);
        if (target.Profile.MinimumSeeders is { } minimumSeeders && (best.Seeders ?? 0) < minimumSeeders)
            return Skip(run, target, row, AutomationReasons.BelowMinimumSeeders, $"Best release has {best.Seeders ?? 0} seeders.", empty: true);

        var upgradeId = target.IsUpgrade ? Guid.NewGuid() : (Guid?)null;
        GrabOperation grab;
        using (var scope = scopes.CreateScope())
        {
            var grabs = scope.ServiceProvider.GetRequiredService<GrabService>();
            try
            {
                // The key is derived from run and target, so a replayed run step cannot create a second grab.
                var (operation, created) = await grabs.CreateAsync(GrabService.AutomationUserId,
                    new GrabRequest(snapshot.SearchId, best.ReleaseId, $"auto-{run.Id:N}-{target.TargetId:N}", true, upgradeId),
                    (_, _) => true, cancellationToken).ConfigureAwait(false);
                grab = operation;
                if (created) scope.ServiceProvider.GetRequiredService<GrabDispatcher>().Schedule(operation.Id, operation.HoldUntil);
            }
            catch (GrabException error)
            {
                var reason = error.Code switch
                {
                    "grab_active" => AutomationReasons.ActiveGrabExists,
                    "held_quality" => AutomationReasons.AlreadyHeldAtCutoff,
                    _ => AutomationReasons.GrabRefused
                };
                return Skip(run, target, row, reason, error.Code, retryIn: TimeSpan.FromHours(1));
            }
        }

        row.LastGrabId = grab.Id;
        row.LastAutoGrabAt = now;
        row.ConsecutiveEmpty = 0;
        // The next search waits for this grab to be imported or to fail; an active grab keeps later runs away anyway.
        row.NextSearchAt = now + FirstBackoff;
        if (upgradeId is { } id)
        {
            database.UpgradeOperations.Add(new UpgradeOperation
            {
                Id = id, EntryId = target.Entry.Id, EpisodeId = target.Episode?.Id, OpenTargetKey = GrabService.TargetKey(target.Entry.Id, target.Episode?.Id),
                SupersededBindingId = target.Upgrade!.Superseded!.BindingId, SupersededQuality = target.Upgrade.HeldBest,
                NewQuality = best.Parsed.Quality, NewGrabId = grab.Id, Mode = target.Profile.UpgradeMode == "add" ? "add" : "replace",
                State = UpgradeStates.Planned, CreatedAt = now, UpdatedAt = now
            });
            run.UpgradesPlanned++;
            row.LastOutcome = AutomationReasons.UpgradePlanned;
            Record(run, target, AutomationDecisionKinds.UpgradePlanned, AutomationReasons.UpgradePlanned,
                $"{target.Upgrade.HeldBest} → {best.Parsed.Quality}: {best.RawTitle}", grabId: grab.Id, indexerId: best.IndexerId);
            return AutomationReasons.UpgradePlanned;
        }

        run.Grabbed++;
        row.LastOutcome = AutomationReasons.Grabbed;
        Record(run, target, AutomationDecisionKinds.Grabbed, AutomationReasons.Grabbed, best.RawTitle, grabId: grab.Id,
            indexerId: best.IndexerId);
        return AutomationReasons.Grabbed;
    }

    /// <summary>Records a skip and schedules the next search: an empty search backs off, a blocked one retries later.</summary>
    private string Skip(AutomationRun run, AutomationTarget target, AutomationTargetState row, string reason, string? detail,
        TimeSpan? retryIn = null, bool empty = false, string kind = AutomationDecisionKinds.Skipped)
    {
        var now = Now;
        if (empty)
        {
            row.ConsecutiveEmpty++;
            row.NextSearchAt = now + Backoff(row.ConsecutiveEmpty);
        }
        else
        {
            row.NextSearchAt = now + (retryIn ?? FirstBackoff);
        }

        row.LastOutcome = reason;
        run.Skipped++;
        Record(run, target, kind, reason, detail);
        return reason;
    }

    /// <summary>12 h, 24 h, 48 h, 96 h, then capped at seven days.</summary>
    public static TimeSpan Backoff(int consecutiveEmpty)
    {
        var hours = FirstBackoff.TotalHours * Math.Pow(2, Math.Max(0, consecutiveEmpty - 1));
        return hours >= MaxBackoff.TotalHours ? MaxBackoff : TimeSpan.FromHours(hours);
    }

    private void Record(AutomationRun run, AutomationTarget target, string kind, string reason, string? detail, Guid? grabId = null,
        Guid? indexerId = null) =>
        database.AutomationDecisions.Add(new AutomationDecision
        {
            RunId = run.Id, TargetId = target.TargetId, EntryId = target.Entry.Id, EpisodeId = target.Episode?.Id, Kind = kind,
            Reason = reason, Detail = detail is null ? null : Bound(detail), GrabId = grabId, IndexerId = indexerId, CreatedAt = Now
        });

    /// <summary>
    /// Monitored movies without a file, aired monitored episodes without a file, and on-disk targets whose profile asks for
    /// a better version. Reclaimed titles only when allowed; specials never (Phase 4 cannot match them yet).
    /// </summary>
    private async Task<IReadOnlyList<AutomationTarget>> TargetsAsync(AcquisitionSettings settings, AcquisitionState state, DateTime now,
        CancellationToken cancellationToken)
    {
        var profiles = await database.AcquisitionQualityProfiles.AsNoTracking().ToDictionaryAsync(profile => profile.Id, cancellationToken)
            .ConfigureAwait(false);
        var liveLibraries = LiveLibraries();
        var entries = await database.Entries.AsNoTracking().Where(entry => entry.Monitored && entry.TargetLibraryId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var targets = new List<AutomationTarget>();
        foreach (var entry in entries.Where(entry => liveLibraries.Contains(entry.TargetLibraryId!.Value)))
        {
            var profileId = entry.QualityProfileId ?? state.Settings.DefaultQualityProfileId;
            if (profileId is not { } id || !profiles.TryGetValue(id, out var profile)) continue;
            var inherited = entry.QualityProfileId is null;
            if (entry.MediaType == "movie")
            {
                var target = await TargetAsync(entry, null, entry.State, profile, inherited, settings, cancellationToken).ConfigureAwait(false);
                if (target is not null) targets.Add(target);
                continue;
            }

            var episodes = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entry.Id && episode.Monitored &&
                    episode.SeasonNumber > 0 && episode.AirDate != null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var episode in episodes.Where(episode =>
                         episode.AirDate!.Value.AddMinutes(Math.Max(0, settings.NewEpisodeDelayMinutes)) <= now))
            {
                var target = await TargetAsync(entry, episode, episode.State, profile, inherited, settings, cancellationToken).ConfigureAwait(false);
                if (target is not null) targets.Add(target);
            }
        }

        return targets;
    }

    private async Task<AutomationTarget?> TargetAsync(Entry entry, Episode? episode, FileState fileState, AcquisitionQualityProfile profile,
        bool inherited, AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        var targetId = episode?.Id ?? entry.Id;
        if (fileState == FileState.OnDisk)
        {
            var held = await VersionQuality.HeldAsync(database, entry.Id, episode?.Id, cancellationToken).ConfigureAwait(false);
            var assessment = UpgradeAssessment.For(profile, held, episode is not null, settings.EpisodeUpgradesEnabled);
            return assessment.Eligible ? new AutomationTarget(targetId, entry, episode, profile, inherited, assessment) : null;
        }

        if (fileState == FileState.Reclaimed && !settings.ReacquireReclaimed) return null;
        return new AutomationTarget(targetId, entry, episode, profile, inherited, null);
    }

    /// <summary>Creates missing schedule rows (due now) and resets backoff when the profile or air date changed.</summary>
    private async Task<Dictionary<Guid, AutomationTargetState>> EnsureTargetStatesAsync(IReadOnlyList<AutomationTarget> targets, DateTime now,
        CancellationToken cancellationToken)
    {
        var ids = targets.Select(target => target.TargetId).ToArray();
        var rows = await database.AutomationTargets.Where(row => ids.Contains(row.TargetId))
            .ToDictionaryAsync(row => row.TargetId, cancellationToken).ConfigureAwait(false);
        foreach (var target in targets)
        {
            if (!rows.TryGetValue(target.TargetId, out var row))
            {
                row = new AutomationTargetState
                {
                    TargetId = target.TargetId, EntryId = target.Entry.Id, EpisodeId = target.Episode?.Id, NextSearchAt = now,
                    ProfileRevisionSeen = target.Profile.Revision, AirDateSeen = target.Episode?.AirDate
                };
                database.AutomationTargets.Add(row);
                rows[target.TargetId] = row;
                continue;
            }

            if (row.ProfileRevisionSeen != target.Profile.Revision || target.Episode is { } episode && row.AirDateSeen != episode.AirDate)
            {
                row.ProfileRevisionSeen = target.Profile.Revision;
                row.AirDateSeen = target.Episode?.AirDate;
                row.ConsecutiveEmpty = 0;
                row.NextSearchAt = now;
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// Refreshes, at most once a day, the metadata of monitored series that still have unaired or undated monitored
    /// episodes, so new air dates arrive without an administrator (P6.M4). Refresh never deletes an episode or changes
    /// availability (P7/T10).
    /// </summary>
    private async Task RefreshAiringSeriesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var series = await database.Entries.AsNoTracking().Where(entry => entry.Monitored && entry.MediaType == "series" &&
                database.Episodes.Any(episode => episode.EntryId == entry.Id && episode.Monitored &&
                    (episode.AirDate == null || episode.AirDate > now)))
            .Select(entry => entry.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var refreshed = 0;
        foreach (var seriesId in series)
        {
            if (refreshed >= MaxRefreshesPerRun) break;
            var row = await database.AutomationTargets.SingleOrDefaultAsync(value => value.TargetId == seriesId, cancellationToken)
                .ConfigureAwait(false);
            if (row?.MetadataRefreshedAt is { } last && now - last < TimeSpan.FromDays(1)) continue;
            if (row is null)
            {
                row = new AutomationTargetState { TargetId = seriesId, EntryId = seriesId, NextSearchAt = DateTime.MaxValue };
                database.AutomationTargets.Add(row);
            }

            row.MetadataRefreshedAt = now;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            refreshed++;
            try
            {
                using var scope = scopes.CreateScope();
                var refresher = scope.ServiceProvider.GetService<SeriesMetadataRefresher>();
                if (refresher is null) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await refresher.RefreshAsync(seriesId, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TmdbException or HttpRequestException or InvalidOperationException or
                OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Automation could not refresh series {Series}: {Error}", seriesId, error.GetType().Name);
            }
        }
    }

    private async Task<bool> ClientReachableAsync(AcquisitionDownloadClient client, CancellationToken cancellationToken)
    {
        if (drivers.Get(client.Kind) is not { } driver) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await driver.ProbeAsync(await configuration.ConnectAsync(client, timeout.Token).ConfigureAwait(false), timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or HttpRequestException or
            OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Reads the library mount's free space with statvfs; the larger of the percentage and byte floors applies.</summary>
    public bool FreeSpaceBelowFloor(Guid? libraryId, AcquisitionSettings settings, out string? detail)
    {
        detail = null;
        var roots = FreeSpace(libraryId);
        foreach (var (root, free, total) in roots)
        {
            var floor = Math.Max((ulong)Math.Max(0, settings.FreeSpaceFloorBytes),
                (ulong)(total * (Math.Clamp(settings.FreeSpaceFloorPercent, 0, 100) / 100.0)));
            if (free < floor)
            {
                detail = $"{free} bytes free on the library mount, floor {floor}.";
                _ = root;
                return true;
            }
        }

        return false;
    }

    /// <summary>Free and total bytes of each root of a library.</summary>
    public IReadOnlyList<(string Root, ulong Free, ulong Total)> FreeSpace(Guid? libraryId)
    {
        try
        {
            var folder = library.GetVirtualFolders()?.FirstOrDefault(value => Guid.TryParse(value.ItemId, out var id) && id == libraryId);
            return (folder?.Locations ?? []).Select(location => files.TryGetFreeSpace(location, out var free, out var total)
                    ? (location, free, total) : (location, 0UL, 0UL))
                .Where(value => value.Item3 > 0).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    private HashSet<Guid> LiveLibraries()
    {
        try
        {
            return (library.GetVirtualFolders() ?? []).Where(folder => folder.CollectionType is CollectionTypeOptions.movies or
                    CollectionTypeOptions.tvshows)
                .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    private async Task PruneAsync(int cap, CancellationToken cancellationToken)
    {
        var keep = Math.Max(100, cap);
        var cutoff = await database.AutomationDecisions.OrderByDescending(decision => decision.CreatedAt).Skip(keep - 1)
            .Select(decision => (DateTime?)decision.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (cutoff is not { } oldest) return;
        database.AutomationDecisions.RemoveRange(await database.AutomationDecisions.Where(decision => decision.CreatedAt < oldest)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(AutomationRun run, string status, string? detail, Dictionary<Guid, int> queries)
    {
        run.Status = status;
        run.Detail = detail is null ? null : Bound(detail);
        run.CompletedAt = Now;
        run.QueriesByIndexerJson = JsonSerializer.Serialize(queries);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];
}
