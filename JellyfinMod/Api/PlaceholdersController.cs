using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Api;

/// <summary>The switch's new value.</summary>
public sealed record PlaceholderSwitchRequest([property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>One library's share of the backfill (P1.FU27).</summary>
public sealed record PlaceholderLibraryProgress(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("writable")] bool Writable,
    [property: JsonPropertyName("titles")] int Titles,
    [property: JsonPropertyName("titlesDone")] int TitlesDone,
    [property: JsonPropertyName("pending")] int Pending);

/// <summary>
/// The backfill's progress (P1.FU27): titles that have all their placeholders out of those that need some, placeholders still
/// to write, whether a batch runs now, when the last ended and when the scheduled sync runs the next.
/// </summary>
public sealed record PlaceholderProgress(
    [property: JsonPropertyName("titles")] int Titles,
    [property: JsonPropertyName("titlesDone")] int TitlesDone,
    [property: JsonPropertyName("pending")] int Pending,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("scanRunning")] bool ScanRunning,
    [property: JsonPropertyName("lastRunAt")] DateTime? LastRunAt,
    [property: JsonPropertyName("deferred")] string? Deferred,
    [property: JsonPropertyName("nextRunMinutes")] int NextRunMinutes,
    [property: JsonPropertyName("libraries")] IReadOnlyList<PlaceholderLibraryProgress> Libraries);

/// <summary>
/// Placeholders for missing episodes (P1.FU24), administrators only: each TV library's switch "Show missing episodes as
/// Jellyfin items" (on by default, turned off per library), the dry run that lists exactly the files a sync would write and remove, the sync itself,
/// and <i>Remove placeholders</i> with its own dry run. See <c>API.md</c>, <i>Placeholders</i>.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod/Placeholders")]
public sealed class PlaceholdersController(DatabaseInitializer readiness, PlaceholderService placeholders) : ControllerBase
{
    /// <summary>The TV libraries with their switch and how many placeholders Jellyfin holds in each.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<PlaceholderLibrary>> Libraries()
    {
        if (!readiness.IsReady) return StatusCode(503);
        return Ok(placeholders.Libraries());
    }

    /// <summary>The dry run: every file a sync of the library would write or remove, and every missing episode it would leave
    /// without a placeholder with the reason. Nothing is changed.</summary>
    [HttpGet("{libraryId:guid}/Plan")]
    public Task<ActionResult<PlaceholderPlan>> Plan(Guid libraryId, CancellationToken cancellationToken) =>
        Run(() => placeholders.PlanAsync(libraryId, cancellationToken));

