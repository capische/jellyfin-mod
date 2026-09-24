using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
        var raceLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies };
        var tvLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        var secondTvLibrary = new EmptyMovieLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        var root = new TestRoot(user.Id, [libraryFolder, secondLibrary, raceLibrary, tvLibrary, secondTvLibrary]);
        var nativeById = new Dictionary<Guid, BaseItem>();
        var library = Stub<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "GetLocalAlternateVersionIds" => Array.Empty<Guid>(),
            "GetLinkedAlternateVersions" => Array.Empty<MediaBrowser.Controller.Entities.Video>(),
            "GetUserRootFolder" => root,
            "GetItemById" when args?[0] is Guid id => nativeById.GetValueOrDefault(id),
            "GetVirtualFolders" => new[] { libraryFolder, secondLibrary, raceLibrary, tvLibrary, secondTvLibrary }
                .Select(folder => new VirtualFolderInfo { ItemId = folder.Id.ToString(), Name = folder.Id.ToString("N") })
                .ToList(),
            "GetCollectionFolders" when args?[0] is BaseItem item => new[] { libraryFolder, secondLibrary, raceLibrary, tvLibrary, secondTvLibrary }
                .Where(folder => folder.Items.Any(native => native.Id == item.Id)).Cast<Folder>().ToList(),
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
        builder.Services.AddSingleton<ReconciliationLibraryLock>();
        builder.Services.AddSingleton(new LibraryWriteBudget(TimeSpan.FromMilliseconds(500)));
        builder.Services.AddSingleton<RetentionExecutionGate>();
        builder.Services.AddTransient(_ => new LibraryAccess(users, library, localization));
        builder.Services.AddTransient(provider => new RetentionEvaluator(
            provider.GetRequiredService<ModDbContext>(), users,
            provider.GetRequiredService<LibraryAccess>(), TimeProvider.System));
        var configuration = Stub<IServerConfigurationManager>.Create((method, args) => method.Name == "get_Configuration"
            ? new ServerConfiguration { SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = [] } : null);
        builder.Services.AddTransient(_ => new CatalogSortName(configuration));
        // P7.S5: the repository publishes itself from JPRM's meta.json, so a directory shaped like an
        // installed plugin is all it needs to be exercised over real HTTP.
        var packageFolder = Path.Combine(folder, "package");
        Directory.CreateDirectory(packageFolder);
        File.WriteAllText(Path.Combine(packageFolder, "meta.json"), "{\"category\":\"General\",\"changelog\":\"smoke\",\"description\":\"Smoke description\",\"guid\":\"6f1a2b3c-4d5e-4f60-9a71-8b2c3d4e5f60\",\"name\":\"JellyfinMod\",\"overview\":\"Smoke overview\",\"owner\":\"kxalex\",\"targetAbi\":\"12.0.0.0\",\"timestamp\":\"2026-09-19T10:49:06.0000000Z\",\"version\":\"0.1.0.0\"}");
        File.WriteAllText(Path.Combine(packageFolder, "JellyfinMod.dll"), "a real file to package, not a real assembly");
        builder.Services.AddSingleton(_ => new PluginRepository(
            packageFolder, Path.Combine(folder, "repo-data"), NullLogger<PluginRepository>.Instance));
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
            if (http.BeforeResponse is not null) await http.BeforeResponse(uri);
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
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
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

        var raceNative = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Race winner" };
        raceNative.ProviderIds["Tmdb"] = "124";
        raceLibrary.Items = [raceNative];
        nativeById[raceNative.Id] = raceNative;
        var metadataReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Body = """{"id":124,"title":"Race winner"}""";
        http.BeforeResponse = async uri =>
        {
            if (!uri.AbsolutePath.EndsWith("/124", StringComparison.Ordinal)) return;
            metadataReached.TrySetResult();
            await releaseMetadata.Task;
        };
        var raceRequest = new { mediaType = "movie", tmdbId = 124, targetLibraryId = raceLibrary.Id };
        var addDuringBackfill = client.PostAsJsonAsync("/JellyfinMod/Entries", raceRequest);
        await metadataReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var reconciliationDatabase = new ModDbContext(dbPath))
        {
            var reconciliation = new ReconciliationService(reconciliationDatabase, new ReconciliationLibraryLock());
            Assert((await reconciliation.ReconcileAsync(new NativeTitleSnapshot("movie", 124, raceLibrary.Id,
                "Race winner", 2026, null, null, null, null,
                [new(raceNative.Id, raceLibrary.Id, true)], []), default)).Outcome == ReconciliationOutcome.Created,
                "Backfill wins the deliberately overlapped create race");
        }

        releaseMetadata.TrySetResult();
        using var concurrentAdd = await addDuringBackfill;
        Assert(concurrentAdd.IsSuccessStatusCode, "Concurrent user add converges after backfill: " +
            await concurrentAdd.Content.ReadAsStringAsync());
        http.BeforeResponse = null;
        http.Body = """{"id":123,"title":"Movie"}""";
        Guid raceEntryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var raceEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 124 &&
                entry.TargetLibraryId == raceLibrary.Id);
            raceEntryId = raceEntry.Id;
            var creationEvents = await database.History.CountAsync(history => history.EntryId == raceEntry.Id &&
                (history.EventType == "added" || history.EventType == "backfilled"));
            Assert(raceEntry.Monitored && raceEntry.State == FileState.OnDisk && creationEvents == 1 &&
                await database.History.CountAsync(history => history.EntryId == raceEntry.Id &&
                    history.EventType == "monitoring_enabled") == 1,
                "Concurrent add preserves user monitoring intent with one creation event and one meaningful update");
            raceEntry.Monitored = false;
            await database.SaveChangesAsync();
        }

        using var preExistingAdd = await client.PostAsJsonAsync("/JellyfinMod/Entries", raceRequest);
        using var preExistingJson = JsonDocument.Parse(await preExistingAdd.Content.ReadAsStringAsync());
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(!preExistingJson.RootElement.GetProperty("created").GetBoolean() &&
                !(await database.Entries.SingleAsync(entry => entry.Id == raceEntryId)).Monitored &&
                await database.History.CountAsync(history => history.EntryId == raceEntryId) == 2,
                "An already-existing duplicate add preserves the administrator's monitoring preference and history");
        }

        var seriesRaceNativeEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Name = "Race pilot", ParentIndexNumber = 1, IndexNumber = 1
        };
        seriesRaceNativeEpisode.ProviderIds["Tmdb"] = "9101";
        var seriesRaceNative = new TestSeries { Id = Guid.NewGuid(), Name = "Series race", Episodes = [seriesRaceNativeEpisode] };
        seriesRaceNative.ProviderIds["Tmdb"] = "125";
        tvLibrary.Items = [seriesRaceNative];
        nativeById[seriesRaceNative.Id] = seriesRaceNative;
        var seriesMetadataReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSeriesMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("""{"season_number":1,"episodes":[{"id":9101,"season_number":1,"episode_number":1,"name":"Pilot"},{"id":9102,"season_number":1,"episode_number":2,"name":"Missing"}]}""")
            : Json("""{"id":125,"name":"Series race","seasons":[{"season_number":1,"episode_count":2}]}""");
        http.BeforeResponse = async uri =>
        {
            if (!uri.AbsolutePath.EndsWith("/125", StringComparison.Ordinal)) return;
            seriesMetadataReached.TrySetResult();
            await releaseSeriesMetadata.Task;
        };
        var seriesAddDuringBackfill = client.PostAsJsonAsync("/JellyfinMod/Entries",
            new { mediaType = "series", tmdbId = 125, targetLibraryId = tvLibrary.Id });
        await seriesMetadataReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Guid backfilledEpisodeId;
        Guid seriesRaceEntryId;
        await using (var reconciliationDatabase = new ModDbContext(dbPath))
        {
            var result = await new ReconciliationService(reconciliationDatabase,
                app.Services.GetRequiredService<ReconciliationLibraryLock>()).ReconcileAsync(new NativeTitleSnapshot(
                "series", 125, tvLibrary.Id, "Series race", 2026, null, null, null, null,
                [new(seriesRaceNative.Id, tvLibrary.Id, true)],
                [new(seriesRaceNativeEpisode.Id, seriesRaceNative.Id, 9101, 1, 1, true)]), default);
            seriesRaceEntryId = result.EntryId!.Value;
            backfilledEpisodeId = await reconciliationDatabase.Episodes.Where(episode => episode.EntryId == seriesRaceEntryId)
                .Select(episode => episode.Id).SingleAsync();
        }

        releaseSeriesMetadata.TrySetResult();
        using var seriesConcurrentAdd = await seriesAddDuringBackfill;
        Assert(seriesConcurrentAdd.IsSuccessStatusCode, "Concurrent series add succeeds: " + await seriesConcurrentAdd.Content.ReadAsStringAsync());
        await using (var database = new ModDbContext(dbPath))
        {
            var episodesAfterRace = await database.Episodes.Where(episode => episode.EntryId == seriesRaceEntryId).ToListAsync();
            Assert(episodesAfterRace.Count == 2 && episodesAfterRace.All(episode => episode.Monitored) &&
                episodesAfterRace.Single(episode => episode.TmdbId == 9101).Id == backfilledEpisodeId &&
                episodesAfterRace.Single(episode => episode.TmdbId == 9101).JellyfinItemId == seriesRaceNativeEpisode.Id &&
                episodesAfterRace.Single(episode => episode.TmdbId == 9102).State == FileState.None &&
                await database.History.CountAsync(history => history.EntryId == seriesRaceEntryId) == 2,
                "Concurrent series add retains its complete monitored episode set and the backfilled native/local identity");
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == seriesRaceEntryId));
            database.History.RemoveRange(database.History.Where(history => history.EntryId == seriesRaceEntryId));
            await database.SaveChangesAsync();
        }

        tvLibrary.Items = [];
        http.BeforeResponse = null;
        http.Response = null;

        await using (var database = new ModDbContext(dbPath))
        {
            database.ReconciliationRuns.Add(new ReconciliationRun
            {
                Status = "completed", TotalItems = 3, ScannedItems = 3, CreatedEntries = 1,
                UpdatedBindings = 1, UnchangedItems = 1
            });
            await database.SaveChangesAsync();
        }

        Assert((await client.GetAsync("/JellyfinMod/Reconciliation/Latest")).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot read item-level reconciliation diagnostics");
        Assert((await client.DeleteAsync($"/JellyfinMod/Entries/{entryId}")).StatusCode == HttpStatusCode.Forbidden, "Ordinary deletion denied by middleware");
        Assert((await client.PatchAsJsonAsync($"/JellyfinMod/Entries/{entryId}", new { monitored = false })).StatusCode == HttpStatusCode.Forbidden, "Ordinary settings denied by middleware");
        using var filtered = await client.GetAsync($"/JellyfinMod/Entries?targetLibraryId={libraryFolder.Id}&state=onDisk&state=reclaimed");
        using var filterJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert(filterJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0, "Repeated state keys bind and filter");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{entryId}")).StatusCode == HttpStatusCode.NotFound, "Restricted entry does not reveal metadata");
        Assert((await client.PostAsJsonAsync("/JellyfinMod/Entries", request)).StatusCode == HttpStatusCode.NotFound, "Restricted add does not disclose existing title");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        client.DefaultRequestHeaders.Add("X-Smoke-Role", "admin");
        using var reconciliationSummary = await client.GetAsync("/JellyfinMod/Reconciliation/Latest");
        using var reconciliationJson = JsonDocument.Parse(await reconciliationSummary.Content.ReadAsStringAsync());
        Assert(reconciliationSummary.StatusCode == HttpStatusCode.OK &&
            reconciliationJson.RootElement.GetProperty("status").GetString() == "completed" &&
            reconciliationJson.RootElement.GetProperty("scannedItems").GetInt32() == 3 &&
            reconciliationJson.RootElement.GetProperty("missingItems").GetInt32() == 0 &&
            reconciliationJson.RootElement.GetProperty("incompleteLibraries").GetInt32() == 0 &&
            reconciliationJson.RootElement.GetProperty("diagnostics").GetArrayLength() == 0,
            "Administrators receive the exact durable reconciliation summary through authenticated HTTP");
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
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.AddRange(Enumerable.Range(1, 20).Select(index => new Entry
            {
                MediaType = "series", TmdbId = 5000 + index, Title = "Bound fixture " + index,
                TargetLibraryId = tvLibrary.Id, JellyfinItemId = nativeSeries.Id, State = FileState.OnDisk
            }));
            await database.SaveChangesAsync();
        }
        tvLibrary.QueryCount = 0;
        using (var batchedEntries = await client.GetAsync("/JellyfinMod/Entries?mediaType=series"))
        {
            using var batchedEntriesJson = JsonDocument.Parse(await batchedEntries.Content.ReadAsStringAsync());
            Assert(batchedEntries.IsSuccessStatusCode && batchedEntriesJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 21 &&
                tvLibrary.QueryCount == 1, "Entry authorization enumerates the native TV library once per HTTP request");
        }
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.RemoveRange(database.Entries.Where(entry => entry.TmdbId >= 5001 && entry.TmdbId <= 5020));
            await database.SaveChangesAsync();
        }
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
        await using (var retentionDatabase = new ModDbContext(dbPath))
        {
            retentionDatabase.RetentionPolicySnapshots.Add(new RetentionPolicySnapshot
            {
                Id = RetentionPolicyService.PolicyId,
                Version = 1,
                Enabled = true,
                ReclaimAfterDays = 14,
                WatchedUserMode = WatchedUserMode.AllUsers,
                EnabledAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow
            });
            retentionDatabase.RetentionEvaluations.Add(new RetentionEvaluation
            {
                EntryId = Guid.Parse(seriesId),
                EpisodeId = Guid.Parse(episodeId),
                TargetId = Guid.Parse(episodeId),
                PolicyVersion = 1,
                State = "scheduled",
                Reason = "completion_policy_satisfied",
                Deadline = DateTime.UtcNow.AddDays(3),
                EvaluatedAt = DateTime.UtcNow
            });
            await retentionDatabase.SaveChangesAsync();
        }
        using var dueBrowse = await client.PostAsJsonAsync("/JellyfinMod/Browse", new
        {
            mediaType = "series", targetLibraryId = tvLibrary.Id, dueWithinDays = 7
        });
        using var dueBrowseJson = JsonDocument.Parse(await dueBrowse.Content.ReadAsStringAsync());
        Assert(dueBrowse.IsSuccessStatusCode &&
            dueBrowseJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 1 &&
            dueBrowseJson.RootElement.GetProperty("items")[0].GetProperty("retention")
                .GetProperty("state").GetString() == "scheduled",
            "Due-within filtering uses persisted episode eligibility before paging and returns its privacy-safe summary");
        var adminRetention = dueBrowseJson.RootElement.GetProperty("items")[0].GetProperty("retention");
        Assert(adminRetention.GetProperty("reason").GetString() == "completion_policy_satisfied" &&
            adminRetention.GetProperty("deadline").ValueKind == JsonValueKind.String,
            "Administrators see the specific retention reason and deadline");
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        // P3.T15: ordinary users get the public vocabulary and no deadline derived from others' activity.
        using (var userBrowse = await client.PostAsJsonAsync("/JellyfinMod/Browse", new
        {
            mediaType = "series", targetLibraryId = tvLibrary.Id, dueWithinDays = 7
        }))
        {
            using var userBrowseJson = JsonDocument.Parse(await userBrowse.Content.ReadAsStringAsync());
            var userRetention = userBrowseJson.RootElement.GetProperty("items")[0].GetProperty("retention");
            Assert(userBrowse.IsSuccessStatusCode && userRetention.GetProperty("state").GetString() == "scheduled" &&
                userRetention.GetProperty("reason").GetString() == "scheduled" &&
                (!userRetention.TryGetProperty("deadline", out var userDeadline) || userDeadline.ValueKind == JsonValueKind.Null),
                "Ordinary users see a public reason and no deadline derived from other users' activity: " + userRetention);
        }
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
            // P2.R9: the bound pilot keeps Jellyfin's display numbering; the unbound episode follows TMDB.
            renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001).GetProperty("episodeNumber").GetInt32() == 1 &&
            renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9002).GetProperty("episodeNumber").GetInt32() == 1 &&
            !renumberedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9001).GetProperty("monitored").GetBoolean() &&
            renumberedJson.RootElement.GetProperty("history").GetRawText() == refreshedJson.RootElement.GetProperty("history").GetRawText(),
            "Renumbering preserves every durable episode ID, monitoring choice and history in the HTTP response");
        await using (var database = new ModDbContext(dbPath))
            Assert((await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId))).EpisodeNumber == 1 &&
                !await database.Episodes.AnyAsync(episode => episode.SeasonNumber < 0), "Only final episode positions persist in SQLite after refresh");
        http.Response = completeSnapshot;
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).IsSuccessStatusCode,
            "The reverse episode-number swap also succeeds through HTTP");
        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("{\"season_number\":1,\"episodes\":[{\"id\":9001,\"season_number\":1,\"episode_number\":1,\"name\":\"Partial\"}]}")
            : uri.AbsolutePath.Contains("/season/0", StringComparison.Ordinal)
                ? Json("{\"season_number\":0,\"episodes\":[{\"id\":9099,\"season_number\":0,\"episode_number\":1,\"name\":\"Future special\"}]}")
                : Json("{\"id\":123,\"name\":\"Partial show\",\"external_ids\":{\"tvdb_id\":777},\"seasons\":[{\"season_number\":0,\"name\":\"Specials\",\"episode_count\":1},{\"season_number\":1,\"name\":\"Season 1\",\"episode_count\":3}]}");
        using var partialRefresh = await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null);
        using var partialRefreshJson = JsonDocument.Parse(await partialRefresh.Content.ReadAsStringAsync());
        Assert(partialRefresh.IsSuccessStatusCode && partialRefreshJson.RootElement.GetProperty("episodes").GetArrayLength() == 4 &&
            partialRefreshJson.RootElement.GetProperty("episodes").EnumerateArray().Any(episode => episode.GetProperty("tmdbId").GetInt32() == 9002),
            "An airing show's partial TMDB snapshot updates known episodes without deleting unmatched local episodes");
        http.Response = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null)).StatusCode == HttpStatusCode.BadGateway,
            "Failed upstream refresh is sanitized at the HTTP boundary");
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.Episodes.CountAsync(episode => episode.EntryId == Guid.Parse(seriesId)) == 4 &&
                !(await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId))).Monitored &&
                await database.History.CountAsync(history => history.EntryId == Guid.Parse(seriesId)) == 1,
                "Partial refresh causes no episode deletion, settings reset, or history loss in SQLite");

        // P3.T10: Refresh is metadata-only. Availability, bindings and reclaimed states belong to
        // reconciliation and retention and survive a refresh unchanged.
        FileState seriesStateBefore;
        await using (var database = new ModDbContext(dbPath))
        {
            var reclaimed = await database.Episodes.SingleAsync(episode => episode.EntryId == Guid.Parse(seriesId) && episode.TmdbId == 9002);
            reclaimed.State = FileState.Reclaimed;
            reclaimed.JellyfinItemId = null;
            seriesStateBefore = (await database.Entries.SingleAsync(entry => entry.Id == Guid.Parse(seriesId))).State;
            await database.SaveChangesAsync();
        }
        http.Response = completeSnapshot;
        using (var keptStates = await client.PostAsync($"/JellyfinMod/Entries/{seriesId}/Refresh", null))
        {
            using var keptJson = JsonDocument.Parse(await keptStates.Content.ReadAsStringAsync());
            var reclaimedDto = keptJson.RootElement.GetProperty("episodes").EnumerateArray()
                .Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9002);
            // P3.T14: a reclaimed episode is reported as reclaimed, not as missing.
            Assert(keptStates.IsSuccessStatusCode && reclaimedDto.GetProperty("availability").GetString() == "reclaimed",
                "Admin refresh succeeds and reports the reclaimed episode's availability as reclaimed: " + reclaimedDto);
        }
        await using (var database = new ModDbContext(dbPath))
        {
            var reclaimed = await database.Episodes.SingleAsync(episode => episode.EntryId == Guid.Parse(seriesId) && episode.TmdbId == 9002);
            var boundPilot = await database.Episodes.SingleAsync(episode => episode.Id == Guid.Parse(episodeId));
            var series = await database.Entries.SingleAsync(entry => entry.Id == Guid.Parse(seriesId));
            Assert(reclaimed.State == FileState.Reclaimed && reclaimed.JellyfinItemId is null &&
                boundPilot.State == FileState.OnDisk && boundPilot.JellyfinItemId is not null && series.State == seriesStateBefore,
                $"Refresh keeps reclaimed and bound states and never resets availability: {reclaimed.State}/{boundPilot.State}/{series.State}");
        }
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
        libraryFolder.Items = [new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Éclair 3", OriginalTitle = "Amélie", SortName = "eclair 0000000003" }];
        using var mixed = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, startIndex = 1, limit = 1 });
        var mixedBody = await mixed.Content.ReadAsStringAsync();
        Assert(mixed.IsSuccessStatusCode, $"Mixed browse returned {(int)mixed.StatusCode}: {mixedBody}");
        using var mixedJson = JsonDocument.Parse(mixedBody);
        Assert(mixedJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 4 &&
            mixedJson.RootElement.GetProperty("items")[0].GetProperty("kind").GetString() == "native" &&
            mixedJson.RootElement.GetProperty("items")[0].GetProperty("nativeItem").GetProperty("Name").GetString() == "Éclair 3",
            "Native DTO and file-less entry interleave before pagination without synthetic native identities");
        using var foldedSearch = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = libraryFolder.Id, query = "amelie" });
        using var foldedSearchJson = JsonDocument.Parse(await foldedSearch.Content.ReadAsStringAsync());
        Assert(foldedSearch.IsSuccessStatusCode && foldedSearchJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 1 &&
            foldedSearchJson.RootElement.GetProperty("items")[0].GetProperty("nativeItem").GetProperty("Name").GetString() == "Éclair 3",
            "Combined search folds diacritics and matches a native original title");
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
        await VerifyStaleAndOrphanedEntriesAsync(client, dbPath, user, libraryFolder.Id);
        await VerifyEpisodeConflictsAsync(client, dbPath, user, tvLibrary.Id);
        await VerifyReclaimedVisibilityAsync(client, dbPath, user, libraryFolder.Id);
        await VerifyDiscoveryBudgetAsync(client, http, user);
        await VerifyContractHygieneAsync(client, dbPath, user, libraryFolder.Id, tvLibrary.Id);
        await VerifyBoundCopyPreferenceAsync(client, dbPath, libraryFolder, secondLibrary, raceLibrary, nativeById);
        // P1.W14: Health lists the request fields this build understands.
        using (var health = await client.GetAsync("/JellyfinMod/Health"))
        {
            using var json = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            Assert(health.IsSuccessStatusCode && json.RootElement.GetProperty("Capabilities").EnumerateArray()
                    .Select(value => value.GetString()).Contains("browse.dueWithinDays"),
                "Health advertises capabilities so newer clients can gate newer request fields");
        }
        // P7.S5: the plugin publishes a repository the server can install from, so the Dashboard details
        // panel has a package to describe. A manifest whose checksum disagrees with the file it points at is
        // worse than no manifest -- the server downloads it and then refuses it -- so both are checked.
        using (var manifestResponse = await client.GetAsync("/JellyfinMod/Repository"))
        {
            Assert(manifestResponse.IsSuccessStatusCode, "The repository manifest is served");
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
            var package = manifest.RootElement.EnumerateArray().Single();
            Assert(package.GetProperty("guid").GetString() == "6f1a2b3c-4d5e-4f60-9a71-8b2c3d4e5f60",
                "The manifest carries the plugin's own guid, so the server matches it to the installed plugin");
            var version = package.GetProperty("versions").EnumerateArray().Single();
            foreach (var required in new[] { "version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp" })
            {
                Assert(version.TryGetProperty(required, out var value) && !string.IsNullOrEmpty(value.GetString()),
                    $"The manifest version carries {required}, which the server requires");
            }

            Assert(version.GetProperty("targetAbi").GetString() == "12.0.0.0",
                "The manifest takes targetAbi from meta.json rather than restating it");

            var sourceUrl = version.GetProperty("sourceUrl").GetString()!;
            using var packageResponse = await client.GetAsync(new Uri(sourceUrl).AbsolutePath);
            Assert(packageResponse.IsSuccessStatusCode, "The manifest sourceUrl resolves to the package");
            var bytes = await packageResponse.Content.ReadAsByteArrayAsync();
            var checksum = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes));
            Assert(string.Equals(checksum, version.GetProperty("checksum").GetString(), StringComparison.OrdinalIgnoreCase),
                "The served package hashes to the checksum the manifest claims, so the server accepts the download");
        }

        using (var missingPackage = await client.GetAsync("/JellyfinMod/Repository/JellyfinMod_9.9.9.9.zip"))
        {
            Assert(missingPackage.StatusCode == HttpStatusCode.NotFound,
                "The repository serves only the package its manifest names");
        }

        // P2.R8: an Add that cannot get the library in time reports "library busy", not a TMDB timeout.
        http.Response = null;
        http.BeforeResponse = null;
        http.Status = HttpStatusCode.OK;
        http.Body = """{"id":88100,"title":"Busy library"}""";
        await using (await app.Services.GetRequiredService<ReconciliationLibraryLock>().AcquireAsync(secondLibrary.Id, default))
        {
            using var busy = await client.PostAsJsonAsync("/JellyfinMod/Entries",
                new { mediaType = "movie", tmdbId = 88100, targetLibraryId = secondLibrary.Id });
            var busyBody = await busy.Content.ReadAsStringAsync();
            Assert(busy.StatusCode == HttpStatusCode.ServiceUnavailable && busy.Headers.RetryAfter is not null &&
                busyBody.Contains("library_busy", StringComparison.Ordinal) &&
                !busyBody.Contains("timed out", StringComparison.Ordinal),
                "An Add to a library held by reconciliation returns a documented 503 library_busy: " + busyBody);
        }
        await app.StopAsync();
        Console.WriteLine("PASS: HTTP auth/access/CRUD, duplicate history, wire-state filter binding, unknown fields and no media deletion");
    }

    /// <summary>P1.P10: global Browse shows the native copy that carries the bound entry.</summary>
    private static async Task VerifyBoundCopyPreferenceAsync(HttpClient client, string dbPath,
        EmptyMovieLibrary first, EmptyMovieLibrary second, EmptyMovieLibrary third, Dictionary<Guid, BaseItem> nativeById)
    {
        var unbound = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = Guid.Parse("00000000-0000-0000-0000-00000000a001"), Name = "Shared copy", SortName = "shared copy"
        };
        var bound = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-00000000b002"), Name = "Shared copy", SortName = "shared copy"
        };
        unbound.ProviderIds["Tmdb"] = bound.ProviderIds["Tmdb"] = "88600";
        // Only these two copies are listed, so the global request sees nothing from earlier fixtures.
        var firstItems = first.Items;
        var secondItems = second.Items;
        var thirdItems = third.Items;
        first.Items = [unbound];
        second.Items = [bound];
        third.Items = [];
        nativeById[unbound.Id] = unbound;
        nativeById[bound.Id] = bound;
        var entry = new Entry
        {
            MediaType = "movie", TmdbId = 88600, TargetLibraryId = second.Id, Title = "Shared copy",
            JellyfinItemId = bound.Id, State = FileState.OnDisk
        };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(entry);
            await database.SaveChangesAsync();
        }

        using var browse = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie" });
        using var json = JsonDocument.Parse(await browse.Content.ReadAsStringAsync());
        var shared = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("kind").GetString() == "native" &&
                Guid.Parse(row.GetProperty("nativeItem").GetProperty("Id").GetString()!) is var id &&
                (id == unbound.Id || id == bound.Id))
            .ToArray();
        Assert(browse.IsSuccessStatusCode && shared.Length == 1 &&
            Guid.Parse(shared[0].GetProperty("nativeItem").GetProperty("Id").GetString()!) == bound.Id &&
            Guid.Parse(shared[0].GetProperty("entry").GetProperty("id").GetString()!) == entry.Id,
            "Global Browse shows the bound copy of a title held in two libraries, with its entry");
        first.Items = firstItems;
        second.Items = secondItems;
        third.Items = thirdItems;
    }

    /// <summary>P1.P11: API keys get 401 on user-scoped endpoints, and any bound copy finds its entry.</summary>
    private static async Task VerifyContractHygieneAsync(HttpClient client, string dbPath, User user, Guid libraryId, Guid tvLibraryId)
    {
        var entry = new Entry
        {
            MediaType = "movie", TmdbId = 88500, TargetLibraryId = libraryId, Title = "Two copies",
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 88500, "Two copies",
                null, null, null, null, null, null, false, null, null, [], [], []))
        };
        var secondCopy = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(entry);
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = entry.Id, JellyfinItemId = secondCopy, TargetLibraryId = libraryId, VersionGroupId = secondCopy
            });
            await database.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        using (var byCopy = await client.GetAsync($"/JellyfinMod/Entries?jellyfinItemId={secondCopy}"))
        {
            using var json = JsonDocument.Parse(await byCopy.Content.ReadAsStringAsync());
            Assert(byCopy.IsSuccessStatusCode && json.RootElement.GetProperty("items").EnumerateArray()
                    .Any(row => Guid.Parse(row.GetProperty("id").GetString()!) == entry.Id),
                "A non-primary bound native copy finds its entry through its binding");
        }

        // P3.T14: a native episode finds its series, and a reclaimed native item finds its former entry.
        var reclaimedNative = Guid.NewGuid();
        var nativeEpisode = Guid.NewGuid();
        var series = new Entry
        {
            MediaType = "series", TmdbId = 88510, TargetLibraryId = tvLibraryId, Title = "Episode lookup",
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 88510, "Episode lookup",
                null, null, null, null, null, null, false, null, null, [], [], []))
        };
        await using (var database = new ModDbContext(dbPath))
        {
            var episode = new Episode { EntryId = series.Id, TmdbId = 88511, SeasonNumber = 1, EpisodeNumber = 1, Title = "One" };
            database.Entries.Add(series);
            database.Episodes.Add(episode);
            database.EpisodeBindings.Add(new EpisodeBinding { EpisodeId = episode.Id, JellyfinItemId = nativeEpisode, TargetLibraryId = tvLibraryId });
            database.RetentionOperations.Add(new RetentionOperation
            {
                ActionId = Guid.NewGuid(), BindingId = Guid.NewGuid(), EntryId = entry.Id, JellyfinItemId = reclaimedNative,
                TargetLibraryId = libraryId, MediaPath = "/gone.mkv", StorageIdentity = "x", PhysicalIdentity = "y",
                State = "completed", PreparedAt = DateTime.UtcNow
            });
            await database.SaveChangesAsync();
        }

        foreach (var (nativeId, expected, message) in new[]
                 {
                     (nativeEpisode, series.Id, "A native episode id finds its series entry"),
                     (reclaimedNative, entry.Id, "A reclaimed native id finds the entry it belonged to")
                 })
        {
            using var lookup = await client.GetAsync($"/JellyfinMod/Entries?jellyfinItemId={nativeId}");
            using var json = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
            Assert(lookup.IsSuccessStatusCode && json.RootElement.GetProperty("items").EnumerateArray()
                .Any(row => Guid.Parse(row.GetProperty("id").GetString()!) == expected), message);
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", Guid.Empty.ToString());
        Assert((await client.GetAsync("/JellyfinMod/Entries")).StatusCode == HttpStatusCode.Unauthorized &&
            (await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "movie" })).StatusCode == HttpStatusCode.Unauthorized &&
            (await client.GetAsync("/JellyfinMod/Discover/Search?q=x&type=movie")).StatusCode == HttpStatusCode.Unauthorized,
            "An API-key request without a user gets 401 from Entries, Browse and Discover");
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
    }

    /// <summary>P1.P8: discovery never walks pages it cannot show, and one failed candidate is skipped.</summary>
    private static async Task VerifyDiscoveryBudgetAsync(HttpClient client, BoundaryHttpFactory http, User user)
    {
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        var upstreamCalls = 0;
        http.BeforeResponse = _ =>
        {
            Interlocked.Increment(ref upstreamCalls);
            return Task.CompletedTask;
        };
        http.Status = HttpStatusCode.OK;
        http.Response = uri => uri.AbsolutePath.Contains("search/movie", StringComparison.Ordinal)
            ? Json("{\"results\":[{\"id\":88401,\"title\":\"One\"},{\"id\":88402,\"title\":\"Broken\"},{\"id\":88403,\"title\":\"Three\"}],\"total_pages\":3}")
            : uri.AbsolutePath.EndsWith("/88402", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json("{\"id\":" + uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..] + ",\"title\":\"Candidate\"}");

        user.SetPreference(PreferenceKind.AllowedTags, ["kids"]);
        using (var allowlisted = await client.GetAsync("/JellyfinMod/Discover/Search?q=title&type=movie"))
        {
            using var json = JsonDocument.Parse(await allowlisted.Content.ReadAsStringAsync());
            Assert(allowlisted.IsSuccessStatusCode && json.RootElement.GetProperty("items").GetArrayLength() == 0 &&
                (!json.RootElement.TryGetProperty("nextPage", out var nextPage) || nextPage.ValueKind == JsonValueKind.Null) &&
                upstreamCalls == 0,
                $"An allowlisted user's search returns no items and no next page without calling TMDB ({upstreamCalls} calls)");
        }

        user.SetPreference(PreferenceKind.AllowedTags, []);
        using (var partial = await client.GetAsync("/JellyfinMod/Discover/Search?q=title&type=movie"))
        {
            using var json = JsonDocument.Parse(await partial.Content.ReadAsStringAsync());
            Assert(partial.IsSuccessStatusCode && json.RootElement.GetProperty("items").GetArrayLength() == 2 &&
                json.RootElement.GetProperty("skipped").GetInt32() == 1 &&
                json.RootElement.GetProperty("nextPage").GetInt32() == 2 && upstreamCalls <= 1 + DiscoverController.MaxDetailCalls,
                "One failed detail call is skipped and counted while the other candidates and the next page survive: " +
                json.RootElement.GetRawText());
        }

        http.BeforeResponse = null;
        http.Response = null;
    }

    /// <summary>P3.T15: a reclaimed title keeps the native tag and rating rules Jellyfin applied to it.</summary>
    private static async Task VerifyReclaimedVisibilityAsync(HttpClient client, string dbPath, User user, Guid libraryId)
    {
        var reclaimed = new Entry
        {
            MediaType = "movie", TmdbId = 88300, TargetLibraryId = libraryId, Title = "Reclaimed and restricted",
            State = FileState.Reclaimed, NativeRating = "R", NativeTagsJson = """["Blocked-Tag"]""",
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 88300, "Reclaimed and restricted",
                null, null, null, null, null, null, false, null, null, [], [], []))
        };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(reclaimed);
            await database.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{reclaimed.Id}")).StatusCode == HttpStatusCode.OK,
            "An unrestricted user can open a reclaimed title");
        user.SetPreference(PreferenceKind.BlockedTags, ["blocked-tag"]);
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{reclaimed.Id}")).StatusCode == HttpStatusCode.NotFound,
            "A reclaimed title carrying a blocked native tag stays hidden");
        user.SetPreference(PreferenceKind.BlockedTags, []);
        user.MaxParentalRatingScore = 13;
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{reclaimed.Id}")).StatusCode == HttpStatusCode.NotFound,
            "A reclaimed title above the user's maximum native rating stays hidden");
        user.MaxParentalRatingScore = null;
        user.SetPreference(PreferenceKind.AllowedTags, ["kids"]);
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{reclaimed.Id}")).StatusCode == HttpStatusCode.NotFound,
            "A reclaimed title without an allowed native tag stays hidden");
        user.SetPreference(PreferenceKind.AllowedTags, []);
    }

    /// <summary>P2.R9: administrators rebind or keep a disagreeing episode through real HTTP.</summary>
    private static async Task VerifyEpisodeConflictsAsync(HttpClient client, string dbPath, User user, Guid libraryId)
    {
        var series = new Entry { MediaType = "series", TmdbId = 88200, TargetLibraryId = libraryId, Title = "Conflicted" };
        var pilot = new Episode { EntryId = series.Id, TmdbId = 88201, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot",
            State = FileState.OnDisk };
        var second = new Episode { EntryId = series.Id, TmdbId = 88203, SeasonNumber = 1, EpisodeNumber = 3, Title = "Third",
            State = FileState.OnDisk };
        var pilotNative = Guid.NewGuid();
        var secondNative = Guid.NewGuid();
        pilot.JellyfinItemId = pilotNative;
        second.JellyfinItemId = secondNative;
        var rebind = new EpisodeConflict { EntryId = series.Id, EpisodeId = pilot.Id, JellyfinItemId = pilotNative,
            ObservedTmdbId = 88202, ObservedSeasonNumber = 1, ObservedEpisodeNumber = 2 };
        var keep = new EpisodeConflict { EntryId = series.Id, EpisodeId = second.Id, JellyfinItemId = secondNative,
            ObservedTmdbId = 88204, ObservedSeasonNumber = 1, ObservedEpisodeNumber = 4 };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(series);
            database.Episodes.AddRange(pilot, second);
            database.EpisodeBindings.AddRange(
                new EpisodeBinding { EpisodeId = pilot.Id, JellyfinItemId = pilotNative, TargetLibraryId = libraryId },
                new EpisodeBinding { EpisodeId = second.Id, JellyfinItemId = secondNative, TargetLibraryId = libraryId });
            database.EpisodeConflicts.AddRange(rebind, keep);
            await database.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        Assert((await client.PostAsync($"/JellyfinMod/Reconciliation/Conflicts/{rebind.Id}/Rebind", null)).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot resolve episode conflicts");
        client.DefaultRequestHeaders.Add("X-Smoke-Role", "admin");
        using var listed = await client.GetAsync("/JellyfinMod/Reconciliation/Conflicts");
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert(listed.StatusCode == HttpStatusCode.OK && listedJson.RootElement.GetArrayLength() == 2,
            "The admin conflict view lists every open episode conflict");
        Assert((await client.PostAsync($"/JellyfinMod/Reconciliation/Conflicts/{rebind.Id}/Rebind", null)).StatusCode ==
                HttpStatusCode.NoContent &&
            (await client.PostAsync($"/JellyfinMod/Reconciliation/Conflicts/{keep.Id}/Keep", null)).StatusCode ==
                HttpStatusCode.NoContent,
            "An administrator can rebind one conflict and keep another");
        await using (var database = new ModDbContext(dbPath))
        {
            var target = await database.Episodes.SingleAsync(episode => episode.EntryId == series.Id && episode.TmdbId == 88202);
            var oldPilot = await database.Episodes.SingleAsync(episode => episode.Id == pilot.Id);
            Assert(target.JellyfinItemId == pilotNative && target.State == FileState.OnDisk &&
                oldPilot.JellyfinItemId is null && oldPilot.State == FileState.None &&
                await database.EpisodeBindings.AnyAsync(binding => binding.JellyfinItemId == pilotNative &&
                    binding.EpisodeId == target.Id) &&
                await database.History.CountAsync(history => history.EntryId == series.Id &&
                    history.EventType == "episode_conflict_rebound") == 1 &&
                !await database.EpisodeConflicts.AnyAsync(conflict => conflict.Id == rebind.Id),
                "Rebind moves the native episode to the reported identity with one history event");
            Assert(await database.EpisodeConflicts.AnyAsync(conflict => conflict.Id == keep.Id &&
                    conflict.State == EpisodeConflictStates.Kept) &&
                (await database.Episodes.SingleAsync(episode => episode.Id == second.Id)).JellyfinItemId == secondNative &&
                await database.History.CountAsync(history => history.EntryId == series.Id &&
                    history.EventType == "episode_conflict_kept") == 1,
                "Keep leaves the binding in place and writes one history event");
        }

        using var afterwards = await client.GetAsync("/JellyfinMod/Reconciliation/Conflicts");
        using var afterwardsJson = JsonDocument.Parse(await afterwards.Content.ReadAsStringAsync());
        Assert(afterwardsJson.RootElement.GetArrayLength() == 0, "Resolved conflicts leave the admin view");
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
    }

    /// <summary>P2.R7: stale and orphaned entries stay manageable by administrators.</summary>
    private static async Task VerifyStaleAndOrphanedEntriesAsync(HttpClient client, string dbPath, User user, Guid libraryId)
    {
        static string Metadata(int tmdbId) => JsonSerializer.Serialize(new TmdbMetadata("movie", tmdbId, "Unrated",
            null, null, null, null, null, null, false, null, null, [], [], []));
        var stale = new Entry
        {
            MediaType = "movie", TmdbId = 87101, TargetLibraryId = libraryId, Title = "Deleted in Jellyfin",
            JellyfinItemId = Guid.NewGuid(), State = FileState.OnDisk, MetadataJson = Metadata(87101)
        };
        var orphan = new Entry
        {
            MediaType = "movie", TmdbId = 87102, TargetLibraryId = Guid.NewGuid(), Title = "Removed library",
            MetadataJson = Metadata(87102)
        };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.AddRange(stale, orphan);
            await database.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{stale.Id}")).StatusCode == HttpStatusCode.OK,
            "An entry whose only native item was deleted falls back to the unbound access rule");
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{orphan.Id}")).StatusCode == HttpStatusCode.NotFound,
            "An entry in a removed library stays hidden from ordinary users");
        client.DefaultRequestHeaders.Add("X-Smoke-Role", "admin");
        Assert((await client.GetAsync($"/JellyfinMod/Entries/{orphan.Id}")).StatusCode == HttpStatusCode.OK,
            "An administrator can open an entry in a removed library");
        using var orphans = await client.GetAsync("/JellyfinMod/Reconciliation/Orphans");
        using var orphansJson = JsonDocument.Parse(await orphans.Content.ReadAsStringAsync());
        Assert(orphans.StatusCode == HttpStatusCode.OK &&
            orphansJson.RootElement.EnumerateArray().Select(row => Guid.Parse(row.GetProperty("id").GetString()!)).SequenceEqual([orphan.Id]),
            "Administrator diagnostics list exactly the entries whose library is no longer live");
        Assert((await client.DeleteAsync($"/JellyfinMod/Entries/{stale.Id}")).StatusCode == HttpStatusCode.NoContent &&
            (await client.DeleteAsync($"/JellyfinMod/Entries/{orphan.Id}")).StatusCode == HttpStatusCode.NoContent,
            "An administrator can remove stale-bound and orphaned entries instead of receiving 404");
        client.DefaultRequestHeaders.Remove("X-Smoke-Role");
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
        public int QueryCount { get; set; }
        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            QueryCount++;
            return new() { Items = Items, TotalRecordCount = Items.Count };
        }
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
