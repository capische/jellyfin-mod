using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Runs a bounded retention batch through the same preview and executor used by administrators.</summary>
public sealed class RetentionRunner(
    ModDbContext database,
    IServiceScopeFactory scopeFactory,
    RetentionRunGate runGate,
    RetentionConfigurationSource configuration,
    TimeProvider clock,
    ILogger<RetentionRunner> logger)
{
    private const int BatchSize = 25;
    private const int MaxAttemptsPerAction = 4;

    /// <summary>Runs recovery and due reclamation without allowing another batch to overlap.</summary>
    public async Task<RetentionRun> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RetentionRunAlreadyActiveException();
        using var operation = SqliteWriteDiagnostics.Operation("retention run");
        var now = clock.GetUtcNow().UtcDateTime;

        // Only this process runs batches, and it holds the run gate, so any other "running" row belongs
        // to a process that stopped before finishing it (P3.T9).
        foreach (var stale in await database.RetentionRuns.Where(candidate => candidate.Status == RetentionRunStatuses.Running)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            stale.Status = RetentionRunStatuses.Interrupted;
            stale.CompletedAt = now;
            stale.Detail = "The process stopped before this run finished.";
        }

        var run = new RetentionRun { StartedAt = now };
        database.RetentionRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Recover interrupted operations first. Finishing an unlinked operation deletes nothing and a
            // prepared one revalidates every protection, so this also runs while retention is disabled.
            var processedActions = 0;
            var blockedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var openActionIds = await database.RetentionOperations.AsNoTracking()
                .Where(operation => operation.State == RetentionOperationStates.Prepared ||
                    operation.State == RetentionOperationStates.Unlinked)
                .GroupBy(operation => operation.ActionId)
                .Select(group => new { ActionId = group.Key, PreparedAt = group.Min(operation => operation.PreparedAt) })
                .OrderBy(action => action.PreparedAt).ThenBy(action => action.ActionId)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var actionIds = openActionIds.Select(action => action.ActionId).ToArray();
            var openOperations = await database.RetentionOperations.AsNoTracking()
                .Where(operation => actionIds.Contains(operation.ActionId) &&
                    (operation.State == RetentionOperationStates.Prepared ||
                     operation.State == RetentionOperationStates.Unlinked))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var action in openActionIds)
            {
                // One action at a time, so cancellation is honoured between physical operations.
                cancellationToken.ThrowIfCancellationRequested();
                var bindingId = openOperations.Where(operation => operation.ActionId == action.ActionId)
                    .OrderBy(operation => operation.Id).First().BindingId;
                var result = await ExecuteAsync(bindingId, cancellationToken).ConfigureAwait(false);
                if (result.State is not (RetentionOperationStates.Prepared or RetentionOperationStates.Unlinked))
                    run.Interrupted++;
                Count(run, result, blockedReasons);
                processedActions++;
                await CheckpointAsync(run, progress, processedActions).ConfigureAwait(false);
            }

            // Removing a stale native item after an earlier cleanup failure deletes no media.
            using (var cleanupScope = scopeFactory.CreateScope())
                await cleanupScope.ServiceProvider.GetRequiredService<RetentionExecutor>()
                    .RetryNativeCleanupAsync(cancellationToken).ConfigureAwait(false);

            if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
            {
                await FinishAsync(run, RetentionRunStatuses.Disabled,
                    "Retention is disabled; no media was changed.").ConfigureAwait(false);
                progress.Report(100);
                return run;
            }

            if (processedActions < BatchSize)
            {
                await RebaselineStorageAsync(cancellationToken).ConfigureAwait(false);
                var preview = await PreviewAsync(cancellationToken).ConfigureAwait(false);
                run.Inspected = preview.Inspected;
                run.Eligible = preview.Due;
                run.Blocked = preview.Blocked;
                var recentlyFailed = (await database.RetentionOperations.AsNoTracking()
                        .Where(operation => operation.State == RetentionOperationStates.Failed &&
                            operation.CompletedAt >= now.AddDays(-1))
                        .Select(operation => operation.BindingId).ToArrayAsync(cancellationToken).ConfigureAwait(false))
                    .ToHashSet();
                // Bindings that failed recently go last, so a few undeletable files cannot starve the rest. Within one
                // target the lowest quality goes first (P6.M7): a higher version blocked by seeding never stops it.
                var qualityRank = await VersionRankAsync(preview.Items, cancellationToken).ConfigureAwait(false);
                var candidates = preview.Items
                    .Where(item => item.State == RetentionPreviewStates.Due && item.CanonicalPath is not null)
                    .GroupBy(item => item.CanonicalPath!, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(item => item.BindingId).First())
                    .OrderBy(item => recentlyFailed.Contains(item.BindingId))
                    .ThenBy(item => item.EpisodeId ?? item.EntryId)
                    .ThenBy(item => qualityRank.GetValueOrDefault(item.BindingId))
                    .ThenBy(item => item.CanonicalPath, StringComparer.Ordinal)
                    .Take(BatchSize * MaxAttemptsPerAction)
                    .ToArray();

                foreach (var candidate in candidates)
                {
                    if (processedActions >= BatchSize) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await FinishAsync(run, RetentionRunStatuses.Disabled,
                            "Retention was disabled during the batch; remaining media was not changed.").ConfigureAwait(false);
                        return run;
                    }

                    var result = await ExecuteAsync(candidate.BindingId, cancellationToken).ConfigureAwait(false);
                    Count(run, result, blockedReasons);
                    // Only candidates that prepared an operation use the batch budget; a blocked candidate
                    // (for example media_not_writable) must not stop writable due media behind it.
                    if (result.OperationId.HasValue) processedActions++;
                    await CheckpointAsync(run, progress, processedActions).ConfigureAwait(false);
                }
            }

            await FinishAsync(run, RetentionRunStatuses.Completed, BlockedSummary(blockedReasons)).ConfigureAwait(false);
            progress.Report(100);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(run, RetentionRunStatuses.Cancelled,
                "Retention was cancelled between physical actions.").ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "JellyfinMod retention run {RunId} failed", run.Id);
            await FinishAsync(run, RetentionRunStatuses.Failed, error.Message).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var policy = await scope.ServiceProvider.GetRequiredService<RetentionPolicyService>()
            .SyncAsync(configuration.Current, cancellationToken).ConfigureAwait(false);
        return policy.Enabled;
    }

    /// <summary>
    /// Refreshes storage identities whose device number changed across a reboot before the preview compares
    /// them (P2.R6). A failure leaves identities unchanged, so the preview still blocks those targets.
    /// </summary>
    private async Task RebaselineStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var libraries = scope.ServiceProvider.GetRequiredService<JellyfinNativeTitleSource>()
                .GetLibraryStorage(cancellationToken);
            var changed = await scope.ServiceProvider.GetRequiredService<ReconciliationService>()
                .RebaselineStorageAsync(libraries, cancellationToken).ConfigureAwait(false);
            if (changed > 0)
                logger.LogInformation("JellyfinMod re-baselined {Count} media storage identities", changed);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "JellyfinMod could not re-baseline media storage identities");
        }
    }

    /// <summary>Ranks each representation by resolution: parsed from its file name, else read from the native item.</summary>
    private Task<Dictionary<Guid, int>> VersionRankAsync(IReadOnlyList<Api.Contracts.RetentionRepresentationDto> items,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var library = scope.ServiceProvider.GetService<MediaBrowser.Controller.Library.ILibraryManager>();
        var result = new Dictionary<Guid, int>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rank = Automation.VersionQuality.ResolutionRank(item.CanonicalPath ?? item.Path);
            if (rank == 0 && library?.GetItemById(item.JellyfinItemId) is MediaBrowser.Controller.Entities.Video { Width: > 0 } video)
                rank = Automation.VersionQuality.WidthRank(video.Width);
            result[item.BindingId] = rank;
        }

        return Task.FromResult(result);
    }

    private async Task<Api.Contracts.RetentionPreviewDto> PreviewAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionPreviewService>()
            .PreviewAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionExecutionResult> ExecuteAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionExecutor>()
            .ReclaimAsync(bindingId, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckpointAsync(RetentionRun run, IProgress<double> progress, int processedActions)
    {
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        progress.Report(Math.Min(99, 100d * processedActions / BatchSize));
    }

    private async Task FinishAsync(RetentionRun run, string status, string? detail)
    {
        run.Status = status;
        run.Detail = detail is null ? null : detail[..Math.Min(1024, detail.Length)];
        run.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Names why each due representation was left alone. A run that reports only a blocked count cannot tell an
    /// administrator which media was spared or why, which matters most for a feature that deletes files (P3.T18).
    /// </summary>
    private static string? BlockedSummary(IReadOnlyDictionary<string, int> blockedReasons) =>
        blockedReasons.Count == 0
            ? null
            : "Left alone: " + string.Join(", ", blockedReasons.OrderByDescending(reason => reason.Value)
                .ThenBy(reason => reason.Key, StringComparer.Ordinal)
                .Select(reason => $"{reason.Key} ({reason.Value})")) + ".";

    private void Count(RetentionRun run, RetentionExecutionResult result, IDictionary<string, int> blockedReasons)
    {
        if (result.State == RetentionOperationStates.Blocked || result.State == RetentionOperationStates.Failed)
        {
            var reason = string.IsNullOrEmpty(result.Reason) ? "unknown" : result.Reason;
            blockedReasons[reason] = blockedReasons.TryGetValue(reason, out var count) ? count + 1 : 1;
            logger.LogInformation(
                "JellyfinMod retention left binding {BindingId} alone: {Reason}", result.BindingId, reason);
        }

        switch (result.State)
        {
            case RetentionOperationStates.Completed:
                run.Reclaimed++;
                run.LogicalBytesUnlinked += result.LogicalBytesUnlinked;
                if (result.PhysicalBytesReleased.HasValue)
                    run.PhysicalBytesReleased += result.PhysicalBytesReleased.Value;
                else
                    run.PhysicalBytesUnknown++;
                break;
            case RetentionOperationStates.Failed:
                run.Failed++;
                break;
            case RetentionOperationStates.Blocked:
                run.Blocked++;
                break;
        }
    }
}

/// <summary>Prevents automatic and administrator-triggered retention batches from overlapping.</summary>
public sealed class RetentionRunGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires the run lease immediately when no batch is active.</summary>
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

/// <summary>Signals that a second retention batch was rejected without waiting.</summary>
public sealed class RetentionRunAlreadyActiveException : InvalidOperationException
{
    /// <summary>Initializes the stable overlap error.</summary>
    public RetentionRunAlreadyActiveException()
        : base("A JellyfinMod retention run is already active.")
    {
    }
}
