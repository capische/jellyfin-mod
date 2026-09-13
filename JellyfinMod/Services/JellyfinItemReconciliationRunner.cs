namespace JellyfinMod.Services;

/// <summary>Re-reads native state and commits it while holding the same per-library lease.</summary>
public sealed class JellyfinItemReconciliationRunner(
    JellyfinNativeTitleSource source,
    ReconciliationService reconciliation,
    ReconciliationLibraryLock libraryLock)
{
    /// <summary>Reconciles the current title containing the notified item, if it still exists.</summary>
    public async Task ReconcileAsync(Guid itemId, CancellationToken cancellationToken)
    {
        foreach (var work in source.GetItemWorkItems(itemId, cancellationToken))
            await ReconcileAsync(work, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a fresh observation after waiting for any earlier library work.</summary>
    public async Task<NativeReconciliationResult> ReconcileAsync(NativeTitleWorkItem work, CancellationToken cancellationToken)
    {
        await using var lease = await libraryLock.AcquireAsync(work.TargetLibraryId, cancellationToken).ConfigureAwait(false);
        var observation = source.GetObservation(work, cancellationToken);
        var result = observation.Snapshot is { } snapshot
            ? await reconciliation.ReconcileUnderLeaseAsync(snapshot, cancellationToken).ConfigureAwait(false) : null;
        return new(observation, result);
    }
}

/// <summary>One observed title and its durable reconciliation outcome.</summary>
public sealed record NativeReconciliationResult(NativeCatalogObservation Observation, ReconciliationResult? Result);
