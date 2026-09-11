using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Runs a bounded, resumable full-library reconciliation through the durable reconciliation service.</summary>
public sealed class CatalogBackfillRunner(
    ModDbContext database,
    ReconciliationService reconciliation,
    JellyfinNativeTitleSource source,
    ReconciliationRunGate runGate,
    ILogger<CatalogBackfillRunner> logger)
{
    private const int BatchSize = 50;
    private const int MaxDiagnostics = 100;

    /// <summary>Runs every current movie and series observation once.</summary>
    public async Task<ReconciliationRun> RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A JellyfinMod reconciliation run is already active.");
        var run = new ReconciliationRun();
        database.ReconciliationRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var observations = source.GetObservations(cancellationToken);
            run.TotalItems = observations.Count;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = new List<ReconciliationDiagnostic>();
            foreach (var batch in observations.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var observation in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    run.ScannedItems++;
                    if (observation.Snapshot is null)
                    {
                        if (observation.IsConflict) run.ConflictedItems++;
                        else run.FailedItems++;
                        AddDiagnostic(diagnostics, observation, observation.Detail ?? "Unable to build a native observation.");
                        progress.Report(run.TotalItems == 0 ? 100 : run.ScannedItems * 100d / run.TotalItems);
                        continue;
                    }

                    try
                    {
                        var result = await reconciliation.ReconcileAsync(observation.Snapshot, cancellationToken)
                            .ConfigureAwait(false);
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        AddDiagnostic(diagnostics, observation, error.Message);
                        logger.LogWarning(error, "JellyfinMod reconciliation failed for native item {ItemId}", observation.NativeItemId);
                        database.ChangeTracker.Clear();
                        run = await database.ReconciliationRuns.SingleAsync(candidate => candidate.Id == run.Id,
                            CancellationToken.None).ConfigureAwait(false);
                        run.ScannedItems++;
                        run.FailedItems++;
                    }

                    progress.Report(run.TotalItems == 0 ? 100 : run.ScannedItems * 100d / run.TotalItems);
                }

                run.DiagnosticsJson = SerializeDiagnostics(diagnostics);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            run.Status = "completed";
            run.CompletedAt = DateTime.UtcNow;
            run.DiagnosticsJson = SerializeDiagnostics(diagnostics);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishInterruptedAsync(run.Id, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await FinishInterruptedAsync(run.Id, "failed").ConfigureAwait(false);
            throw;
        }
    }

    private async Task FinishInterruptedAsync(Guid runId, string status)
    {
        database.ChangeTracker.Clear();
        var interrupted = await database.ReconciliationRuns.SingleAsync(run => run.Id == runId,
            CancellationToken.None).ConfigureAwait(false);
        interrupted.Status = status;
        interrupted.CompletedAt = DateTime.UtcNow;
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
