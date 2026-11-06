using System.Net;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NativeEpisode = MediaBrowser.Controller.Entities.TV.Episode;

internal static class PlaceholderNextUpScenarios
{
    public static async Task RunAsync(HttpClient client, Dictionary<Guid, BaseItem> nativeById)
    {
        var seriesId = Guid.NewGuid();
        var missing = new NativeEpisode
        {
            Id = Guid.NewGuid(), Path = PlaceholderItems.EpisodePath(seriesId, 1, 1), IsPlaceHolder = true,
            SeriesId = seriesId, SeriesPresentationUniqueKey = "next-up-smoke", ParentIndexNumber = 1, IndexNumber = 1
        };
        var real = new VisibleEpisode
        {
            Id = Guid.NewGuid(), Path = "/media/NextUp/S01E02.mkv", SeriesId = seriesId,
            SeriesPresentationUniqueKey = "next-up-smoke", ParentIndexNumber = 1, IndexNumber = 2
        };
        nativeById.Add(missing.Id, missing);
        try
        {
            using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
            using var refused = await anonymous.GetAsync($"/Shows/NextUp?itemId={missing.Id}&jfmodPlaceholders=true");
            Assert(refused.StatusCode == HttpStatusCode.Unauthorized, "The opt-in still requires authentication");
            foreach (var hasReal in new[] { false, true })
            {
                if (hasReal) nativeById.Add(real.Id, real);
                foreach (var parameter in new[] { "", "&jfmodPlaceholders=false", "&jfmodPlaceholders=invalid",
                             "&jfmodPlaceholders=true", "&jfmodPlaceholders=TrUe" })
                {
                    using var response = await client.GetAsync($"/Shows/NextUp?itemId={missing.Id}{parameter}");
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var page = json.RootElement;
                    var keep = parameter.EndsWith("=true", StringComparison.OrdinalIgnoreCase);
                    var count = keep || hasReal ? 1 : 0;
                    Assert(page.GetProperty("Items").GetArrayLength() == count && page.GetProperty("TotalRecordCount").GetInt32() == count,
                        "Next Up drops a missing next episode or substitutes the next real episode unless explicitly opted in: " + parameter);
                    if (count == 0) continue;
                    var item = page.GetProperty("Items")[0];
                    Assert(Guid.Parse(item.GetProperty("Id").GetString()!) == (keep ? missing.Id : real.Id),
                        "The opt-in preserves the actual next episode's identity even when a later real episode exists");
                    if (keep)
                        Assert(item.GetProperty("IsPlaceHolder").GetBoolean() && !item.GetProperty("CanDownload").GetBoolean(),
                            "The kept episode remains a placeholder and cannot be downloaded");
                }
            }
        }
        finally
        {
            nativeById.Remove(missing.Id);
            nativeById.Remove(real.Id);
        }
        Console.WriteLine("PASS: Next Up placeholder opt-in over authenticated HTTP (drop, replacement, identity and download protection)");
    }

    // Library visibility is part of the simulated native boundary, as is Next Up selection below.
    private sealed class VisibleEpisode : NativeEpisode
    {
        public override bool IsVisibleStandalone(User user) => true;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

/// <summary>
/// Native Next Up selection is the fixture boundary; the host runs real authentication, production MVC filters,
/// serialization and SQLite migrations. No running Jellyfin server is contacted.
/// </summary>
[ApiController]
[Authorize]
[Route("Shows")]
public sealed class TvShowsController(ILibraryManager library) : ControllerBase
{
    /// <summary>The native answer before the plugin's production result filter shapes it.</summary>
    [HttpGet("NextUp")]
    public QueryResult<BaseItemDto> GetNextUp([FromQuery] Guid itemId)
    {
        var item = (NativeEpisode)library.GetItemById(itemId)!;
        return new QueryResult<BaseItemDto>(0, 1, [new BaseItemDto { Id = item.Id, IsPlaceHolder = item.IsPlaceHolder, CanDownload = true }]);
    }
}
