using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

internal static class ApiSmoke
{
    internal static async Task RunAsync(string folder)
    {
        var user = new User("ordinary", "auth", "reset") { Id = Guid.NewGuid() };
        var otherUser = new User("restricted", "auth", "reset") { Id = Guid.NewGuid() };
        var libraryFolder = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies };
        var secondLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies };
        var tvLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        var secondTvLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        var root = new TestRoot(user.Id, [libraryFolder, secondLibrary, tvLibrary, secondTvLibrary]);
        var nativeById = new Dictionary<Guid, BaseItem>();
        var library = Stub<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "GetUserRootFolder" => root,
            "GetItemById" when args?[0] is Guid id => nativeById.GetValueOrDefault(id),
            _ => null
        });
        var users = Stub<IUserManager>.Create((method, args) => method.Name == "GetUserById" ? ((Guid)args![0]! == user.Id ? user : otherUser) : null);
        var localization = Stub<ILocalizationManager>.Create((method, args) => method.Name == "GetRatingScore" && args![0]?.ToString() == "R"
            ? new MediaBrowser.Model.Entities.ParentalRatingScore(18, null) : null);
        var http = new BoundaryHttpFactory { Body = """{"id":123,"title":"Movie"}""" };
        var streamsByItem = new Dictionary<Guid, List<MediaBrowser.Model.Entities.MediaStream>>();
        var dbPath = Path.Combine(folder, "api.db");
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = folder, ApplicationName = typeof(EntriesController).Assembly.FullName });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(EntriesController).Assembly).AddJsonOptions(options =>
        {
            // Match the pinned Jellyfin host's native DTO contract, including its converters.
            var defaults = Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions;
            options.JsonSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.JsonSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
            foreach (var converter in defaults.Converters) options.JsonSerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddAuthentication("Smoke").AddScheme<AuthenticationSchemeOptions, SmokeAuthentication>("Smoke", null);
        builder.Services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation, policy => policy.RequireRole("admin")));
        builder.Services.AddTransient(_ => new ModDbContext(dbPath));
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddTransient(_ => new LibraryAccess(users, library, localization));
        var configuration = Stub<IServerConfigurationManager>.Create((method, args) => method.Name == "get_Configuration"
            ? new ServerConfiguration { SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = [] } : null);
        builder.Services.AddTransient(_ => new CatalogSortName(configuration));
        builder.Services.AddSingleton(Stub<IDtoService>.Create((method, args) => method.Name == "GetBaseItemDto"
            ? new BaseItemDto { Id = ((BaseItem)args![0]!).Id, Name = ((BaseItem)args[0]!).Name } : null));
        builder.Services.AddSingleton(Stub<IUserDataManager>.Create((method, args) => method.Name == "GetUserData" ? new UserItemData { Key = "smoke" } : null));
        builder.Services.AddSingleton(Stub<IMediaSourceManager>.Create((method, args) => method.Name == "GetMediaStreams"
            ? streamsByItem.GetValueOrDefault((Guid)args![0]!) ?? [] : null));
        var pluginConfiguration = new PluginConfiguration { TmdbReadAccessToken = "private-test-token" };
        builder.Services.AddTransient(_ => new TmdbClient(http, () => pluginConfiguration, NullLogger<TmdbClient>.Instance));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        // The TMDB fixture is a real HTTP boundary. The plugin's client still sends/parses HTTP.
        app.MapGet("/3/{**path}", async context =>
        {
            http.LastAuthorization = context.Request.Headers.Authorization;
            http.LastApiKey = context.Request.Query["api_key"];
            if (context.Request.Query["language"].Count != 1)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var uri = new Uri("https://api.themoviedb.org" + context.Request.Path + context.Request.QueryString);
            using var fixture = http.Response?.Invoke(uri) ?? new HttpResponseMessage(http.Status) { Content = new StringContent(http.Body) };
            context.Response.StatusCode = (int)fixture.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(await fixture.Content.ReadAsStringAsync());
        });
        await app.Services.GetRequiredService<DatabaseInitializer>().StartAsync(default);
        Console.WriteLine("HTTP smoke: starting isolated loopback host");
        await app.StartAsync();
        Console.WriteLine("HTTP smoke: host ready");
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        http.BaseAddress = new Uri(address);
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        Assert((await client.GetAsync("/JellyfinMod/Entries")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous list is rejected by middleware");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        Assert((await client.GetAsync($"/JellyfinMod/Entries?mediaType=movie&targetLibraryId={tvLibrary.Id}")).StatusCode == HttpStatusCode.NotFound,
            "Entries list rejects a library incompatible with the supplied media type");
        Assert((await client.GetAsync($"/JellyfinMod/Entries?targetLibraryId={Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound,
            "Entries list rejects an inaccessible library instead of returning an empty result");
        Assert((await client.GetAsync($"/JellyfinMod/Entries?targetLibraryId={tvLibrary.Id}")).StatusCode == HttpStatusCode.OK,
            "Entries list accepts an accessible library without requiring a redundant media type");
        var request = new { mediaType = "movie", tmdbId = 123, targetLibraryId = libraryFolder.Id };
        using var created = await client.PostAsJsonAsync("/JellyfinMod/Entries", request);
        Assert(created.StatusCode == HttpStatusCode.OK, "Ordinary user adds in accessible library: " + await created.Content.ReadAsStringAsync());
        var response = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var entryId = Guid.Parse(response.RootElement.GetProperty("entry").GetProperty("id").GetString()!);
        Assert(response.RootElement.GetProperty("created").GetBoolean(), "First add is created");
        Assert(http.LastAuthorization == "Bearer private-test-token" && string.IsNullOrEmpty(http.LastApiKey),
            "TMDB Read Access Token crosses the real HTTP boundary as bearer authorization without entering the query string");
        using var duplicate = await client.PostAsJsonAsync("/JellyfinMod/Entries", request);
        using var duplicateJson = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert(!duplicateJson.RootElement.GetProperty("created").GetBoolean(), "Duplicate add is idempotent");
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.Entries.CountAsync() == 1 && await database.History.CountAsync() == 1, "Exactly one entry and added event");
        Assert((await client.DeleteAsync($"/JellyfinMod/Entries/{entryId}")).StatusCode == HttpStatusCode.Forbidden, "Ordinary deletion denied by middleware");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{entryId}", new { monitored = false })).StatusCode == HttpStatusCode.Forbidden, "Ordinary settings denied by middleware");
        using var filtered = await client.GetAsync("/JellyfinMod/Entries?state=onDisk&state=reclaimed");
        using var filterJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert(filterJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0, "Repeated state keys bind and filter");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{entryId}")).StatusCode == HttpStatusCode.NotFound, "Restricted entry does not reveal metadata");
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", request)).StatusCode == HttpStatusCode.NotFound, "Restricted add does not disclose existing title");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Smoke-Role", "admin");
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "bogus", tmdbId = -1, targetLibraryId = libraryFolder.Id })).StatusCode == HttpStatusCode.BadRequest, "Invalid create fields rejected");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{entryId}", new { qualityProfileId = 1 })).StatusCode == HttpStatusCode.BadRequest, "Unknown patch field rejected");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{entryId}", new { monitored = false })).StatusCode == HttpStatusCode.OK, "Admin monitoring update works");
        Assert((await client.DeleteAsync($"/JellyfinMod/Entries/{entryId}?deleteFiles=true")).StatusCode == HttpStatusCode.BadRequest, "File deletion remains unavailable");
        Assert((await client.DeleteAsync($"/JellyfinMod/Entries/{entryId}")).StatusCode == HttpStatusCode.NoContent, "Admin entry removal works");
        using var otherLibraryAdd = await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "movie", tmdbId = 123, targetLibraryId = secondLibrary.Id });
        Assert(otherLibraryAdd.StatusCode == HttpStatusCode.OK, "Same title can be independently added to another library");
        pluginConfiguration.TmdbReadAccessToken = string.Empty;
        pluginConfiguration.TmdbApiKey = "private-test-key";
        http.Response = uri => uri.AbsolutePath.Contains("search/movie", StringComparison.Ordinal)
            ? Json("{\"results\":[{\"id\":123,\"title\":\"Held\"},{\"id\":999,\"title\":\"Removed\"},{\"id\":555,\"title\":\"Eligible\"}],\"total_pages\":2}")
            : uri.AbsolutePath.EndsWith("/999", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("{\"id\":555,\"title\":\"Eligible\"}");
        using var discovery = await client.GetAsync("/JellyfinMod/Discover/Search?q=title&type=movie");
        using var suggestions = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert(discovery.StatusCode == HttpStatusCode.OK && suggestions.RootElement.GetProperty("items").GetArrayLength() == 1 &&
            suggestions.RootElement.GetProperty("items")[0].GetProperty("tmdbId").GetInt32() == 555 && suggestions.RootElement.GetProperty("nextPage").GetInt32() == 2,
            "HTTP discovery excludes held titles, tolerates stale404 and preserves continuation");
        Assert(string.IsNullOrEmpty(http.LastAuthorization) && http.LastApiKey == "private-test-key",
            "Existing TMDB v3 API-key configurations remain compatible at the real HTTP boundary");
        var nativeEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Name = "TVDB-only episode", ParentIndexNumber = 1, IndexNumber = 1
        };
        nativeEpisode.ProviderIds["Tvdb"] = "88001";
        var nativeSeries = new TestSeries { Id = Guid.NewGuid(), Name = "TVDB-only series", SortName = "tvdb only series", Episodes = [nativeEpisode] };
        nativeSeries.ProviderIds["Tvdb"] = "777";
        tvLibrary.Items = [nativeSeries];
        nativeById[nativeSeries.Id] = nativeSeries;
        nativeById[nativeEpisode.Id] = nativeEpisode;
        http.Response = uri => uri.AbsolutePath.Contains("/search/", StringComparison.Ordinal)
            ? Json("""{"results":[{"id":123,"name":"Owned show"},{"id":124,"name":"Eligible show"}],"total_pages":1}""")
            : uri.AbsolutePath.EndsWith("/123", StringComparison.Ordinal)
                ? Json("""{"id":123,"name":"Owned show","external_ids":{"tvdb_id":777},"seasons":[]}""")
                : Json("""{"id":124,"name":"Eligible show","external_ids":{"tvdb_id":778},"seasons":[]}""");
        foreach (var scope in new[] { string.Empty, $"&targetLibraryId={tvLibrary.Id}" })
        {
            using var ownedDiscovery = await client.GetAsync("/JellyfinMod/Discover/Search?q=show&type=series" + scope);
            using var ownedDiscoveryJson = JsonDocument.Parse(await ownedDiscovery.Content.ReadAsStringAsync());
            Assert(ownedDiscovery.IsSuccessStatusCode && ownedDiscoveryJson.RootElement.GetProperty("items").GetArrayLength() == 1 &&
                ownedDiscoveryJson.RootElement.GetProperty("items")[0].GetProperty("tmdbId").GetInt32() == 124,
                "Discovery excludes a TVDB-only owned series before any plugin entry exists, globally and in its library");
        }
        using var otherLibraryDiscovery = await client.GetAsync($"/JellyfinMod/Discover/Search?q=show&type=series&targetLibraryId={secondTvLibrary.Id}");
        using var otherLibraryDiscoveryJson = JsonDocument.Parse(await otherLibraryDiscovery.Content.ReadAsStringAsync());
        Assert(otherLibraryDiscovery.IsSuccessStatusCode && otherLibraryDiscoveryJson.RootElement.GetProperty("items").GetArrayLength() == 2,
            "A TVDB match in a different library does not suppress scoped discovery");
        root.OtherUserLibraries = [secondTvLibrary];
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        using var restrictedDiscovery = await client.GetAsync("/JellyfinMod/Discover/Search?q=show&type=series");
        using var restrictedDiscoveryJson = JsonDocument.Parse(await restrictedDiscovery.Content.ReadAsStringAsync());
        Assert(restrictedDiscovery.IsSuccessStatusCode && restrictedDiscoveryJson.RootElement.GetProperty("items").GetArrayLength() == 2,
            "A TVDB match in an inaccessible library does not suppress global discovery");
        root.OtherUserLibraries = [];
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        http.Response = uri => uri.AbsolutePath.Contains("/season/0", StringComparison.Ordinal)
            ? Json("{\"season_number\":0,\"episodes\":[{\"id\":9099,\"season_number\":0,\"episode_number\":1,\"name\":\"Future special\",\"air_date\":\"2099-01-02\"}]}")
            : uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
                ? Json("{\"season_number\":1,\"episodes\":[{\"id\":9001,\"season_number\":1,\"episode_number\":1,\"name\":\"Pilot\",\"air_date\":\"2020-01-02\"},{\"id\":9002,\"season_number\":1,\"episode_number\":2,\"name\":\"Aired without media\",\"air_date\":\"2020-01-09\"}]}")
                : Json("{\"id\":123,\"name\":\"Tracked show\",\"external_ids\":{\"tvdb_id\":777},\"seasons\":[{\"season_number\":0,\"name\":\"Specials\",\"episode_count\":1},{\"season_number\":1,\"name\":\"Season 1\",\"episode_count\":2}]}");
        using var seriesAdd = await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "series", tmdbId = 123, targetLibraryId = tvLibrary.Id });
        using var seriesJson = JsonDocument.Parse(await seriesAdd.Content.ReadAsStringAsync());
        var seriesId = seriesJson.RootElement.GetProperty("entry").GetProperty("id").GetString()!;
        var nativeLookup = $"/JellyfinMod/Entries?jellyfinItemId={nativeSeries.Id}&limit=1";
        using var nativeEntries = await client.GetAsync(nativeLookup);
        using var nativeEntriesJson = JsonDocument.Parse(await nativeEntries.Content.ReadAsStringAsync());
        Assert(nativeEntries.IsSuccessStatusCode && nativeEntriesJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 1 &&
            nativeEntriesJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString() == seriesId,
            "Native item lookup returns only the accessible canonical bound entry before paging");
        foreach (var missingLookup in new[] { $"/JellyfinMod/Entries?jellyfinItemId={Guid.NewGuid()}", nativeLookup + "&mediaType=movie" })
        {
            using var missingEntries = await client.GetAsync(missingLookup);
            using var missingEntriesJson = JsonDocument.Parse(await missingEntries.Content.ReadAsStringAsync());
            Assert(missingEntries.IsSuccessStatusCode && missingEntriesJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0 &&
                missingEntriesJson.RootElement.GetProperty("items").GetArrayLength() == 0, "Native lookup composes with existing filters and never falls back to unrelated entries");
        }
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        Assert((await client.GetAsync(nativeLookup)).StatusCode == HttpStatusCode.Unauthorized, "Native entry lookup requires authentication");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        using var inaccessibleEntries = await client.GetAsync(nativeLookup);
        using var inaccessibleEntriesJson = JsonDocument.Parse(await inaccessibleEntries.Content.ReadAsStringAsync());
        Assert(inaccessibleEntries.IsSuccessStatusCode && inaccessibleEntriesJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0 &&
            inaccessibleEntriesJson.RootElement.GetProperty("items").GetArrayLength() == 0, "Native entry lookup does not expose another user's inaccessible library");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        using var detail = await client.GetAsync($"/JellyfinMod/Entries/{seriesId}");
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var episodeRows = detailJson.RootElement.GetProperty("episodes").EnumerateArray().ToArray();
        var pilot = episodeRows.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001);
        var episodeId = pilot.GetProperty("id").GetString()!;
        Assert(pilot.GetProperty("state").GetString() == "onDisk" && Guid.Parse(pilot.GetProperty("jellyfinItemId").GetString()!) == nativeEpisode.Id &&
            episodeRows.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9002).GetProperty("availability").GetString() == "missing" &&
            episodeRows.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9099).GetProperty("availability").GetString() == "unaired" &&
            detailJson.RootElement.GetProperty("history")[0].GetProperty("entryId").GetString()! == seriesId,
            "TVDB-only series and episode bind by verified parent identity while missing and unaired states remain distinct: " + await detail.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{seriesId}/Episodes/{episodeId}", new { monitored = false })).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot change episode monitoring");
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot refresh shared series metadata");
        client.DefaultRequestHeaders.Add("X-Smoke-Role", "admin");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{seriesId}/Episodes/{episodeId}", new { monitored = false })).StatusCode == HttpStatusCode.OK,
            "Admin can change episode monitoring");
        http.Response = uri => uri.AbsolutePath.Contains("/season/0", StringComparison.Ordinal)
            ? Json("{\"season_number\":0,\"episodes\":[{\"id\":9099,\"season_number\":0,\"episode_number\":1,\"name\":\"Future special updated\",\"air_date\":\"2099-01-02\"}]}")
            : uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
                ? Json("{\"season_number\":1,\"episodes\":[{\"id\":9001,\"season_number\":1,\"episode_number\":1,\"name\":\"Pilot updated\",\"air_date\":\"2020-01-02\"},{\"id\":9002,\"season_number\":1,\"episode_number\":2,\"name\":\"Aired without media\",\"air_date\":\"2020-01-09\"},{\"id\":9003,\"season_number\":1,\"episode_number\":3,\"name\":\"New episode\",\"air_date\":\"2020-01-16\"}]}")
                : Json("{\"id\":123,\"name\":\"Tracked show updated\",\"external_ids\":{\"tvdb_id\":777},\"seasons\":[{\"season_number\":0,\"name\":\"Specials\",\"episode_count\":1},{\"season_number\":1,\"name\":\"Season 1\",\"episode_count\":3}]}");
        using var refreshed = await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null);
        using var refreshedJson = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        var refreshedEpisodes = refreshedJson.RootElement.GetProperty("episodes").EnumerateArray().ToArray();
        var refreshedPilot = refreshedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001);
        Assert(refreshed.IsSuccessStatusCode && refreshedPilot.GetProperty("id").GetString() == episodeId &&
            !refreshedPilot.GetProperty("monitored").GetBoolean() && refreshedPilot.GetProperty("state").GetString() == "onDisk" &&
            refreshedEpisodes.Length == 4 && refreshedJson.RootElement.GetProperty("history").GetArrayLength() == 1,
            "Complete HTTP metadata refresh preserves local IDs, monitoring, native binding and history while adding episodes");
        var completeSnapshot = http.Response;
        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("""{"season_number":1,"episodes":[{"id":9001,"season_number":1,"episode_number":2,"name":"Pilot renumbered"},{"id":9002,"season_number":1,"episode_number":1,"name":"Second episode renumbered"},{"id":9003,"season_number":1,"episode_number":3,"name":"New episode"}]}""")
            : completeSnapshot(uri);
        using var renumbered = await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null);
        Assert(renumbered.IsSuccessStatusCode, "HTTP refresh accepts an episode-number swap without a unique-index conflict");
        using var renumberedJson = JsonDocument.Parse(await renumbered.Content.ReadAsStringAsync());
        var renumberedEpisodes = renumberedJson.RootElement.GetProperty("episodes").EnumerateArray().ToArray();
        Assert(renumberedEpisodes.Length == refreshedEpisodes.Length && renumberedEpisodes.All(episode =>
                episode.GetProperty("id").GetString() == refreshedEpisodes.Single(previous => previous.GetProperty("tmdbId").GetInt32() == episode.GetProperty("tmdbId").GetInt32()).GetProperty("id").GetString()) &&
            renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001).GetProperty("episodeNumber").GetInt32() == 2 &&
            renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9002).GetProperty("episodeNumber").GetInt32() == 1 &&
            !renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001).GetProperty("monitored").GetBoolean() &&
            renumberedJson.RootElement.GetProperty("history").GetRawText() == refreshedJson.RootElement.GetProperty("history").GetRawText(),
            "Renumbering preserves every durable episode ID, monitoring choice and history in the HTTP response");
        await using (var database = new ModDbContext(dbPath))
            Assert((await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId))).EpisodeNumber == 2 &&
                !await database.Episodes.AnyAsync(episode => episode.SeasonNumber < 0), "Only final episode positions persist in SQLite after refresh");
        http.Response = completeSnapshot;
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).IsSuccessStatusCode,
            "The reverse episode-number swap also succeeds through HTTP");
        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("{\"season_number\":1,\"episodes\":[{\"id\":9001,\"season_number\":1,\"episode_number\":1,\"name\":\"Partial\"}]}")
            : uri.AbsolutePath.Contains("/season/0", StringComparison.Ordinal)
                ? Json("{\"season_number\":0,\"episodes\":[{\"id\":9099,\"season_number\":0,\"episode_number\":1,\"name\":\"Future special\"}]}")
                : Json("{\"id\":123,\"name\":\"Partial show\",\"external_ids\":{\"tvdb_id\":777},\"seasons\":[{\"season_number\":0,\"name\":\"Specials\",\"episode_count\":1},{\"season_number\":1,\"name\":\"Season 1\",\"episode_count\":3}]}");
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).StatusCode == HttpStatusCode.BadGateway,
            "Partial refresh is rejected at the HTTP boundary");
        http.Response = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).StatusCode == HttpStatusCode.BadGateway,
            "Failed upstream refresh is sanitized at the HTTP boundary");
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.Episodes.CountAsync(episode => episode.EntryId == Guid.Parse(seriesId)) == 4 &&
                !(await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId))).Monitored &&
                await database.History.CountAsync(history => history.EntryId == Guid.Parse(seriesId)) == 1,
                "Partial refresh causes no episode deletion, settings reset, or history loss in SQLite");
        foreach (var malformed in new[] { "{}", "{\"episodes\":{}}", "{\"season_number\":2,\"episodes\":[]}" })
        {
            http.Response = uri => uri.AbsolutePath.Contains("/season/", StringComparison.Ordinal) ? Json(malformed)
                : Json("{\"id\":456,\"name\":\"Incomplete show\",\"seasons\":[{\"season_number\":1,\"name\":\"Season 1\"}]}");
            Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "series", tmdbId = 456, targetLibraryId = tvLibrary.Id })).StatusCode == HttpStatusCode.BadGateway,
                "Malformed upstream episode snapshots fail through the HTTP boundary");
        }
        http.Response = uri => Json("{\"id\":456,\"name\":\"Missing seasons\"}");
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "series", tmdbId = 456, targetLibraryId = tvLibrary.Id })).StatusCode == HttpStatusCode.BadGateway,
            "Missing seasons cannot commit an incomplete tracked series");
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(!await database.Entries.AnyAsync(e => e.TmdbId == 456), "Metadata failure leaves no partial catalog entry");
            Assert(!(await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId))).Monitored, "Episode monitoring persists in SQLite");
        }
        http.Response = null;
        http.Body = """{"id":799,"title":"Excluded adult title","adult":true,"release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"R"}]}]}}""";
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "movie", tmdbId = 799, targetLibraryId = libraryFolder.Id })).StatusCode == HttpStatusCode.NotFound,
            "Direct create cannot bypass discovery's adult metadata boundary");
        foreach (var title in new[] { (701, "The Éclair 10"), (702, "The Éclair 2"), (703, "A Zebra") })
        {
            http.Body = JsonSerializer.Serialize(new { id = title.Item1, title = title.Item2 });
            Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "movie", tmdbId = title.Item1, targetLibraryId = libraryFolder.Id })).IsSuccessStatusCode,
                "Create browse fixture through TMDB HTTP and catalog HTTP");
        }
        using var browse = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, sortBy = new[] { "SortName" }, startIndex = 1, limit = 1 });
        using var browseJson = JsonDocument.Parse(await browse.Content.ReadAsStringAsync());
        Assert(browse.IsSuccessStatusCode && browseJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 3 &&
            browseJson.RootElement.GetProperty("items")[0].GetProperty("entry").GetProperty("title").GetString() == "The Éclair 10",
            "Combined HTTP browse applies configured article, numeric and diacritic sort before paging with exact totals");
        using var fileBrowse = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, state = new[] { "onDisk" } });
        using var fileBrowseJson = JsonDocument.Parse(await fileBrowse.Content.ReadAsStringAsync());
        Assert(fileBrowseJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0, "Browse filters file states before total and page");
        libraryFolder.Items = [new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Éclair 3", SortName = "eclair 0000000003" }];
        using var mixed = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, startIndex = 1, limit = 1 });
        var mixedBody = await mixed.Content.ReadAsStringAsync();
        Assert(mixed.IsSuccessStatusCode, $"Mixed browse returned {(int)mixed.StatusCode}: {mixedBody}");
        using var mixedJson = JsonDocument.Parse(mixedBody);
        Assert(mixedJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 4 &&
            mixedJson.RootElement.GetProperty("items")[0].GetProperty("kind").GetString() == "native" &&
            mixedJson.RootElement.GetProperty("items")[0].GetProperty("nativeItem").GetProperty("Name").GetString() == "Éclair 3",
            "Native DTO and file-less entry interleave before pagination without synthetic native identities");
        foreach (var direction in new[] { "Ascending", "Descending" })
        {
            var titles = new List<string>();
            for (var offset = 0; offset < 4; offset++)
            {
                using var page = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, startIndex = offset, limit = 1, sortOrder = direction });
                var body = await page.Content.ReadAsStringAsync();
                Assert(page.IsSuccessStatusCode, $"Mixed page returned {(int)page.StatusCode}: {body}");
                using var json = JsonDocument.Parse(body);
                Assert(json.RootElement.GetProperty("totalRecordCount").GetInt32() == 4, "Every mixed page reports the same exact total");
                var row = json.RootElement.GetProperty("items")[0];
                titles.Add(row.GetProperty("kind").GetString() == "native" ? row.GetProperty("nativeItem").GetProperty("Name").GetString()! : row.GetProperty("entry").GetProperty("title").GetString()!);
            }
            string[] expected = ["The Éclair 2", "Éclair 3", "The Éclair 10", "A Zebra"];
            Assert(titles.SequenceEqual(direction == "Ascending" ? expected : expected.Reverse()), "Mixed pages preserve complete ordered identities in both directions");
        }
        using var unknownUserState = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, filters = new { status = new[] { "IsUnplayed" } } });
        using var stateJson = JsonDocument.Parse(await unknownUserState.Content.ReadAsStringAsync());
        Assert(stateJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 1, "Unknown wanted user state does not qualify as Unplayed");
        using var favoriteUnplayed = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, filters = new { status = new[] { "IsUnplayed", "IsFavorite" } } });
        using var favoriteJson = JsonDocument.Parse(await favoriteUnplayed.Content.ReadAsStringAsync());
        Assert(favoriteUnplayed.IsSuccessStatusCode && favoriteJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0,
            "Favorite additionally constrains Unplayed instead of widening it");
        using var audio = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, filters = new { audioLanguages = new[] { "eng" } } });
        using var audioJson = JsonDocument.Parse(await audio.Content.ReadAsStringAsync());
        Assert(audio.IsSuccessStatusCode && audioJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0,
            "Native and wanted items without a matching audio stream cannot match a language filter");
        var languageEpisode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "Language fixture" };
        tvLibrary.Items = [new TestSeries { Id = Guid.NewGuid(), Name = "Native language show", SortName = "native language show", Episodes = [languageEpisode] }];
        streamsByItem[languageEpisode.Id] =
        [
            new MediaBrowser.Model.Entities.MediaStream { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Language = "eng" },
            new MediaBrowser.Model.Entities.MediaStream { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Language = "spa" }
        ];
        using var seriesLanguages = await client.PostAsJsonAsync("/JellyfinMod/Browse", new
        {
            mediaType = "series", targetLibraryId = tvLibrary.Id,
            filters = new { audioLanguages = new[] { "eng" }, subtitleLanguages = new[] { "spa" } }
        });
        using var seriesLanguagesJson = JsonDocument.Parse(await seriesLanguages.Content.ReadAsStringAsync());
        Assert(seriesLanguages.IsSuccessStatusCode && seriesLanguagesJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 1,
            "Series language filters inspect visible descendant episode streams");
        using var absentSeriesLanguage = await client.PostAsJsonAsync("/JellyfinMod/Browse", new
        {
            mediaType = "series", targetLibraryId = tvLibrary.Id, filters = new { audioLanguages = new[] { "fra" } }
        });
        using var absentSeriesLanguageJson = JsonDocument.Parse(await absentSeriesLanguage.Content.ReadAsStringAsync());
        Assert(absentSeriesLanguage.IsSuccessStatusCode && absentSeriesLanguageJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0,
            "Series without a requested episode stream language are excluded");
        var randomRequest = new { mediaType = "movie", targetLibraryId = libraryFolder.Id, sortBy = new[] { "Random" }, randomSeed = "same-browse-session", limit = 2 };
        using var randomFirst = await client.PostAsJsonAsync("/JellyfinMod/Browse", randomRequest);
        using var randomAgain = await client.PostAsJsonAsync("/JellyfinMod/Browse", randomRequest);
        Assert(await randomFirst.Content.ReadAsStringAsync() == await randomAgain.Content.ReadAsStringAsync(), "Seeded random page remains stable across HTTP requests");
        using var randomSecond = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, sortBy = new[] { "Random" }, randomSeed = "same-browse-session", startIndex = 2, limit = 2 });
        using var randomFirstJson = JsonDocument.Parse(await randomFirst.Content.ReadAsStringAsync());
        using var randomSecondJson = JsonDocument.Parse(await randomSecond.Content.ReadAsStringAsync());
        var randomRows = randomFirstJson.RootElement.GetProperty("items").EnumerateArray().Concat(randomSecondJson.RootElement.GetProperty("items").EnumerateArray());
        Assert(randomRows.Select(row => row.GetProperty("kind").GetString() == "native" ? row.GetProperty("nativeItem").GetProperty("Id").GetString() : row.GetProperty("entry").GetProperty("id").GetString()).Distinct().Count() == 4,
            "Seeded random pages contain every mixed identity exactly once");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id })).StatusCode == HttpStatusCode.NotFound,
            "Combined browse does not disclose an inaccessible library");
        await app.StopAsync();
        Console.WriteLine("PASS: HTTP auth/access/CRUD, duplicate history, wire-state filter binding, unknown fields and no media deletion");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class TestRoot(Guid userId, CollectionFolder[] libraries) : Folder
    {
        public IReadOnlyList<BaseItem> OtherUserLibraries { get; set; } = [];
        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) => user.Id == userId ? libraries : OtherUserLibraries;
    }
    private sealed class EmptyMovieLibrary : CollectionFolder
    {
        public IReadOnlyList<BaseItem> Items { get; set; } = [];
        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) => new() { Items = Items, TotalRecordCount = Items.Count };
    }
    private sealed class TestSeries : Folder
    {
        public IReadOnlyList<BaseItem> Episodes { get; init; } = [];
        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
            new() { Items = Episodes, TotalRecordCount = Episodes.Count };
    }
}

/// <summary>Loopback-only test authentication; never included in the plugin assembly.</summary>
public sealed class SmokeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Smoke-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString()), new Claim(ClaimTypes.Role, Request.Headers["X-Smoke-Role"].ToString())], "Smoke");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Smoke")));
    }
}
