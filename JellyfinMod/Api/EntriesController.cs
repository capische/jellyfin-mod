using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>Authorized durable entries; no Phase 1 operation deletes media.</summary>
[ApiController, Authorize, Route("JellyfinMod/Entries")]
public sealed class EntriesController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    TmdbClient tmdb,
    ReconciliationLibraryLock libraryLock,
    RetentionExecutionGate retentionGate,
    RetentionEvaluator retentionEvaluator,
    CatalogSortName sortNames,
    JellyfinItemReconciliationRunner? reconciliation = null,
    IAuthorizationService? authorization = null,
    LibraryWriteBudget? writeBudget = null) : ControllerBase
{
    /// <summary>Lists accessible entries with exact totals after filters.</summary>
    [HttpGet]
    public async Task<ActionResult<EntriesResult>> List([FromQuery] string? mediaType, [FromQuery] Guid? targetLibraryId, [FromQuery] Guid? jellyfinItemId,
        [FromQuery] string? query, [FromQuery] string[]? state, [FromQuery] int startIndex = 0, [FromQuery] int limit = 100,
        [FromQuery] string sortBy = "SortName", [FromQuery] string sortOrder = "Ascending", CancellationToken cancellationToken = default)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (mediaType is not (null or "movie" or "series") || startIndex < 0 || limit is < 1 or > 200 || query?.Length > 200 ||
            sortBy is not ("SortName" or "DateCreated" or "ProductionYear") || sortOrder is not ("Ascending" or "Descending") ||
            state?.Any(value => !Enum.GetValues<FileState>().Any(s => FileStates.ToWire(s) == value)) == true) return BadRequest();
        if (targetLibraryId is { } libraryId && (mediaType is { } type
                ? !access.CanUseLibrary(user, type, libraryId)
                : !access.CanUseLibrary(user, libraryId))) return NotFound();
        var candidates = database.Entries.AsNoTracking().AsQueryable();
        if (mediaType is not null) candidates = candidates.Where(entry => entry.MediaType == mediaType);
        if (targetLibraryId.HasValue) candidates = candidates.Where(entry => entry.TargetLibraryId == targetLibraryId);
        // Any bound native copy finds its entry, not only the selected one (P1.P11).
        if (jellyfinItemId.HasValue)
            candidates = candidates.Where(entry => entry.JellyfinItemId == jellyfinItemId ||
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id && binding.JellyfinItemId == jellyfinItemId));
        var candidateRows = await candidates.ToListAsync(cancellationToken);
        var nativeIds = candidateRows.Where(entry => entry.JellyfinItemId.HasValue).Select(entry => entry.MediaType).Distinct()
            .ToDictionary(type => type, type => (IReadOnlySet<Guid>)access.GetNativeItems(user, type, targetLibraryId).Select(item => item.Id).ToHashSet());
        var visible = candidateRows.Where(entry => access.CanRead(user, entry, nativeIds.GetValueOrDefault(entry.MediaType) ?? new HashSet<Guid>()));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = sortNames.GetSearchKey(query);
            visible = visible.Where(entry => sortNames.GetSearchKey(entry.Title).Contains(search, StringComparison.Ordinal));
        }
        if (state?.Length > 0) visible = visible.Where(entry => state.Contains(FileStates.ToWire(entry.State)));
        var rows = visible.ToArray();
        Func<Entry, object?> key = sortBy switch { "DateCreated" => entry => entry.AddedAt, "ProductionYear" => entry => entry.Year, _ => entry => entry.Title };
        var ordered = sortOrder == "Descending" ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return new EntriesResult(ordered.ThenBy(entry => entry.Id).Skip(startIndex).Take(limit).Select(entry => new EntryDto(entry)).ToArray(), rows.Length);
    }

    /// <summary>Creates a title and episode snapshot atomically, or returns its unchanged existing entry.</summary>
    [HttpPost]
    public async Task<ActionResult<CreateEntryResult>> Create(CreateEntryRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (!access.CanUseLibrary(user, request.MediaType, request.TargetLibraryId)) return NotFound();
        var existing = await FindExisting(request, cancellationToken);
        if (existing is not null && access.CanRead(user, existing)) return new CreateEntryResult(new EntryDto(existing), false);
        // A hidden existing title answers exactly like a metadata-restricted one, after the same TMDB call, so
        // neither the body nor the timing reveals that it is held (P1.P11).
        var hiddenExisting = existing is not null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var metadata = await tmdb.GetDetailsAsync(request.MediaType, request.TmdbId, timeout.Token);
            var episodes = await tmdb.GetEpisodesAsync(metadata, timeout.Token);
            if (hiddenExisting) return NotFound();
            await using var lease = await libraryLock.TryAcquireAsync(request.TargetLibraryId, LibraryWait, timeout.Token);
            if (lease is null) return LibraryBusy();
            var owned = access.FindOwned(user, metadata, request.TargetLibraryId);
            if (owned is null && !access.CanReadMetadata(user, metadata)) return NotFound();
            existing = await FindExisting(request, timeout.Token);
            if (existing is not null)
                return await CompleteConcurrentAdd(existing, episodes, user, timeout.Token);
            var entry = new Entry
            {
                MediaType = request.MediaType, TmdbId = request.TmdbId, TargetLibraryId = request.TargetLibraryId,
                Title = metadata.Title, Year = metadata.PremiereDate?.Year, ImdbId = metadata.ImdbId,
                Overview = metadata.Overview, PosterPath = metadata.PosterPath, MetadataJson = JsonSerializer.Serialize(metadata),
                JellyfinItemId = owned?.Id, State = owned is null ? FileState.None : FileState.OnDisk,
                NativeRating = owned is null ? null : owned.CustomRating ?? owned.OfficialRating,
                NativeTagsJson = owned is null ? null
                    : JsonSerializer.Serialize((owned.Tags ?? []).Order(StringComparer.Ordinal).ToArray())
            };
            database.Entries.Add(entry);
            foreach (var episode in episodes) episode.EntryId = entry.Id;
            access.BindEpisodes(user, owned, episodes);
            database.Episodes.AddRange(episodes);
            database.History.Add(new HistoryRecord { EntryId = entry.Id, EventType = "added", Summary = "Added to library" });
            try
            {
                // EF commits entries, episodes and history in the same transaction.
                await database.SaveChangesAsync(timeout.Token);
            }
            catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                database.ChangeTracker.Clear();
                existing = await FindExisting(request, cancellationToken);
                if (existing is null) throw;
                return await CompleteConcurrentAdd(existing, episodes, user, cancellationToken);
            }

            return new CreateEntryResult(new EntryDto(entry), true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: 504, title: "TMDB metadata timed out. Please try again.");
        }
        catch (TmdbException error)
        {
            return Problem(statusCode: (int)error.StatusCode, title: error.Message);
        }
    }

    /// <summary>Refreshes a series metadata snapshot atomically while preserving durable local episode identity and settings.</summary>
    [HttpPost("{id:guid}/Refresh"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<EntryDetail>> Refresh(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var visible = await database.Entries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (visible is null || !access.CanManage(user, visible)) return NotFound();
        if (visible.MediaType != "series" || visible.TargetLibraryId is not { } libraryId) return BadRequest();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // Both remote snapshots are fully validated before any lock is taken or entity changed.
            var metadata = await tmdb.GetDetailsAsync(visible.MediaType, visible.TmdbId, timeout.Token);
            var snapshot = await tmdb.GetEpisodesAsync(metadata, timeout.Token);

            // Refresh changes metadata only. Availability, bindings and retention state belong to
            // reconciliation and retention, so it serializes with both and never sets them (P3.T10).
            Guid? nativeSeriesId;
            await using (await retentionGate.AcquireAsync(timeout.Token))
            await using (var libraryLease = await libraryLock.TryAcquireAsync(libraryId, LibraryWait, timeout.Token))
            {
                if (libraryLease is null) return LibraryBusy();
                database.ChangeTracker.Clear();
                var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, timeout.Token);
                if (entry is null) return NotFound();
                await using var transaction = await database.Database.BeginTransactionAsync(timeout.Token);
                var existing = await database.Episodes.Where(episode => episode.EntryId == entry.Id).ToListAsync(timeout.Token);
                var byTmdbId = existing.ToDictionary(episode => episode.TmdbId);
                // TMDB is identity; a bound episode keeps Jellyfin's display numbering, so Refresh and
                // reconciliation stop overwriting each other (P2.R9). Unmatched episodes keep theirs too.
                var originalPositions = existing.ToDictionary(episode => episode.Id,
                    episode => (episode.SeasonNumber, episode.EpisodeNumber));
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
                    await database.SaveChangesAsync(timeout.Token);
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
                    else
                    {
                        remote.EntryId = entry.Id;
                        database.Episodes.Add(remote);
                    }
                }

                // TMDB's series and season endpoints can briefly disagree for airing shows.
                // Preserve unmatched local episodes so a partial snapshot is never interpreted as deletion.
                foreach (var unmatched in byTmdbId.Values)
                    (unmatched.SeasonNumber, unmatched.EpisodeNumber) = originalPositions[unmatched.Id];
                entry.Title = metadata.Title;
                entry.Year = metadata.PremiereDate?.Year;
                entry.ImdbId = metadata.ImdbId;
                entry.Overview = metadata.Overview;
                entry.PosterPath = metadata.PosterPath;
                entry.MetadataJson = JsonSerializer.Serialize(metadata);
                await database.SaveChangesAsync(timeout.Token);
                await transaction.CommitAsync(timeout.Token);
                nativeSeriesId = entry.JellyfinItemId;
            }

            // New episodes bind through the one Phase 2 matcher, never a second one here.
            if (nativeSeriesId is { } seriesId && reconciliation is not null)
                await reconciliation.ReconcileAsync(seriesId, timeout.Token);
            database.ChangeTracker.Clear();
            var refreshed = await database.Entries.AsNoTracking().SingleAsync(e => e.Id == id, cancellationToken);
            return await BuildDetail(refreshed, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: 504, title: "TMDB metadata timed out. Please try again.");
        }
        catch (TmdbException error)
        {
            return Problem(statusCode: (int)error.StatusCode, title: error.Message);
        }
    }

    /// <summary>Gets an entry, history and tracked episodes without native IDs for inaccessible media.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EntryDetail>> Detail(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        // Administrators can open an entry whose library was removed, so they can Refresh or Remove it (P2.R7).
        if (entry is null || !access.CanRead(user, entry) &&
            !(!access.IsLiveLibrary(entry.TargetLibraryId) && await IsAdministratorAsync())) return NotFound();
        return await BuildDetail(entry, cancellationToken);
    }

    /// <summary>Updates admin-controlled future monitoring; unsupported settings are rejected.</summary>
    [HttpPatch("{id:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<EntryDto>> Patch(Guid id, PatchEntryRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        if (!request.Monitored.HasValue) return BadRequest();
        entry.Monitored = request.Monitored.Value;
        await database.SaveChangesAsync(cancellationToken);
        return new EntryDto(entry);
    }

    /// <summary>Exempts an entry, including every episode in a series, from automatic retention.</summary>
    [HttpPost("{id:guid}/Keep"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<EntryDto>> Keep(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var visible = await database.Entries.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == id, cancellationToken).ConfigureAwait(false);
        if (visible is null || !access.CanManage(user, visible)) return NotFound();
        if (!visible.TargetLibraryId.HasValue) return BadRequest();

        await using var executionLease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var libraryLease = await libraryLock.AcquireAsync(
            visible.TargetLibraryId.Value, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        var entry = await database.Entries.SingleOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken).ConfigureAwait(false);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        if (entry.RetentionPolicy == RetentionPolicy.Never) return new EntryDto(entry);

        entry.RetentionPolicy = RetentionPolicy.Never;
        database.History.Add(new HistoryRecord
        {
            EntryId = entry.Id,
            EventType = "retention_kept",
            Summary = entry.MediaType == "series"
                ? "Kept series and all episodes indefinitely"
                : "Kept media indefinitely"
        });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await retentionEvaluator.EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
        return new EntryDto(entry);
    }

    /// <summary>Updates an individual episode's future monitoring, restricted to administrators.</summary>
    [HttpPatch("{id:guid}/Episodes/{episodeId:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<EpisodeDto>> PatchEpisode(Guid id, Guid episodeId, PatchEntryRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        var episode = await database.Episodes.SingleOrDefaultAsync(e => e.EntryId == id && e.Id == episodeId, cancellationToken);
        if (episode is null || !access.CanReadEpisode(user, episode)) return NotFound();
        if (!request.Monitored.HasValue) return BadRequest();
        episode.Monitored = request.Monitored.Value;
        await database.SaveChangesAsync(cancellationToken);
        return new EpisodeDto(episode);
    }

    /// <summary>
    /// Removes only a catalog entry and its dependent records. File deletion is unavailable. Removal is
    /// refused while a retention operation is open or native media is still bound, because reconciliation
    /// would recreate the entry without its Keep or retention settings (P3.T10).
    /// </summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Remove(Guid id, [FromQuery] bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (deleteFiles) return BadRequest("File deletion is not available in Phase 1.");
        var visible = await database.Entries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (visible is null || !access.CanManage(user, visible) || visible.TargetLibraryId is not { } libraryId) return NotFound();

        // Serialized with retention and reconciliation, so a Remove can never race an unlink.
        await using var executionLease = await retentionGate.AcquireAsync(cancellationToken);
        await using var libraryLease = await libraryLock.AcquireAsync(libraryId, cancellationToken);
        database.ChangeTracker.Clear();
        var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null) return NotFound();
        if (await database.RetentionOperations.AnyAsync(operation => operation.EntryId == id &&
                (operation.State == RetentionOperationStates.Prepared || operation.State == RetentionOperationStates.Unlinked),
                cancellationToken))
            return Conflict(new ProblemDetails
            {
                Status = 409, Title = "Automatic removal of this title's media is still in progress. Try again later."
            });
        var episodeIds = await database.Episodes.Where(episode => episode.EntryId == id).Select(episode => episode.Id)
            .ToArrayAsync(cancellationToken);
        if (await database.EntryBindings.AnyAsync(binding => binding.EntryId == id, cancellationToken) ||
            await database.EpisodeBindings.AnyAsync(binding => episodeIds.Contains(binding.EpisodeId), cancellationToken))
            return Conflict(new ProblemDetails
            {
                Status = 409,
                Title = "This title still has media in the library. It would be added back without its settings, " +
                    "so it cannot be removed while the media exists."
            });

        database.Entries.Remove(entry);
        database.History.RemoveRange(database.History.Where(h => h.EntryId == id));
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult<CreateEntryResult>> CompleteConcurrentAdd(Entry existing,
        IReadOnlyList<Episode> snapshot, Jellyfin.Database.Implementations.Entities.User user,
        CancellationToken cancellationToken)
    {
        if (!access.CanRead(user, existing)) return NotFound();
        var episodes = await database.Episodes.Where(episode => episode.EntryId == existing.Id)
            .ToDictionaryAsync(episode => episode.TmdbId, cancellationToken);
        // Only the request which observed no entry initially completes its requested episode set.
        // A later duplicate request still returns early without changing administrator settings.
        foreach (var remote in snapshot)
        {
            if (episodes.TryGetValue(remote.TmdbId, out var local))
            {
                local.Monitored = true;
            }
            else
            {
                remote.EntryId = existing.Id;
                database.Episodes.Add(remote);
            }
        }

        if (!existing.Monitored)
        {
            existing.Monitored = true;
            database.History.Add(new HistoryRecord
            {
                EntryId = existing.Id,
                EventType = "monitoring_enabled",
                Summary = "Enabled monitoring when added by user"
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        return new CreateEntryResult(new EntryDto(existing), false);
    }

    private Task<Entry?> FindExisting(CreateEntryRequest request, CancellationToken cancellationToken) => database.Entries.SingleOrDefaultAsync(entry =>
        entry.MediaType == request.MediaType && entry.TmdbId == request.TmdbId && entry.TargetLibraryId == request.TargetLibraryId, cancellationToken);

    private TimeSpan LibraryWait => (writeBudget ?? LibraryWriteBudget.Default).Wait;

    /// <summary>
    /// A documented 503 with Retry-After when the library stays locked by reconciliation, so a slow check is
    /// never reported as a TMDB timeout (P2.R8).
    /// </summary>
    private ObjectResult LibraryBusy()
    {
        Response.Headers.RetryAfter = "30";
        return Problem(statusCode: 503, title: "The library is busy being checked. Try again in a moment.",
            type: "library_busy");
    }

    private async Task<bool> IsAdministratorAsync() => authorization is not null &&
        (await authorization.AuthorizeAsync(User, Policies.RequiresElevation)).Succeeded;

    private async Task<EntryDetail> BuildDetail(Entry entry, CancellationToken cancellationToken)
    {
        var user = access.GetUser(User)!;
        var history = await database.History.AsNoTracking().Where(h => h.EntryId == entry.Id).OrderByDescending(h => h.CreatedAt).ThenBy(h => h.Id).ToListAsync(cancellationToken);
        var episodes = await database.Episodes.AsNoTracking().Where(e => e.EntryId == entry.Id).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(cancellationToken);
        var targetIds = episodes.Select(episode => episode.Id).Append(entry.Id).ToArray();
        var evaluations = await database.RetentionEvaluations.AsNoTracking()
            .Where(evaluation => targetIds.Contains(evaluation.TargetId))
            .ToDictionaryAsync(evaluation => evaluation.TargetId, cancellationToken).ConfigureAwait(false);
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        var visibleEpisodeIds = new HashSet<Guid>();
        if (entry.JellyfinItemId is { } nativeId)
        {
            var series = access.GetNativeItems(user, "series", entry.TargetLibraryId).SingleOrDefault(item => item.Id == nativeId);
            if (series is not null) visibleEpisodeIds.UnionWith(access.GetEpisodes(user, series).Select(item => item.Id));
        }
        var isAdmin = await IsAdministratorAsync();
        var readableEpisodes = episodes.Where(e => LibraryAccess.CanReadEpisode(e, visibleEpisodeIds)).ToArray();
        var episodeDtos = readableEpisodes
            .Select(e => new EpisodeDto(e, RetentionSummaries.ForViewer(RetentionSummaries.ForTarget(entry, policy,
                evaluations.GetValueOrDefault(e.Id)), isAdmin))).ToArray();
        // A series aggregate is built only from episodes this requester may read (P3.T15).
        var readableTargets = readableEpisodes.Select(e => e.Id).Append(entry.Id).ToHashSet();
        var entryRetention = RetentionSummaries.ForViewer(RetentionSummaries.ForEntry(entry, policy,
            evaluations.Values.Where(evaluation => readableTargets.Contains(evaluation.TargetId))), isAdmin);
        return new EntryDetail(new EntryDto(entry), history.Select(h => new HistoryDto(h.Id, h.EntryId, h.EventType, h.Summary,
            DateTime.SpecifyKind(h.CreatedAt, DateTimeKind.Utc))).ToArray(), episodeDtos, entryRetention);
    }
}
