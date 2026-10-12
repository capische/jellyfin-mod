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
            "GetItemById" or "RetrieveItem" when args?[0] is Guid id => nativeById.GetValueOrDefault(id),
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
        builder.Services.AddSingleton(library);
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
            http.Requests.Enqueue(context.Request.Path + "?append_to_response=" + context.Request.Query["append_to_response"]);
            if (http.BeforeResponse is not null) await http.BeforeResponse(uri);
            HttpResponseMessage Fixture(Uri target) =>
                http.Response?.Invoke(target) ?? new HttpResponseMessage(http.Status) { Content = new StringContent(http.Body) };
            using var fixture = Fixture(uri);
            var body = await fixture.Content.ReadAsStringAsync();
            // P1.FU4: TMDB appends each requested season/N to the series response as a property of that name, and leaves out
            // a season it cannot read; the per-season route above stays for the client's fallback.
            var appended = context.Request.Query["append_to_response"].ToString().Split(',')
                .Where(item => item.StartsWith("season/", StringComparison.Ordinal)).ToArray();
            if (fixture.IsSuccessStatusCode && appended.Length > 0)
            {
                var series = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
                foreach (var item in appended)
                {
                    if (http.OmitAppendedSeason?.Invoke(int.Parse(item["season/".Length..], System.Globalization.CultureInfo.InvariantCulture)) == true)
                        continue;
                    using var season = Fixture(new Uri("https://api.themoviedb.org" + context.Request.Path + "/" + item + "?language=en-US"));
                    if (season.IsSuccessStatusCode)
                        series[item] = System.Text.Json.Nodes.JsonNode.Parse(await season.Content.ReadAsStringAsync());
                }
                body = series.ToJsonString();
            }
            context.Response.StatusCode = (int)fixture.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
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

        // Dead bindings: a row whose Jellyfin item is gone is not listed as a version (it still counts for the removal that follows).
        var deadItemId = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = raceEntryId, JellyfinItemId = deadItemId, TargetLibraryId = raceLibrary.Id, VersionGroupId = deadItemId,
                MediaPath = Path.Combine(folder, "old-mount", "Race winner.mkv")
            });
            await database.SaveChangesAsync();
        }

        using (var deadDetail = await client.GetAsync($"/JellyfinMod/Entries/{raceEntryId}"))
        {
            using var deadDetailJson = JsonDocument.Parse(await deadDetail.Content.ReadAsStringAsync());
            var versions = deadDetailJson.RootElement.GetProperty("versions").EnumerateArray().ToArray();
            Assert(deadDetail.IsSuccessStatusCode && versions.Length == 1 &&
                Guid.Parse(versions[0].GetProperty("jellyfinItemId").GetString()!) == raceNative.Id,
                "A binding whose Jellyfin item is gone is not listed as a version: " + deadDetailJson.RootElement.GetProperty("versions").GetRawText());
        }

        await using (var database = new ModDbContext(dbPath))
        {
            database.EntryBindings.RemoveRange(await database.EntryBindings.Where(binding => binding.JellyfinItemId == deadItemId).ToListAsync());
            await database.SaveChangesAsync();
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
        // Whole-review chunk 3b, P2 4: the only due episode is hidden from this user by a blocked tag. Browse must neither
        // list the series as due nor summarize it from that episode.
        var pilotTags = nativeEpisode.Tags;
        nativeEpisode.Tags = ["hidden-episode"];
        user.SetPreference(PreferenceKind.BlockedTags, ["hidden-episode"]);
        using (var hiddenDue = await client.PostAsJsonAsync("/JellyfinMod/Browse", new
        {
            mediaType = "series", targetLibraryId = tvLibrary.Id, dueWithinDays = 7
        }))
        using (var hiddenAll = await client.PostAsJsonAsync("/JellyfinMod/Browse", new { mediaType = "series", targetLibraryId = tvLibrary.Id }))
        {
            using var hiddenDueJson = JsonDocument.Parse(await hiddenDue.Content.ReadAsStringAsync());
            using var hiddenAllJson = JsonDocument.Parse(await hiddenAll.Content.ReadAsStringAsync());
            var row = hiddenAllJson.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.TryGetProperty("entry", out var entry) && entry.ValueKind == JsonValueKind.Object &&
                    entry.GetProperty("id").GetString() == seriesId);
            Assert(hiddenDue.IsSuccessStatusCode && hiddenDueJson.RootElement.GetProperty("totalRecordCount").GetInt32() == 0 &&
                row.GetProperty("retention").GetProperty("state").GetString() != "scheduled",
                "A series is not listed as due, nor summarized as scheduled, from an episode hidden from the requester " +
                "(whole-review c3bf4): " + row.GetProperty("retention"));
        }

        user.SetPreference(PreferenceKind.BlockedTags, []);
        nativeEpisode.Tags = pilotTags;
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
            refreshedEpisodes.Length == 4 && refreshedJson.RootElement.GetProperty("history").GetArrayLength() == 1 &&
            refreshedEpisodes.Single(episode => episode.GetProperty("tmdbId").GetInt32() == 9003).GetProperty("monitored").GetBoolean() &&
            refreshedJson.RootElement.GetProperty("episodeListLoaded").GetBoolean(),
            "Complete HTTP metadata refresh preserves local IDs, monitoring, native binding and history while adding episodes, " +
            "and a monitored series' new episode is monitored (P1.FU)");
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
        // Whole-review chunk 2c, P2 5: TMDB moves an unbound episode onto the number a position-tracked file already holds,
        // with the same title and air date. Refresh folds the two into one row: the file's row takes the TMDB identity and
        // the moved row's monitoring and Keep; no TMDB row is left beside the position row.
        var foldedSeries = new Entry
        {
            MediaType = "series", TmdbId = 8800, TargetLibraryId = tvLibrary.Id, Title = "Folded",
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 8800, "Folded", null, null, null, null, null, null, false,
                null, null, [], [], []))
        };
        var movedRow = new Episode { EntryId = foldedSeries.Id, TmdbId = 8803, SeasonNumber = 1, EpisodeNumber = 3, Title = "Fourth",
            AirDate = new DateTime(2020, 1, 23, 0, 0, 0, DateTimeKind.Utc), Monitored = true, RetentionPolicy = RetentionPolicy.Never };
        var positionRow = new Episode { EntryId = foldedSeries.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 4, Title = "Fourth",
            AirDate = new DateTime(2020, 1, 23, 0, 0, 0, DateTimeKind.Utc), Monitored = false, JellyfinItemId = Guid.NewGuid(),
            State = FileState.OnDisk };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(foldedSeries);
            database.Episodes.AddRange(movedRow, positionRow);
            await database.SaveChangesAsync();
        }

        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("""{"season_number":1,"episodes":[{"id":8803,"season_number":1,"episode_number":4,"name":"Fourth","air_date":"2020-01-23"}]}""")
            : Json("""{"id":8800,"name":"Folded","seasons":[{"season_number":1,"name":"Season 1","episode_count":1}]}""");
        using (var folded = await client.PostAsync($"/JellyfinMod/Entries/{foldedSeries.Id}/Refresh", null))
        await using (var database = new ModDbContext(dbPath))
        {
            var rows = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == foldedSeries.Id).ToListAsync();
            Assert(folded.IsSuccessStatusCode && rows.Count == 1 && rows[0].Id == positionRow.Id && rows[0].TmdbId == 8803 &&
                rows[0].SeasonNumber == 1 && rows[0].EpisodeNumber == 4 && rows[0].JellyfinItemId == positionRow.JellyfinItemId &&
                rows[0].Monitored && rows[0].RetentionPolicy == RetentionPolicy.Never,
                "Refresh never leaves a TMDB row beside a position row at the same number; with matching evidence they become one " +
                "row that keeps the file and the moved row's settings (whole-review c2cf5): " +
                string.Join(", ", rows.Select(row => $"{row.TmdbId} S{row.SeasonNumber}E{row.EpisodeNumber} {row.Monitored} {row.RetentionPolicy}")));
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == foldedSeries.Id));
            database.History.RemoveRange(database.History.Where(history => history.EntryId == foldedSeries.Id));
            await database.SaveChangesAsync();
        }

        // Codex round 2 P1: a recently un-kept, file-less TMDB episode folds into a position row whose schedule expired long
        // ago. The TMDB row's evaluation goes with it, so its grace must be carried first: the surviving file waits out the
        // grace instead of becoming due at once. A second TMDB row's one-day window never shortens the position row's
        // inherited one (P2-d applies to the fold too).
        var graceSeries = new Entry
        {
            MediaType = "series", TmdbId = 8810, TargetLibraryId = tvLibrary.Id, Title = "Grace fold",
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 8810, "Grace fold", null, null, null, null, null, null,
                false, null, null, [], [], []))
        };
        var unkeptRow = new Episode { EntryId = graceSeries.Id, TmdbId = 8813, SeasonNumber = 1, EpisodeNumber = 3, Title = "Fourth",
            AirDate = new DateTime(2020, 1, 23, 0, 0, 0, DateTimeKind.Utc) };
        var expiredRow = new Episode { EntryId = graceSeries.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 4, Title = "Fourth",
            AirDate = new DateTime(2020, 1, 23, 0, 0, 0, DateTimeKind.Utc), JellyfinItemId = Guid.NewGuid(), State = FileState.OnDisk };
        var shortRow = new Episode { EntryId = graceSeries.Id, TmdbId = 8815, SeasonNumber = 1, EpisodeNumber = 5, Title = "Sixth",
            AirDate = new DateTime(2020, 2, 6, 0, 0, 0, DateTimeKind.Utc), RetentionPolicy = RetentionPolicy.Days, ReclaimAfterDays = 1 };
        var inheritingRow = new Episode { EntryId = graceSeries.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 6, Title = "Sixth",
            AirDate = new DateTime(2020, 2, 6, 0, 0, 0, DateTimeKind.Utc), JellyfinItemId = Guid.NewGuid(), State = FileState.OnDisk };
        var unkeptAt = DateTime.UtcNow.AddHours(-1);
        var foldedGrabId = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(graceSeries);
            database.Episodes.AddRange(unkeptRow, expiredRow, shortRow, inheritingRow);
            database.RetentionEvaluations.AddRange(
                new RetentionEvaluation { EntryId = graceSeries.Id, EpisodeId = unkeptRow.Id, TargetId = unkeptRow.Id,
                    State = "waiting", Reason = "waiting_for_completion", EvaluatedAt = unkeptAt, BaselineAt = unkeptAt.AddDays(-60),
                    GraceNotBefore = unkeptAt },
                new RetentionEvaluation { EntryId = graceSeries.Id, EpisodeId = expiredRow.Id, TargetId = expiredRow.Id,
                    State = "scheduled", Reason = "scheduled", EvaluatedAt = unkeptAt, BaselineAt = unkeptAt.AddDays(-60),
                    CompletionBasisAt = unkeptAt.AddDays(-50), EligibleAt = unkeptAt.AddDays(-50), Deadline = unkeptAt.AddDays(-36) });
            // Codex delta review 2: a finished grab of the folded row names the surviving row afterwards, never no episode.
            database.GrabOperations.Add(new GrabOperation
            {
                Id = foldedGrabId, RequestedBy = Guid.NewGuid(), IdempotencyKey = "p1-fold-grab", RequestFingerprint = "p1",
                EntryId = graceSeries.Id, EpisodeId = unkeptRow.Id, RawTitle = "Grace.Fold.S01E04", State = "cancelled",
                CreatedAt = unkeptAt, UpdatedAt = unkeptAt, HoldUntil = unkeptAt
            });
            await database.SaveChangesAsync();
        }

        http.Response = uri => uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
            ? Json("""{"season_number":1,"episodes":[{"id":8813,"season_number":1,"episode_number":4,"name":"Fourth","air_date":"2020-01-23"},{"id":8815,"season_number":1,"episode_number":6,"name":"Sixth","air_date":"2020-02-06"}]}""")
            : Json("""{"id":8810,"name":"Grace fold","seasons":[{"season_number":1,"name":"Season 1","episode_count":2}]}""");
        using (var folded = await client.PostAsync($"/JellyfinMod/Entries/{graceSeries.Id}/Refresh", null))
        await using (var database = new ModDbContext(dbPath))
        {
            var rows = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == graceSeries.Id).ToListAsync();
            var survivorEvaluation = await database.RetentionEvaluations.AsNoTracking()
                .SingleOrDefaultAsync(evaluation => evaluation.TargetId == expiredRow.Id);
            var inheriting = rows.SingleOrDefault(row => row.Id == inheritingRow.Id);
            Assert(folded.IsSuccessStatusCode && rows.Count == 2 && rows.Any(row => row.Id == expiredRow.Id && row.TmdbId == 8813) &&
                survivorEvaluation is { State: not "scheduled", Deadline: null, GraceNotBefore: { } grace } &&
                Math.Abs((grace - unkeptAt).TotalSeconds) < 1,
                "A fold carries the folded row's grace to the surviving file and drops its expired schedule (Codex round 2 P1): " +
                $"{survivorEvaluation?.State}/{survivorEvaluation?.Deadline:O}/{survivorEvaluation?.GraceNotBefore:O}");
            var foldedGrab = await database.GrabOperations.AsNoTracking().SingleAsync(grab => grab.Id == foldedGrabId);
            Assert(foldedGrab.EpisodeId == expiredRow.Id,
                $"A fold moves the folded row's records to the surviving row (Codex delta review 2): {foldedGrab.EpisodeId}");
            database.GrabOperations.Remove(await database.GrabOperations.SingleAsync(grab => grab.Id == foldedGrabId));
            Assert(inheriting is { TmdbId: 8815, RetentionPolicy: RetentionPolicy.Inherit },
                "A folded row's shorter window never replaces the surviving row's inherited one: " +
                $"{inheriting?.TmdbId}/{inheriting?.RetentionPolicy}/{inheriting?.ReclaimAfterDays}");
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == graceSeries.Id));
            database.History.RemoveRange(database.History.Where(history => history.EntryId == graceSeries.Id));
            await database.SaveChangesAsync();
        }

        // P1.FU: a series reconciliation discovered in a TVDB-scraped library has only position rows for its files and no TMDB
        // list. The administrator's Refresh loads the whole list: a row with evidence is adopted, a held position without
        // evidence gets no second row, every other TMDB episode becomes an unmonitored row, and the first load is recorded.
        var discovered = new Entry
        {
            MediaType = "series", TmdbId = 8820, TargetLibraryId = tvLibrary.Id, Title = "Discovered", Monitored = false,
            State = FileState.OnDisk,
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 8820, "Discovered", null, null, null, null, null, null,
                false, null, null, [], [], []))
        };
        var discoveredPilot = new Episode { EntryId = discovered.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot",
            AirDate = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc), Monitored = false, State = FileState.OnDisk };
        var discoveredSecond = new Episode { EntryId = discovered.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 2,
            Title = "TVDB Name", AirDate = new DateTime(2021, 3, 8, 0, 0, 0, DateTimeKind.Utc), Monitored = false, State = FileState.OnDisk };
        var discoveredBinding = new EpisodeBinding { EpisodeId = discoveredPilot.Id, JellyfinItemId = Guid.NewGuid(),
            SeriesItemId = Guid.NewGuid(), TargetLibraryId = tvLibrary.Id, MediaPath = "/media/Discovered/S01E01.mkv" };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Add(discovered);
            database.Episodes.AddRange(discoveredPilot, discoveredSecond);
            database.EpisodeBindings.Add(discoveredBinding);
            database.History.Add(new HistoryRecord { EntryId = discovered.Id, EventType = "backfilled", Summary = "Discovered in Jellyfin library" });
            await database.SaveChangesAsync();
        }

        async Task<JsonElement> DiscoveredDetailAsync()
        {
            using var response = await client.GetAsync($"/JellyfinMod/Entries/{discovered.Id}");
            Assert(response.IsSuccessStatusCode, $"The discovered series' detail answers: {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        var beforeList = await DiscoveredDetailAsync();
        Assert(!beforeList.GetProperty("episodeListLoaded").GetBoolean() && beforeList.GetProperty("episodes").GetArrayLength() == 2,
            "A discovered series reports that its TMDB episode list is not loaded yet (P1.FU): " + beforeList.GetProperty("episodeListLoaded"));
        http.Response = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert((await client.PostAsync($"/JellyfinMod/Entries/{discovered.Id}/Refresh", null)).StatusCode == HttpStatusCode.NotFound,
            "A series TMDB no longer knows answers 404 and nothing is guessed (P1.FU)");
        var afterMissing = await DiscoveredDetailAsync();
        Assert(!afterMissing.GetProperty("episodeListLoaded").GetBoolean() && afterMissing.GetProperty("episodes").GetArrayLength() == 2,
            "A failed load leaves the list as it was and still not loaded (P1.FU)");

        var discoveredSnapshot = (Func<Uri, HttpResponseMessage>)(uri =>
            uri.AbsolutePath.Contains("/season/0", StringComparison.Ordinal)
                ? Json("""{"season_number":0,"episodes":[{"id":8829,"season_number":0,"episode_number":1,"name":"Behind the Scenes","air_date":"2021-02-20"}]}""")
            : uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
                ? Json("""{"season_number":1,"episodes":[{"id":8821,"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2021-03-01"},{"id":8822,"season_number":1,"episode_number":2,"name":"TMDB Name","air_date":"2021-03-10"},{"id":8823,"season_number":1,"episode_number":3,"name":"Not Downloaded","air_date":"2021-03-15"}]}""")
            : uri.AbsolutePath.Contains("/season/2", StringComparison.Ordinal)
                ? Json("""{"season_number":2,"episodes":[{"id":8824,"season_number":2,"episode_number":1,"name":"Next Year","air_date":"2099-03-01"}]}""")
            : Json("""{"id":8820,"name":"Discovered","seasons":[{"season_number":0,"name":"Specials","episode_count":1},{"season_number":1,"name":"Season 1","episode_count":3},{"season_number":2,"name":"Season 2","episode_count":1}]}"""));
        http.Response = discoveredSnapshot;
        using (var loaded = await client.PostAsync($"/JellyfinMod/Entries/{discovered.Id}/Refresh", null))
        {
            var body = await loaded.Content.ReadAsStringAsync();
            Assert(loaded.IsSuccessStatusCode, $"The administrator's Refresh loads a discovered series' list: {(int)loaded.StatusCode} {body}");
            using var json = JsonDocument.Parse(body);
            var rows = json.RootElement.GetProperty("episodes").EnumerateArray()
                .ToDictionary(row => (row.GetProperty("seasonNumber").GetInt32(), row.GetProperty("episodeNumber").GetInt32()));
            var summary = string.Join(", ", rows.Values.Select(row => $"S{row.GetProperty("seasonNumber")}E{row.GetProperty("episodeNumber")} " +
                $"{row.GetProperty("tmdbId")} {row.GetProperty("availability")} {row.GetProperty("monitored")}"));
            Assert(json.RootElement.GetProperty("episodeListLoaded").GetBoolean() && rows.Count == 5 &&
                Guid.Parse(rows[(1, 1)].GetProperty("id").GetString()!) == discoveredPilot.Id && rows[(1, 1)].GetProperty("tmdbId").GetInt32() == 8821 &&
                Guid.Parse(rows[(1, 2)].GetProperty("id").GetString()!) == discoveredSecond.Id && rows[(1, 2)].GetProperty("tmdbId").GetInt32() == 0 &&
                rows[(1, 3)].GetProperty("availability").GetString() == "missing" &&
                rows[(2, 1)].GetProperty("availability").GetString() == "unaired" &&
                rows[(0, 1)].GetProperty("tmdbId").GetInt32() == 8829 &&
                rows.Values.All(row => !row.GetProperty("monitored").GetBoolean()),
                "The full list arrives: the pilot is adopted by its title, the held S01E02 gets no second row, the missing, " +
                "unaired and special episodes are rows of their own, and none of them is monitored (P1.FU): " + summary);
            var listed = json.RootElement.GetProperty("history").EnumerateArray()
                .Where(record => record.GetProperty("eventType").GetString() == "episodes_listed").ToArray();
            Assert(listed.Length == 1 && listed[0].GetProperty("summary").GetString() == "Loaded 3 seasons and 5 episodes from TMDB",
                "The first load is recorded once in History (P1.FU): " + json.RootElement.GetProperty("history"));
        }

        using (var again = await client.PostAsync($"/JellyfinMod/Entries/{discovered.Id}/Refresh", null))
        await using (var database = new ModDbContext(dbPath))
        {
            var rows = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == discovered.Id).ToListAsync();
            var pilotBinding = await database.EpisodeBindings.AsNoTracking().SingleAsync(binding => binding.Id == discoveredBinding.Id);
            Assert(again.IsSuccessStatusCode && rows.Count == 5 && pilotBinding.EpisodeId == discoveredPilot.Id &&
                !rows.Any(row => row.TmdbId != 0 && row.SeasonNumber == 1 && row.EpisodeNumber == 2) &&
                await database.History.CountAsync(record => record.EntryId == discovered.Id && record.EventType == "episodes_listed") == 1 &&
                !(await database.Entries.SingleAsync(entry => entry.Id == discovered.Id)).Monitored,
                "A second Refresh changes nothing: the file stays on its row, no TMDB row joins a held position, the load is " +
                "recorded once and the series stays unmonitored (P1.FU)");
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == discovered.Id));
            database.History.RemoveRange(database.History.Where(history => history.EntryId == discovered.Id));
            await database.SaveChangesAsync();
        }

        // P1.FU9: anyone who may see a series loads its full TMDB list by opening it, through EpisodeList. It loads once, shares
        // one TMDB fetch between concurrent callers, answers a loaded list or a movie without asking TMDB, is refused like the
        // detail to a user who may not see the entry, and a failure leaves the list unloaded for the next opening.
        using var ordinary = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
        ordinary.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        using var restricted = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
        restricted.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        var opened = new Entry
        {
            MediaType = "series", TmdbId = 8840, TargetLibraryId = tvLibrary.Id, Title = "Opened", Monitored = false,
            State = FileState.OnDisk,
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 8840, "Opened", null, null, null, null, null, null,
                false, null, null, [], [], []))
        };
        var openedPilot = new Episode { EntryId = opened.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot",
            AirDate = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc), Monitored = false, State = FileState.OnDisk };
        var openedMovie = new Entry
        {
            MediaType = "movie", TmdbId = 8849, TargetLibraryId = libraryFolder.Id, Title = "Opened Movie", Monitored = false,
            State = FileState.Reclaimed,
            MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 8849, "Opened Movie", null, null, null, null, null, null,
                false, null, null, [], [], []))
        };
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.AddRange(opened, openedMovie);
            database.Episodes.Add(openedPilot);
            await database.SaveChangesAsync();
        }
        string[] OpenedRequests() => http.Requests.Where(request => request.StartsWith("/3/tv/8840", StringComparison.Ordinal)).ToArray();
        async Task<(HttpStatusCode Status, JsonElement Body)> OpenAsync(HttpClient caller, Guid entryId)
        {
            using var response = await caller.PostAsync($"/JellyfinMod/Entries/{entryId}/EpisodeList", null);
            var text = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return (response.StatusCode, json.RootElement.Clone());
        }
        async Task<bool> OpenedLoadedAsync()
        {
            using var response = await ordinary.GetAsync($"/JellyfinMod/Entries/{opened.Id}");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("episodeListLoaded").GetBoolean();
        }

        http.Requests.Clear();
        Assert((await OpenAsync(restricted, opened.Id)).Status == HttpStatusCode.NotFound && OpenedRequests().Length == 0,
            "A user who may not see a series cannot load its list, and is refused like its detail (P1.FU9)");
        var openedMovieResult = await OpenAsync(ordinary, openedMovie.Id);
        Assert(openedMovieResult.Status == HttpStatusCode.OK && openedMovieResult.Body.GetProperty("episodeListLoaded").GetBoolean() &&
            !http.Requests.Any(request => request.StartsWith("/3/movie/8849", StringComparison.Ordinal)),
            "A movie's detail is answered unchanged without asking TMDB (P1.FU9): " + openedMovieResult.Status);

        // Failures map as the Refresh's, ask TMDB once per call and leave the list unloaded.
        var (token, apiKey) = (pluginConfiguration.TmdbReadAccessToken, pluginConfiguration.TmdbApiKey);
        (pluginConfiguration.TmdbReadAccessToken, pluginConfiguration.TmdbApiKey) = (string.Empty, string.Empty);
        var notConfigured = await OpenAsync(ordinary, opened.Id);
        (pluginConfiguration.TmdbReadAccessToken, pluginConfiguration.TmdbApiKey) = (token, apiKey);
        Assert(notConfigured.Status == HttpStatusCode.ServiceUnavailable && OpenedRequests().Length == 0 && !await OpenedLoadedAsync(),
            "Without a TMDB credential the load answers 503 and the list stays unloaded (P1.FU9): " + notConfigured.Status);
        (pluginConfiguration.TmdbReadAccessToken, pluginConfiguration.TmdbApiKey) = (string.Empty, string.Empty);
        http.Requests.Clear();
        using var unconfiguredSearch = await ordinary.GetAsync("/JellyfinMod/Discover/Search?q=title&type=movie");
        (pluginConfiguration.TmdbReadAccessToken, pluginConfiguration.TmdbApiKey) = (token, apiKey);
        Assert(unconfiguredSearch.StatusCode == HttpStatusCode.ServiceUnavailable && http.Requests.IsEmpty,
            "Without a TMDB credential and with no Jellyfin TMDb connection in the host, discovery still answers 503 and asks no TMDB (P1.FU13): " +
            unconfiguredSearch.StatusCode);
        http.Response = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var unknown = await OpenAsync(ordinary, opened.Id);
        Assert(unknown.Status == HttpStatusCode.NotFound && OpenedRequests().Length == 1 && !await OpenedLoadedAsync(),
            "A series TMDB no longer knows answers 404 after one request and stays unloaded (P1.FU9): " +
            $"{unknown.Status} {string.Join(" | ", OpenedRequests())}");
        http.Requests.Clear();
        http.Response = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var broken = await OpenAsync(ordinary, opened.Id);
        Assert(broken.Status == HttpStatusCode.BadGateway && OpenedRequests().Length == 1 && !await OpenedLoadedAsync(),
            "A TMDB failure answers 502 after one request and the list stays unloaded (P1.FU9): " + broken.Status);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.BeforeResponse = async uri =>
        {
            if (uri.AbsolutePath.StartsWith("/3/tv/8840", StringComparison.Ordinal)) await releaseSlow.Task;
        };
        var slow = await OpenAsync(ordinary, opened.Id);
        releaseSlow.TrySetResult();
        http.BeforeResponse = null;
        Assert(slow.Status == HttpStatusCode.GatewayTimeout && !await OpenedLoadedAsync(),
            "A TMDB request that times out answers 504 and the list stays unloaded (P1.FU9): " + slow.Status);
        // P1.FU14: the season's overview and each episode's rating come with the same requests. P1.FU15: so do each episode's
        // guest stars and crew (the pilot has 17 guest stars listed out of order, and crew Jellyfin keeps and drops; the second
        // episode names nobody; season 2's response has no credit lists at all), and the series' regular cast, appended to
        // the series' details (16 regulars, one unnamed).
        static string People(int count, int first, string role) => string.Join(",", Enumerable.Range(0, count).Reverse().Select(index =>
            $$"""{"id":{{first + index}},"name":" {{role}} {{index}} ","character":"Role {{index}}","profile_path":{{(index % 2 == 0 ? $"\"/p{first + index}.jpg\"" : "null")}},"order":{{index}}}"""));
        const string pilotCrew = """[{"id":71,"name":"Dee Rector","department":"Directing","job":"Director","profile_path":"/d.jpg"},{"id":72,"name":"Ed Itor","department":"Editing","job":"Editor"},{"id":73,"name":"Wren Writer","department":"Writing","job":"Screenplay"},{"id":74,"name":"Story Teller","department":"Writing","job":"Story"},{"id":75,"name":"Pro Ducer","department":"Production","job":"Producer"},{"id":76,"name":"  ","department":"Directing","job":"Director"},{"id":77,"name":"Exec Utive","department":"Production","job":"Executive Producer"},{"id":71,"name":"Dee Rector","department":"Writing","job":"Writer","profile_path":"/d.jpg"}]""";
        var openedSnapshot = (Func<Uri, HttpResponseMessage>)(uri =>
            uri.AbsolutePath.Contains("/season/1", StringComparison.Ordinal)
                ? Json($$$"""{"season_number":1,"episodes":[{"id":8841,"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2021-03-01","vote_average":7.46,"still_path":"/pilot.jpg","overview":"The pilot.","runtime":52,"guest_stars":[{{{People(17, 500, "Guest")}}}],"crew":{{{pilotCrew}}}},{"id":8842,"season_number":1,"episode_number":2,"name":"Second","air_date":"2021-03-08","vote_average":0,"guest_stars":[],"crew":[]}]}""")
            : uri.AbsolutePath.Contains("/season/2", StringComparison.Ordinal)
                ? Json("""{"season_number":2,"episodes":[{"id":8843,"season_number":2,"episode_number":1,"name":"Return","air_date":"2022-03-01"}]}""")
            : Json($$$"""{"id":8840,"name":"Opened","seasons":[{"season_number":1,"name":"Season 1","episode_count":2,"overview":"The first season.","poster_path":"/s1.jpg"},{"season_number":2,"name":"Season 2","episode_count":1}],"credits":{"cast":[{{{People(16, 600, "Regular")}}},{"id":699,"name":"","order":-1}]}}"""));
        http.Response = openedSnapshot;
        await using (var lease = await app.Services.GetRequiredService<ReconciliationLibraryLock>()
            .TryAcquireAsync(tvLibrary.Id, TimeSpan.FromSeconds(5), default))
        {
            Assert(lease is not null, "The smoke holds the TV library for the busy case");
            var busy = await OpenAsync(ordinary, opened.Id);
            Assert(busy.Status == HttpStatusCode.ServiceUnavailable && busy.Body.GetProperty("type").GetString() == "library_busy" &&
                !await OpenedLoadedAsync(),
                "A library busy being checked answers 503 library_busy and the list stays unloaded (P1.FU9): " + busy.Status);
        }

        // Two concurrent openings share one load: the second waits for the first's TMDB fetch and answers the loaded list.
        http.Requests.Clear();
        var firstReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        http.BeforeResponse = async uri =>
        {
            if (!uri.AbsolutePath.StartsWith("/3/tv/8840", StringComparison.Ordinal)) return;
            firstReached.TrySetResult();
            await releaseFirst.Task;
        };
        var firstOpening = OpenAsync(ordinary, opened.Id);
        await firstReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondOpening = OpenAsync(ordinary, opened.Id);
        await Task.Delay(300);
        releaseFirst.TrySetResult();
        var openings = await Task.WhenAll(firstOpening, secondOpening);
        http.BeforeResponse = null;
        foreach (var opening in openings)
        {
            var rows = opening.Body.GetProperty("episodes").EnumerateArray()
                .Select(row => (row.GetProperty("seasonNumber").GetInt32(), row.GetProperty("episodeNumber").GetInt32(),
                    row.GetProperty("tmdbId").GetInt32(), row.GetProperty("monitored").GetBoolean())).ToArray();
            Assert(opening.Status == HttpStatusCode.OK && opening.Body.GetProperty("episodeListLoaded").GetBoolean() &&
                rows.Length == 3 && rows.Contains((1, 1, 8841, false)) && rows.Contains((1, 2, 8842, false)) && rows.Contains((2, 1, 8843, false)),
                "An ordinary user's opening loads the full list, unmonitored for an unmonitored series (P1.FU9): " +
                $"{opening.Status} {string.Join(", ", rows)}");
        }
        Assert(OpenedRequests().Length == 2,
            "Two concurrent openings make one load: the series details and one appended season request (P1.FU9): " +
            string.Join(" | ", OpenedRequests()));
        await using (var database = new ModDbContext(dbPath))
        {
            var listed = await database.History.AsNoTracking()
                .Where(record => record.EntryId == opened.Id && record.EventType == "episodes_listed").ToListAsync();
            Assert(listed.Count == 1 && listed[0].Summary == "Loaded 2 seasons and 3 episodes from TMDB" &&
                (await database.Episodes.AsNoTracking().SingleAsync(episode => episode.Id == openedPilot.Id)).TmdbId == 8841,
                "The opening records the first load once, as the Refresh does, and adopts the pilot by its title (P1.FU9)");
        }
        http.Requests.Clear();
        var reopened = await OpenAsync(ordinary, opened.Id);
        Assert(reopened.Status == HttpStatusCode.OK && reopened.Body.GetProperty("episodes").GetArrayLength() == 3 &&
            OpenedRequests().Length == 0,
            "Opening a loaded series again answers its list without asking TMDB (P1.FU9): " + string.Join(" | ", OpenedRequests()));
        // P1.FU14: an episode Jellyfin has no item for is drawn like one it has, so the detail carries the season's overview and
        // poster, the episode's still, overview and runtime, and TMDB's episode rating (an unrated episode has none).
        static string Covers(JsonElement detail)
        {
            var metadata = detail.GetProperty("entry").GetProperty("metadata");
            var season = metadata.GetProperty("seasons").EnumerateArray().Single(row => row.GetProperty("number").GetInt32() == 1);
            var pilot = detail.GetProperty("episodes").EnumerateArray().Single(row => row.GetProperty("tmdbId").GetInt32() == 8841);
            var ratings = metadata.GetProperty("episodeRatings");
            return string.Join("|", season.GetProperty("overview").GetString(), season.GetProperty("posterPath").GetString(),
                pilot.GetProperty("stillPath").GetString(), pilot.GetProperty("overview").GetString(),
                pilot.GetProperty("runtimeMinutes").GetInt32(), ratings.ValueKind == JsonValueKind.Object
                    ? string.Join(",", ratings.EnumerateObject().Select(rating => rating.Name + "=" + rating.Value.GetDouble()))
                    : ratings.ValueKind.ToString());
        }
        const string expectedCovers = "The first season.|/s1.jpg|/pilot.jpg|The pilot.|52|8841=7.5";
        Assert(Covers(reopened.Body) == expectedCovers,
            "The loaded list carries the season overview and poster, the episode's still, overview, runtime and rating (P1.FU14): " +
            Covers(reopened.Body));
        // P1.FU15: an episode Jellyfin has no item for gets the people a downloaded one shows. Each episode carries its guest stars
        // (TMDB's order, at most 15, names trimmed) and the crew Jellyfin keeps (directors, writers by writer, screenplay or
        // novel, producers; not an editor, a story credit or an executive producer, nor an unnamed person), in TMDB's order;
        // an episode TMDB names nobody for has empty lists, never null; the series keeps its first 15 named regulars.
        static string Text(JsonElement person, string name) =>
            person.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "-";
        static string Credits(JsonElement detail, int tmdbId)
        {
            var credits = detail.GetProperty("episodes").EnumerateArray().Single(row => row.GetProperty("tmdbId").GetInt32() == tmdbId)
                .GetProperty("credits");
            if (credits.ValueKind != JsonValueKind.Object) return credits.ValueKind.ToString();
            var guests = credits.GetProperty("guestStars").EnumerateArray().ToArray();
            return $"{guests.Length}:" + string.Join(",", guests.Take(2).Concat(guests.TakeLast(1)).Select(guest =>
                    $"{guest.GetProperty("id").GetInt32()}/{Text(guest, "name")}/{Text(guest, "character")}/{Text(guest, "profilePath")}")) +
                "|" + string.Join(",", credits.GetProperty("crew").EnumerateArray().Select(person =>
                    $"{person.GetProperty("id").GetInt32()}/{Text(person, "name")}/{Text(person, "type")}/{Text(person, "job")}"));
        }
        static string SeriesCast(JsonElement detail)
        {
            var metadata = detail.GetProperty("entry").GetProperty("metadata");
            if (!metadata.TryGetProperty("cast", out var cast) || cast.ValueKind != JsonValueKind.Array) return "absent";
            var people = cast.EnumerateArray().ToArray();
            return $"{people.Length}:" + string.Join(",", people.Take(1).Concat(people.TakeLast(1)).Select(person =>
                $"{Text(person, "name")}/{Text(person, "character")}/{person.GetProperty("order").GetInt32()}"));
        }
        const string expectedPilot = "15:500/Guest 0/Role 0//p500.jpg,501/Guest 1/Role 1/-,514/Guest 14/Role 14//p514.jpg|" +
            "71/Dee Rector/Director/Director,73/Wren Writer/Writer/Screenplay,75/Pro Ducer/Producer/Producer,71/Dee Rector/Writer/Writer";
        const string expectedCast = "15:Regular 0/Role 0/0,Regular 14/Role 14/14";
        string CreditsSummary(JsonElement detail) =>
            $"{Credits(detail, 8841)} || {Credits(detail, 8842)} || {Credits(detail, 8843)} || {SeriesCast(detail)}";
        var expectedCredits = $"{expectedPilot} || 0:| || 0:| || {expectedCast}";
        Assert(CreditsSummary(reopened.Body) == expectedCredits,
            "The loaded list carries each episode's guest stars and crew as Jellyfin keeps them, empty lists where TMDB names nobody, " +
            "and the series' regular cast (P1.FU15): " + CreditsSummary(reopened.Body));
        using (var plainDetail = await ordinary.GetAsync($"/JellyfinMod/Entries/{opened.Id}"))
        {
            using var json = JsonDocument.Parse(await plainDetail.Content.ReadAsStringAsync());
            Assert(plainDetail.IsSuccessStatusCode && CreditsSummary(json.RootElement) == expectedCredits,
                "GET Entries/{id} returns the same credits and cast (P1.FU15): " + CreditsSummary(json.RootElement));
        }
        // A list loaded before the series' metadata kept ratings is loaded once more by the next opening, and then not again.
        await using (var database = new ModDbContext(dbPath))
        {
            var stale = await database.Entries.SingleAsync(entry => entry.Id == opened.Id);
            stale.MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 8840, "Opened", null, null, null, null, null, null,
                false, null, null, [], [], [new TmdbSeason(1, "Season 1", 2, null, null), new TmdbSeason(2, "Season 2", 1, null, null)]));
            await database.SaveChangesAsync();
        }
        Assert(await OpenedLoadedAsync(), "A list loaded before P1.FU14 still reads as loaded (P1.FU14)");
        http.Requests.Clear();
        var backfilled = await OpenAsync(ordinary, opened.Id);
        var backfillRequests = OpenedRequests().Length;
        http.Requests.Clear();
        var settled = await OpenAsync(ordinary, opened.Id);
        Assert(backfilled.Status == HttpStatusCode.OK && backfillRequests > 0 && Covers(backfilled.Body) == expectedCovers &&
            backfilled.Body.GetProperty("episodes").GetArrayLength() == 3 && settled.Status == HttpStatusCode.OK && OpenedRequests().Length == 0,
            "Opening a series whose list predates P1.FU14 loads it once more and fills the overviews and ratings, then answers " +
            $"without TMDB (P1.FU14): {backfilled.Status} {backfillRequests} {Covers(backfilled.Body)} then {OpenedRequests().Length}");
        // P1.FU15: a list loaded with ratings but before credits were kept (no cast in the series' metadata, no credits on its
        // episodes, as the EpisodeCredits migration leaves every row) shows null credits, is loaded once more by the next
        // opening, and is then answered without TMDB, credits filled. A series TMDB names no regulars for is not reloaded.
        await using (var database = new ModDbContext(dbPath))
        {
            var stale = await database.Entries.SingleAsync(entry => entry.Id == opened.Id);
            stale.MetadataJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<TmdbMetadata>(stale.MetadataJson!)! with { Cast = null });
            foreach (var row in await database.Episodes.Where(episode => episode.EntryId == opened.Id).ToListAsync()) row.CreditsJson = null;
            await database.SaveChangesAsync();
        }
        using (var staleDetail = await ordinary.GetAsync($"/JellyfinMod/Entries/{opened.Id}"))
        {
            using var json = JsonDocument.Parse(await staleDetail.Content.ReadAsStringAsync());
            Assert(CreditsSummary(json.RootElement) == "Null || Null || Null || absent" && json.RootElement.GetProperty("episodeListLoaded").GetBoolean(),
                "A list loaded before P1.FU15 reads as loaded, with null credits and no cast (P1.FU15): " + CreditsSummary(json.RootElement));
        }
        http.Requests.Clear();
        var creditsBackfill = await OpenAsync(ordinary, opened.Id);
        var creditsRequests = OpenedRequests().Length;
        http.Requests.Clear();
        var creditsSettled = await OpenAsync(ordinary, opened.Id);
        Assert(creditsBackfill.Status == HttpStatusCode.OK && creditsRequests > 0 && CreditsSummary(creditsBackfill.Body) == expectedCredits &&
            creditsSettled.Status == HttpStatusCode.OK && OpenedRequests().Length == 0 && CreditsSummary(creditsSettled.Body) == expectedCredits,
            "Opening a series whose list predates P1.FU15 loads it once more and fills the credits and cast, then answers without " +
            $"TMDB (P1.FU15): {creditsBackfill.Status} {creditsRequests} {CreditsSummary(creditsBackfill.Body)} then {OpenedRequests().Length}");
        await using (var database = new ModDbContext(dbPath))
        {
            var castlessEntry = await database.Entries.SingleAsync(entry => entry.Id == opened.Id);
            castlessEntry.MetadataJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<TmdbMetadata>(castlessEntry.MetadataJson!)! with { Cast = [] });
            await database.SaveChangesAsync();
        }
        http.Requests.Clear();
        var castless = await OpenAsync(ordinary, opened.Id);
        Assert(castless.Status == HttpStatusCode.OK && OpenedRequests().Length == 0 && SeriesCast(castless.Body) == "0:",
            "A series whose stored cast is empty (TMDB names no regulars) is not loaded again (P1.FU15): " + OpenedRequests().Length);

        // The administrator's Refresh still refreshes a loaded list, and stays administrator-only.
        Assert((await ordinary.PostAsync($"/JellyfinMod/Entries/{opened.Id}/Refresh", null)).StatusCode == HttpStatusCode.Forbidden,
            "Refresh stays administrator-only (P1.FU9)");
        http.Response = uri => uri.AbsolutePath.Contains("/season/2", StringComparison.Ordinal)
            ? Json("""{"season_number":2,"episodes":[{"id":8843,"season_number":2,"episode_number":1,"name":"Return","air_date":"2022-03-01"},{"id":8844,"season_number":2,"episode_number":2,"name":"Newly Listed","air_date":"2022-03-08"}]}""")
            : openedSnapshot(uri);
        using (var openedRefresh = await client.PostAsync($"/JellyfinMod/Entries/{opened.Id}/Refresh", null))
        {
            using var json = JsonDocument.Parse(await openedRefresh.Content.ReadAsStringAsync());
            Assert(openedRefresh.IsSuccessStatusCode && json.RootElement.GetProperty("episodes").GetArrayLength() == 4 &&
                json.RootElement.GetProperty("history").EnumerateArray()
                    .Count(record => record.GetProperty("eventType").GetString() == "episodes_listed") == 1,
                "The administrator's Refresh adds a newly listed episode to a loaded list and records no second load (P1.FU9): " +
                (int)openedRefresh.StatusCode);
        }
        http.Response = null;
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.RemoveRange(await database.Entries.Where(entry => entry.Id == opened.Id || entry.Id == openedMovie.Id).ToListAsync());
            database.History.RemoveRange(database.History.Where(history => history.EntryId == opened.Id || history.EntryId == openedMovie.Id));
            await database.SaveChangesAsync();
        }

        // P1.FU4: a series' seasons arrive appended to one request, up to twenty seasons each, instead of one request per
        // season; a season TMDB leaves out is read on its own.
        Func<Uri, HttpResponseMessage> ManySeasons(int tmdbId, int seasons) => uri =>
        {
            var marker = "/season/";
            var at = uri.AbsolutePath.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                return Json(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = tmdbId, ["name"] = "Long running " + tmdbId,
                    ["seasons"] = Enumerable.Range(1, seasons).Select(number => new Dictionary<string, object>
                        { ["season_number"] = number, ["name"] = "Season " + number, ["episode_count"] = 2 }).ToArray()
                }));
            var season = int.Parse(uri.AbsolutePath[(at + marker.Length)..], System.Globalization.CultureInfo.InvariantCulture);
            return Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["season_number"] = season,
                ["episodes"] = Enumerable.Range(1, 2).Select(number => new Dictionary<string, object>
                {
                    ["id"] = tmdbId * 100 + season * 10 + number, ["season_number"] = season, ["episode_number"] = number,
                    ["name"] = $"S{season}E{number}"
                }).ToArray()
            }));
        };

        async Task<(Guid EntryId, string[] Requests)> AddManySeasonsAsync(int tmdbId, int seasons)
        {
            http.Response = ManySeasons(tmdbId, seasons);
            http.Requests.Clear();
            using var added = await client.PostAsJsonAsync("/JellyfinMod/Entries", new { mediaType = "series", tmdbId, targetLibraryId = tvLibrary.Id });
            var body = await added.Content.ReadAsStringAsync();
            Assert(added.IsSuccessStatusCode, $"A {seasons}-season series is added: {(int)added.StatusCode} {body}");
            using var json = JsonDocument.Parse(body);
            var entryId = Guid.Parse(json.RootElement.GetProperty("entry").GetProperty("id").GetString()!);
            await using var database = new ModDbContext(dbPath);
            var rows = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId).ToListAsync();
            Assert(rows.Count == seasons * 2 && rows.Select(row => row.SeasonNumber).Distinct().Count() == seasons,
                $"Every season's episodes of a {seasons}-season series are stored: {rows.Count}");
            return (entryId, http.Requests.Where(request => request.StartsWith("/3/tv/" + tmdbId, StringComparison.Ordinal)).ToArray());
        }

        async Task RemoveEntryAsync(Guid entryId)
        {
            await using var database = new ModDbContext(dbPath);
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == entryId));
            database.History.RemoveRange(database.History.Where(history => history.EntryId == entryId));
            await database.SaveChangesAsync();
        }

        var (threeSeasons, threeRequests) = await AddManySeasonsAsync(8830, 3);
        Assert(threeRequests.Length == 2 && threeRequests.Count(request => request.Contains("season/1,season/2,season/3", StringComparison.Ordinal)) == 1 &&
            !threeRequests.Any(request => request.Contains("/season/", StringComparison.Ordinal)),
            "A three-season series is read with the series details and one request for every season (P1.FU4): " + string.Join(" | ", threeRequests));
        http.OmitAppendedSeason = season => season == 2;
        http.Requests.Clear();
        using (var refreshedOmitted = await client.PostAsync($"/JellyfinMod/Entries/{threeSeasons}/Refresh", null))
        await using (var database = new ModDbContext(dbPath))
        {
            var requests = http.Requests.Where(request => request.StartsWith("/3/tv/8830", StringComparison.Ordinal)).ToArray();
            Assert(refreshedOmitted.IsSuccessStatusCode && await database.Episodes.CountAsync(episode => episode.EntryId == threeSeasons) == 6 &&
                requests.Count(request => request.StartsWith("/3/tv/8830/season/", StringComparison.Ordinal)) == 1 &&
                requests.Single(request => request.StartsWith("/3/tv/8830/season/", StringComparison.Ordinal)).StartsWith("/3/tv/8830/season/2?", StringComparison.Ordinal),
                "A season TMDB leaves out of the appended response is read on its own instead of failing the series (P1.FU4): " +
                string.Join(" | ", requests));
        }
        http.OmitAppendedSeason = null;
        await RemoveEntryAsync(threeSeasons);

        var (longRunning, longRequests) = await AddManySeasonsAsync(8831, 25);
        var appendedRequests = longRequests.Where(request => request.Contains("season/", StringComparison.Ordinal)).ToArray();
        Assert(longRequests.Length == 3 && appendedRequests.Length == 2 &&
            appendedRequests.Select(request => request.Split("season/").Length - 1).Order().SequenceEqual([5, 20]) &&
            !longRequests.Any(request => request.Contains("/season/", StringComparison.Ordinal)),
            "A 25-season series takes two appended requests of at most twenty seasons (P1.FU4): " + string.Join(" | ", longRequests));
        await RemoveEntryAsync(longRunning);
        http.Response = null;

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
        await VerifyEpisodeConflictsAsync(client, dbPath, user, otherUser, tvLibrary, nativeById);
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
    private static async Task VerifyEpisodeConflictsAsync(HttpClient client, string dbPath, User user, User otherUser,
        EmptyMovieLibrary tvLibrary, Dictionary<Guid, BaseItem> nativeById)
    {
        var libraryId = tvLibrary.Id;
        // A conflict is reported on a bound native series: its two episodes are Jellyfin's own items.
        var pilotItem = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "Pilot", ParentIndexNumber = 1, IndexNumber = 1 };
        var secondItem = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "Third", ParentIndexNumber = 1, IndexNumber = 3 };
        var nativeSeries = new TestSeries { Id = Guid.NewGuid(), Name = "Conflicted", SortName = "conflicted", Episodes = [pilotItem, secondItem] };
        var previousItems = tvLibrary.Items;
        tvLibrary.Items = [.. previousItems, nativeSeries];
        nativeById[nativeSeries.Id] = nativeSeries;
        nativeById[pilotItem.Id] = pilotItem;
        nativeById[secondItem.Id] = secondItem;
        var series = new Entry { MediaType = "series", TmdbId = 88200, TargetLibraryId = libraryId, Title = "Conflicted",
            JellyfinItemId = nativeSeries.Id };
        var pilot = new Episode { EntryId = series.Id, TmdbId = 88201, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot",
            State = FileState.OnDisk };
        var second = new Episode { EntryId = series.Id, TmdbId = 88203, SeasonNumber = 1, EpisodeNumber = 3, Title = "Third",
            State = FileState.OnDisk };
        var pilotNative = pilotItem.Id;
        var secondNative = secondItem.Id;
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
        // Whole-review chunk 3b, P2 5: an administrator limited to other libraries neither lists nor resolves these
        // conflicts, and one whose blocked tag hides an episode neither lists nor resolves that episode's conflict.
        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", otherUser.Id.ToString());
        using (var restrictedList = await client.GetAsync("/JellyfinMod/Reconciliation/Conflicts"))
        {
            using var restrictedJson = JsonDocument.Parse(await restrictedList.Content.ReadAsStringAsync());
            Assert(restrictedList.StatusCode == HttpStatusCode.OK && restrictedJson.RootElement.GetArrayLength() == 0 &&
                (await client.PostAsync($"/JellyfinMod/Reconciliation/Conflicts/{rebind.Id}/Rebind", null)).StatusCode == HttpStatusCode.NotFound,
                "An administrator without the library neither lists nor rebinds its conflicts (whole-review c3bf5)");
        }

        client.DefaultRequestHeaders.Remove("X-Smoke-User");
        client.DefaultRequestHeaders.Add("X-Smoke-User", user.Id.ToString());
        secondItem.Tags = ["hidden-episode"];
        user.SetPreference(PreferenceKind.BlockedTags, ["hidden-episode"]);
        using (var taggedList = await client.GetAsync("/JellyfinMod/Reconciliation/Conflicts"))
        {
            using var taggedJson = JsonDocument.Parse(await taggedList.Content.ReadAsStringAsync());
            Assert(taggedList.StatusCode == HttpStatusCode.OK && taggedJson.RootElement.GetArrayLength() == 1 &&
                Guid.Parse(taggedJson.RootElement[0].GetProperty("id").GetString()!) == rebind.Id &&
                (await client.PostAsync($"/JellyfinMod/Reconciliation/Conflicts/{keep.Id}/Keep", null)).StatusCode == HttpStatusCode.NotFound,
                "An administrator from whom a blocked tag hides an episode neither lists nor keeps its conflict (whole-review c3bf5): " +
                taggedJson.RootElement.GetRawText());
        }

        user.SetPreference(PreferenceKind.BlockedTags, []);
        secondItem.Tags = [];
        using var listed = await client.GetAsync("/JellyfinMod/Reconciliation/Conflicts");
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert(listed.StatusCode == HttpStatusCode.OK && listedJson.RootElement.GetArrayLength() == 2,
            "The admin conflict view lists every open episode conflict");
        // Each entry says when it was found, as a UTC stamp (settings polish, 2026-10-09).
        Assert(listedJson.RootElement.EnumerateArray().All(row => row.TryGetProperty("detectedAt", out var detected) &&
                detected.GetString()!.EndsWith('Z') && DateTime.Parse(detected.GetString()!).ToUniversalTime() > DateTime.UtcNow.AddHours(-1)),
            "Every listed episode conflict carries when it was detected, in UTC");
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
        tvLibrary.Items = previousItems;
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
        Assert(orphansJson.RootElement.EnumerateArray().All(row => row.TryGetProperty("addedAt", out var added) &&
                added.GetString()!.EndsWith('Z') && DateTime.Parse(added.GetString()!).ToUniversalTime() > DateTime.UtcNow.AddHours(-1)),
            "Every listed orphan carries when it was added, in UTC");
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
        // Like Jellyfin, a user's blocked tags hide an episode from that user's queries.
        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            var blocked = query.User?.GetPreference(PreferenceKind.BlockedTags) ?? [];
            var items = Episodes.Where(episode => !episode.Tags.Intersect(blocked, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
            return new() { Items = items, TotalRecordCount = items.Length };
        }
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
