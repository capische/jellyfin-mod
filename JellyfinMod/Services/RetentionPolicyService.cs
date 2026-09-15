using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Persists the effective retention policy and advances its revision only on change.</summary>
public sealed class RetentionPolicyService(ModDbContext database, TimeProvider clock)
{
    /// <summary>The stable singleton policy row.</summary>
    public static readonly Guid PolicyId = Guid.Parse("5d9b73de-98c1-4bc1-92c7-c72a80f7c402");

    /// <summary>Synchronizes XML configuration into durable policy evidence.</summary>
    public async Task<RetentionPolicySnapshot> SyncAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var selectedUserId = configuration.RetentionWatchedUserMode == WatchedUserMode.SelectedUser
            ? configuration.RetentionSelectedUserId
            : null;
        var reclaimAfterDays = Math.Clamp(configuration.ReclaimAfterDays, 1, 3650);
        var snapshot = await database.RetentionPolicySnapshots.SingleOrDefaultAsync(
            policy => policy.Id == PolicyId, cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {
            snapshot = new RetentionPolicySnapshot
            {
                Id = PolicyId,
                Version = 1,
                Enabled = configuration.RetentionEnabled,
                WatchedUserMode = configuration.RetentionWatchedUserMode,
                SelectedUserId = selectedUserId,
                ReclaimAfterDays = reclaimAfterDays,
                ExemptFavourites = configuration.ExemptFavourites,
                EnabledAt = configuration.RetentionEnabled ? now : null,
                UpdatedAt = now
            };
            database.RetentionPolicySnapshots.Add(snapshot);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }

        var changed = snapshot.Enabled != configuration.RetentionEnabled ||
            snapshot.WatchedUserMode != configuration.RetentionWatchedUserMode ||
            snapshot.SelectedUserId != selectedUserId ||
            snapshot.ReclaimAfterDays != reclaimAfterDays ||
            snapshot.ExemptFavourites != configuration.ExemptFavourites;
        if (!changed) return snapshot;

        if (!snapshot.Enabled && configuration.RetentionEnabled) snapshot.EnabledAt = now;
        if (snapshot.Enabled && !configuration.RetentionEnabled) snapshot.EnabledAt = null;
        snapshot.Enabled = configuration.RetentionEnabled;
        snapshot.WatchedUserMode = configuration.RetentionWatchedUserMode;
        snapshot.SelectedUserId = selectedUserId;
        snapshot.ReclaimAfterDays = reclaimAfterDays;
        snapshot.ExemptFavourites = configuration.ExemptFavourites;
        snapshot.Version++;
        snapshot.UpdatedAt = now;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
}
