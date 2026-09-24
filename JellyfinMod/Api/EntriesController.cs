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
    LibraryWriteBudget? writeBudget = null,
    JellyfinMod.Services.Import.ClientSnapshotCache? snapshots = null,
    MediaBrowser.Controller.Library.IMediaSourceManager? mediaSources = null,
    UnixFileInspector? files = null,
    TimeProvider? clock = null) : ControllerBase
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
        // Any bound native copy finds its entry, not only the selected one (P1.P11); a native episode finds its
        // series, and a reclaimed native item finds the entry it belonged to through the reclaim audit (P3.T14).
        if (jellyfinItemId.HasValue)
            candidates = candidates.Where(entry => entry.JellyfinItemId == jellyfinItemId ||
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id && binding.JellyfinItemId == jellyfinItemId) ||
                database.Episodes.Any(episode => episode.EntryId == entry.Id && (episode.JellyfinItemId == jellyfinItemId ||
                    database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id && binding.JellyfinItemId == jellyfinItemId))) ||
                database.RetentionOperations.Any(operation => operation.EntryId == entry.Id &&
                    operation.JellyfinItemId == jellyfinItemId && operation.State == RetentionOperationStates.Completed));
        var candidateRows = await candidates.ToListAsync(cancellationToken);
        var nativeIds = candidateRows.Where(entry => entry.JellyfinItemId.HasValue).Select(entry => entry.MediaType).Distinct()
            .ToDictionary(type => type, type => (IReadOnlySet<Guid>)access.GetNativeItems(user, type, targetLibraryId).Select(item => item.Id).ToHashSet());
        var visible = candidateRows.Where(entry => access.CanRead(user, entry, nativeIds.GetValueOrDefault(entry.MediaType) ?? new HashSet<Guid>()));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = sortNames.GetSearchKey(query);
            visible = visible.Where(entry => sortNames.GetSearchKey(entry.Title).Contains(search, StringComparison.Ordinal));
        }
        var visibleRows = visible.ToArray();
        var projections = await JellyfinMod.Services.Import.QueueReadModel.ProjectAsync(database, snapshots,
            visibleRows.Select(entry => entry.Id).ToArray(), cancellationToken);
        // The File filter follows the projected state, so a downloading title is found under Downloading (PHASE4 A6 (d)).
        var rows = visibleRows.Select(entry => new EntryDto(entry, projections.GetValueOrDefault(entry.Id)))
            .Where(dto => state is not { Length: > 0 } || state.Contains(dto.State)).ToArray();
        Func<EntryDto, object?> key = sortBy switch { "DateCreated" => entry => entry.AddedAt, "ProductionYear" => entry => entry.Year, _ => entry => entry.Title };
        var ordered = sortOrder == "Descending" ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return new EntriesResult(ordered.ThenBy(entry => entry.Id).Skip(startIndex).Take(limit).ToArray(), rows.Length);
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
            var outcome = await new SeriesMetadataRefresher(database, tmdb, libraryLock, retentionGate, reconciliation, writeBudget)
                .RefreshAsync(id, timeout.Token);
            if (outcome == SeriesRefreshOutcome.LibraryBusy) return LibraryBusy();
            if (outcome == SeriesRefreshOutcome.NotFound) return NotFound();
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
        if (!request.Monitored.HasValue && !request.QualityProfileIdSpecified && request.SearchNow != true) return BadRequest();
        if (request.QualityProfileId is { } profileId &&
            !await database.AcquisitionQualityProfiles.AnyAsync(profile => profile.Id == profileId, cancellationToken))
            return BadRequest(new ProblemDetails { Status = 400, Type = "invalid_quality_profile", Title = "The quality profile does not exist." });
        if (request.Monitored.HasValue) entry.Monitored = request.Monitored.Value;
        // Saving a profile is a separate administrator action; searches never change it (P4.A1).
        if (request.QualityProfileIdSpecified) entry.QualityProfileId = request.QualityProfileId;
        if (request.SearchNow == true)
        {
            // A series asks for each of its episodes that still lacks a file.
            List<(Guid TargetId, Guid? EpisodeId)> targets = entry.MediaType == "movie"
                ? [(entry.Id, null)]
                : (await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entry.Id && episode.State != FileState.OnDisk)
                    .Select(episode => episode.Id).ToListAsync(cancellationToken)).Select(episodeId => (episodeId, (Guid?)episodeId)).ToList();
            foreach (var (targetId, episodeId) in targets)
                await RequestSearchAsync(entry.Id, targetId, episodeId, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return new EntryDto(entry);
    }

    /// <summary>Resets a target's backoff and asks the next automation run to search it (P6.M3).</summary>
    private async Task RequestSearchAsync(Guid entryId, Guid targetId, Guid? episodeId, CancellationToken cancellationToken)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var row = await database.AutomationTargets.SingleOrDefaultAsync(value => value.TargetId == targetId, cancellationToken);
        if (row is null)
        {
            row = new AutomationTargetState { TargetId = targetId, EntryId = entryId, EpisodeId = episodeId };
            database.AutomationTargets.Add(row);
        }

        row.SearchNowRequestedAt = now;
        row.NextSearchAt = now;
        row.ConsecutiveEmpty = 0;
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

    /// <summary>
    /// Exempts one episode, every version of it, from automatic retention (P10.E2). Administrators only; one action, no
    /// confirmation, idempotent, one history event on change. Keep on the series already covers every episode.
    /// </summary>
    [HttpPost("{id:guid}/Episodes/{episodeId:guid}/Keep"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<EpisodeDto>> KeepEpisode(Guid id, Guid episodeId, CancellationToken cancellationToken) =>
        ChangeEpisodeRetentionAsync(id, episodeId, RetentionPolicy.Never, null, cancellationToken);

    /// <summary>
    /// Stops keeping one episode (PHASE10 Q4, answered 2026-09-24). Its own window, if it has one, applies again, else its
    /// series'; its grace restarts now rather than reusing an old deadline. A kept series still keeps the episode.
    /// </summary>
    [HttpDelete("{id:guid}/Episodes/{episodeId:guid}/Keep"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<EpisodeDto>> UnkeepEpisode(Guid id, Guid episodeId, CancellationToken cancellationToken) =>
        ChangeEpisodeRetentionAsync(id, episodeId, null, null, cancellationToken);

    /// <summary>
    /// Sets one episode's own retention: inherit its series' window, its own number of days, or never (PHASE10 Q4). The
    /// episode's grace restarts now. Series Keep wins over any episode window (PHASE10 Q2).
    /// </summary>
    [HttpPut("{id:guid}/Episodes/{episodeId:guid}/Retention"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<EpisodeDto>> SetEpisodeRetention(Guid id, Guid episodeId, EpisodeRetentionRequest request,
        CancellationToken cancellationToken)
    {
        RetentionPolicy? policy = request.Policy switch
        {
            "inherit" => RetentionPolicy.Inherit,
            "days" => RetentionPolicy.Days,
            "never" => RetentionPolicy.Never,
            _ => null
        };
        if (policy is null || policy == RetentionPolicy.Days && request.ReclaimAfterDays is not (>= 1 and <= 3650) ||
            policy != RetentionPolicy.Days && request.ReclaimAfterDays is not null)
            return BadRequest(new ProblemDetails { Status = 400, Type = "invalid_retention",
                Title = "Choose inherit, never, or a number of days from 1 to 3650." });
        return await ChangeEpisodeRetentionAsync(id, episodeId, policy, request.ReclaimAfterDays, cancellationToken);
    }

    /// <summary>
    /// Applies an administrator's episode retention change under the retention gate and the library lock, so a change that
    /// returns has won any race with an unlink of this episode. <paramref name="policy"/> null means un-Keep.
    /// </summary>
    private async Task<ActionResult<EpisodeDto>> ChangeEpisodeRetentionAsync(Guid id, Guid episodeId, RetentionPolicy? policy,
        int? days, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var visible = await database.Entries.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == id, cancellationToken).ConfigureAwait(false);
        if (visible is null || !access.CanManage(user, visible) || visible.MediaType != "series") return NotFound();
        if (!visible.TargetLibraryId.HasValue) return BadRequest();

        await using var executionLease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var libraryLease = await libraryLock.AcquireAsync(
            visible.TargetLibraryId.Value, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        var entry = await database.Entries.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        var episode = await database.Episodes.SingleOrDefaultAsync(candidate => candidate.EntryId == id &&
            candidate.Id == episodeId, cancellationToken).ConfigureAwait(false);
        if (episode is null || !access.CanReadEpisode(user, episode)) return NotFound();

        var keeping = policy == RetentionPolicy.Never && days is null;
        var targetPolicy = policy ?? (episode.ReclaimAfterDays is > 0 ? RetentionPolicy.Days : RetentionPolicy.Inherit);
        if (policy is null && episode.RetentionPolicy != RetentionPolicy.Never) targetPolicy = episode.RetentionPolicy;
        var targetDays = policy == RetentionPolicy.Days ? days : policy is null ? episode.ReclaimAfterDays
            : policy == RetentionPolicy.Never ? episode.ReclaimAfterDays : null;
        if (episode.RetentionPolicy != targetPolicy || episode.ReclaimAfterDays != targetDays)
        {
            var label = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
            var (eventType, summary) = targetPolicy switch
            {
                RetentionPolicy.Never => ("episode_kept", $"Kept {label} indefinitely"),
                _ when episode.RetentionPolicy == RetentionPolicy.Never && policy is null =>
                    ("episode_unkept", $"Stopped keeping {label}"),
                RetentionPolicy.Days => ("episode_retention_changed", $"{label} is removed {targetDays} days after watching"),
                _ => ("episode_retention_changed", $"{label} follows its series' retention")
            };
            episode.RetentionPolicy = targetPolicy;
            episode.ReclaimAfterDays = targetDays;
            database.History.Add(new HistoryRecord
            {
                EntryId = entry.Id,
                EventType = eventType,
                Summary = summary,
                Data = JsonSerializer.Serialize(new { episodeId = episode.Id, episode.SeasonNumber, episode.EpisodeNumber,
                    policy = RetentionPolicies.ToWire(targetPolicy), reclaimAfterDays = targetDays })
            });
            // Anything but a Keep restarts the episode's grace now: a changed window never reuses a deadline computed
            // under the old one, so shortening it cannot make the episode due at once (P3.T13).
            if (!keeping)
            {
                var evaluation = await database.RetentionEvaluations.SingleOrDefaultAsync(
                    item => item.TargetId == episode.Id, cancellationToken).ConfigureAwait(false);
                if (evaluation is not null && evaluation.State == RetentionEvaluationStates.Scheduled)
                {
                    evaluation.State = RetentionEvaluationStates.Waiting;
                    evaluation.Reason = RetentionEvaluationReasons.WaitingForCompletion;
                    evaluation.Deadline = null;
                    evaluation.EligibleAt = null;
                    evaluation.CompletionBasisAt = null;
                }
            }

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await retentionEvaluator.EvaluateEpisodeAsync(episode.Id, cancellationToken).ConfigureAwait(false);
        var snapshot = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        var current = await database.RetentionEvaluations.AsNoTracking().SingleOrDefaultAsync(
            item => item.TargetId == episode.Id, cancellationToken).ConfigureAwait(false);
        return new EpisodeDto(episode, RetentionSummaries.ForTarget(entry, snapshot, current, episode));
    }

    /// <summary>
    /// Keeps one file of a movie or an episode while its other versions may go (PHASE10 Q3, answered 2026-09-24).
    /// Administrators only; idempotent, one history event on change. The Keep follows the file's path and identity.
    /// </summary>
    [HttpPost("{id:guid}/Versions/{bindingId:guid}/Keep"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<VersionKeepResult>> KeepVersion(Guid id, Guid bindingId, CancellationToken cancellationToken) =>
        ChangeVersionKeepAsync(id, bindingId, true, cancellationToken);

    /// <summary>Stops keeping one file; it follows its title's retention again (PHASE10 Q3, Q4).</summary>
    [HttpDelete("{id:guid}/Versions/{bindingId:guid}/Keep"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<ActionResult<VersionKeepResult>> UnkeepVersion(Guid id, Guid bindingId, CancellationToken cancellationToken) =>
        ChangeVersionKeepAsync(id, bindingId, false, cancellationToken);

    private async Task<ActionResult<VersionKeepResult>> ChangeVersionKeepAsync(Guid id, Guid bindingId, bool keep,
        CancellationToken cancellationToken)
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
        var entry = await database.Entries.SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        string? path;
        Episode? episode = null;
        if (entry.MediaType == "movie")
        {
            path = await database.EntryBindings.AsNoTracking().Where(binding => binding.Id == bindingId && binding.EntryId == id)
                .Select(binding => binding.MediaPath).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var binding = await database.EpisodeBindings.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == bindingId, cancellationToken).ConfigureAwait(false);
            episode = binding is null ? null : await database.Episodes.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == binding.EpisodeId && candidate.EntryId == id, cancellationToken).ConfigureAwait(false);
            if (episode is not null && !access.CanReadEpisode(user, episode)) episode = null;
            path = episode is null ? null : binding!.MediaPath;
        }

        if (string.IsNullOrEmpty(path)) return NotFound();
        var inspector = files ?? new UnixFileInspector();
        var identity = inspector.TryInspect(path, out var observed) ? observed.PhysicalIdentity : null;
        var existing = await database.VersionKeeps.Where(candidate => candidate.EntryId == id && (candidate.MediaPath == path ||
                identity != null && candidate.PhysicalIdentity == identity)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var name = Path.GetFileName(path);
        if (keep && existing.Count == 0)
        {
            database.VersionKeeps.Add(new VersionKeep { EntryId = id, EpisodeId = episode?.Id, MediaPath = path,
                PhysicalIdentity = identity, CreatedAt = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime });
            database.History.Add(new HistoryRecord { EntryId = id, EventType = "version_kept", Summary = $"Kept {name} indefinitely",
                Data = JsonSerializer.Serialize(new { episodeId = episode?.Id, bindingId }) });
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!keep && existing.Count > 0)
        {
            database.VersionKeeps.RemoveRange(existing);
            database.History.Add(new HistoryRecord { EntryId = id, EventType = "version_unkept", Summary = $"Stopped keeping {name}",
                Data = JsonSerializer.Serialize(new { episodeId = episode?.Id, bindingId }) });
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new VersionKeepResult(bindingId, keep);
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
        // Episodes inherit their series profile; monitoring and a search request are the episode settings.
        if (!request.Monitored.HasValue && request.SearchNow != true || request.QualityProfileIdSpecified) return BadRequest();
        if (request.Monitored.HasValue) episode.Monitored = request.Monitored.Value;
        if (request.SearchNow == true) await RequestSearchAsync(id, episode.Id, episode.Id, cancellationToken);
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
        // A grab still owning a client torrent keeps its title: removal would orphan the client association (P4.A6).
        if (await database.GrabOperations.AnyAsync(operation => operation.EntryId == id && operation.ActiveTarget != null,
                cancellationToken))
            return Conflict(new ProblemDetails
            {
                Status = 409, Type = "grab_active",
                Title = "A grab for this title is still active. Resolve it in the download client, then recheck it."
            });
        // An open import or seeding copy keeps its title: removal would orphan the library link or the torrent (P5.I7).
        if (await database.ImportOperations.AnyAsync(operation => operation.EntryId == id &&
                JellyfinMod.Data.ImportStates.Open.Contains(operation.State), cancellationToken) ||
            await database.SeedReleaseOperations.AnyAsync(operation => operation.EntryId == id &&
                JellyfinMod.Data.SeedReleaseStates.Open.Contains(operation.State), cancellationToken))
            return Conflict(new ProblemDetails
            {
                Status = 409, Type = "import_active",
                Title = "This title is still downloading, importing or seeding. Remove it from the queue first."
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
        var tracked = await database.Episodes.Where(episode => episode.EntryId == existing.Id).ToListAsync(cancellationToken);
        var episodes = tracked.Where(episode => !episode.IsPositionIdentity).ToDictionary(episode => episode.TmdbId);
        // An episode tracked by its position (P10.E1) came from a library file whose numbering may not be TMDB's. An Add
        // is no evidence that the TMDB episode listed at that position is that file, and adding an existing title is no
        // wish to search for it (RET-R2): the position row is left exactly as it is, and no second row is created there.
        var heldPositions = tracked.Where(episode => episode.IsPositionIdentity)
            .Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)).ToHashSet();
        // Only the request which observed no entry initially completes its requested episode set.
        // A later duplicate request still returns early without changing administrator settings.
        foreach (var remote in snapshot)
        {
            if (episodes.TryGetValue(remote.TmdbId, out var local))
            {
                local.Monitored = true;
            }
            else if (!heldPositions.Contains((remote.SeasonNumber, remote.EpisodeNumber)))
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

    /// <summary>
    /// The running retention windows of a title's movie or episodes (PHASE10 Q8): a scheduled evaluation whose target is not
    /// kept, with the cause the window-start event recorded and the files that will go, which leaves out kept files. Every
    /// viewer gets the date (PHASE10 Q11 overrides the T15 date hiding for this warning only).
    /// </summary>
    private async Task<Dictionary<Guid, RetentionWarningDto>> RetentionWarningsAsync(Entry entry, IReadOnlyList<Episode> episodes,
        RetentionPolicySnapshot? policy, IReadOnlyDictionary<Guid, RetentionEvaluation> evaluations, IReadOnlyList<HistoryRecord> history,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, RetentionWarningDto>();
        if (policy?.Enabled != true) return result;
        var running = evaluations.Values.Where(evaluation => evaluation.State == RetentionEvaluationStates.Scheduled &&
            evaluation.Deadline.HasValue).ToArray();
        if (running.Length == 0) return result;
        var episodeIds = episodes.Select(episode => episode.Id).ToArray();
        var paths = entry.MediaType == "movie"
            ? (await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entry.Id)
                .Select(binding => new { TargetId = binding.EntryId, binding.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false))
            : (await database.EpisodeBindings.AsNoTracking().Where(binding => episodeIds.Contains(binding.EpisodeId))
                .Select(binding => new { TargetId = binding.EpisodeId, binding.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false));
        var kept = (await database.VersionKeeps.AsNoTracking().Where(keep => keep.EntryId == entry.Id)
            .Select(keep => keep.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        foreach (var evaluation in running)
        {
            var episode = episodes.FirstOrDefault(candidate => candidate.Id == evaluation.TargetId);
            if (evaluation.TargetId != entry.Id && episode is null) continue;
            if (RetentionOverrides.IsKept(entry, episode)) continue;
            var files = RetentionFileNames.Distinct(paths.Where(path => path.TargetId == evaluation.TargetId && path.MediaPath is not null &&
                !kept.Contains(path.MediaPath)).Select(path => path.MediaPath!));
            if (files.Length == 0) continue;
            var started = history.Where(record => record.EventType == "retention_started" &&
                    HistoryDto.EpisodeOf(record.Data) == episode?.Id)
                .OrderByDescending(record => record.CreatedAt).FirstOrDefault();
            result[evaluation.TargetId] = new RetentionWarningDto(DateTime.SpecifyKind(evaluation.Deadline!.Value, DateTimeKind.Utc),
                CauseOf(started?.Data) ?? "watched", files);
        }

        return result;
    }

    private static string? CauseOf(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("cause", out var cause) && cause.ValueKind == JsonValueKind.String
                ? cause.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
        // The newest grab per target supplies the acquisition summary; file availability stays separate (P4.A6).
        var grabs = await database.GrabOperations.AsNoTracking().Where(operation => operation.EntryId == entry.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var latestGrab = grabs.GroupBy(operation => operation.EpisodeId)
            .ToDictionary(group => group.Key ?? Guid.Empty, group => group.OrderByDescending(operation => operation.CreatedAt).First());
        AcquisitionSummaryDto? Summary(Guid? targetId) => latestGrab.TryGetValue(targetId ?? Guid.Empty, out var operation)
            ? AcquisitionSummaryDto.From(operation, isAdmin) : null;
        var projections = await JellyfinMod.Services.Import.QueueReadModel.ProjectAsync(database, snapshots, [entry.Id], cancellationToken)
            .ConfigureAwait(false);
        var versions = new JellyfinMod.Services.Automation.VersionReader(database, mediaSources, files ?? new UnixFileInspector());
        var warnings = await RetentionWarningsAsync(entry, episodes, policy, evaluations, history, cancellationToken).ConfigureAwait(false);
        var episodeDtos = new List<EpisodeDto>();
        foreach (var e in readableEpisodes)
            episodeDtos.Add(new EpisodeDto(e, RetentionSummaries.ForViewer(RetentionSummaries.ForTarget(entry, policy,
                evaluations.GetValueOrDefault(e.Id), e), isAdmin), Summary(e.Id), projections.GetValueOrDefault(e.Id))
            {
                RetentionWarning = warnings.GetValueOrDefault(e.Id),
                Versions = e.State == FileState.OnDisk
                    ? await versions.ForAsync(entry.Id, e.Id, evaluations.GetValueOrDefault(e.Id), isAdmin, cancellationToken)
                    : []
            });
        // A series aggregate is built only from episodes this requester may read (P3.T15).
        var readableTargets = readableEpisodes.Select(e => e.Id).Append(entry.Id).ToHashSet();
        var entryRetention = RetentionSummaries.ForViewer(RetentionSummaries.ForEntry(entry, policy,
            evaluations.Values.Where(evaluation => readableTargets.Contains(evaluation.TargetId))), isAdmin);
        return new EntryDetail(new EntryDto(entry, projections.GetValueOrDefault(entry.Id)), history.Select(h => new HistoryDto(h.Id, h.EntryId, h.EventType, h.Summary,
            DateTime.SpecifyKind(h.CreatedAt, DateTimeKind.Utc)) { EpisodeId = HistoryDto.EpisodeOf(h.Data) }).ToArray(), episodeDtos, entryRetention,
            entry.MediaType == "movie" ? Summary(null) : null)
        {
            Versions = entry.MediaType == "movie" && entry.State == FileState.OnDisk
                ? await versions.ForAsync(entry.Id, null, evaluations.GetValueOrDefault(entry.Id), isAdmin, cancellationToken)
                : [],
            Upgrade = isAdmin && entry.MediaType == "movie" ? await versions.UpgradeAsync(entry, cancellationToken) : null,
            RetentionWarning = entry.MediaType == "movie" ? warnings.GetValueOrDefault(entry.Id) : null
        };
    }
}
