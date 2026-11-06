using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

internal static class PlaceholderNextUpScenarios
{
    public static async Task RunAsync(HttpClient admin, World world, string stubPath)
    {
        var stub = world.Native.Items.Single(item => item.Path == stubPath);
        foreach (var parameter in new[] { "", "&jfmodPlaceholders=false", "&jfmodPlaceholders=true" })
        {
            using var response = await admin.GetAsync($"/Shows/NextUp?itemId={stub.Id}{parameter}");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var page = json.RootElement;
            var keep = parameter.EndsWith("=true", StringComparison.Ordinal);
            Phase5.Assert(page.GetProperty("Items").GetArrayLength() == (keep ? 1 : 0) &&
                page.GetProperty("TotalRecordCount").GetInt32() == (keep ? 1 : 0),
                "Next Up keeps a file-less next episode only with jfmodPlaceholders=true");
            if (keep)
            {
                var item = page.GetProperty("Items")[0];
                Phase5.Assert(item.GetProperty("Id").GetString()!.Replace("-", "", StringComparison.Ordinal) == stub.Id.ToString("N") &&
                    item.GetProperty("IsPlaceHolder").GetBoolean() && !item.GetProperty("CanDownload").GetBoolean(),
                    "The opt-in keeps the actual next episode and its placeholder identity; file download stays blocked");
            }
        }
    }
}

// Jellyfin's Next Up item selection is the simulated native boundary. Real HTTP, authentication, MVC filters and serialization
// exercise the plugin's production result shaping; the series has no real episode following this placeholder.
[ApiController]
[Authorize]
[Route("Shows")]
public sealed class TvShowsController(ILibraryManager library) : ControllerBase
{
    [HttpGet("NextUp")]
    public QueryResult<BaseItemDto> GetNextUp([FromQuery] Guid itemId)
    {
        var item = (MediaBrowser.Controller.Entities.TV.Episode)library.GetItemById(itemId)!;
        return new QueryResult<BaseItemDto>(0, 1, [new BaseItemDto { Id = item.Id, IsPlaceHolder = item.IsPlaceHolder, CanDownload = true }]);
    }
}
