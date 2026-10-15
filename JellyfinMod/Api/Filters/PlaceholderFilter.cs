using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
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
/// <item><term><c>GET /Shows/NextUp</c></term><description>stub episodes are left out and the total reduced (a series whose next
/// episode is missing drops out of Next Up; P1.FU28 may put the next real episode in its place, see
/// <see cref="ShapeNextUp"/>).</description></item>
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
public sealed class PlaceholderFilter(ILibraryManager library, ILogger<PlaceholderFilter> logger) : IAsyncActionFilter, IAsyncResultFilter
{
    private enum Kind
    {
        PlaybackInfo,
        Blocked,
        Report,
        NextUp,
        Latest
    }

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
        ["UserLibrary.GetLatestMediaLegacy"] = Kind.Latest
    };

    private int _failed;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        IActionResult? replacement = null;
        try
        {
            if (KindOf(context.ActionDescriptor) is { } kind and not (Kind.NextUp or Kind.Latest) && ItemIdOf(context) is { } itemId && IsStub(itemId))
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
                if (kind == Kind.NextUp && result.Value is QueryResult<BaseItemDto> page)
                    result.Value = ShapeNextUp(page);
                else if (kind == Kind.Latest && result.Value is IEnumerable<BaseItemDto> latest)
                    result.Value = latest.Where(item => item.IsPlaceHolder != true || !IsStub(item.Id)).ToArray();
                else
                    ClearDownload(result.Value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Failed(error);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Next Up without placeholders: each stub episode is left out and the total reduced by as many (a page of 20 can then hold
    /// 19). P1.FU28 extends this one place: instead of dropping a series, it asks the library in process for that user's next
    /// unplayed episode of the series after the stub that is not a placeholder, and puts its answer in the stub's slot.
    /// </summary>
    private QueryResult<BaseItemDto> ShapeNextUp(QueryResult<BaseItemDto> page)
    {
        if (page.Items.All(item => item.IsPlaceHolder != true)) return page;
        var kept = page.Items.Where(item => item.IsPlaceHolder != true || !IsStub(item.Id)).ToArray();
        var removed = page.Items.Count - kept.Length;
        if (removed == 0) return page;
        return new QueryResult<BaseItemDto>(page.StartIndex, Math.Max(0, page.TotalRecordCount - removed), kept);
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
