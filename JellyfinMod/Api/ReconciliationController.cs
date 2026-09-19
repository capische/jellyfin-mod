using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>Exposes bounded reconciliation diagnostics to administrators.</summary>
[ApiController, Route("JellyfinMod/Reconciliation"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class ReconciliationController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    ReconciliationLibraryLock libraryLock,
    RetentionExecutionGate retentionGate) : ControllerBase
{
    /// <summary>Gets the latest running or completed reconciliation summary.</summary>
    [HttpGet("Latest")]
    public async Task<ActionResult<ReconciliationRunDto>> Latest(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var run = await database.ReconciliationRuns.AsNoTracking().OrderByDescending(candidate => candidate.StartedAt)
            .ThenByDescending(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null) return NotFound();
        IReadOnlyList<ReconciliationDiagnostic> diagnostics = run.DiagnosticsJson is null
            ? []
            : JsonSerializer.Deserialize<ReconciliationDiagnostic[]>(run.DiagnosticsJson) ?? [];
        return new ReconciliationRunDto(run, diagnostics);
    }

    /// <summary>
    /// Lists entries whose target library is no longer a configured movie or TV library (P2.R7). They are
    /// hidden from ordinary users; reconciliation re-homes them when their media appears in another library.
    /// </summary>
    [HttpGet("Orphans")]
    public async Task<ActionResult<IReadOnlyList<OrphanedEntryDto>>> Orphans(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var entries = await database.Entries.AsNoTracking().OrderBy(entry => entry.Title)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(entries.Where(entry => !access.IsLiveLibrary(entry.TargetLibraryId))
            .Select(entry => new OrphanedEntryDto(entry.Id, entry.Title, entry.MediaType, entry.TmdbId,
                entry.TargetLibraryId, FileStates.ToWire(entry.State)))
            .ToArray());
    }

    /// <summary>Lists bound native episodes whose provider identity disagrees with their tracked episode (P2.R9).</summary>
    [HttpGet("Conflicts")]
    public async Task<ActionResult<IReadOnlyList<EpisodeConflictDto>>> Conflicts(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var rows = await (from conflict in database.EpisodeConflicts.AsNoTracking()
                join entry in database.Entries.AsNoTracking() on conflict.EntryId equals entry.Id
                join episode in database.Episodes.AsNoTracking() on conflict.EpisodeId equals episode.Id
                where conflict.State == EpisodeConflictStates.Open
                orderby entry.Title, episode.SeasonNumber, episode.EpisodeNumber
                select new EpisodeConflictDto(conflict.Id, entry.Id, entry.Title, conflict.JellyfinItemId,
                    episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, conflict.ObservedTmdbId,
                    conflict.ObservedSeasonNumber, conflict.ObservedEpisodeNumber))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>Moves the native episode to the identity Jellyfin now reports, writing one history event.</summary>
    [HttpPost("Conflicts/{id:guid}/Rebind")]
    public Task<IActionResult> Rebind(Guid id, CancellationToken cancellationToken) =>
        ResolveAsync(id, rebind: true, cancellationToken);

    /// <summary>Keeps the current identity until Jellyfin reports a different one, writing one history event.</summary>
    [HttpPost("Conflicts/{id:guid}/Keep")]
    public Task<IActionResult> Keep(Guid id, CancellationToken cancellationToken) =>
        ResolveAsync(id, rebind: false, cancellationToken);

    private async Task<IActionResult> ResolveAsync(Guid id, bool rebind, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var visible = await database.EpisodeConflicts.AsNoTracking().Where(conflict => conflict.Id == id)
            .Join(database.Entries.AsNoTracking(), conflict => conflict.EntryId, entry => entry.Id,
                (conflict, entry) => new { conflict.State, entry.TargetLibraryId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (visible is null || visible.State != EpisodeConflictStates.Open || visible.TargetLibraryId is not { } libraryId)
            return NotFound();

        // Serialized with retention and reconciliation, so a rebind never races an unlink or a scan.
        await using var executionLease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var libraryLease = await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false);
        var conflict = await database.EpisodeConflicts.SingleOrDefaultAsync(candidate => candidate.Id == id &&
            candidate.State == EpisodeConflictStates.Open, cancellationToken).ConfigureAwait(false);
        if (conflict is null) return NotFound();
        var tracked = await database.Episodes.SingleAsync(episode => episode.Id == conflict.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        var label = $"S{conflict.ObservedSeasonNumber:00}E{conflict.ObservedEpisodeNumber:00}";
        if (!rebind)
        {
            conflict.State = EpisodeConflictStates.Kept;
            database.History.Add(new HistoryRecord
            {
                EntryId = conflict.EntryId,
                EventType = "episode_conflict_kept",
                Summary = $"Kept S{tracked.SeasonNumber:00}E{tracked.EpisodeNumber:00} although Jellyfin reports {label}",
                Data = JsonSerializer.Serialize(new { episodeId = tracked.Id, trackedTmdbId = tracked.TmdbId,
                    observedTmdbId = conflict.ObservedTmdbId, jellyfinItemId = conflict.JellyfinItemId })
            });
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return NoContent();
        }

        var now = DateTime.UtcNow;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var target = await database.Episodes.SingleOrDefaultAsync(episode => episode.EntryId == conflict.EntryId &&
            episode.TmdbId == conflict.ObservedTmdbId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            target = new Episode
            {
                EntryId = conflict.EntryId, TmdbId = conflict.ObservedTmdbId,
                SeasonNumber = conflict.ObservedSeasonNumber, EpisodeNumber = conflict.ObservedEpisodeNumber,
                Monitored = false
            };
            database.Episodes.Add(target);
        }

        var binding = await database.EpisodeBindings.SingleOrDefaultAsync(candidate =>
            candidate.JellyfinItemId == conflict.JellyfinItemId, cancellationToken).ConfigureAwait(false);
        if (binding is not null) binding.EpisodeId = target.Id;
        target.JellyfinItemId = conflict.JellyfinItemId;
        target.State = FileState.OnDisk;
        if (tracked.JellyfinItemId == conflict.JellyfinItemId)
        {
            var remaining = await database.EpisodeBindings.FirstOrDefaultAsync(candidate => candidate.EpisodeId == tracked.Id &&
                candidate.JellyfinItemId != conflict.JellyfinItemId, cancellationToken).ConfigureAwait(false);
            tracked.JellyfinItemId = remaining?.JellyfinItemId;
            tracked.State = remaining is null ? FileState.None : FileState.OnDisk;
            if (remaining is null)
                await RetentionTargetReset.ResetAsync(database, conflict.EntryId, tracked.Id, now, cancellationToken)
                    .ConfigureAwait(false);
        }

        // The moved file starts a fresh retention clock on its new episode.
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RetentionTargetReset.ResetAsync(database, conflict.EntryId, target.Id, now, cancellationToken)
            .ConfigureAwait(false);
        database.History.Add(new HistoryRecord
        {
            EntryId = conflict.EntryId,
            EventType = "episode_conflict_rebound",
            Summary = $"Moved a native episode from S{tracked.SeasonNumber:00}E{tracked.EpisodeNumber:00} to {label}",
            Data = JsonSerializer.Serialize(new { fromEpisodeId = tracked.Id, toEpisodeId = target.Id,
                fromTmdbId = tracked.TmdbId, toTmdbId = conflict.ObservedTmdbId, jellyfinItemId = conflict.JellyfinItemId })
        });
        database.EpisodeConflicts.Remove(conflict);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
