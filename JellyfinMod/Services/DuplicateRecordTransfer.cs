using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>
/// Moves the audit records that name a duplicate row (grabs, imports, reclamations, upgrades, seed releases, blocklistings,
/// automation decisions) onto the row that survives it, before the duplicate is deleted. Deleting it would detach them,
/// leaving records about one episode that no longer say which, so a history read could no longer tell whether a requester
/// may see them (Codex delta review 2). Runs in the caller's transaction; saves nothing tracked.
/// </summary>
internal static class DuplicateRecordTransfer
{
    /// <summary>Moves every record of episode <paramref name="from"/> to episode <paramref name="to"/>.</summary>
    public static async Task MoveEpisodeAsync(ModDbContext database, Guid from, Guid to, CancellationToken cancellationToken)
    {
        await database.GrabOperations.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.ImportOperations.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.RetentionOperations.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.UpgradeOperations.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.SeedReleaseOperations.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.ReleaseBlocklist.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
        await database.AutomationDecisions.Where(row => row.EpisodeId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EpisodeId, to), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves every record of entry <paramref name="from"/> to entry <paramref name="to"/>.</summary>
    public static async Task MoveEntryAsync(ModDbContext database, Guid from, Guid to, CancellationToken cancellationToken)
    {
        await database.GrabOperations.Where(row => row.EntryId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryId, to), cancellationToken).ConfigureAwait(false);
        await database.ImportOperations.Where(row => row.EntryId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryId, to), cancellationToken).ConfigureAwait(false);
        await database.RetentionOperations.Where(row => row.EntryId == from)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryId, to), cancellationToken).ConfigureAwait(false);
    }
}
