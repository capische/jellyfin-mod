using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Runs a bounded, resumable full-library reconciliation through the durable reconciliation service.</summary>
public sealed class CatalogBackfillRunner(
    ModDbContext database,
    IServiceScopeFactory scopeFactory,
    JellyfinNativeTitleSource source,
    ReconciliationRunGate runGate,
    ILogger<CatalogBackfillRunner> logger)
{
    private const int MaxDiagnostics = 100;

    /// <summary>Runs every current movie and series observation once.</summary>
    public async Task<ReconciliationRun> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A JellyfinMod reconciliation run is already active.");
        var run = new ReconciliationRun();
        database.ReconciliationRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var diagnostics = new List<ReconciliationDiagnostic>();
        var countedConflicts = new HashSet<(Guid LibraryId, Guid NativeId)>();
        try
        {
            run.TotalItems = source.GetNativeTitleCount(cancellationToken);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var work in source.GetWorkItems(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Each item owns its context. Failed writes cannot poison the next item,
                    // and summaries never share tracking state with reconciliation entities.
                    using var scope = scopeFactory.CreateScope();
                    var observed = await scope.ServiceProvider.GetRequiredService<JellyfinItemReconciliationRunner>()
                        .ReconcileAsync(work, cancellationToken).ConfigureAwait(false);
                    var observation = observed.Observation;
                    if (observation.IsConflict && !countedConflicts.Add((work.TargetLibraryId, observation.NativeItemId)))
                        continue;
                    run.ScannedItems++;
                    if (observed.Result is { } result)
                    {
                        switch (result.Outcome)
                        {
                            case ReconciliationOutcome.Created: run.CreatedEntries++; break;
                            case ReconciliationOutcome.Updated: run.UpdatedBindings++; break;
                            case ReconciliationOutcome.Unchanged: run.UnchangedItems++; break;
                            case ReconciliationOutcome.Unmatched: run.UnmatchedItems++; break;
                            case ReconciliationOutcome.Conflict: run.ConflictedItems++; break;
                        }

                        if (result.Outcome is ReconciliationOutcome.Unmatched or ReconciliationOutcome.Conflict)
                            AddDiagnostic(diagnostics, observation, result.Detail ?? result.Outcome.ToString());
                    }
                    else
                    {
                        if (observation.IsConflict) run.ConflictedItems++;
                        else run.FailedItems++;
                        AddDiagnostic(diagnostics, observation, observation.Detail ?? "Unable to build a native observation.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    run.ScannedItems++;
                    run.FailedItems++;
                    AddDiagnostic(diagnostics, new(work.NativeItemId, work.TargetLibraryId, work.Title, null, false, null), error.Message);
                    logger.LogWarning(error, "JellyfinMod reconciliation failed for native item {ItemId}", work.NativeItemId);
                }

                // The initial native count includes copies; finish with the exact logical-title count.
                run.TotalItems = Math.Max(run.TotalItems, run.ScannedItems);
                run.DiagnosticsJson = SerializeDiagnostics(diagnostics);
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                progress.Report(Math.Min(99, 100d * run.ScannedItems / Math.Max(1, run.TotalItems)));
            }

            run.TotalItems = run.ScannedItems;
            run.Status = "completed";
            run.CompletedAt = DateTime.UtcNow;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            progress.Report(100);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishInterruptedAsync(run, diagnostics, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await FinishInterruptedAsync(run, diagnostics, "failed").ConfigureAwait(false);
            throw;
        }
    }

    private async Task FinishInterruptedAsync(ReconciliationRun run,
        IReadOnlyCollection<ReconciliationDiagnostic> diagnostics, string status)
    {
        run.Status = status;
        run.CompletedAt = DateTime.UtcNow;
        run.DiagnosticsJson = SerializeDiagnostics(diagnostics);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void AddDiagnostic(
        ICollection<ReconciliationDiagnostic> diagnostics,
        NativeCatalogObservation observation,
        string detail)
    {
        if (diagnostics.Count < MaxDiagnostics)
            diagnostics.Add(new(observation.NativeItemId, observation.TargetLibraryId, observation.Title, detail));
    }

    private static string? SerializeDiagnostics(IReadOnlyCollection<ReconciliationDiagnostic> diagnostics) =>
        diagnostics.Count == 0 ? null : JsonSerializer.Serialize(diagnostics);
}

/// <summary>Prevents scheduled, post-scan, and manually invoked full runs from overlapping.</summary>
public sealed class ReconciliationRunGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires the full-run lease without waiting when no other run is active.</summary>
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

/// <summary>One bounded detail visible only through the administrator reconciliation endpoint.</summary>
public sealed record ReconciliationDiagnostic(
    [property: JsonPropertyName("nativeItemId")] Guid NativeItemId,
    [property: JsonPropertyName("targetLibraryId")] Guid TargetLibraryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail);
