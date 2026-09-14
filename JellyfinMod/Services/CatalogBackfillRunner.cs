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
    ReconciliationLibraryLock libraryLock,
    ILogger<CatalogBackfillRunner> logger)
{
    private const int MaxDiagnostics = 100;

    /// <summary>Runs every current movie and series observation once.</summary>
    public async Task<ReconciliationRun> RunAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        bool confirmAbsence = false)
    {
        await using var lease = await runGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A JellyfinMod reconciliation run is already active.");
        var run = new ReconciliationRun();
        database.ReconciliationRuns.Add(run);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var diagnostics = new List<ReconciliationDiagnostic>();
        var countedConflicts = new HashSet<(Guid LibraryId, Guid NativeId)>();
        Dictionary<Guid, CompleteLibraryObservation> libraryObservations = [];
        try
        {
            if (confirmAbsence)
                libraryObservations = source.GetLibraryStorage(cancellationToken).ToDictionary(
                    storage => storage.LibraryId, storage => new CompleteLibraryObservation(storage));
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
                    if (libraryObservations.TryGetValue(work.TargetLibraryId, out var libraryObservation))
                        libraryObservation.Observe(observation, observed.Result);
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
                    if (libraryObservations.TryGetValue(work.TargetLibraryId, out var libraryObservation))
                        libraryObservation.IsComplete = false;
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

            if (confirmAbsence)
                await ConfirmAbsenceAsync(run, libraryObservations.Values, diagnostics, cancellationToken)
                    .ConfigureAwait(false);

            run.TotalItems = run.ScannedItems;
            run.Status = "completed";
            run.CompletedAt = DateTime.UtcNow;
            run.DiagnosticsJson = SerializeDiagnostics(diagnostics);
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

    private async Task ConfirmAbsenceAsync(
        ReconciliationRun run,
        IEnumerable<CompleteLibraryObservation> observations,
        ICollection<ReconciliationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = string.Empty;
            if (!observation.IsComplete || !LibraryStorageProbe.IsAvailable(observation.Storage.Locations, out detail))
            {
                RecordIncompleteLibrary(run, diagnostics, observation.Storage,
                    observation.IsComplete ? detail : "One or more native titles could not be observed completely.");
                continue;
            }

            await using var libraryLease = await libraryLock.AcquireAsync(observation.Storage.LibraryId, cancellationToken)
                .ConfigureAwait(false);
            var current = ObserveLibrary(observation.Storage, cancellationToken);
            var confirmation = ObserveLibrary(observation.Storage, cancellationToken);

            if (!current.IsComplete || !confirmation.IsComplete ||
                !current.IsEquivalentTo(confirmation) ||
                !LibraryStorageProbe.IsAvailable(current.Storage.Locations, out detail))
            {
                RecordIncompleteLibrary(run, diagnostics, current.Storage,
                    !current.IsComplete || !confirmation.IsComplete
                        ? "The final native-library observation could not be completed."
                        : !current.IsEquivalentTo(confirmation)
                            ? "The native library changed during final enumeration; absence was not confirmed."
                            : detail);
                continue;
            }

            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<ReconciliationService>()
                .ConfirmAbsenceUnderLeaseAsync(confirmation.ToSnapshot(), cancellationToken).ConfigureAwait(false);
            if (result.IsComplete)
                run.MissingItems += result.MissingItems;
            else
                RecordIncompleteLibrary(run, diagnostics, current.Storage,
                    result.Detail ?? "The stored media mount could not be confirmed.");
        }
    }

    private CompleteLibraryObservation ObserveLibrary(
        NativeLibraryStorage storage,
        CancellationToken cancellationToken)
    {
        var observation = new CompleteLibraryObservation(storage);
        try
        {
            foreach (var work in source.GetLibraryWorkItems(storage.LibraryId, cancellationToken))
                observation.ObserveNative(source.GetObservation(work, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            observation.IsComplete = false;
        }

        return observation;
    }

    private static void RecordIncompleteLibrary(
        ReconciliationRun run,
        ICollection<ReconciliationDiagnostic> diagnostics,
        NativeLibraryStorage storage,
        string detail)
    {
        run.IncompleteLibraries++;
        AddDiagnostic(diagnostics, new(Guid.Empty, storage.LibraryId, storage.Name, null, false, null), detail);
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

    private sealed class CompleteLibraryObservation(NativeLibraryStorage storage)
    {
        public NativeLibraryStorage Storage { get; } = storage;
        public bool IsComplete { get; set; } = true;
        private HashSet<Guid> TitleIds { get; } = [];
        private HashSet<Guid> PlayableTitleIds { get; } = [];
        private HashSet<Guid> EpisodeIds { get; } = [];
        private HashSet<Guid> PlayableEpisodeIds { get; } = [];

        public void Observe(NativeCatalogObservation observation, ReconciliationResult? result)
        {
            if (result is null || result.Outcome == ReconciliationOutcome.Conflict)
            {
                IsComplete = false;
                return;
            }

            ObserveNative(observation);
        }

        public void ObserveNative(NativeCatalogObservation observation)
        {
            if (observation.Snapshot is not { } snapshot)
            {
                IsComplete = false;
                return;
            }

            foreach (var representation in snapshot.Representations)
            {
                TitleIds.Add(representation.JellyfinItemId);
                var isPlayable = snapshot.MediaType == "movie" && representation.IsPlayable ||
                    snapshot.MediaType == "series" && snapshot.Episodes.Any(episode =>
                        episode.SeriesItemId == representation.JellyfinItemId && episode.IsPlayable);
                if (isPlayable) PlayableTitleIds.Add(representation.JellyfinItemId);
            }

            foreach (var episode in snapshot.Episodes)
            {
                EpisodeIds.Add(episode.JellyfinItemId);
                if (episode.IsPlayable) PlayableEpisodeIds.Add(episode.JellyfinItemId);
            }
        }

        public bool IsEquivalentTo(CompleteLibraryObservation other) =>
            TitleIds.SetEquals(other.TitleIds) &&
            PlayableTitleIds.SetEquals(other.PlayableTitleIds) &&
            EpisodeIds.SetEquals(other.EpisodeIds) &&
            PlayableEpisodeIds.SetEquals(other.PlayableEpisodeIds);

        public ConfirmedLibrarySnapshot ToSnapshot() => new(Storage.LibraryId, Storage.Locations,
            TitleIds, PlayableTitleIds, EpisodeIds, PlayableEpisodeIds);
    }
}

/// <summary>Conservatively verifies that every configured library path is present and readable.</summary>
internal static class LibraryStorageProbe
{
    /// <summary>Returns true only when every configured path can be enumerated.</summary>
    public static bool IsAvailable(IReadOnlyList<string> locations, out string detail)
    {
        if (locations.Count == 0)
        {
            detail = "The native library has no configured storage locations.";
            return false;
        }

        foreach (var location in locations.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
                {
                    detail = $"Library storage is unavailable: {location}";
                    return false;
                }

                using var entries = Directory.EnumerateFileSystemEntries(location).GetEnumerator();
                _ = entries.MoveNext();
            }
            catch (Exception)
            {
                detail = $"Library storage could not be enumerated: {location}";
                return false;
            }
        }

        detail = string.Empty;
        return true;
    }
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
