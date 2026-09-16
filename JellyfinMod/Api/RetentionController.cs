using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>Exposes deletion-free retention diagnostics to administrators.</summary>
[ApiController, Route("JellyfinMod/Retention"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class RetentionController(RetentionPreviewService preview, DatabaseInitializer readiness) : ControllerBase
{
    /// <summary>Evaluates current deadlines and every physical protection without deleting media.</summary>
    [HttpGet("Preview")]
    public async Task<ActionResult<RetentionPreviewDto>> Preview(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await preview.PreviewAsync(cancellationToken).ConfigureAwait(false);
    }
}
