using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Manual release search and grab (P4.A3–A5). Administrator-only (P4.A1); every target is checked against the
/// requester's library and content access, and inaccessible targets answer with the concealed 404.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod")]
public sealed class ReleasesController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    AcquisitionConfiguration configuration,
    ReleaseSearchService search,
    GrabService grabs,
    GrabDispatcher dispatcher,
    GrabHoldOptions hold) : ControllerBase
{
    /// <summary>Searches enabled indexers for one movie entry or one episode. Never submits anything.</summary>
    [HttpGet("Releases")]
    public async Task<ActionResult<ReleaseSearchDto>> Search([FromQuery] Guid entryId, [FromQuery] Guid? episodeId,
        [FromQuery] Guid? profileId, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        Episode? episode = null;
        if (entry.MediaType == "series")
        {
            if (episodeId is null) return Error(400, "episode_required", "Choose an episode to search for.");
            episode = await database.Episodes.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == episodeId && value.EntryId == entry.Id, cancellationToken);
            if (episode is null || !access.CanReadEpisode(user, episode)) return NotFound();
        }
        else if (episodeId is not null) return Error(400, "episode_not_applicable", "Movies have no episodes.");

        var state = await configuration.GetStateAsync(database, cancellationToken);
        var inherited = profileId is null && entry.QualityProfileId is null;
        var chosenId = profileId ?? entry.QualityProfileId ?? state.Settings.DefaultQualityProfileId;
        if (chosenId is null) return Error(409, "no_quality_profile", "Choose a default quality profile in the plugin settings.");
        var profile = await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == chosenId, cancellationToken);
        if (profile is null) return Error(400, "invalid_quality_profile", "The quality profile does not exist.");
        if (!await database.AcquisitionIndexers.AnyAsync(value => value.Enabled, cancellationToken))
            return Error(409, "no_indexers", "No indexer is enabled in the plugin settings.");

        var snapshot = await search.SearchAsync(user.Id, Target(entry, episode),
            new EvaluationProfile(profile.Id, profile.Name, profile.Revision, AcquisitionConfiguration.Qualities(profile),
                profile.MinimumBytesPerHour, profile.MaximumBytesPerHour),
            inherited, state.Settings.Revision, cancellationToken);
        var targetKey = GrabService.TargetKey(entry.Id, episode?.Id);
        var active = await database.GrabOperations.AsNoTracking()
            .FirstOrDefaultAsync(value => value.ActiveTarget == targetKey, cancellationToken);
        var reason = !state.Settings.Enabled ? "acquisition_disabled"
            : !state.Ready ? state.Blockers[0]
            : active is not null ? "grab_active"
            : null;
        return ReleaseSearchDto.From(snapshot,
            new GrabAvailabilityDto(reason is null, reason, (int)Math.Ceiling(hold.Hold.TotalSeconds), active?.Id));
    }

    /// <summary>
    /// Grabs one eligible release from a search. The server derives target, client, locator and profile from its own
    /// records; the operation is held for the documented window before anything is sent (user decision 2).
    /// </summary>
    [HttpPost("Releases/Grab")]
    public async Task<ActionResult<GrabOperationDto>> Grab(GrabReleaseRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        try
        {
            var (operation, created) = await grabs.CreateAsync(user.Id,
                new GrabRequest(request.SearchId, request.ReleaseId, request.IdempotencyKey),
                (entry, episode) => CanAccess(user, entry, episode), cancellationToken);
            if (created) dispatcher.Schedule(operation.Id, operation.HoldUntil);
            var openUrl = await OpenUrlAsync(operation, cancellationToken);
            return StatusCode(created ? 202 : 200, GrabOperationDto.From(operation, openUrl));
        }
        catch (GrabException error)
        {
            return Error(error.Status, error.Code, error.Message, error.OperationId);
        }
    }

    /// <summary>Gets one grab operation.</summary>
    [HttpGet("Grabs/{id:guid}")]
    public async Task<ActionResult<GrabOperationDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var operation = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (operation is null || !await CanAccessAsync(user, operation, cancellationToken)) return NotFound();
        return GrabOperationDto.From(operation, await OpenUrlAsync(operation, cancellationToken));
    }

    /// <summary>Lists grabs that are still active or unresolved, for administrator follow-up.</summary>
    [HttpGet("Grabs")]
    public async Task<ActionResult<IReadOnlyList<GrabOperationDto>>> Active(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var operations = await database.GrabOperations.AsNoTracking()
            .Where(value => value.ActiveTarget != null || value.State == GrabStates.Unknown)
            .OrderByDescending(value => value.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var result = new List<GrabOperationDto>();
        foreach (var operation in operations)
            if (await CanAccessAsync(user, operation, cancellationToken))
                result.Add(GrabOperationDto.From(operation, await OpenUrlAsync(operation, cancellationToken)));
        return result;
    }

    /// <summary>Cancels a grab during its hold. Idempotent; 409 once submission has started.</summary>
    [HttpPost("Grabs/{id:guid}/Cancel")]
    public async Task<ActionResult<GrabOperationDto>> Cancel(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var visible = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (visible is null || !await CanAccessAsync(user, visible, cancellationToken)) return NotFound();
        try
        {
            return GrabOperationDto.From(await grabs.CancelAsync(id, user.Id, cancellationToken), null);
        }
        catch (GrabException error)
        {
            return Error(error.Status, error.Code, error.Message, error.OperationId);
        }
    }

    /// <summary>Resolves an uncertain or accepted grab by identity lookup in the client. Never resubmits.</summary>
    [HttpPost("Grabs/{id:guid}/Recheck")]
    public async Task<ActionResult<GrabOperationDto>> Recheck(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var visible = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (visible is null || !await CanAccessAsync(user, visible, cancellationToken)) return NotFound();
        var operation = await grabs.RecheckAsync(id, startup: false, cancellationToken);
        return operation is null ? NotFound() : GrabOperationDto.From(operation, await OpenUrlAsync(operation, cancellationToken));
    }

    private bool CanAccess(User user, Entry entry, Episode? episode) =>
        access.CanRead(user, entry) && (episode is null || access.CanReadEpisode(user, episode));

    private async Task<bool> CanAccessAsync(User user, GrabOperation operation, CancellationToken cancellationToken)
    {
        if (operation.EntryId is not { } entryId) return true;
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken);
        var episode = operation.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
            : null;
        return entry is not null && CanAccess(user, entry, episode);
    }

    private async Task<string?> OpenUrlAsync(GrabOperation operation, CancellationToken cancellationToken) =>
        await database.AcquisitionDownloadClients.AsNoTracking().Where(value => value.Id == operation.DownloadClientId)
            .Select(value => value.OpenUrl).SingleOrDefaultAsync(cancellationToken);

    private static ReleaseTarget Target(Entry entry, Episode? episode)
    {
        var metadata = entry.MetadataJson is { } json ? JsonSerializer.Deserialize<TmdbMetadata>(json) : null;
        return new ReleaseTarget(entry.Id, episode?.Id, entry.MediaType, entry.Title, metadata?.OriginalTitle, entry.Year,
            episode?.SeasonNumber, episode?.EpisodeNumber,
            // An episode's own runtime only: a series average would be a guessed duration (P4.A4).
            entry.MediaType == "series" ? episode?.RuntimeMinutes : metadata?.RuntimeMinutes,
            entry.ImdbId ?? metadata?.ImdbId, entry.TmdbId, metadata?.TvdbId);
    }

    private ObjectResult Error(int status, string code, string title, Guid? operationId = null)
    {
        var problem = new ProblemDetails { Status = status, Type = code, Title = title };
        if (operationId is { } id) problem.Extensions["operationId"] = id;
        return StatusCode(status, problem);
    }
}
