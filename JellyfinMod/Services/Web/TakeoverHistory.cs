using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Writes the <c>interface_patched</c> and <c>interface_restored</c> history rows (§4.6, REVIEW-2026-09-24 S4-R4),
/// so "why did /web change?" has a durable answer that outlives the next patch.
/// </summary>
/// <remarks>Rows carry ids, hashes and who applied the change; never a path.</remarks>
public sealed class TakeoverHistory(IServiceScopeFactory scopes)
{
    /// <summary>Records one patch or restore.</summary>
    /// <param name="change">What happened.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task RecordAsync(TakeoverEvent change, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
        database.History.Add(new HistoryRecord
        {
            EntryId = Guid.Empty,
            EventType = change.EventType,
            Summary = change.EventType == "interface_patched"
                ? $"The JellyfinMod interface was applied at /web ({change.By})"
                : $"The original Jellyfin interface was restored at /web ({change.By})",
            Data = JsonSerializer.Serialize(change.EventType == "interface_patched"
                ? (object)new
                {
                    bundleId = change.BundleId, previousBundleId = change.PreviousBundleId, stockSha256 = change.StockSha256,
                    patchedSha256 = change.PatchedSha256, patchedBy = change.By
                }
                : new
                {
                    previousBundleId = change.PreviousBundleId, stockSha256 = change.StockSha256,
                    patchedSha256 = change.PatchedSha256, restoredBy = change.By
                })
        });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
