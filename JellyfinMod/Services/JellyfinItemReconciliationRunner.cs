namespace JellyfinMod.Services;

/// <summary>Reconciles one current native item by re-reading its complete library-scoped title snapshot.</summary>
public sealed class JellyfinItemReconciliationRunner(
    JellyfinNativeTitleSource source,
    ReconciliationService reconciliation)
{
    /// <summary>Reconciles the current title containing the notified item, if it still exists.</summary>
    public async Task ReconcileAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var observation = source.GetObservation(itemId, cancellationToken);
        if (observation?.Snapshot is { } snapshot)
            await reconciliation.ReconcileAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
}