    /// <summary>Turns the library's switch on (and syncs at once) or off (writes nothing more; existing placeholders stay).</summary>
    [HttpPut("{libraryId:guid}")]
    public async Task<ActionResult<PlaceholderPlan>> Switch(Guid libraryId, PlaceholderSwitchRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (placeholders.Libraries().All(item => item.LibraryId != libraryId)) return NotFound();
        await using (await SettingsMutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
            PlaceholderSettings.Set(libraryId, request.Enabled);
        return request.Enabled
            ? await Run(() => placeholders.SyncAsync(libraryId, cancellationToken)).ConfigureAwait(false)
            : await Run(() => placeholders.PlanAsync(libraryId, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Syncs the library now: writes (when the switch is on) and removes as the dry run lists.</summary>
    [HttpPost("{libraryId:guid}/Sync")]
    public Task<ActionResult<PlaceholderPlan>> Sync(Guid libraryId, CancellationToken cancellationToken) =>
        Run(() => placeholders.SyncAsync(libraryId, cancellationToken));

    /// <summary>The dry run of <i>Remove placeholders</i>: every placeholder of the plugin's in the library.</summary>
    [HttpGet("{libraryId:guid}/Removal")]
    public Task<ActionResult<PlaceholderPlan>> Removal(Guid libraryId, CancellationToken cancellationToken) =>
        Run(() => placeholders.RemovalPlanAsync(libraryId, cancellationToken));

    /// <summary>
    /// <i>Remove placeholders</i>: deletes every placeholder of the plugin's in the library (each checked for the marker again just
    /// before) and the season folders that left empty; nothing else. The switch is not changed, so turn it off first to keep
    /// them from coming back at the next sync.
    /// </summary>
    [HttpPost("{libraryId:guid}/Remove")]
    public Task<ActionResult<PlaceholderPlan>> Remove(Guid libraryId, CancellationToken cancellationToken) =>
        Run(() => placeholders.RemoveAllAsync(libraryId, cancellationToken));

    /// <summary>
    /// The backfill's progress (P1.FU27): per library and in all, how many titles have their placeholders out of those that need
    /// some and how many placeholders are pending, read now (a dry run of each library), with the scheduled sync's state.
    /// </summary>
    [HttpGet("Progress")]
    public async Task<ActionResult<PlaceholderProgress>> Progress([FromServices] ILibraryManager library, [FromServices] TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return Ok(await ProgressAsync(library, time, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <i>Sync Now</i> (P1.FU27): runs the scheduled sync's next batch at once (every library, the same bounds and the same
    /// library-scan guard) and answers the progress after it. 409 <c>sync_running</c> while a batch runs: never two at once.
    /// </summary>
    [HttpPost("SyncNow")]
    public async Task<ActionResult<PlaceholderProgress>> SyncNow([FromServices] IServiceScopeFactory scopes, [FromServices] ILibraryManager library,
        [FromServices] TimeProvider time, [FromServices] ILogger<PlaceholdersController> logger, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var plans = await PlaceholderBackfill.TryRunAsync(scopes, library, time, logger, null, cancellationToken).ConfigureAwait(false);
        if (plans is null) return Conflict(new { error = "sync_running", message = "A placeholder sync is running; try again when it ends." });
        return Ok(await ProgressAsync(library, time, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The dry run of <i>Restore Watched State From Retention History</i> (P1.FU27): every watched mark the plugin's retention
    /// history would put back on a reclaimed title's placeholder. Nothing is changed.
    /// </summary>
    [HttpGet("RestoreWatched")]
    public async Task<ActionResult<WatchedRestorePlan>> RestoreWatchedPlan([FromServices] PlaceholderWatchedRestore restore,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return Ok(await restore.PlanAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <i>Restore Watched State From Retention History</i> (P1.FU27): puts back the marks the dry run lists, each only where
    /// Jellyfin holds no data of that user for the placeholder; a second run changes nothing.
    /// </summary>
    [HttpPost("RestoreWatched")]
    public async Task<ActionResult<WatchedRestorePlan>> RestoreWatched([FromServices] PlaceholderWatchedRestore restore,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return Ok(await restore.ApplyAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<PlaceholderProgress> ProgressAsync(ILibraryManager library, TimeProvider time, CancellationToken cancellationToken)
    {
        var rows = new List<PlaceholderLibraryProgress>();
        foreach (var item in placeholders.Libraries())
        {
            // A library that is off or cannot be written gets no placeholders: the mod's own surfaces stay its fallback.
            if (!item.Enabled || !item.Writable)
            {
                rows.Add(new(item.LibraryId, item.Name, item.MediaType, item.Enabled, item.Writable, 0, 0, 0));
                continue;
            }

            try
            {
                var plan = await placeholders.PlanAsync(item.LibraryId, cancellationToken).ConfigureAwait(false);
                rows.Add(new(item.LibraryId, item.Name, item.MediaType, true, true, plan.Titles, plan.TitlesDone, plan.Pending));
            }
            catch (KeyNotFoundException)
            {
                // The library went meanwhile.
            }
        }

        var (lastRunAt, _, deferred) = PlaceholderBackfill.State;
        bool scanRunning;
        try
        {
            scanRunning = library.IsScanRunning;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            scanRunning = false;
        }

        return new(rows.Sum(row => row.Titles), rows.Sum(row => row.TitlesDone), rows.Sum(row => row.Pending), PlaceholderBackfill.IsRunning,
            scanRunning, lastRunAt, deferred, (int)Math.Ceiling(PlaceholderBackfill.NextRunIn(time.GetUtcNow().UtcDateTime).TotalMinutes), rows);
    }

    private async Task<ActionResult<PlaceholderPlan>> Run(Func<Task<PlaceholderPlan>> action)
    {
        if (!readiness.IsReady) return StatusCode(503);
        try
        {
            return Ok(await action().ConfigureAwait(false));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
