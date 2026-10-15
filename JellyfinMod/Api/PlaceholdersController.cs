using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>The switch's new value.</summary>
public sealed record PlaceholderSwitchRequest([property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// Placeholders for missing episodes (P1.FU24), administrators only: each TV library's switch "Show missing episodes as
/// Jellyfin items" (off by default), the dry run that lists exactly the files a sync would write and remove, the sync itself,
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
