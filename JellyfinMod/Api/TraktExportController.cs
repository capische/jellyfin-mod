using JellyfinMod.Api.Contracts;
using JellyfinMod.Services.Trakt;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>
/// Missed watches to Trakt (Phase 13): the state of the stock Trakt plugin's export, the Sync Missing Watches Now action and
/// the automatic switch. Administrators only. JellyfinMod never calls Trakt; it starts the plugin's own export.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyfinMod/Trakt/Export")]
public sealed class TraktExportController(TraktExportService export, MediaBrowser.Controller.Library.IUserManager users) : ControllerBase
{
    /// <summary>Reports the export's state.</summary>
    /// <response code="200">The state.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TraktExportStatusDto>> Get() => await DescribeAsync().ConfigureAwait(false);

    /// <summary>Starts the plugin's export now, or joins the run already going.</summary>
    /// <response code="202">Started, or already running.</response>
    /// <response code="409">Refused; <c>error</c> says why (see <c>TraktExportRefusal</c>).</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TraktExportStatusDto>> Run()
    {
        var refusal = export.RunNow();
        if (refusal is not null) return Conflict(new { error = refusal });
        return Accepted(await DescribeAsync().ConfigureAwait(false));
    }

    /// <summary>Turns the automatic export, or the import from Trakt, on or off.</summary>
    /// <param name="request">The wanted state.</param>
    /// <response code="200">The resulting state.</response>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TraktExportStatusDto>> PutSettings([FromBody] TraktExportSettingsRequest request)
    {
        var configuration = Plugin.Instance!.Configuration;
        var changed = false;
        if (request.AutoExport is { } autoExport && configuration.TraktAutoExport != autoExport)
        {
            configuration.TraktAutoExport = autoExport;
            changed = true;
        }

        if (request.Import is { } import && configuration.TraktImport != import)
        {
            configuration.TraktImport = import;
            changed = true;
        }

        if (changed) Plugin.Instance.UpdateConfiguration(configuration);

        return await DescribeAsync().ConfigureAwait(false);
    }

    private async Task<TraktExportStatusDto> DescribeAsync()
    {
        var status = export.Describe();
        var log = await export.ReadActivityAsync().ConfigureAwait(false);
        string? NameOf(Guid id) => users.GetUserById(id)?.Username;
        var viewer = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var viewerId) ? NameOf(viewerId) : null;
        var testMinutes = Plugin.Instance?.Configuration.TraktExportTestMinutes ?? 0;
        var last = status.LastRun;
        return new TraktExportStatusDto(
            status.Installed,
            status.Version,
            TraktPluginState.MinimumExportVersion.ToString(),
            status.AutoExport,
            status.Running,
            status.Refusal,
            last is null ? null : new TraktExportRunDto(last.Status.ToString(), Utc(last.StartTimeUtc), Utc(last.EndTimeUtc)),
            status.LastRunClean,
            status.NextAttemptAt,
            status.Failures,
            testMinutes > 0 ? testMinutes : 10,
            status.Users.Select(pair =>
            {
                var profile = status.Profiles.GetValueOrDefault(pair.Link.UserId);
                return new TraktExportUserDto(pair.Link.UserId, pair.Name, pair.Link.ReconnectNeeded ? "reconnect" : "linked",
                    pair.Link.ReconnectNeeded ? pair.Link.TokenExpiresAt : null, pair.Link.SynchronizeCollections, pair.Link.PostWatchedHistory,
                    profile?.Username, profile?.AvatarUrl);
            }).ToList(),
            log.Select(row => new TraktActivityDto(Utc(row.At), NameOf(row.UserId) ?? string.Empty, row.Title, row.Code, row.Kind, row.Direction,
                row.State, row.Detail)).ToList(),
            status.ImportEnabled, status.ImportRefusal, status.ImportRunning, status.NextExportAt, status.LastError, viewer);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
