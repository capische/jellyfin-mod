using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Automation;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Administrator-only automation surfaces (P6.M2/M3/M8): status with budgets and breakers, a manual run through the
/// native task, the bounded decision log, per-target schedules, and the automation settings.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod")]
public sealed class AutomationController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    AutomationStatusService status,
    AutomationRunGate runGate,
    ITaskManager taskManager) : ControllerBase
{
    /// <summary>Gets whether automation runs, why it is paused, its budgets and the last run.</summary>
    [HttpGet("Automation/Status")]
    public async Task<ActionResult<AutomationStatusDto>> Status(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await status.StatusAsync(cancellationToken);
    }

    /// <summary>Queues the native automation task; it obeys every budget. 409 while a run is active.</summary>
    [HttpPost("Automation/Run")]
    public ActionResult Run()
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (runGate.Busy)
            return Conflict(new ProblemDetails { Status = 409, Type = "automation_run_active", Title = "An automation run is already active." });
        AutomationSearchTask.RequestManual();
        taskManager.QueueScheduledTask<AutomationSearchTask>();
        return Accepted(new { status = "queued" });
    }

    /// <summary>Pages the decision log, newest first.</summary>
    [HttpGet("Automation/Decisions")]
    public async Task<ActionResult<AutomationDecisionsDto>> Decisions([FromQuery] Guid? entryId, [FromQuery] Guid? episodeId,
        [FromQuery] string? kind, [FromQuery] int startIndex = 0, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (startIndex < 0 || limit is < 1 or > 200) return BadRequest();
        var query = database.AutomationDecisions.AsNoTracking().AsQueryable();
        if (entryId is { } entry) query = query.Where(decision => decision.EntryId == entry);
        if (episodeId is { } episode) query = query.Where(decision => decision.EpisodeId == episode);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(decision => decision.Kind == kind);
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(decision => decision.CreatedAt).ThenBy(decision => decision.Id)
            .Skip(startIndex).Take(limit).ToListAsync(cancellationToken);
        var entryIds = page.Where(decision => decision.EntryId.HasValue).Select(decision => decision.EntryId!.Value).Distinct().ToArray();
        var titles = await database.Entries.AsNoTracking().Where(value => entryIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.Title, cancellationToken);
        return new AutomationDecisionsDto(page.Select(decision => new AutomationDecisionDto(decision.Id, decision.RunId, decision.TargetId,
            decision.EntryId, decision.EpisodeId, decision.EntryId is { } id ? titles.GetValueOrDefault(id) : null, decision.Kind, decision.Reason,
            decision.Detail, decision.IndexerId, decision.GrabId, DateTime.SpecifyKind(decision.CreatedAt, DateTimeKind.Utc))).ToArray(), total);
    }

    /// <summary>Gets the schedule and upgrade state of an entry's targets: the movie, or each episode.</summary>
    [HttpGet("Automation/Targets")]
    public async Task<ActionResult<IReadOnlyList<AutomationTargetDto>>> Targets([FromQuery] Guid entryId, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var profileId = entry.QualityProfileId ?? settings.DefaultQualityProfileId;
        var profile = profileId is { } id
            ? await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken)
            : null;
        var episodes = entry.MediaType == "series"
            ? await database.Episodes.AsNoTracking().Where(value => value.EntryId == entry.Id).OrderBy(value => value.SeasonNumber)
                .ThenBy(value => value.EpisodeNumber).ToListAsync(cancellationToken)
            : [];
        var targetIds = entry.MediaType == "series" ? episodes.Select(value => value.Id).ToArray() : [entry.Id];
        var rows = await database.AutomationTargets.AsNoTracking().Where(row => targetIds.Contains(row.TargetId))
            .ToDictionaryAsync(row => row.TargetId, cancellationToken);
        var result = new List<AutomationTargetDto>();
        foreach (var targetId in targetIds)
        {
            var episode = episodes.FirstOrDefault(value => value.Id == targetId);
            var held = await VersionQuality.HeldAsync(database, entry.Id, episode?.Id, cancellationToken);
            var assessment = UpgradeAssessment.For(profile, held, episode is not null, settings.EpisodeUpgradesEnabled);
            var row = rows.GetValueOrDefault(targetId);
            string? blocked = !entry.Monitored || episode is { Monitored: false } ? AutomationReasons.Unmonitored
                : episode is { SeasonNumber: 0 } ? AutomationReasons.SpecialExcluded
                : episode is not null && (episode.AirDate is null || episode.AirDate > DateTime.UtcNow) ? AutomationReasons.Unaired
                : held.Count > 0 ? assessment.BlockedReason : null;
            result.Add(new AutomationTargetDto(targetId, entry.Id, episode?.Id, Utc(row?.NextSearchAt), Utc(row?.LastSearchedAt),
                row?.ConsecutiveEmpty ?? 0, row?.LastOutcome, row?.SearchNowRequestedAt is not null, assessment.HeldBest, assessment.Cutoff,
                assessment.Eligible, blocked));
        }

        return result;
    }

    /// <summary>Gets the automation settings.</summary>
    [HttpGet("Settings/Automation")]
    public async Task<ActionResult<AutomationSettingsDto>> Settings(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return ToDto(await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken));
    }

    /// <summary>Replaces the automation settings; the request must echo the current revision.</summary>
    [HttpPatch("Settings/Automation")]
    public async Task<ActionResult<AutomationSettingsDto>> PatchSettings(AutomationSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.AutomationRevision)
            return Conflict(new ProblemDetails
            {
                Status = 409, Type = "revision_conflict", Title = "These settings changed since they were loaded. Reload and try again."
            });
        settings.AutomationEnabled = request.AutomationEnabled;
        settings.AutomationIntervalHours = request.AutomationIntervalHours;
        settings.AutomationBatchSize = request.AutomationBatchSize;
        settings.NewEpisodeDelayMinutes = request.NewEpisodeDelayMinutes;
        settings.DailyAutoGrabBudget = request.DailyAutoGrabBudget;
        settings.MaxConcurrentImports = request.MaxConcurrentImports;
        settings.FreeSpaceFloorPercent = request.FreeSpaceFloorPercent;
        settings.FreeSpaceFloorBytes = request.FreeSpaceFloorBytes;
        settings.DecisionLogCap = request.DecisionLogCap;
        settings.EpisodeUpgradesEnabled = request.EpisodeUpgradesEnabled;
        settings.ReacquireReclaimed = request.ReacquireReclaimed;
        settings.AutomationRevision++;
        await database.SaveChangesAsync(cancellationToken);
        return ToDto(settings);
    }

    private static AutomationSettingsDto ToDto(AcquisitionSettings settings) => new(settings.AutomationEnabled,
        settings.AutomationIntervalHours, settings.AutomationBatchSize, settings.NewEpisodeDelayMinutes, settings.DailyAutoGrabBudget,
        settings.MaxConcurrentImports, settings.FreeSpaceFloorPercent, settings.FreeSpaceFloorBytes, settings.DecisionLogCap,
        settings.EpisodeUpgradesEnabled, settings.ReacquireReclaimed, settings.AutomationRevision);

    private static DateTime? Utc(DateTime? value) => value is { } date && date != DateTime.MaxValue
        ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;
}
