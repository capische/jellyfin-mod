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

    /// <summary>Runs recovery and due reclamation without allowing another batch to overlap.</summary>
    public async Task<RetentionRun> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RetentionRunAlreadyActiveException();
        var run = new RetentionRun { StartedAt = clock.GetUtcNow().UtcDateTime };
        database.RetentionRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
            {
                await FinishAsync(run, RetentionRunStatuses.Disabled,
                    "Retention is disabled; no media was changed.").ConfigureAwait(false);
                progress.Report(100);
                return run;
            }

            var processedActions = 0;
            var openActionIds = await database.RetentionOperations.AsNoTracking()
                .Where(operation => operation.State == RetentionOperationStates.Prepared ||
                    operation.State == RetentionOperationStates.Unlinked)
                .GroupBy(operation => operation.ActionId)
                .Select(group => new
                {
                    ActionId = group.Key,
                    PreparedAt = group.Min(operation => operation.PreparedAt)
                })
                .OrderBy(action => action.PreparedAt)
                .ThenBy(action => action.ActionId)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var actionIds = openActionIds.Select(action => action.ActionId).ToArray();
            var openOperations = await database.RetentionOperations.AsNoTracking()
                .Where(operation => actionIds.Contains(operation.ActionId) &&
                    (operation.State == RetentionOperationStates.Prepared ||
                     operation.State == RetentionOperationStates.Unlinked))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var openActions = openActionIds
                .Select(group => new
                {
                    BindingId = openOperations.Where(operation => operation.ActionId == group.ActionId)
                        .OrderBy(operation => operation.Id).First().BindingId
                })
                .ToArray();

            foreach (var action in openActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
                {
                    await FinishAsync(run, RetentionRunStatuses.Disabled,
                        "Retention was disabled during the batch; remaining media was not changed.").ConfigureAwait(false);
                    return run;
                }

                var result = await ExecuteAsync(action.BindingId, cancellationToken).ConfigureAwait(false);
                run.Interrupted++;
                Count(run, result);
                processedActions++;
                await CheckpointAsync(run, progress, processedActions).ConfigureAwait(false);
            }

            if (processedActions < BatchSize)
            {
                var preview = await PreviewAsync(cancellationToken).ConfigureAwait(false);
                run.Inspected = preview.Inspected;
                run.Eligible = preview.Due;
                run.Blocked = preview.Blocked;
                var candidates = preview.Items
                    .Where(item => item.State == RetentionPreviewStates.Due && item.CanonicalPath is not null)
                    .GroupBy(item => item.CanonicalPath!, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(item => item.BindingId).First())
                    .Take(BatchSize - processedActions)
                    .ToArray();

                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await FinishAsync(run, RetentionRunStatuses.Disabled,
                            "Retention was disabled during the batch; remaining media was not changed.").ConfigureAwait(false);
                        return run;
                    }

                    var result = await ExecuteAsync(candidate.BindingId, cancellationToken).ConfigureAwait(false);
                    Count(run, result);
                    processedActions++;
                    await CheckpointAsync(run, progress, processedActions).ConfigureAwait(false);
                }
            }

            await FinishAsync(run, RetentionRunStatuses.Completed, null).ConfigureAwait(false);
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

    private static void Count(RetentionRun run, RetentionExecutionResult result)
    {
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
