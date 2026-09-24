using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>What a metadata-only series refresh did.</summary>
public enum SeriesRefreshOutcome
{
    /// <summary>The episode list and metadata were refreshed.</summary>
    Refreshed,

    /// <summary>The entry no longer exists.</summary>
    NotFound,

    /// <summary>The library stayed locked by reconciliation for the whole write budget.</summary>
    LibraryBusy
}

/// <summary>
/// The metadata-only series Refresh (P3.T10), shared by the administrator's Refresh action and the automation's daily
/// refresh of airing series (P6.M4). It changes metadata only: availability, bindings and retention belong to
/// reconciliation and retention, so it serializes with both and never sets them.
/// </summary>
public sealed class SeriesMetadataRefresher(
    ModDbContext database,
    TmdbClient tmdb,
    ReconciliationLibraryLock libraryLock,
    RetentionExecutionGate retentionGate,
    JellyfinItemReconciliationRunner? reconciliation = null,
    LibraryWriteBudget? writeBudget = null)
{
    /// <summary>Refreshes one series entry. TMDB failures surface as <see cref="TmdbException"/>.</summary>
    public async Task<SeriesRefreshOutcome> RefreshAsync(Guid id, CancellationToken cancellationToken)
    {
        var visible = await database.Entries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (visible is null || visible.MediaType != "series" || visible.TargetLibraryId is not { } libraryId)
            return SeriesRefreshOutcome.NotFound;
        var libraryWait = (writeBudget ?? LibraryWriteBudget.Default).Wait;
        // Both remote snapshots are fully validated before any lock is taken or entity changed.
        var metadata = await tmdb.GetDetailsAsync(visible.MediaType, visible.TmdbId, cancellationToken);
        var snapshot = await tmdb.GetEpisodesAsync(metadata, cancellationToken);

        // Refresh changes metadata only. Availability, bindings and retention state belong to
        // reconciliation and retention, so it serializes with both and never sets them (P3.T10).
        Guid? nativeSeriesId;
        await using (await retentionGate.AcquireAsync(cancellationToken))
        await using (var libraryLease = await libraryLock.TryAcquireAsync(libraryId, libraryWait, cancellationToken))
        {
            if (libraryLease is null) return SeriesRefreshOutcome.LibraryBusy;
            database.ChangeTracker.Clear();
            var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (entry is null) return SeriesRefreshOutcome.NotFound;
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var existing = await database.Episodes.Where(episode => episode.EntryId == entry.Id).ToListAsync(cancellationToken);
            var byTmdbId = existing.Where(episode => !episode.IsPositionIdentity).ToDictionary(episode => episode.TmdbId);
            // TMDB is identity; a bound episode keeps Jellyfin's display numbering, so Refresh and
            // reconciliation stop overwriting each other (P2.R9). Unmatched episodes keep theirs too.
            var originalPositions = existing.ToDictionary(episode => episode.Id,
                episode => (episode.SeasonNumber, episode.EpisodeNumber));
            // An episode tracked by its position (P10.E1) takes the TMDB episode listed at that position, keeping its
            // identity, bindings, Keep and retention evidence, so the same file is never tracked twice.
            var byPosition = existing.Where(episode => episode.IsPositionIdentity)
                .ToDictionary(episode => originalPositions[episode.Id]);
            if (snapshot.Any(remote => byTmdbId.TryGetValue(remote.TmdbId, out var local) &&
                (local.SeasonNumber != remote.SeasonNumber || local.EpisodeNumber != remote.EpisodeNumber)))
            {
                // Free the unique display positions before applying swaps. Negative seasons
                // cannot come from TMDB, and the transaction hides these temporary positions.
                for (var index = 0; index < existing.Count; index++)
                {
                    existing[index].SeasonNumber = -1;
                    existing[index].EpisodeNumber = index + 1;
                }
                await database.SaveChangesAsync(cancellationToken);
            }

            foreach (var remote in snapshot)
            {
                if (byTmdbId.Remove(remote.TmdbId, out var local))
                {
                    (local.SeasonNumber, local.EpisodeNumber) = local.JellyfinItemId.HasValue
                        ? originalPositions[local.Id]
                        : (remote.SeasonNumber, remote.EpisodeNumber);
                    local.Title = remote.Title;
                    local.Overview = remote.Overview;
                    local.StillPath = remote.StillPath;
                    local.AirDate = remote.AirDate;
                    local.RuntimeMinutes = remote.RuntimeMinutes;
                }
                else if (byPosition.Remove((remote.SeasonNumber, remote.EpisodeNumber), out var positional))
                {
                    positional.TmdbId = remote.TmdbId;
                    (positional.SeasonNumber, positional.EpisodeNumber) = originalPositions[positional.Id];
                    positional.Title = remote.Title;
                    positional.Overview = remote.Overview;
                    positional.StillPath = remote.StillPath;
                    positional.AirDate = remote.AirDate;
                    positional.RuntimeMinutes = remote.RuntimeMinutes;
                }
                else
                {
                    remote.EntryId = entry.Id;
                    database.Episodes.Add(remote);
                }
            }

            // TMDB's series and season endpoints can briefly disagree for airing shows.
            // Preserve unmatched local episodes so a partial snapshot is never interpreted as deletion.
            foreach (var unmatched in byTmdbId.Values.Concat(byPosition.Values))
                (unmatched.SeasonNumber, unmatched.EpisodeNumber) = originalPositions[unmatched.Id];
            entry.Title = metadata.Title;
            entry.Year = metadata.PremiereDate?.Year;
            entry.ImdbId = metadata.ImdbId;
            entry.Overview = metadata.Overview;
            entry.PosterPath = metadata.PosterPath;
            entry.MetadataJson = JsonSerializer.Serialize(metadata);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            nativeSeriesId = entry.JellyfinItemId;
        }

        // New episodes bind through the one Phase 2 matcher, never a second one here.
        if (nativeSeriesId is { } seriesId && reconciliation is not null)
            await reconciliation.ReconcileAsync(seriesId, cancellationToken);
        return SeriesRefreshOutcome.Refreshed;
    }
}
