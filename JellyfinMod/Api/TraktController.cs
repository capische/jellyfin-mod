using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Trakt;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;
using TvEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Api;

/// <summary>
/// The Trakt indicator's one question (P7.Q16): did watch history for this title arrive from Trakt for me? Answers
/// only for the signed-in user, who is read from authentication and never from the request.
/// </summary>
[ApiController, Authorize, Route("JellyfinMod/Trakt")]
public sealed class TraktController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    ILibraryManager library,
    TraktPluginState trakt) : ControllerBase
{
    /// <summary>Reports the requesting user's Trakt history state for a visible movie, episode, season or series.</summary>
    /// <response code="200">The item is visible to the user.</response>
    /// <response code="401">No signed-in user.</response>
    /// <response code="404">The item does not exist or the user cannot see it.</response>
    [HttpGet("Items/{itemId:guid}")]
    public async Task<ActionResult<TraktItemStatusDto>> GetItem([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        // The host's own visibility check: libraries, parental rating and tags. A hidden item answers exactly like a
        // missing one, whether or not anything was recorded for it.
        var item = library.GetItemById<BaseItem>(itemId, user);
        if (item is null) return NotFound();
        if (!trakt.Describe().Installed) return new TraktItemStatusDto(false, false, null);

        var rows = database.TraktObservations.AsNoTracking().Where(row => row.UserId == user.Id);
        rows = item switch
        {
            Movie or TvEpisode => rows.Where(row => row.JellyfinItemId == item.Id),
            Season => rows.Where(row => row.SeasonId == item.Id),
            Series => rows.Where(row => row.SeriesId == item.Id),
            _ => rows.Where(_ => false)
        };
        // Newest first; the first recorded episode the user can still see answers both questions. An episode that is
        // gone or hidden from this user since its import never counts for its season or series.
        var candidates = await rows.OrderByDescending(row => row.LastSyncedAt)
            .Select(row => new { row.JellyfinItemId, row.LastSyncedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (candidate.JellyfinItemId != item.Id && library.GetItemById<BaseItem>(candidate.JellyfinItemId, user) is null) continue;
            return new TraktItemStatusDto(true, true, DateTime.SpecifyKind(candidate.LastSyncedAt, DateTimeKind.Utc));
        }

        return new TraktItemStatusDto(true, false, null);
    }
}
