using System.Net.Mime;
using JellyfinMod.Services.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>
/// Administrator control over the interface takeover (P7.S4).
/// </summary>
/// <remarks>
/// The takeover applies itself, so these endpoints exist to answer two questions an administrator will have:
/// why did the page change, and how do I put it back.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinMod/Settings/Interface")]
[Produces(MediaTypeNames.Application.Json)]
public class InterfaceSettingsController : ControllerBase
{
    private readonly WebRootTakeover _takeover;
    private readonly WebBundleStore _bundles;
    private readonly IApplicationPaths _paths;

    /// <summary>Initializes a new instance of the <see cref="InterfaceSettingsController"/> class.</summary>
    public InterfaceSettingsController(WebRootTakeover takeover, WebBundleStore bundles, IApplicationPaths paths)
    {
        _takeover = takeover;
        _bundles = bundles;
        _paths = paths;
    }

    /// <summary>Reports the interface state.</summary>
    /// <response code="200">The current state.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Get() => Describe();

    /// <summary>Turns the takeover on or off.</summary>
    /// <param name="request">The wanted state.</param>
    /// <response code="200">The resulting state.</response>
    [HttpPatch]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Patch([FromBody] InterfaceSettingsRequest request)
    {
        var configuration = Plugin.Instance!.Configuration;
        if (configuration.UiTakeoverEnabled != request.TakeoverEnabled)
        {
            configuration.UiTakeoverEnabled = request.TakeoverEnabled;
            Plugin.Instance.UpdateConfiguration(configuration);
        }

        await _takeover.ReconcileAsync(request.TakeoverEnabled, WebPath, "setting", HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Describe();
    }

    /// <summary>
    /// Puts the host's own interface back without changing the switch.
    /// </summary>
    /// <remarks>
    /// Separate from turning the takeover off on purpose: this is the button an administrator reaches for when
    /// something looks wrong and they want the familiar page back right now, without deciding anything permanent.
    /// The next startup re-applies the takeover if the switch is still on.
    /// </remarks>
    /// <response code="200">The resulting state.</response>
    [HttpPost("RestoreStock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> RestoreStock()
    {
        await _takeover.ReconcileAsync(false, WebPath, "restore requested", HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Describe();
    }

    private string? WebPath => (_paths as IServerApplicationPaths)?.WebPath;

    private ActionResult<object> Describe()
    {
        var state = _takeover.State;
        return Ok(new
        {
            TakeoverEnabled = Plugin.Instance!.Configuration.UiTakeoverEnabled,
            state.Status,
            state.WebRoot,
            state.BundleId,
            RetainedBundleIds = _bundles.RetainedBundleIds,
            state.StockSha256,
            state.PatchedSha256,
            state.PatchedAt,
            state.PatchedBy,
            state.Blocker,
            state.Recovery,
            ModAddress = "/web-mod/"
        });
    }
}

/// <summary>A request to turn the interface takeover on or off.</summary>
public sealed class InterfaceSettingsRequest
{
    /// <summary>Gets or sets a value indicating whether the plugin should replace the interface at /web.</summary>
    public bool TakeoverEnabled { get; set; }
}
