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
public sealed class EntriesController(ModDbContext database, DatabaseInitializer readiness, LibraryAccess access, TmdbClient tmdb) : ControllerBase
{
    /// <summary>Lists accessible entries with exact totals after filters.</summary>
    [HttpGet]
    public async Task<ActionResult<EntriesResult>> List([FromQuery] string? mediaType, [FromQuery] Guid? targetLibraryId,
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
        var visible = (await candidates.ToListAsync(cancellationToken)).Where(entry => access.CanRead(user, entry));
        if (!string.IsNullOrWhiteSpace(query)) visible = visible.Where(entry => entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
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
        if (existing is not null) return access.CanRead(user, existing) ? new CreateEntryResult(new EntryDto(existing), false) : NotFound();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var metadata = await tmdb.GetDetailsAsync(request.MediaType, request.TmdbId, timeout.Token);
            var owned = access.FindOwned(user, metadata, request.TargetLibraryId);
            if (owned is null && !access.CanReadMetadata(user, metadata)) return NotFound();
            var episodes = await tmdb.GetEpisodesAsync(metadata, timeout.Token);
            var entry = new Entry
            {
                MediaType = request.MediaType, TmdbId = request.TmdbId, TargetLibraryId = request.TargetLibraryId,
                Title = metadata.Title, Year = metadata.PremiereDate?.Year, ImdbId = metadata.ImdbId,
                Overview = metadata.Overview, PosterPath = metadata.PosterPath, MetadataJson = JsonSerializer.Serialize(metadata),
                JellyfinItemId = owned?.Id, State = owned is null ? FileState.None : FileState.OnDisk
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
                return access.CanRead(user, existing) ? new CreateEntryResult(new EntryDto(existing), false) : NotFound();
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
        var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        if (entry.MediaType != "series" || entry.TargetLibraryId is not { } libraryId) return BadRequest();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // Both remote snapshots are fully validated before any tracked entity is changed.
            var metadata = await tmdb.GetDetailsAsync(entry.MediaType, entry.TmdbId, timeout.Token);
            var snapshot = await tmdb.GetEpisodesAsync(metadata, timeout.Token);
            var existing = await database.Episodes.Where(episode => episode.EntryId == entry.Id).ToListAsync(timeout.Token);
            var byTmdbId = existing.ToDictionary(episode => episode.TmdbId);
            foreach (var remote in snapshot)
            {
                if (byTmdbId.Remove(remote.TmdbId, out var local))
                {
                    local.SeasonNumber = remote.SeasonNumber;
                    local.EpisodeNumber = remote.EpisodeNumber;
                    local.Title = remote.Title;
                    local.Overview = remote.Overview;
                    local.StillPath = remote.StillPath;
                    local.AirDate = remote.AirDate;
                    local.RuntimeMinutes = remote.RuntimeMinutes;
                    local.JellyfinItemId = null;
                    if (local.State == FileState.OnDisk) local.State = FileState.None;
                }
                else
                {
                    remote.EntryId = entry.Id;
                    database.Episodes.Add(remote);
                }
            }

            database.Episodes.RemoveRange(byTmdbId.Values);
            entry.Title = metadata.Title;
            entry.Year = metadata.PremiereDate?.Year;
            entry.ImdbId = metadata.ImdbId;
            entry.Overview = metadata.Overview;
            entry.PosterPath = metadata.PosterPath;
            entry.MetadataJson = JsonSerializer.Serialize(metadata);
            var owned = access.FindOwned(user, metadata, libraryId);
            entry.JellyfinItemId = owned?.Id;
            entry.State = owned is null ? FileState.None : FileState.OnDisk;
            access.BindEpisodes(user, owned, existing.Where(episode => !byTmdbId.ContainsKey(episode.TmdbId))
                .Concat(snapshot.Where(remote => !existing.Any(local => local.TmdbId == remote.TmdbId))).ToArray());
            await database.SaveChangesAsync(timeout.Token);
            return await BuildDetail(entry, cancellationToken);
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
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
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

    /// <summary>Removes only a catalog entry and its dependent records. File deletion is unavailable.</summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Remove(Guid id, [FromQuery] bool deleteFiles = false, CancellationToken cancellationToken = default)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (deleteFiles) return BadRequest("File deletion is not available in Phase 1.");
        var entry = await database.Entries.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        database.Entries.Remove(entry);
        database.History.RemoveRange(database.History.Where(h => h.EntryId == id));
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private Task<Entry?> FindExisting(CreateEntryRequest request, CancellationToken cancellationToken) => database.Entries.SingleOrDefaultAsync(entry =>
        entry.MediaType == request.MediaType && entry.TmdbId == request.TmdbId && entry.TargetLibraryId == request.TargetLibraryId, cancellationToken);

    private async Task<EntryDetail> BuildDetail(Entry entry, CancellationToken cancellationToken)
    {
        var user = access.GetUser(User)!;
        var history = await database.History.AsNoTracking().Where(h => h.EntryId == entry.Id).OrderByDescending(h => h.CreatedAt).ThenBy(h => h.Id).ToListAsync(cancellationToken);
        var episodes = await database.Episodes.AsNoTracking().Where(e => e.EntryId == entry.Id).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(cancellationToken);
        return new EntryDetail(new EntryDto(entry), history.Select(h => new HistoryDto(h.Id, h.EntryId, h.EventType, h.Summary,
            DateTime.SpecifyKind(h.CreatedAt, DateTimeKind.Utc))).ToArray(), episodes.Where(e => access.CanReadEpisode(user, e)).Select(e => new EpisodeDto(e)).ToArray());
    }
}
