using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>Exposes retention diagnostics and bounded manual execution to administrators.</summary>
[ApiController, Route("JellyfinMod/Retention"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class RetentionController(
    RetentionPreviewService preview,
    ITaskManager taskManager,
    ModDbContext database,
    DatabaseInitializer readiness) : ControllerBase
{
    /// <summary>Evaluates current deadlines and every physical protection without deleting media.</summary>
    [HttpGet("Preview")]
    public async Task<ActionResult<RetentionPreviewDto>> Preview(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await preview.PreviewAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the newest durable scheduled or manual run summary.</summary>
    [HttpGet("Runs/Latest")]
    public async Task<ActionResult<RetentionRunDto>> Latest(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var run = await database.RetentionRuns.AsNoTracking().OrderByDescending(item => item.StartedAt)
            .ThenByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return run is null ? NotFound() : new RetentionRunDto(run);
    }

    /// <summary>
    /// Queues the same bounded batch exposed by Jellyfin's scheduled-task page. The batch runs on the
    /// native task, so a closed tab or proxy timeout cannot cancel it; poll Runs/Latest for the outcome.
    /// </summary>
    [HttpPost("Run")]
    public ActionResult<RetentionRunQueuedDto> Run()
    {
        if (!readiness.IsReady) return StatusCode(503);
        taskManager.QueueScheduledTask<RetentionReclamationTask>();
        return Accepted(new RetentionRunQueuedDto("queued"));
    }
}
