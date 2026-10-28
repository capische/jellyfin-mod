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
    GrabHoldOptions hold,
    MediaBrowser.Controller.Library.ILibraryManager? library = null) : ControllerBase
{
    /// <summary>
    /// Searches enabled indexers for one movie entry or one episode. Never submits anything. <paramref name="indexerIds"/> (repeated)
    /// asks only those of the enabled indexers, as the picker's Sources choose (P4.FU25); without it every enabled one is asked.
    /// </summary>
    [HttpGet("Releases")]
    public async Task<ActionResult<ReleaseSearchDto>> Search([FromQuery] Guid entryId, [FromQuery] Guid? episodeId,
        [FromQuery] Guid? profileId, [FromQuery] string? intent, CancellationToken cancellationToken,
        [FromQuery] string? scope = null, [FromQuery] int? seasonNumber = null, [FromQuery] string? progressKey = null,
        [FromQuery] Guid[]? indexerIds = null)
    {
        var chosen = indexerIds is { Length: > 0 } ? indexerIds.Distinct().ToArray() : null;
        intent ??= GrabIntents.Acquire;
        if (progressKey is not null && !ReleaseProgressTable.IsValidKey(progressKey))
            return Error(400, "invalid_progress_key", "The progress key must be 1 to 64 letters, digits, dashes or underscores.");
        if (intent is not (GrabIntents.Acquire or GrabIntents.AddVersion))
            return Error(400, "invalid_intent", "The intent must be acquire or addVersion.");
        if (scope is not (null or ReleaseScopes.Episode or ReleaseScopes.Season or ReleaseScopes.Series))
            return Error(400, "invalid_scope", "The scope must be episode, season or series.");
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        if (ReleaseScopes.IsPack(scope))
            return await SearchPackAsync(user, entry, scope!, seasonNumber, episodeId, profileId, intent, progressKey, chosen, cancellationToken);
        if (seasonNumber is not null) return Error(400, "invalid_scope", "A season number belongs to a season search.");
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
        // Another quality is only for a title that already plays (P6.M6). Adding one by hand never needs episode upgrades; only
        // Replace does, so with upgrades off the search offers Add alone (user, 2026-10-09).
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (intent == GrabIntents.AddVersion)
        {
            var playable = episode is null ? entry.State == FileState.OnDisk : episode.State == FileState.OnDisk;
            if (!playable) return Error(409, "no_playable_version", "Another quality can only be added to a title that has a file.");
            held = (await JellyfinMod.Services.Automation.VersionQuality.HeldAsync(database, entry.Id, episode?.Id, cancellationToken, library))
                .Select(version => version.Quality).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }

        var inherited = profileId is null && entry.QualityProfileId is null;
        var chosenId = profileId ?? entry.QualityProfileId ?? state.Settings.DefaultQualityProfileId;
        if (chosenId is null) return Error(409, "no_quality_profile", "Choose a default quality profile in the plugin settings.");
        var profile = await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == chosenId, cancellationToken);
        if (profile is null) return Error(400, "invalid_quality_profile", "The quality profile does not exist.");
        if (await NoIndexerAsync(chosen, cancellationToken) is { } none) return none;

        var snapshot = await search.SearchAsync(user.Id, ReleaseTargets.For(entry, episode),
            new EvaluationProfile(profile.Id, profile.Name, profile.Revision, AcquisitionConfiguration.Qualities(profile),
                profile.MinimumBytesPerHour, profile.MaximumBytesPerHour),
            inherited, state.Settings.Revision, cancellationToken, new ReleaseSearchOptions(intent, held, progressKey, chosen));
        return ReleaseSearchDto.From(snapshot, await AvailabilityAsync(snapshot, cancellationToken));
    }

    /// <summary>
    /// Whether a grab can start from a search, the rows of a running one included (P4.FU24): grabbing on, acquisition ready, no
    /// active grab holding the target, and the modes its rows may offer.
    /// </summary>
    private async Task<GrabAvailabilityDto> AvailabilityAsync(ReleaseSearchSnapshot snapshot, CancellationToken cancellationToken)
    {
        var target = snapshot.Target;
        var pack = ReleaseScopes.IsPack(target.Scope);
        var addVersion = snapshot.Intent == GrabIntents.AddVersion;
        var state = await configuration.GetStateAsync(database, cancellationToken);
        var targetKey = pack
            ? GrabService.PackKey(target.EntryId, target.Scope == ReleaseScopes.Season ? target.SeasonNumber : null)
            : GrabService.TargetKey(target.EntryId, target.EpisodeId) + (addVersion ? "+add" : string.Empty);
        var active = await database.GrabOperations.AsNoTracking()
            .FirstOrDefaultAsync(value => value.ActiveTarget == targetKey, cancellationToken);
        // A pack that claimed this episode holds it as well as a single-episode grab would.
        if (active is null && !pack && target.EpisodeId is not null &&
            await database.GrabClaims.AsNoTracking().FirstOrDefaultAsync(claim => claim.ActiveKey == targetKey, cancellationToken) is { } claim)
            active = await database.GrabOperations.AsNoTracking().FirstOrDefaultAsync(value => value.Id == claim.GrabId, cancellationToken);
        var reason = !state.Settings.Enabled ? "acquisition_disabled"
            : !state.Ready ? state.Blockers[0]
            : active is not null ? "grab_active"
            : null;
        // A movie's Replace removes every file the movie holds, so its confirmation names how many (user, 2026-10-28).
        int? heldFiles = addVersion && !pack && target.EpisodeId is null
            ? await database.EntryBindings.AsNoTracking().CountAsync(binding => binding.EntryId == target.EntryId && binding.MediaPath != null,
                cancellationToken)
            : null;
        return new GrabAvailabilityDto(reason is null, reason, (int)Math.Ceiling(hold.Hold.TotalSeconds), active?.Id,
            GrabService.ModesFor(pack, addVersion, state.Settings.EpisodeUpgradesEnabled), heldFiles);
    }

    /// <summary>
    /// Searches season or series packs (season and series packs, 2026-10-08): the covered episodes are the series' aired,
    /// tracked episodes of that season, or of every season but specials, and the requester must be able to read each one.
    /// </summary>
    private async Task<ActionResult<ReleaseSearchDto>> SearchPackAsync(User user, Entry entry, string scope, int? seasonNumber,
        Guid? episodeId, Guid? profileId, string intent, string? progressKey, Guid[]? indexerIds, CancellationToken cancellationToken)
    {
        if (entry.MediaType != "series") return Error(400, "scope_not_applicable", "Only a series has seasons.");
        if (episodeId is not null) return Error(400, "invalid_scope", "A season or All Seasons search takes no episode.");
        if (intent != GrabIntents.Acquire)
            return Error(400, "invalid_intent", "A pack search is an acquire search; its grab chooses to fill, add or replace.");
        if (scope == ReleaseScopes.Season ? seasonNumber is not > 0 : seasonNumber is not null)
            return Error(400, "season_required", scope == ReleaseScopes.Season
                ? "Choose a season (specials are not searched as packs)."
                : "An All Seasons search takes no season number.");
        var episodes = await database.Episodes.AsNoTracking().Where(value => value.EntryId == entry.Id).ToListAsync(cancellationToken);
        var covered = ReleaseTargets.Covered(episodes, scope, seasonNumber, DateTime.UtcNow);
        if (covered.Count == 0)
            return Error(409, "no_covered_episodes", "This season has no aired episodes to search for.");
        if (covered.Any(episode => !access.CanReadEpisode(user, episode))) return StatusCode(403);

        var state = await configuration.GetStateAsync(database, cancellationToken);
        var inherited = profileId is null && entry.QualityProfileId is null;
        var chosenId = profileId ?? entry.QualityProfileId ?? state.Settings.DefaultQualityProfileId;
        if (chosenId is null) return Error(409, "no_quality_profile", "Choose a default quality profile in the plugin settings.");
        var profile = await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == chosenId, cancellationToken);
        if (profile is null) return Error(400, "invalid_quality_profile", "The quality profile does not exist.");
        if (await NoIndexerAsync(indexerIds, cancellationToken) is { } none) return none;

        var snapshot = await search.SearchAsync(user.Id, ReleaseTargets.ForPack(entry, scope, seasonNumber, covered, episodes),
            new EvaluationProfile(profile.Id, profile.Name, profile.Revision, AcquisitionConfiguration.Qualities(profile),
                profile.MinimumBytesPerHour, profile.MaximumBytesPerHour),
            inherited, state.Settings.Revision, cancellationToken, new ReleaseSearchOptions(ProgressKey: progressKey, IndexerIds: indexerIds));
        return ReleaseSearchDto.From(snapshot, await AvailabilityAsync(snapshot, cancellationToken));
    }

    /// <summary>
    /// What a running search has done so far, for the release picker's live view: one line per enabled indexer, whether the
    /// search is over, and the rows of the indexers that have answered as a search the page can grab from (P4.FU24; null until
    /// one has). 404 for an unknown key, one that is not the caller's, or one that finished more than a minute ago.
    /// </summary>
    [HttpGet("Releases/Progress/{progressKey}")]
    public async Task<ActionResult<ReleaseProgressDto>> Progress(string progressKey, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (!ReleaseProgressTable.IsValidKey(progressKey) || search.Progress(progressKey, user.Id) is not { } progress) return NotFound();
        var rows = search.Snapshot(progress.SearchId, user.Id);
        // Access is read again on every poll, as the final answer's was at the start: a target the requester can no longer read
        // gets the concealed 404 (P4.FU24 review).
        if (rows is not null && !await CanReadTargetAsync(user, rows.Target, cancellationToken)) return NotFound();
        return ReleaseProgressDto.From(progress,
            rows is null ? null : ReleaseSearchDto.From(rows, await AvailabilityAsync(rows, cancellationToken)));
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
                new GrabRequest(request.SearchId, request.ReleaseId, request.IdempotencyKey, Mode: request.Mode,
                    AcceptRejected: request.AcceptRejected),
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

    /// <summary>The refusal when no indexer would be asked: none enabled, or none of the chosen ones (P4.FU25).</summary>
    private async Task<ObjectResult?> NoIndexerAsync(Guid[]? chosen, CancellationToken cancellationToken)
    {
        if (chosen is null)
            return await database.AcquisitionIndexers.AnyAsync(value => value.Enabled, cancellationToken)
                ? null : Error(409, "no_indexers", "No indexer is enabled in the plugin settings.");
        return await database.AcquisitionIndexers.AnyAsync(value => value.Enabled && chosen.Contains(value.Id), cancellationToken)
            ? null : Error(409, "no_chosen_indexers", "None of the chosen indexers is enabled.");
    }

    private bool CanAccess(User user, Entry entry, Episode? episode) =>
        access.CanRead(user, entry) && (episode is null || access.CanReadEpisode(user, episode));

    /// <summary>Whether the requester may still read a search's target: its entry, its episode, and every episode a pack covers.</summary>
    private async Task<bool> CanReadTargetAsync(User user, ReleaseTarget target, CancellationToken cancellationToken)
    {
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == target.EntryId, cancellationToken);
        var episode = target.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
            : null;
        if (entry is null || target.EpisodeId is not null && episode is null || !CanAccess(user, entry, episode)) return false;
        var covered = target.Covered.Select(value => value.Id).ToArray();
        if (covered.Length == 0) return true;
        var episodes = await database.Episodes.AsNoTracking().Where(value => covered.Contains(value.Id)).ToListAsync(cancellationToken);
        return episodes.Count == covered.Length && episodes.All(value => access.CanReadEpisode(user, value));
    }

    private async Task<bool> CanAccessAsync(User user, GrabOperation operation, CancellationToken cancellationToken)
    {
        if (operation.EntryId is not { } entryId) return true;
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken);
        var episode = operation.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
            : null;
        if (entry is null || !CanAccess(user, entry, episode)) return false;
        if (!ReleaseScopes.IsPack(operation.Scope)) return true;
        // A pack is visible only to a requester who may read every episode it claimed.
        var claimed = await database.GrabClaims.AsNoTracking().Where(claim => claim.GrabId == operation.Id)
            .Select(claim => claim.EpisodeId).ToListAsync(cancellationToken);
        var episodes = await database.Episodes.AsNoTracking().Where(value => claimed.Contains(value.Id)).ToListAsync(cancellationToken);
        return episodes.All(value => access.CanReadEpisode(user, value));
    }

    private async Task<string?> OpenUrlAsync(GrabOperation operation, CancellationToken cancellationToken) =>
        await database.AcquisitionDownloadClients.AsNoTracking().Where(value => value.Id == operation.DownloadClientId)
            .Select(value => value.OpenUrl).SingleOrDefaultAsync(cancellationToken);


    private ObjectResult Error(int status, string code, string title, Guid? operationId = null)
    {
        var problem = new ProblemDetails { Status = status, Type = code, Title = title };
        if (operationId is { } id) problem.Extensions["operationId"] = id;
        return StatusCode(status, problem);
    }
}
