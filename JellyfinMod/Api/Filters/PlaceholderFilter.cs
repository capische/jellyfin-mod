using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Api.Filters;

/// <summary>
/// Shapes Jellyfin's own API answers for the plugin's placeholder episodes (P1.FU24, plan §2.2), for every client, the stock
/// apps included: a placeholder is a real item (✓, ♥, metadata, counts) that nothing may offer or start as playable.
/// </summary>
/// <remarks>
/// <para>Registered for every stock controller through <c>MvcOptions</c> from the plugin's service registrator (proven live
/// by the plan's experiment X2). It touches only items that are the plugin's own stubs (a <c>.disc</c> placeholder with the
/// marker line, <see cref="PlaceholderStub"/>), whatever the per-library switch says: stubs written before the switch was
/// turned off still exist, and stay unplayable until an administrator removes them. A user's own <c>.disc</c> stub is left as
/// Jellyfin answers it.</para>
/// <list type="table">
/// <item><term><c>GET /Shows/NextUp</c></term><description>a stub episode's slot holds that series' next real episode after the
/// gap, or is left out when none follows (P1.FU28, see <see cref="ShapeNextUp"/>).</description></item>
/// <item><term><c>GET /UserItems/Resume</c></term><description>stub items are left out (a stub cannot be resumed; it can only
/// carry a position from the file it stands in for).</description></item>
/// <item><term><c>GET /Shows/{id}/Episodes</c> with <c>startItemId</c></term><description>stubs after the start item are left out
/// (P1.FU28): <c>startItemId</c> is what play-queue builders send ("play from this episode": upstream web, Jellyfin for Android
/// TV), and no known client sends it for a listing, so season and series listings keep their stubs.</description></item>
/// <item><term><c>/Items/{id}/PlaybackInfo</c></term><description><c>ErrorCode: NotAllowed</c> and no media source, so no
/// client starts a stream or a transcode.</description></item>
/// <item><term><c>/Items/{id}/Download</c>, <c>/Videos/{id}/stream*</c>, the HLS playlists and segments</term><description>404.</description></item>
/// <item><term>playback reports (<c>/Sessions/Playing*</c>, <c>/PlayingItems/{id}*</c>)</term><description>answered 204 and
/// dropped, so no report can mark a stub played; marking it played by hand works as for any item.</description></item>
/// <item><term><c>/Items/Latest</c></term><description>stub episodes are left out: a placeholder is not newly added media (its
/// date added is also its air date, see <see cref="PlaceholderService.StampAsync(BaseItem, CancellationToken)"/>).</description></item>
/// <item><term>item answers</term><description><c>CanDownload</c> false on a stub.</description></item>
/// </list>
/// <para>Fail-safe: any error while deciding lets the original request and answer through unchanged and is logged once.
/// Per request it costs a dictionary lookup on the action name; items are read only for the few that are placeholders.</para>
/// </remarks>
public sealed class PlaceholderFilter(
    ILibraryManager library,
    IUserManager users,
    IUserDataManager userData,
    IDtoService dtos,
    ILogger<PlaceholderFilter> logger) : IAsyncActionFilter, IAsyncResultFilter
{
    private enum Kind
    {
        PlaybackInfo,
        Blocked,
        Report,
        NextUp,
        Latest,
        Resume,
        Episodes
    }

    /// <summary>Where the action phase keeps a shaped list action's bound arguments for the result phase.</summary>
    private const string ArgumentsKey = "JellyfinMod.PlaceholderFilter.Arguments";

    private static readonly Dictionary<string, Kind> Actions = new(StringComparer.Ordinal)
    {
        ["MediaInfo.GetPlaybackInfo"] = Kind.PlaybackInfo,
        ["MediaInfo.GetPostedPlaybackInfo"] = Kind.PlaybackInfo,
        ["Library.GetDownload"] = Kind.Blocked,
        ["Videos.GetVideoStream"] = Kind.Blocked,
        ["Videos.GetVideoStreamByContainer"] = Kind.Blocked,
        ["DynamicHls.GetMasterHlsVideoPlaylist"] = Kind.Blocked,
        ["DynamicHls.GetVariantHlsVideoPlaylist"] = Kind.Blocked,
        ["DynamicHls.GetHlsVideoSegment"] = Kind.Blocked,
        ["DynamicHls.GetLiveHlsStream"] = Kind.Blocked,
        ["Playstate.ReportPlaybackStart"] = Kind.Report,
        ["Playstate.ReportPlaybackProgress"] = Kind.Report,
        ["Playstate.ReportPlaybackStopped"] = Kind.Report,
        ["Playstate.OnPlaybackStart"] = Kind.Report,
        ["Playstate.OnPlaybackStartLegacy"] = Kind.Report,
        ["Playstate.OnPlaybackProgress"] = Kind.Report,
        ["Playstate.OnPlaybackProgressLegacy"] = Kind.Report,
        ["Playstate.OnPlaybackStopped"] = Kind.Report,
        ["Playstate.OnPlaybackStoppedLegacy"] = Kind.Report,
        ["TvShows.GetNextUp"] = Kind.NextUp,
        ["UserLibrary.GetLatestMedia"] = Kind.Latest,
        ["UserLibrary.GetLatestMediaLegacy"] = Kind.Latest,
        ["Items.GetResumeItems"] = Kind.Resume,
        ["Items.GetResumeItemsLegacy"] = Kind.Resume,
        ["TvShows.GetEpisodes"] = Kind.Episodes
    };

    private int _failed;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        IActionResult? replacement = null;
        try
        {
            var kind = KindOf(context.ActionDescriptor);
            // The list shapes below need the request's own bound values (fields, images, user, start item) in the result phase.
            if (kind is Kind.NextUp or Kind.Episodes)
                context.HttpContext.Items[ArgumentsKey] = new Dictionary<string, object?>(context.ActionArguments, StringComparer.OrdinalIgnoreCase);
            else if (kind is not (null or Kind.Latest or Kind.Resume) && ItemIdOf(context) is { } itemId && IsStub(itemId))
            {
                replacement = kind switch
                {
                    Kind.PlaybackInfo => new OkObjectResult(new PlaybackInfoResponse
                    {
                        MediaSources = [],
                        ErrorCode = PlaybackErrorCode.NotAllowed
                    }),
                    Kind.Report => new NoContentResult(),
                    _ => new NotFoundResult()
                };
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Failed(error);
            replacement = null;
        }

        if (replacement is not null)
        {
            context.Result = replacement;
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            if (context.Result is ObjectResult result)
            {
                var kind = KindOf(context.ActionDescriptor);
                var arguments = context.HttpContext.Items.TryGetValue(ArgumentsKey, out var stored)
                    ? stored as IReadOnlyDictionary<string, object?>
                    : null;
                if (kind == Kind.NextUp && result.Value is QueryResult<BaseItemDto> page && arguments is not null)
                    result.Value = ShapeNextUp(page, arguments, context.HttpContext.User);
                else if (kind == Kind.Latest && result.Value is IEnumerable<BaseItemDto> latest)
                    result.Value = latest.Where(item => item.IsPlaceHolder != true || !IsStub(item.Id)).ToArray();
                else if (kind == Kind.Resume && result.Value is QueryResult<BaseItemDto> resume)
                    result.Value = WithoutStubs(resume, keep: null);
                else if (kind == Kind.Episodes && result.Value is QueryResult<BaseItemDto> episodes
                    && arguments?.GetValueOrDefault("startItemId") is Guid startItemId && startItemId != Guid.Empty)
                    // The start item itself stays even when it is a stub: the client asked for it, and its playback is refused
                    // with NotAllowed as before; only the stubs the queue would run into after it are left out.
                    result.Value = WithoutStubs(episodes, keep: startItemId);

                if (result.Value is not null && kind is not Kind.Latest) ClearDownload(result.Value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Failed(error);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Next Up shows the next real episode after a gap (P1.FU28; the user's answer to the plan's question 2): Jellyfin's Next Up
    /// takes, per series, the first unplayed, not virtual episode after the last played one, and a placeholder is not virtual.
    /// Each stub in the page is replaced, in its own slot, by the first episode of the same series after it that is neither a
    /// placeholder nor virtual and has the stub's played state (unplayed, or played for a rewatching slot), specials left out
    /// as Jellyfin's Next Up leaves them out; the slot is dropped (and the total lowered) when none follows, when that episode is
    /// already in the page, or when it has a resume position and the request asked for no resumable episodes. Asked in process
    /// through the library with a user-scoped query (parental rating and tags) and kept only when the user may open the item
    /// (library access too); the answer is the stock DTO, built with the request's own fields, image and user-data options.
    /// Replacing one for one keeps the page size and the total right; the series filter, parent library and date cutoff chose
    /// the series, and the replacement is from the same series.
    /// </summary>
    private QueryResult<BaseItemDto> ShapeNextUp(QueryResult<BaseItemDto> page, IReadOnlyDictionary<string, object?> arguments,
        System.Security.Claims.ClaimsPrincipal principal)
    {
        if (page.Items.All(item => item.IsPlaceHolder != true)) return page;
        User? user = null;
        DtoOptions? options = null;
        var taken = page.Items.Select(item => item.Id).ToHashSet();
        var items = new List<BaseItemDto>(page.Items.Count);
        var changed = false;
        var removed = 0;
        foreach (var item in page.Items)
        {
            if (item.IsPlaceHolder != true || !IsStub(item.Id))
            {
                items.Add(item);
                continue;
            }

            changed = true;
            user ??= UserOf(arguments, principal);
            options ??= DtoOptionsOf(arguments);
            var next = user is null ? null : NextRealEpisode(item.Id, user, arguments);
            if (next is not null && taken.Add(next.Id))
                items.Add(dtos.GetBaseItemDto(next, options, user));
            else
                removed++;
        }

        return !changed
            ? page
            : new QueryResult<BaseItemDto>(page.StartIndex, Math.Max(0, page.TotalRecordCount - removed), items);
    }

    /// <summary>The first real episode of the stub's series after it, with the stub's played state, for this user.</summary>
    private BaseItem? NextRealEpisode(Guid stubId, User user, IReadOnlyDictionary<string, object?> arguments)
    {
        if (library.GetItemById(stubId) is not MediaBrowser.Controller.Entities.TV.Episode stub
            || stub.ParentIndexNumber is not { } season || stub.IndexNumber is not { } number
            || string.IsNullOrEmpty(stub.SeriesPresentationUniqueKey))
            return null;
        // A stub in a rewatching slot is played (marked by hand); the next one to rewatch is the next played real episode.
        var played = userData.GetUserData(user, stub)?.Played == true;
        var after = Math.Max(number, stub.IndexNumberEnd ?? number) + 1;
        var next = library.GetItemList(new InternalItemsQuery(user)
        {
            SeriesPresentationUniqueKey = stub.SeriesPresentationUniqueKey,
            IncludeItemTypes = [BaseItemKind.Episode],
            IsVirtualItem = false,
            IsPlaceHolder = false,
            IsPlayed = played,
            ParentIndexNumberNotEquals = 0,
            MinParentAndIndexNumber = (season, after),
            GroupByPresentationUniqueKey = false,
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
            Limit = 1
        }).FirstOrDefault();
        // A query by series key applies the user's parental rating and tags but not their library access (Jellyfin's own Next
        // Up with seriesId answers for a library the user cannot open); the replacement must be an item the user may open.
        if (next is null || !next.IsVisibleStandalone(user)) return null;
        // enableResumable=false (default true) leaves out a series whose next episode has a resume position, as Next Up does.
        if (!played && arguments.GetValueOrDefault("enableResumable") is false
            && userData.GetUserData(user, next)?.PlaybackPositionTicks > 0)
            return null;
        return next;
    }

    /// <summary>The request's user: its <c>userId</c> (Jellyfin already checked the caller may ask for it), else the caller.</summary>
    private User? UserOf(IReadOnlyDictionary<string, object?> arguments, System.Security.Claims.ClaimsPrincipal principal)
    {
        if (arguments.GetValueOrDefault("userId") is Guid userId && userId != Guid.Empty) return users.GetUserById(userId);
        return Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var id) && id != Guid.Empty ? users.GetUserById(id) : null;
    }

    /// <summary>The DTO options the stock controller builds from the request (<c>DtoExtensions.AddAdditionalDtoOptions</c>).</summary>
    private static DtoOptions DtoOptionsOf(IReadOnlyDictionary<string, object?> arguments)
    {
        var options = new DtoOptions { Fields = arguments.GetValueOrDefault("fields") as ItemFields[] ?? [] };
        options.EnableImages = arguments.GetValueOrDefault("enableImages") as bool? ?? true;
        if (arguments.GetValueOrDefault("imageTypeLimit") is int limit) options.ImageTypeLimit = limit;
        if (arguments.GetValueOrDefault("enableUserData") is bool enableUserData) options.EnableUserData = enableUserData;
        if (arguments.GetValueOrDefault("enableImageTypes") is ImageType[] { Length: > 0 } types) options.ImageTypes = types;
        return options;
    }

    /// <summary>A list without the plugin's stubs (one may be kept), its total lowered by as many.</summary>
    private QueryResult<BaseItemDto> WithoutStubs(QueryResult<BaseItemDto> page, Guid? keep)
    {
        if (page.Items.All(item => item.IsPlaceHolder != true)) return page;
        var kept = page.Items.Where(item => item.IsPlaceHolder != true || item.Id == keep || !IsStub(item.Id)).ToArray();
        var removed = page.Items.Count - kept.Length;
        return removed == 0 ? page : new QueryResult<BaseItemDto>(page.StartIndex, Math.Max(0, page.TotalRecordCount - removed), kept);
    }

    /// <summary>A stub's item answer says it cannot be downloaded, so apps hide their download button.</summary>
    private void ClearDownload(object? value)
    {
        switch (value)
        {
            case BaseItemDto item:
                Clear(item);
                break;
            case QueryResult<BaseItemDto> page:
                foreach (var item in page.Items) Clear(item);
                break;
            case IEnumerable<BaseItemDto> items:
                foreach (var item in items) Clear(item);
                break;
        }

        void Clear(BaseItemDto item)
        {
            if (item.IsPlaceHolder == true && item.CanDownload == true && IsStub(item.Id)) item.CanDownload = false;
        }
    }

    private static Kind? KindOf(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor descriptor) =>
        descriptor is ControllerActionDescriptor action && Actions.TryGetValue(action.ControllerName + "." + action.ActionName, out var kind)
            ? kind
            : null;

    /// <summary>The item a request is about: its route or query <c>itemId</c>, or a playback report's own item.</summary>
    private static Guid? ItemIdOf(ActionExecutingContext context)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            switch (argument)
            {
                case PlaybackProgressInfo progress when progress.ItemId != Guid.Empty:
                    return progress.ItemId;
                case PlaybackStopInfo stop when stop.ItemId != Guid.Empty:
                    return stop.ItemId;
            }
        }

        return context.ActionArguments.TryGetValue("itemId", out var raw) && raw is Guid id && id != Guid.Empty ? id : null;
    }

    private bool IsStub(Guid itemId) =>
        library.GetItemById(itemId) is Video { IsPlaceHolder: true } video && PlaceholderStub.IsStubFile(video.Path);

    private void Failed(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
            logger.LogWarning(error, "JellyfinMod's placeholder filter failed; the answer was passed through unchanged (logged once)");
    }
}
