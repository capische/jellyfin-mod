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
public sealed class TraktExportController(TraktExportService export) : ControllerBase
{
    /// <summary>Reports the export's state.</summary>
    /// <response code="200">The state.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TraktExportStatusDto> Get() => Describe();

    /// <summary>Starts the plugin's export now, or joins the run already going.</summary>
    /// <response code="202">Started, or already running.</response>
    /// <response code="409">Refused; <c>error</c> says why (see <c>TraktExportRefusal</c>).</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<TraktExportStatusDto> Run()
    {
        var refusal = export.RunNow();
        if (refusal is not null) return Conflict(new { error = refusal });
        return Accepted(Describe());
    }

    /// <summary>Turns the automatic export on or off.</summary>
    /// <param name="request">The wanted state.</param>
    /// <response code="200">The resulting state.</response>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TraktExportStatusDto> PutSettings([FromBody] TraktExportSettingsRequest request)
    {
        var configuration = Plugin.Instance!.Configuration;
        if (configuration.TraktAutoExport != request.AutoExport)
        {
            configuration.TraktAutoExport = request.AutoExport;
            Plugin.Instance.UpdateConfiguration(configuration);
        }

        return Describe();
    }

    private TraktExportStatusDto Describe()
    {
        var status = export.Describe();
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
            status.Activity.Select(run => new TraktExportActivityDto(Utc(run.StartedAt), Utc(run.EndedAt), run.Status, run.Clean, run.Why,
                run.EpisodesWatched, run.MoviesWatched, run.EpisodesCollected, run.MoviesCollected, run.Error)).ToList(),
            status.RunningWhy);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
