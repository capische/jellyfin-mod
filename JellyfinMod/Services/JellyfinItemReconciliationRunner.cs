namespace JellyfinMod.Services;

/// <summary>Re-reads native state and commits it while holding the same per-library lease.</summary>
public sealed class JellyfinItemReconciliationRunner(
    JellyfinNativeTitleSource source,
    ReconciliationService reconciliation,
    ReconciliationLibraryLock libraryLock)
{
    /// <summary>
    /// Reconciles the current title containing the notified item, if it still exists, and returns the entries (with their library)
    /// it resolved to, and among them the ones whose catalog row or binding changed.
    /// </summary>
    public async Task<ReconciledTitles> ReconcileAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var changed = new List<(Guid, Guid)>();
        var titles = new List<(Guid, Guid)>();
        foreach (var work in source.GetItemWorkItems(itemId, cancellationToken))
        {
            if ((await ReconcileAsync(work, cancellationToken).ConfigureAwait(false)).Result is not { EntryId: { } entryId } result) continue;
            titles.Add((work.TargetLibraryId, entryId));
            if (result.Outcome is ReconciliationOutcome.Created or ReconciliationOutcome.Updated) changed.Add((work.TargetLibraryId, entryId));
        }

        return new(changed, titles);
    }

    /// <summary>Builds a fresh observation after waiting for any earlier library work.</summary>
    public async Task<NativeReconciliationResult> ReconcileAsync(NativeTitleWorkItem work, CancellationToken cancellationToken)
    {
        await using var lease = await libraryLock.AcquireAsync(work.TargetLibraryId, cancellationToken).ConfigureAwait(false);
        using var operation = JellyfinMod.Data.SqliteWriteDiagnostics.Operation("reconciliation");
        var observation = source.GetObservation(work, cancellationToken);
        var result = observation.Snapshot is { } snapshot
            ? await reconciliation.ReconcileUnderLeaseAsync(snapshot, cancellationToken).ConfigureAwait(false) : null;
        return new(observation, result);
    }
}

/// <summary>One observed title and its durable reconciliation outcome.</summary>
public sealed record NativeReconciliationResult(NativeCatalogObservation Observation, ReconciliationResult? Result);

/// <summary>The entries a notified item resolved to, and the ones among them that changed.</summary>
public sealed record ReconciledTitles(IReadOnlyList<(Guid LibraryId, Guid EntryId)> Changed, IReadOnlyList<(Guid LibraryId, Guid EntryId)> Titles);
