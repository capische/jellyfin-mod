using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Ratings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Phase 9 ratings (R2-R5, the R8 suite): a real Kestrel plugin host with authentication, authorization, MVC serialization,
// EF migrations and SQLite, against a real HTTP boundary that answers as MDBList and as TMDB. Simulated host services:
// Jellyfin's users, library roots and item store (DispatchProxy stubs), as in every plugin suite; nothing calls a third party.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-nine-ratings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var logs = new CapturingLoggerProvider();
try
{
    await MigrationAsync(folder);
    await RunAsync(folder, logs);
    Console.WriteLine($"PASS: Phase 9 ratings — migration, settings, secret, Test, fetcher, budget, breaker, claims, projection, access ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

// ---- R2: the migration on top of the released 0.1.0.0 schema, and on a copy of a real database when one is given.
static async Task MigrationAsync(string folder)
{
    var dbPath = Path.Combine(folder, "released.db");
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20261002014147_WholeReviewQueueCleanupManifest");
        var entryId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
            VALUES ({entryId}, 'movie', 1, 'Existing', 4, 1, '2026-09-01T00:00:00', {Guid.NewGuid()}, 0)
            """);
        Assert(!(await TableNamesAsync(database)).Contains("TitleRatings"), "The released 0.1.0.0 schema has no ratings tables");
        await database.Database.MigrateAsync();
        var tables = await TableNamesAsync(database);
        Assert(new[] { "RatingsSettings", "RatingsProviderStates", "TitleRatings", "RatingsFetches" }.All(tables.Contains),
            "PhaseNineRatings adds the four ratings tables on top of the released schema");
        Assert(await database.Entries.AnyAsync(entry => entry.Id == entryId), "Existing rows survive the Phase 9 migration");
        var unique = await database.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_index_list('TitleRatings') WHERE \"unique\" = 1").ToListAsync();
        Assert(unique.Contains("IX_TitleRatings_EntryId_Source_Provider"), "One stored rating per entry, source and provider is enforced");
        var fetchUnique = await database.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_index_list('RatingsFetches') WHERE \"unique\" = 1").ToListAsync();
        Assert(fetchUnique.Contains("IX_RatingsFetches_EntryId"), "One fetch row per entry keeps the attempt log bounded");
        Assert((await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync())
            .SequenceEqual(["ok"]), "The migrated database passes its integrity check");
        Assert(!(await database.Database.GetPendingMigrationsAsync()).Any(), "No migration is left pending");
    }

    if (Environment.GetEnvironmentVariable("RATINGS_DB_COPY") is { Length: > 0 } source && File.Exists(source))
    {
        var copy = Path.Combine(folder, "instance-copy.db");
        File.Copy(source, copy);
        await using var database = new ModDbContext(copy);
        var before = await CountsAsync(database);
        var pending = (await database.Database.GetPendingMigrationsAsync()).ToList();
        await database.Database.MigrateAsync();
        var after = await CountsAsync(database);
        Assert(pending.Any(name => name.EndsWith("_PhaseNineRatings", StringComparison.Ordinal)) && before.SequenceEqual(after),
            $"A copy of the isolated instance's database migrates ({pending.Count} pending: {string.Join(", ", pending.Select(name => name[15..]))}) and keeps every row: {string.Join(", ", after)}");
        Assert((await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync())
            .SequenceEqual(["ok"]) && (await database.Database.SqlQueryRaw<string>("SELECT 'x' AS Value FROM pragma_foreign_key_check").ToListAsync()).Count == 0,
            "The migrated copy passes integrity and foreign-key checks");
    }
    else
    {
        Console.WriteLine("note - RATINGS_DB_COPY not set: the instance-copy migration check is skipped in this run");
    }
}

static async Task<string[]> CountsAsync(ModDbContext database)
{
    var result = new List<string>();
    foreach (var table in new[] { "Entries", "Episodes", "EntryBindings", "EpisodeBindings", "History", "CompletionObservations", "RetentionEvaluations" })
#pragma warning disable EF1002 // a fixed list of table names, not input
        result.Add($"{table}={(await database.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM \"{table}\"").ToListAsync()).Single()}");
#pragma warning restore EF1002
    return result.ToArray();
}

static async Task RunAsync(string folder, CapturingLoggerProvider logs)
{
    var media = Path.Combine(folder, "media");
    foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
    var world = new World
    {
        Admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() },
        SecondAdmin = new User("admin2", "auth", "reset") { Id = Guid.NewGuid() },
        RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() },
        Ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() },
        Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
        Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
        Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
        Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
        Folder = folder
    };
    var dbPath = Path.Combine(folder, "jellyfinmod.db");
    await using var boundary = await Boundary.StartAsync();
    const string Key = "mdblist-fixture-key-0123456789abcdef";
    const string Key2 = "mdblist-replacement-key-fedcba9876543210";
    boundary.ExpectedKey = Key;

    // The host's native items with what its metadata providers stored (R1 evidence): a series bound to an entry, and a movie
    // with no entry. The library's fetcher order decides who wrote CommunityRating (plan decision 3).
    var series = new Series { Id = Guid.NewGuid(), Name = "JellyfinMod Ratings Native Series", CommunityRating = 8.082f, CriticRating = 100,
        DateLastRefreshed = DateTime.UtcNow.AddDays(-1) };
    var lonelyMovie = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Ratings Native Movie", CommunityRating = 6.5f, CriticRating = 40,
        DateLastRefreshed = DateTime.UtcNow.AddDays(-1) };
    var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "Episode", SeriesId = series.Id, CommunityRating = 9f };
    var hiddenMovie = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Ratings Hidden", CommunityRating = 5f };
    var items = new Dictionary<Guid, BaseItem> { [series.Id] = series, [lonelyMovie.Id] = lonelyMovie, [episode.Id] = episode, [hiddenMovie.Id] = hiddenMovie };
    string[] order = ["TheMovieDb", "The Open Movie Database"];
    LibraryOptions Options() => new()
    {
        TypeOptions = [new TypeOptions { Type = "Movie", MetadataFetchers = order, MetadataFetcherOrder = order },
            new TypeOptions { Type = "Series", MetadataFetchers = order, MetadataFetcherOrder = order }]
    };
    var hostLibrary = Stub<ILibraryManager>.Create((method, arguments) => method.Name switch
    {
        "GetItemById" when arguments is [Guid id] => items.GetValueOrDefault(id),
        "GetItemById" when arguments is [Guid id, User user] => id == hiddenMovie.Id && user.Id == world.Ordinary.Id ? null : items.GetValueOrDefault(id),
        "GetLibraryOptions" => Options(),
        _ => method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null
    });

    var time = new ShiftedTimeProvider();
    var configuration = new PluginConfiguration { TmdbReadAccessToken = "tmdb-fixture-token" };
    void Configure(IServiceCollection services)
    {
        services.AddSingleton(hostLibrary);
        // Browse's host services, as the Phase 1 suite stubs them.
        services.AddSingleton(Stub<MediaBrowser.Controller.Dto.IDtoService>.Create((method, args) => method.Name == "GetBaseItemDto"
            ? new MediaBrowser.Model.Dto.BaseItemDto { Id = ((BaseItem)args![0]!).Id, Name = ((BaseItem)args[0]!).Name } : null));
        services.AddSingleton(Stub<IUserDataManager>.Create((method, _) => method.Name == "GetUserData" ? new UserItemData { Key = "ratings" } : null));
        services.AddSingleton(Stub<IMediaSourceManager>.Create((method, _) => method.Name == "GetMediaStreams"
            ? new List<MediaBrowser.Model.Entities.MediaStream>() : null));
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(), () => configuration,
            provider.GetRequiredService<ILogger<TmdbClient>>(), null, null, new TmdbEndpoint(boundary.Address)));
        services.AddSingleton(new AcquisitionSecretStore(folder));
        RatingsServices.Add(services, () => configuration);
        services.AddSingleton(new RatingsEndpoint(boundary.Address));
        // A call every 20 ms instead of every second; the other bounds are the documented ones.
        services.AddSingleton(RatingsOptions.Default with { MinInterval = TimeSpan.FromMilliseconds(20) });
        RatingsServices.AddHostedServices(services);
    }

    var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
    var anonymous = host.Client(null, false);
    var admin = host.Client(world.Admin, true);
    var viewer = host.Client(world.Ordinary, false);
    var tvAdmin = host.Client(world.RestrictedAdmin, true);
    var bodies = new List<string>();

    async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = body is string raw ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        bodies.Add(text);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType?.Contains("json") == true ? Json.Parse(text) : default);
    }

    Task<(HttpStatusCode Status, JsonElement Body)> Get(HttpClient client, string path) => Send(client, HttpMethod.Get, path);
    async Task<JsonElement> Settings() => (await Get(admin, "JellyfinMod/Settings/Ratings")).Body;
    async Task<(HttpStatusCode Status, JsonElement Body)> Patch(object body) => await Send(admin, HttpMethod.Patch, "JellyfinMod/Settings/Ratings", body);
    async Task<JsonElement> Status() => (await Get(admin, "JellyfinMod/Ratings/Status")).Body;
    async Task Run()
    {
        using var scope = host.App.Services.CreateScope();
        // The native task, exactly as Jellyfin's task manager runs it.
        await new RatingsRefreshTask(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteAsync(new Progress<double>(), CancellationToken.None);
    }

    static Dictionary<string, JsonElement> BySource(JsonElement ratings) =>
        ratings.EnumerateArray().ToDictionary(rating => rating.GetProperty("source").GetString()!, rating => rating);

    async Task<Dictionary<string, JsonElement>> EntryRatings(HttpClient client, Guid id)
    {
        var detail = await Get(client, $"JellyfinMod/Entries/{id}");
        Assert(detail.Status == HttpStatusCode.OK, $"entry {id} is readable");
        return BySource(detail.Body.GetProperty("ratings"));
    }

    async Task<Guid> Add(string mediaType, int tmdbId, Guid library)
    {
        var response = await Send(admin, HttpMethod.Post, "JellyfinMod/Entries", new { mediaType, tmdbId, targetLibraryId = library });
        Assert(response.Status == HttpStatusCode.OK, $"{mediaType} {tmdbId} is added through the real add path (TMDB boundary)");
        await Task.Delay(15); // distinct AddedAt, so "newest first" has an order to prove
        return response.Body.GetProperty("entry").GetProperty("id").GetString()!.ToGuid();
    }

    // ---- Authentication and authorization (R2, R5).
    foreach (var path in new[] { "JellyfinMod/Settings/Ratings", "JellyfinMod/Ratings/Status", "JellyfinMod/Ratings/Defaults", $"JellyfinMod/Ratings/Items/{series.Id}" })
        Assert((await Get(anonymous, path)).Status == HttpStatusCode.Unauthorized, $"Anonymous {path} is 401");
    Assert((await Get(viewer, "JellyfinMod/Settings/Ratings")).Status == HttpStatusCode.Forbidden, "An ordinary user cannot read the ratings settings (403)");
    Assert((await Send(viewer, HttpMethod.Patch, "JellyfinMod/Settings/Ratings", new { revision = 1 })).Status == HttpStatusCode.Forbidden,
        "An ordinary user cannot change the ratings settings (403)");
    Assert((await Send(viewer, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test")).Status == HttpStatusCode.Forbidden, "An ordinary user cannot run Test (403)");
    Assert((await Get(viewer, "JellyfinMod/Ratings/Status")).Status == HttpStatusCode.Forbidden, "An ordinary user cannot read the fetcher's status (403)");
    var health = (await Get(viewer, "JellyfinMod/Health")).Body;
    Assert(new[] { "ratings", "ratings.cards", "settings.ratings" }.All(name =>
        health.GetProperty("Capabilities").EnumerateArray().Any(value => value.GetString() == name)), "Health lists ratings, ratings.cards and settings.ratings");

    // ---- Defaults (decisions 5-6): on, no key, 14 days, 500 a day, IMDb, RT critics, RT audience, TMDB, Trakt.
    var settings = await Settings();
    Assert(settings.GetProperty("enabled").GetBoolean() && !settings.GetProperty("apiKeyConfigured").GetBoolean() &&
        settings.GetProperty("refreshDays").GetInt32() == 14 && settings.GetProperty("dailyBudget").GetInt32() == 500 &&
        string.Join(",", settings.GetProperty("defaultSources").EnumerateArray().Select(value => value.GetString())) == "imdb,tomatoes_critic,tomatoes_audience,tmdb,trakt" &&
        settings.GetProperty("availableSources").GetArrayLength() == 9 && !settings.GetProperty("verified").GetBoolean(),
        "The ratings settings start on, without a key, every 14 days, 500 calls a day, in the decided default order");
    var defaults = (await Get(viewer, "JellyfinMod/Ratings/Defaults")).Body;
    Assert(defaults.GetProperty("enabled").GetBoolean() && defaults.GetProperty("defaultSources").GetArrayLength() == 5 &&
        !defaults.TryGetProperty("apiKeyConfigured", out _), "An ordinary user reads the default order and nothing about the key");

    // ---- Disposable titles through the real add path: the TMDB snapshot now carries the vote count (R4).
    var movieA = await Add("movie", 9101, world.Movies.Id);
    var movieB = await Add("movie", 9102, world.Movies.Id);
    var seriesS = await Add("series", 9201, world.Tv.Id);
    // The same title in a second library: another entry, the same MDBList answer.
    var twin = await Add("movie", 9101, world.Movies2.Id);
    var extra = new List<Guid>();
    for (var id = 9103; id <= 9108; id++) extra.Add(await Add("movie", id, world.Movies.Id));
    // Reconciliation binds the series to its native item (Phase 2); the suite's item store has no library walk, so the binding
    // the reconciler would write is written here.
    await using (var database = new ModDbContext(dbPath))
    {
        var row = await database.Entries.SingleAsync(entry => entry.Id == seriesS);
        row.JellyfinItemId = series.Id;
        row.State = FileState.OnDisk;
        await database.SaveChangesAsync();
    }

    var aRatings = await EntryRatings(admin, movieA);
    Assert(aRatings.Count == 1 && aRatings["tmdb"].GetProperty("provider").GetString() == "tmdb" && aRatings["tmdb"].GetProperty("value").GetDouble() == 7.8 &&
        aRatings["tmdb"].GetProperty("votes").GetInt32() == 1234 && aRatings["tmdb"].GetProperty("scale").GetString() == "ten",
        "Unconfigured: a file-less entry shows TMDB only, from its own snapshot, with the vote count");
    var sRatings = await EntryRatings(admin, seriesS);
    Assert(sRatings.Count == 2 && sRatings["tmdb"].GetProperty("provider").GetString() == "host_tmdb" && sRatings["tmdb"].GetProperty("value").GetDouble() == 8.1 &&
        sRatings["tomatoes_critic"].GetProperty("provider").GetString() == "host_omdb" && sRatings["tomatoes_critic"].GetProperty("value").GetDouble() == 100 &&
        sRatings["tomatoes_critic"].GetProperty("scale").GetString() == "percent" && !sRatings.ContainsKey("imdb"),
        "Unconfigured: an on-disk title shows RT critics as host_omdb and, with TheMovieDb first, the native score as TMDB (host_tmdb), never as IMDb");
    order = ["The Open Movie Database", "TheMovieDb"];
    sRatings = await EntryRatings(admin, seriesS);
    Assert(sRatings["imdb"].GetProperty("provider").GetString() == "host_omdb" && sRatings["imdb"].GetProperty("value").GetDouble() == 8.1 &&
        sRatings["imdb"].GetProperty("votes").ValueKind == JsonValueKind.Null && !sRatings.ContainsKey("tmdb"),
        "With OMDb first in the fetcher order the native score is IMDb (host_omdb), without votes, as Jellyfin stores none");
    order = ["TheMovieDb"];
    Assert(!(await EntryRatings(admin, seriesS)).ContainsKey("tomatoes_critic"), "Without OMDb enabled the native critic score is not claimed as Rotten Tomatoes");
    order = ["TheMovieDb", "The Open Movie Database"];

    // Native item pages (R5): with an entry, without one, an episode, and a hidden item.
    var item = await Get(viewer, $"JellyfinMod/Ratings/Items/{series.Id}");
    Assert(item.Status == HttpStatusCode.OK && item.Body.GetProperty("entryId").GetString()!.ToGuid() == seriesS && item.Body.GetProperty("ratings").GetArrayLength() == 2,
        "A native series page reads its entry's ratings and the host's");
    item = await Get(viewer, $"JellyfinMod/Ratings/Items/{lonelyMovie.Id}");
    Assert(item.Body.GetProperty("ratings").EnumerateArray().All(rating => !rating.GetProperty("stale").GetBoolean()),
        "The host's values refreshed a day ago are current");
    Assert(item.Status == HttpStatusCode.OK && item.Body.GetProperty("entryId").ValueKind == JsonValueKind.Null &&
        BySource(item.Body.GetProperty("ratings"))["tomatoes_critic"].GetProperty("value").GetDouble() == 40,
        "A native movie with no catalog entry still shows what the host stored");
    item = await Get(viewer, $"JellyfinMod/Ratings/Items/{episode.Id}");
    Assert(item.Status == HttpStatusCode.OK && item.Body.GetProperty("ratings").GetArrayLength() == 0, "An episode page has no ratings line");
    Assert((await Get(viewer, $"JellyfinMod/Ratings/Items/{hiddenMovie.Id}")).Status == HttpStatusCode.NotFound &&
        (await Get(viewer, $"JellyfinMod/Ratings/Items/{Guid.NewGuid()}")).Status == HttpStatusCode.NotFound,
        "An item the user cannot see answers 404, like an unknown one");
    Assert((await Get(tvAdmin, $"JellyfinMod/Entries/{movieA}")).Status == HttpStatusCode.NotFound,
        "A user without access to the movie library cannot read its entry or its ratings (404)");

    // Without a key nothing is fetched and a manual refresh says why.
    await Run();
    Assert(boundary.Calls.IsEmpty && (await Status()).GetProperty("lastRun").GetProperty("stopReason").GetString() == "not_configured",
        "Unconfigured, the daily task makes no call and records why it stopped");
    var refused = await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh");
    Assert(refused.Status == HttpStatusCode.Conflict && refused.Body.GetProperty("type").GetString() == "not_configured",
        "A manual refresh without a key is 409 not_configured");
    Assert((await Send(viewer, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh")).Status == HttpStatusCode.Forbidden,
        "An ordinary user cannot refresh ratings (403)");

    // ---- The key: validation, revision, write-only (R2).
    var revision = settings.GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision = revision + 5, apiKey = new { action = "replace", value = Key } })).Status == HttpStatusCode.Conflict,
        "A save against a stale revision is 409");
    Assert((await Patch($"{{\"revision\":{revision},\"surprise\":1}}")).Status == HttpStatusCode.BadRequest, "An unknown field is 400");
    Assert((await Patch(new { revision, defaultSources = new[] { "imdb", "google" } })).Body.GetProperty("type").GetString() == "invalid_sources",
        "Google is not a source (user decision 2) and an unknown source is 400");
    Assert((await Patch(new { revision, refreshDays = 0 })).Status == HttpStatusCode.BadRequest &&
        (await Patch(new { revision, dailyBudget = 0 })).Status == HttpStatusCode.BadRequest, "Out-of-range refresh days and budgets are 400");
    var saved = await Patch(new { revision, apiKey = new { action = "replace", value = Key } });
    Assert(saved.Status == HttpStatusCode.OK && saved.Body.GetProperty("apiKeyConfigured").GetBoolean() &&
        saved.Body.GetProperty("revision").GetInt32() == revision + 1 && !saved.Body.GetProperty("verified").GetBoolean(),
        "Replacing the key reports it configured, bumps the revision and is not yet verified");
    var storeFile = Path.Combine(folder, "acquisition-secrets.json");
    Assert(File.ReadAllText(storeFile).Contains(Key, StringComparison.Ordinal) &&
        (OperatingSystem.IsWindows() || File.GetUnixFileMode(storeFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)),
        "The key lives in the 0600 secret store, not in SQLite or the XML configuration");
    await using (var database = new ModDbContext(dbPath))
    {
        var history = await database.History.AsNoTracking().Where(record => record.EventType == "settings_changed").ToListAsync();
        Assert(history.Any(record => record.Data!.Contains("\"ratings\"")) && history.All(record => !record.Data!.Contains(Key)) &&
            !(await database.RatingsSettings.AsNoTracking().SingleAsync()).ApiKeyRef!.Contains(Key, StringComparison.Ordinal),
            "The change is on the record as area and revision only; SQLite holds an opaque reference");
    }

    // ---- Test (R2): one call for TMDB movie 278.
    var test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Status == HttpStatusCode.OK && test.Body.GetProperty("ok").GetBoolean() && test.Body.GetProperty("code").GetString() == "ok" &&
        test.Body.GetProperty("sources").EnumerateArray().Select(value => value.GetString()).Contains("tomatoes_audience") &&
        boundary.CallsFor("movie", 278) == 1 && (await Settings()).GetProperty("verified").GetBoolean(),
        "Test makes one call for the fixed title, answers ok with the sources and marks the key verified");

    // ---- The daily task (R3): every title once, never-fetched first, newest first.
    boundary.TitleModes[9102] = "partial";
    boundary.TitleModes[9106] = "keyurl";
    var callsBefore = boundary.Calls.Count;
    await Run();
    var calls = boundary.Calls.Skip(callsBefore).ToArray();
    Assert(calls.Length == 9 && calls.Distinct().Count() == 9 && boundary.WrongKeyCalls == 0,
        "The first run calls MDBList once per title identity (9 titles, 10 entries), with the saved key");
    Assert(calls[0] == "movie:9108" && calls[1] == "movie:9107" && calls[6] == "movie:9101" && calls[7] == "show:9201" && calls[^1] == "movie:9102",
        "Titles never fetched go newest first (a title's newest entry counts); a series is asked for as a show");
    Assert((await EntryRatings(admin, twin)).Count == 9 && (await EntryRatings(admin, twin))["imdb"].GetProperty("value").GetDouble() == 8.1,
        "One call served both libraries' entries of the same title");
    aRatings = await EntryRatings(admin, movieA);
    Assert(string.Join(",", aRatings.Keys) == "imdb,tomatoes_critic,tomatoes_audience,tmdb,trakt,metacritic,metacritic_user,letterboxd,rogerebert",
        "Every known source arrives, one value each, in the complete order; the unknown and the zero-without-votes sources are not shown");
    Assert(aRatings["imdb"].GetProperty("value").GetDouble() == 8.1 && aRatings["imdb"].GetProperty("scale").GetString() == "ten" &&
        aRatings["imdb"].GetProperty("votes").GetInt32() == 250000 && aRatings["imdb"].GetProperty("provider").GetString() == "mdblist" &&
        aRatings["imdb"].GetProperty("url").GetString()!.StartsWith("https://www.imdb.com/", StringComparison.Ordinal) &&
        !aRatings["imdb"].GetProperty("stale").GetBoolean() && aRatings["imdb"].GetProperty("fetchedAt").ValueKind == JsonValueKind.String,
        "IMDb: 8.1 on ten, with votes, via MDBList, with its fetch time");
    Assert(aRatings["tomatoes_critic"].GetProperty("value").GetDouble() == 91 && aRatings["tomatoes_audience"].GetProperty("value").GetDouble() == 88 &&
        aRatings["tomatoes_audience"].GetProperty("scale").GetString() == "percent" && aRatings["letterboxd"].GetProperty("scale").GetString() == "five" &&
        aRatings["rogerebert"].GetProperty("scale").GetString() == "four" && aRatings["rogerebert"].GetProperty("value").GetDouble() == 3.5 &&
        aRatings["metacritic_user"].GetProperty("scale").GetString() == "ten",
        "Each value stays in its own scale: RT critics and audience in percent, Letterboxd of five, Roger Ebert of four");
    Assert(aRatings["tmdb"].GetProperty("provider").GetString() == "tmdb" && aRatings["tmdb"].GetProperty("value").GetDouble() == 7.8,
        "TMDB stays first-party: the entry's own snapshot wins over MDBList's TMDB value");
    var bRatings = await EntryRatings(admin, movieB);
    Assert(!bRatings.ContainsKey("tomatoes_audience") && bRatings.ContainsKey("tomatoes_critic"),
        "A partial answer stores what arrived: no audience score, and no placeholder for it");
    sRatings = await EntryRatings(admin, seriesS);
    Assert(sRatings["tomatoes_critic"].GetProperty("provider").GetString() == "mdblist" && sRatings["tmdb"].GetProperty("provider").GetString() == "mdblist" &&
        sRatings["tmdb"].GetProperty("scale").GetString() == "percent" && sRatings["imdb"].GetProperty("provider").GetString() == "mdblist",
        "Configured: MDBList values replace the host fallback (a backfilled title's TMDB comes from MDBList)");
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.TitleRatings.AnyAsync(row => row.Source == "myanimelist" && row.Scale == "unknown") &&
            !await database.TitleRatings.AnyAsync(row => row.Value == 0), "An unknown source is stored raw and hidden; no zero is ever stored");

    // ---- Provider links (review 2026-10-07, P1): a link echoing the key, or pointing anywhere but the source's own site, is
    // never stored or shown; the rest are kept as https with their path only.
    var linked = await EntryRatings(viewer, extra[3]);
    Assert(linked["imdb"].GetProperty("url").GetString() == "https://www.imdb.com/title/tt9106/" &&
        linked["letterboxd"].GetProperty("url").ValueKind == JsonValueKind.Null && linked["trakt"].GetProperty("url").ValueKind == JsonValueKind.Null &&
        linked["tomatoes_critic"].GetProperty("url").ValueKind == JsonValueKind.Null && linked["metacritic"].GetProperty("url").ValueKind == JsonValueKind.Null,
        "A provider link keeps only the source's own https site and path: a key in its query, another host, a port, credentials or a key in the path are dropped");
    await using (var database = new ModDbContext(dbPath))
        Assert(!await database.TitleRatings.AnyAsync(row => row.Url != null && (row.Url.Contains("apikey") || row.Url.Contains(Key) || row.Url.Contains("?"))),
            "No stored link carries a query or the key");
    Assert(bodies.All(body => !body.Contains(Key, StringComparison.Ordinal)), "An ordinary user's detail answer never carries the key the provider echoed");
    // An older row stored before the check is cleaned on the way out too.
    await using (var database = new ModDbContext(dbPath))
    {
        var row = await database.TitleRatings.SingleAsync(rating => rating.EntryId == extra[3] && rating.Source == "imdb");
        row.Url = $"https://www.imdb.com/title/tt9106/?apikey={Key}";
        await database.SaveChangesAsync();
    }

    Assert((await EntryRatings(viewer, extra[3]))["imdb"].GetProperty("url").GetString() == "https://www.imdb.com/title/tt9106/",
        "A link stored before the check is cleaned when it is shown");

    // Within the window nothing is called again.
    callsBefore = boundary.Calls.Count;
    await Run();
    Assert(boundary.Calls.Count == callsBefore, "A second run inside the refresh window makes no call");

    // ---- A title added to another library joins its title's fetch state (review 2026-10-07, P2 4).
    var bTwin = await Add("movie", 9102, world.Movies2.Id);
    callsBefore = boundary.Calls.Count;
    await Run();
    var bTwinRatings = await EntryRatings(admin, bTwin);
    Assert(boundary.Calls.Count == callsBefore && bTwinRatings.Count == (await EntryRatings(admin, movieB)).Count &&
        bTwinRatings["imdb"].GetProperty("provider").GetString() == "mdblist" && !bTwinRatings.ContainsKey("tomatoes_audience"),
        "The same title added to another library inside the window makes no call and takes the title's stored values");

    // After the window every title is due once more, newest first; values are flagged stale only past the window.
    time.Offset = TimeSpan.FromDays(15);
    Assert((await EntryRatings(admin, movieA))["imdb"].GetProperty("stale").GetBoolean(), "A value older than the refresh window is marked stale, never presented as current");
    var lonely = BySource((await Get(viewer, $"JellyfinMod/Ratings/Items/{lonelyMovie.Id}")).Body.GetProperty("ratings"));
    Assert(lonely["tomatoes_critic"].GetProperty("stale").GetBoolean() && lonely["tmdb"].GetProperty("stale").GetBoolean(),
        "The host's values are marked stale too once its last metadata refresh is older than the window (review 2026-10-07, P3 8)");
    await Run();
    Assert(boundary.Calls.Count == callsBefore + 9 && boundary.Calls.Skip(callsBefore).Distinct().Count() == 9 &&
        !(await EntryRatings(admin, movieA))["imdb"].GetProperty("stale").GetBoolean(),
        "Past the window each title is fetched exactly once again, and its values are current again");

    // ---- Budget (R3): a lower budget stops the run and a manual refresh.
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision, dailyBudget = 2 })).Status == HttpStatusCode.OK, "The administrator lowers the daily budget to 2");
    time.Offset = TimeSpan.FromDays(30);
    // The oldest attempt is the first title's; the newest titles still go first (user decision 5, review 2026-10-07 P2 7).
    await using (var database = new ModDbContext(dbPath))
    {
        foreach (var row in await database.RatingsFetches.Where(row => row.EntryId == movieA || row.EntryId == twin).ToListAsync())
            row.AttemptedAt = time.GetUtcNow().UtcDateTime.AddDays(-100);
        await database.SaveChangesAsync();
    }

    callsBefore = boundary.Calls.Count;
    await Run();
    var status = await Status();
    var overdue = boundary.Calls.Skip(callsBefore).ToArray();
    Assert(overdue.SequenceEqual(["movie:9102", "movie:9108"]),
        "Among overdue titles the newest go first (9102 was just added to a second library, then 9108), not the oldest attempt (9101)", overdue);
    Assert(boundary.Calls.Count == callsBefore + 2 && status.GetProperty("budget").GetProperty("used").GetInt32() == 2 &&
        status.GetProperty("lastRun").GetProperty("stopReason").GetString() == "budget_spent" && status.GetProperty("entriesWithoutRatings").GetInt32() >= 0,
        "The budget holds: two calls, then the run stops with budget_spent");
    refused = await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh");
    Assert(refused.Status == HttpStatusCode.Conflict && refused.Body.GetProperty("type").GetString() == "budget_spent", "A manual refresh inside a spent budget is 409 budget_spent");
    var testCalls = boundary.CallsFor("movie", 278);
    test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(!test.Body.GetProperty("ok").GetBoolean() && test.Body.GetProperty("code").GetString() == "budget_spent" &&
        boundary.CallsFor("movie", 278) == testCalls && (await Status()).GetProperty("budget").GetProperty("used").GetInt32() == 2,
        "Test inside a spent budget makes no call and spends nothing (review 2026-10-07, P2 3)", test.Body);
    revision = (await Settings()).GetProperty("revision").GetInt32();
    await Patch(new { revision, dailyBudget = 500 });

    // ---- Manual refresh (decision 5): queued, fetched once, deduplicated while queued.
    callsBefore = boundary.CallsFor("movie", 9101);
    var queued = await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh");
    Assert(queued.Status == HttpStatusCode.Accepted && queued.Body.GetProperty("queued").GetBoolean(), "A manual refresh is accepted (202)");
    await WaitFor(() => Task.FromResult(boundary.CallsFor("movie", 9101) == callsBefore + 1), "the queued refresh is fetched");
    await using (var database = new ModDbContext(dbPath))
        Assert((await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.EntryId == movieA)).Manual, "The attempt is recorded as manual");
    Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{Guid.NewGuid()}/Ratings/Refresh")).Status == HttpStatusCode.NotFound,
        "Refreshing an unknown entry is 404");

    // ---- Structure (review 2026-10-07, P2 5): a wrong-shaped item makes the answer malformed and keeps every stored value.
    async Task<string> RefreshAndWait(Guid entry, int tmdb)
    {
        var before = boundary.CallsFor("movie", tmdb);
        Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{entry}/Ratings/Refresh")).Status == HttpStatusCode.Accepted, $"refresh of {tmdb} queued");
        await WaitFor(() => Task.FromResult(boundary.CallsFor("movie", tmdb) == before + 1), $"the refresh of {tmdb}");
        string outcome = "pending";
        await WaitFor(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            outcome = (await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.EntryId == entry)).Outcome;
            return outcome != "pending";
        }, $"the outcome of {tmdb}");
        return outcome;
    }

    var kept = (await EntryRatings(admin, movieA))["imdb"].GetProperty("fetchedAt").GetDateTime();
    foreach (var mode in new[] { "badstructure", "badvalue" })
    {
        boundary.TitleModes[9101] = mode;
        var outcome = await RefreshAndWait(movieA, 9101);
        var after = await EntryRatings(admin, movieA);
        Assert(outcome == "malformed" && after.Count == 9 && after["imdb"].GetProperty("fetchedAt").GetDateTime() == kept,
            $"A {mode} answer is malformed and keeps every stored value", new { outcome, sources = after.Count });
    }

    Assert((await Status()).GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32() == 2, "Both count as provider failures");
    boundary.TitleModes[9107] = "absent";
    Assert(await RefreshAndWait(extra[4], 9107) == "ok", "Values MDBList does not have (null, \"\", N/A) are absent, not malformed");
    var absent = await EntryRatings(admin, extra[4]);
    Assert(!absent.ContainsKey("imdb") && !absent.ContainsKey("tomatoes_critic") && absent["trakt"].GetProperty("value").GetDouble() == 77 &&
        absent["trakt"].GetProperty("votes").GetInt32() == 1200 && (await Status()).GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32() == 0,
        "Numbers sent as text are read; the answer resets the failure streak");
    boundary.TitleModes.Remove(9101, out _);
    boundary.TitleModes.Remove(9107, out _);

    // ---- In flight (review 2026-10-07, P2 2): a key replaced while its call is out is not blocked by that call's 401.
    boundary.HoldTitle = 9102;
    Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieB}/Ratings/Refresh")).Status == HttpStatusCode.Accepted, "A refresh is queued");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision, apiKey = new { action = "replace", value = Key2 } })).Status == HttpStatusCode.OK, "The key is replaced while the call is out");
    boundary.ExpectedKey = Key2;
    boundary.Release.TrySetResult();
    await WaitFor(async () =>
    {
        await using var database = new ModDbContext(dbPath);
        return (await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.EntryId == movieB)).Outcome == "unauthorized";
    }, "the old key's answer");
    boundary.ResetHold();
    Assert((await Status()).GetProperty("blocker").ValueKind == JsonValueKind.Null, "The old key's 401 does not block the new key");
    Assert(await RefreshAndWait(movieB, 9102) == "ok", "The new key fetches at once");

    // A run that is under way stops at its next call when ratings are turned off, and when the budget is lowered.
    foreach (var change in new[] { "off", "budget" })
    {
        boundary.HoldAny = true;
        callsBefore = boundary.Calls.Count;
        var running = Run();
        await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        revision = (await Settings()).GetProperty("revision").GetInt32();
        var used = (await Status()).GetProperty("budget").GetProperty("used").GetInt32();
        await Patch(change == "off" ? new { revision, enabled = (bool?)false, dailyBudget = (int?)null } : new { revision, enabled = (bool?)null, dailyBudget = (int?)used });
        boundary.Release.TrySetResult();
        await running;
        boundary.ResetHold();
        var stop = (await Status()).GetProperty("lastRun").GetProperty("stopReason").GetString();
        Assert(boundary.Calls.Count == callsBefore + 1 && stop == (change == "off" ? "ratings_disabled" : "budget_spent"),
            change == "off" ? "Turning ratings off stops a running fetch after the call already out" : "Lowering the budget stops a running fetch at the new limit",
            new { calls = boundary.Calls.Count - callsBefore, stop });
        revision = (await Settings()).GetProperty("revision").GetInt32();
        await Patch(new { revision, enabled = true, dailyBudget = 500 });
    }

    // Back to the first key for the rest of the run.
    revision = (await Settings()).GetProperty("revision").GetInt32();
    await Patch(new { revision, apiKey = new { action = "replace", value = Key } });
    boundary.ExpectedKey = Key;

    // ---- 401: a blocker until the key is replaced; existing values stay.
    time.Offset = TimeSpan.FromDays(45);
    boundary.TitleModes.Clear();
    boundary.Mode = "unauthorized";
    callsBefore = boundary.Calls.Count;
    await Run();
    status = await Status();
    Assert(boundary.Calls.Count == callsBefore + 1 && status.GetProperty("blocker").GetString() == "unauthorized" &&
        status.GetProperty("lastRun").GetProperty("stopReason").GetString() == "unauthorized", "401 stops the run after one call and blocks fetching",
        new { calls = boundary.Calls.Count - callsBefore, status = status.ToString() });
    Assert((await EntryRatings(admin, movieA)).ContainsKey("imdb"), "Existing values are kept and shown with their fetch time");
    await Run();
    Assert(boundary.Calls.Count == callsBefore + 1, "While blocked, the next run makes no call");
    refused = await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh");
    Assert(refused.Body.GetProperty("type").GetString() == "unauthorized", "A manual refresh while blocked is 409 unauthorized");
    boundary.Mode = "full";
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision, apiKey = new { action = "replace", value = Key } })).Status == HttpStatusCode.OK &&
        (await Status()).GetProperty("blocker").ValueKind == JsonValueKind.Null, "Replacing the key lifts the blocker");
    boundary.Mode = "errorkey";
    test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("code").GetString() == "unauthorized" && (await Status()).GetProperty("blocker").GetString() == "unauthorized",
        "A 200 whose body names a refused key is unauthorized too, from Test as from a run");
    boundary.Mode = "full";
    test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("ok").GetBoolean() && (await Status()).GetProperty("blocker").ValueKind == JsonValueKind.Null, "A passing Test lifts the blocker");

    // ---- 429 with Retry-After: a breaker for the rest of the UTC day at least.
    time.Offset = TimeSpan.FromDays(60);
    boundary.Mode = "ratelimited";
    callsBefore = boundary.Calls.Count;
    await Run();
    status = await Status();
    var until = status.GetProperty("breaker").GetProperty("until").GetDateTime().ToUniversalTime();
    var now = time.GetUtcNow().UtcDateTime;
    Assert(boundary.Calls.Count == callsBefore + 1 && status.GetProperty("breaker").GetProperty("open").GetBoolean() &&
        status.GetProperty("breaker").GetProperty("reason").GetString() == "rate_limited" && until >= now.AddSeconds(110) && until >= now.Date.AddDays(1).AddSeconds(-1),
        "429 opens the breaker past Retry-After and to the end of the UTC day");
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.RatingsFetches.AsNoTracking().AnyAsync(row => row.Outcome == "rate_limited" && row.RetryAfter != null), "The attempt records when the provider asked to be called again");
    await Run();
    refused = await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieA}/Ratings/Refresh");
    Assert(boundary.Calls.Count == callsBefore + 1 && refused.Body.GetProperty("type").GetString() == "breaker_open",
        "While the breaker is open nothing is called; a manual refresh is 409 breaker_open");
    testCalls = boundary.CallsFor("movie", 278);
    test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("code").GetString() == "breaker_open" && boundary.CallsFor("movie", 278) == testCalls,
        "Test while the breaker is open makes no call (review 2026-10-07, P2 3)", test.Body);
    boundary.Mode = "full";
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision, apiKey = new { action = "replace", value = Key } })).Status == HttpStatusCode.OK &&
        !(await Status()).GetProperty("breaker").GetProperty("open").GetBoolean(),
        "Replacing the key closes a breaker opened by a 429: the daily quota is the key's");
    boundary.ResetAt = time.GetUtcNow().AddDays(2);
    boundary.Mode = "bothheaders";
    Assert(await RefreshAndWait(movieA, 9101) == "rate_limited", "A 429 with a short Retry-After and a reset two days away");
    until = (await Status()).GetProperty("breaker").GetProperty("until").GetDateTime().ToUniversalTime();
    Assert(until >= boundary.ResetAt.UtcDateTime.AddSeconds(-1), "The breaker stays open until the later reset, not the shorter Retry-After (review 2026-10-07, P2 6)",
        new { until, reset = boundary.ResetAt });
    boundary.Mode = "full";
    revision = (await Settings()).GetProperty("revision").GetInt32();
    await Patch(new { revision, apiKey = new { action = "replace", value = Key } });
    time.Offset = TimeSpan.FromDays(61);

    // ---- 5xx, malformed and timeout: transient failures, a one-hour breaker after five in a row; values unchanged.
    var oldA = (await EntryRatings(admin, movieA))["imdb"].GetProperty("fetchedAt").GetDateTime();
    time.Offset = TimeSpan.FromDays(76);
    boundary.TitleModes.Clear();
    boundary.Mode = "fail";
    callsBefore = boundary.Calls.Count;
    await Run();
    status = await Status();
    Assert(boundary.Calls.Count == callsBefore + 5 && status.GetProperty("breaker").GetProperty("reason").GetString() == "failures" &&
        status.GetProperty("lastRun").GetProperty("failed").GetInt32() == 5, "Five server errors in a row open a one-hour breaker and stop the run");
    Assert((await EntryRatings(admin, movieA))["imdb"].GetProperty("fetchedAt").GetDateTime() == oldA, "A failure changes no stored value");
    time.Offset += TimeSpan.FromMinutes(61);
    boundary.Mode = "malformed";
    callsBefore = boundary.Calls.Count;
    await Run();
    Assert(boundary.Calls.Count == callsBefore + 4 && (await EntryRatings(admin, movieA))["imdb"].GetProperty("fetchedAt").GetDateTime() == oldA,
        "After the hour fetching resumes; malformed bodies count as failures (the remaining four titles) and change nothing");
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.RatingsFetches.AsNoTracking().CountAsync(row => row.Outcome == "malformed") >= 4,
            "Each malformed answer is recorded as such, on every entry of its title");
    boundary.Mode = "full";
    time.Offset += TimeSpan.FromHours(2);
    boundary.TitleModes[9103] = "slow";
    boundary.TitleModes[9104] = "notfound";
    boundary.TitleModes[9105] = "otherid";
    time.Offset += TimeSpan.FromDays(2);
    await Run();
    await using (var database = new ModDbContext(dbPath))
    {
        var outcomes = await database.RatingsFetches.AsNoTracking().ToDictionaryAsync(row => row.EntryId, row => row.Outcome);
        Assert(outcomes[extra[0]] == "timeout" && outcomes[extra[1]] == "not_found" && outcomes[extra[2]] == "malformed",
            "A slow provider times out, an unknown title is not_found, an answer about another title is malformed");
    }

    Assert((await EntryRatings(admin, extra[1])).ContainsKey("imdb"), "not_found keeps the values the title already had");
    boundary.TitleModes.Clear();

    // ---- A killed run (R3): the claim commits before the call, so a restart never fetches that title again that day.
    time.Offset += TimeSpan.FromDays(20);
    boundary.HoldTitle = 9108;
    using (var kill = new CancellationTokenSource())
    {
        var scope = host.App.Services.CreateScope();
        var running = scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>().RunAsync(null, kill.Token);
        await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        kill.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }

        scope.Dispose();
    }

    boundary.Release.TrySetResult();
    await host.DisposeAsync();
    boundary.ResetHold();
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.RatingsFetches.AsNoTracking().CountAsync(row => row.Outcome == "pending") == 1,
            "The killed run left its claim behind, committed before the call");
    var heldCalls = boundary.CallsFor("movie", 9108);
    host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
    admin = host.Client(world.Admin, true);
    viewer = host.Client(world.Ordinary, false);
    await Run();
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.RatingsFetches.AsNoTracking().CountAsync(row => row.Outcome == "pending") == 0 &&
            (await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.EntryId == extra[5])).Outcome == "interrupted",
            "After a restart the left claim counts as an interrupted attempt");
    Assert(boundary.CallsFor("movie", 9108) == heldCalls && boundary.CallsFor("movie", 9101) >= 1,
        "The interrupted title is not fetched again that day; the others are");
    var killTwin = await Add("movie", 9108, world.Movies2.Id);
    await Run();
    Assert(boundary.CallsFor("movie", 9108) == heldCalls, "The same title added to another library does not skip the interrupted attempt's wait (review 2026-10-07, P2 4)");
    time.Offset += TimeSpan.FromDays(1.5);
    await Run();
    Assert(boundary.CallsFor("movie", 9108) == heldCalls + 1 && (await EntryRatings(admin, killTwin)).Count == 9,
        "A day later the interrupted title is fetched once, for both libraries' entries");

    // ---- Persistence across the restart.
    settings = await Settings();
    Assert(settings.GetProperty("apiKeyConfigured").GetBoolean() && settings.GetProperty("dailyBudget").GetInt32() == 500 &&
        (await EntryRatings(admin, movieA)).Count == 9, "Settings, the key and the stored ratings survive a restart");

    // ---- Browse (R5, R7): no rating unless asked; one source when asked.
    var browseBase = new { mediaType = "movie", targetLibraryId = world.Movies.Id };
    var browse = await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", browseBase);
    Assert(browse.Status == HttpStatusCode.OK && browse.Body.GetProperty("items").GetArrayLength() >= 8 &&
        browse.Body.GetProperty("items").EnumerateArray().All(row => !row.TryGetProperty("rating", out _)),
        "Browse rows carry no rating unless the request asks for one (list payloads do not grow)");
    browse = await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = world.Movies.Id, ratingSource = "imdb" });
    var withRating = browse.Body.GetProperty("items").EnumerateArray().Where(row => row.TryGetProperty("rating", out _)).ToArray();
    Assert(withRating.Length >= 8 && withRating.All(row => row.GetProperty("rating").GetProperty("source").GetString() == "imdb" &&
        row.GetProperty("rating").GetProperty("value").GetDouble() == 8.1), "With ratingSource each row carries that one source and nothing else");
    Assert((await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", new { mediaType = "movie", ratingSource = "google" })).Status == HttpStatusCode.BadRequest,
        "An unknown card source is 400");

    // ---- Ratings off: absent everywhere, nothing fetched.
    revision = settings.GetProperty("revision").GetInt32();
    Assert((await Patch(new { revision, enabled = false })).Status == HttpStatusCode.OK, "The administrator turns ratings off");
    callsBefore = boundary.Calls.Count;
    time.Offset += TimeSpan.FromDays(30);
    await Run();
    browse = await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = world.Movies.Id, ratingSource = "imdb" });
    Assert(boundary.Calls.Count == callsBefore && (await EntryRatings(viewer, movieA)).Count == 0 &&
        (await Get(viewer, $"JellyfinMod/Ratings/Items/{series.Id}")).Body.GetProperty("ratings").GetArrayLength() == 0 &&
        browse.Body.GetProperty("items").EnumerateArray().All(row => !row.TryGetProperty("rating", out _)) &&
        !(await Get(viewer, "JellyfinMod/Ratings/Defaults")).Body.GetProperty("enabled").GetBoolean(),
        "Turned off, nothing is fetched and ratings are absent from detail, item pages and cards");

    // ---- Clear: the indicator flips and the stored key is removed.
    revision = (await Settings()).GetProperty("revision").GetInt32();
    var cleared = await Patch(new { revision, enabled = true, apiKey = new { action = "clear" } });
    Assert(cleared.Status == HttpStatusCode.OK && !cleared.Body.GetProperty("apiKeyConfigured").GetBoolean() &&
        !File.ReadAllText(storeFile).Contains(Key, StringComparison.Ordinal), "Clear removes the key from the secret store");

    // ---- Removing an entry removes its ratings and its attempt (cascade).
    Assert((await Send(admin, HttpMethod.Delete, $"JellyfinMod/Entries/{movieB}")).Status is HttpStatusCode.NoContent or HttpStatusCode.OK, "An entry is removed");
    await using (var database = new ModDbContext(dbPath))
        Assert(!await database.TitleRatings.AnyAsync(row => row.EntryId == movieB) && !await database.RatingsFetches.AnyAsync(row => row.EntryId == movieB),
            "Its ratings and fetch attempt go with it");

    // ---- Leak check: the key never in a response or a log line; the HTTP client's own request logs are redacted.
    Assert(bodies.Count > 60 && bodies.All(body => !body.Contains(Key, StringComparison.Ordinal) && !body.Contains(Key2, StringComparison.Ordinal) &&
            !body.Contains("sec_", StringComparison.Ordinal)),
        $"None of the {bodies.Count} responses carries the key or its secret-store reference");
    var lines = logs.Lines.ToArray();
    Assert(lines.All(line => !line.Contains(Key, StringComparison.Ordinal) && !line.Contains(Key2, StringComparison.Ordinal)),
        $"None of the {lines.Length} log lines carries either key");
    Assert(lines.Any(line => line.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal) && line.Contains("/tmdb/movie/", StringComparison.Ordinal)),
        "The HTTP client's own request log lines were captured (at Debug) and are among those checked");
    Console.WriteLine("evidence - a captured HTTP client line: " +
        lines.First(line => line.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal) && line.Contains("/tmdb/movie/", StringComparison.Ordinal))
            .Replace(boundary.Address.Authority, "<boundary>", StringComparison.Ordinal));
    await host.DisposeAsync();
}

static async Task WaitFor(Func<Task<bool>> condition, string what)
{
    for (var attempt = 0; attempt < 300; attempt++)
    {
        if (await condition()) return;
        await Task.Delay(50);
    }

    throw new Exception("FAIL (timeout): " + what);
}

static async Task<List<string>> TableNamesAsync(ModDbContext database) =>
    await database.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();

static void Assert(bool condition, string message, object? detail = null)
{
    if (!condition && detail is not null) throw new Exception("FAIL: " + message + " :: " + JsonSerializer.Serialize(detail));
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("ok - " + message);
}

internal static class GuidText
{
    /// <summary>The host's serializer writes Guids without dashes; parse either form.</summary>
    public static Guid ToGuid(this string value) => Guid.Parse(value);
}
