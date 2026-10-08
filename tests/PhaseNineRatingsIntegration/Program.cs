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
    await AutomaticAsync(folder, logs);
    Console.WriteLine($"PASS: Phase 9 ratings — migration, settings, secret, Test, fetcher, budget, breaker, claims, projection, access, automatic fetching ({stopwatch.Elapsed.TotalSeconds:F1}s)");
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
        Assert(fetchUnique.Contains("IX_RatingsFetches_MediaType_TmdbId"), "One attempt per title identity keeps the attempt log bounded");
        Assert(!(await ColumnsAsync(database, "TitleRatings")).Contains("Url") && !(await ColumnsAsync(database, "RatingsFetches")).Contains("EntryId"),
            "No provider link is stored, and an attempt belongs to the title, not to an entry (review 2026-10-07 round 2, P1 and P2 5)");
        Assert((await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync())
            .SequenceEqual(["ok"]), "The migrated database passes its integrity check");
        Assert(!(await database.Database.GetPendingMigrationsAsync()).Any(), "No migration is left pending");
    }

    // From the first Phase 9 shape (deployed to the isolated instance only): links go, and each title keeps its latest attempt.
    var firstPath = Path.Combine(folder, "phase-nine-first.db");
    await using (var database = new ModDbContext(firstPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20261007043632_PhaseNineRatings");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        foreach (var (id, tmdb, library) in new[] { (a, 7001, Guid.NewGuid()), (b, 7001, Guid.NewGuid()), (c, 7002, Guid.NewGuid()) })
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                VALUES ({id}, 'movie', {tmdb}, 'Existing', 4, 1, '2026-09-01T00:00:00', {library}, 0)
                """);
        foreach (var (entry, at, outcome) in new[] { (a, "2026-10-01 00:00:00", "ok"), (b, "2026-10-05 00:00:00", "interrupted"), (c, "2026-10-02 00:00:00", "not_found") })
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RatingsFetches (Id, EntryId, AttemptedAt, Outcome, RetryAfter, Error, Manual) VALUES ({Guid.NewGuid()}, {entry}, {at}, {outcome}, NULL, NULL, 0)
                """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO TitleRatings (Id, EntryId, Source, Provider, Value, Scale, Votes, FetchedAt, Url)
            VALUES ({Guid.NewGuid()}, {a}, 'imdb', 'mdblist', 8.1, 'ten', 10, '2026-10-01 00:00:00', 'https://letterboxd.com/film/secret-key/')
            """);
        await database.Database.MigrateAsync();
        var attempts = await database.RatingsFetches.AsNoTracking().OrderBy(row => row.TmdbId).ToListAsync();
        Assert(attempts.Count == 2 && attempts[0].TmdbId == 7001 && attempts[0].Outcome == "interrupted" && attempts[1].Outcome == "not_found" &&
            (await database.TitleRatings.AsNoTracking().SingleAsync()).Value == 8.1 && !(await ColumnsAsync(database, "TitleRatings")).Contains("Url"),
            "From the first Phase 9 shape each title keeps its latest attempt, ratings stay, and stored links are gone",
            attempts.Select(row => new { row.TmdbId, row.Outcome }));
        Assert((await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync())
            .SequenceEqual(["ok"]) && (await database.Database.SqlQueryRaw<string>("SELECT 'x' AS Value FROM pragma_foreign_key_check").ToListAsync()).Count == 0,
            "The converted database passes integrity and foreign-key checks");
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
        Assert(pending.Any(name => name.Contains("_PhaseNineRatings", StringComparison.Ordinal)) && before.SequenceEqual(after),
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
        // A call every 20 ms instead of every second; the other bounds are the documented ones. This part proves the daily task's
        // own rules, so automatic fetching (user decision 8) is off here; AutomaticAsync below runs it as a real host does.
        services.AddSingleton(RatingsOptions.Default with { MinInterval = TimeSpan.FromMilliseconds(20), Automatic = false });
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

    async Task<RatingsFetch> Attempt(int tmdb, string mediaType = "movie")
    {
        await using var database = new ModDbContext(dbPath);
        return await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.MediaType == mediaType && row.TmdbId == tmdb);
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

    // ---- Provider links (review 2026-10-07, P1; round 2, P1): no link is kept or shown at all — a provider-written link can
    // carry the key in its query, its path, its host name or double-encoded, and nothing in JellyfinMod uses one.
    var linked = await EntryRatings(viewer, extra[3]);
    Assert(linked["imdb"].GetProperty("value").GetDouble() == 8.1 && linked.Values.All(rating => !rating.TryGetProperty("url", out _)),
        "A rating carries no provider link, whatever the provider sent; its value is kept", linked.Keys);
    Assert(bodies.All(body => !body.Contains(Key, StringComparison.Ordinal)), "No answer so far carries the key the provider echoed");

    // Within the window nothing is called again.
    callsBefore = boundary.Calls.Count;
    await Run();
    Assert(boundary.Calls.Count == callsBefore, "A second run inside the refresh window makes no call");

    // ---- A title added to another library joins its title's fetch state (review 2026-10-07, P2 4). Two titles join at once;
    // one of them cannot be written (a SQLite trigger stands in for its entry vanishing mid-write, review round 3 P2 2): the
    // run carries on, the other title adopts, and the next run adopts the one that failed.
    var bTwin = await Add("movie", 9102, world.Movies2.Id);
    var cTwin = await Add("movie", 9103, world.Movies2.Id);
    await using (var database = new ModDbContext(dbPath))
    {
#pragma warning disable EF1003 // DDL cannot take parameters; the id is a Guid this suite created
        await database.Database.ExecuteSqlRawAsync(
            $"CREATE TRIGGER jfmod_test_vanish BEFORE INSERT ON TitleRatings WHEN NEW.EntryId = '{bTwin.ToString().ToUpperInvariant()}' " +
            "BEGIN SELECT RAISE(ABORT, 'simulated: the entry went meanwhile'); END");
#pragma warning restore EF1003
    }
    callsBefore = boundary.Calls.Count;
    await Run();
    var runAfterVanish = await Status();
    Assert(boundary.Calls.Count == callsBefore && (await EntryRatings(admin, cTwin))["imdb"].GetProperty("provider").GetString() == "mdblist" &&
        !(await EntryRatings(admin, bTwin)).ContainsKey("imdb") && runAfterVanish.GetProperty("lastRun").GetProperty("finishedAt").ValueKind == JsonValueKind.String,
        "A title whose new entry cannot take its values is skipped; the run finishes and the other title adopts its values (round 3, P2 2)");
    await using (var database = new ModDbContext(dbPath))
        await database.Database.ExecuteSqlRawAsync("DROP TRIGGER jfmod_test_vanish");
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
        foreach (var row in await database.RatingsFetches.Where(row => row.MediaType == "movie" && row.TmdbId == 9101).ToListAsync())
            row.AttemptedAt = time.GetUtcNow().UtcDateTime.AddDays(-100);
        await database.SaveChangesAsync();
    }

    callsBefore = boundary.Calls.Count;
    await Run();
    var status = await Status();
    var overdue = boundary.Calls.Skip(callsBefore).ToArray();
    Assert(overdue.SequenceEqual(["movie:9103", "movie:9102"]),
        "Among overdue titles the newest go first (9103, then 9102, were just added to a second library), not the oldest attempt (9101)", overdue);
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
        Assert((await Attempt(9101)).Manual, "The attempt is recorded as manual");
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
            outcome = (await Attempt(tmdb)).Outcome;
            return outcome != "pending";
        }, $"the outcome of {tmdb}");
        return outcome;
    }

    var kept = (await EntryRatings(admin, movieA))["imdb"].GetProperty("fetchedAt").GetDateTime();
    foreach (var mode in new[] { "badstructure", "badvalue", "badscore" })
    {
        boundary.TitleModes[9101] = mode;
        var outcome = await RefreshAndWait(movieA, 9101);
        var after = await EntryRatings(admin, movieA);
        Assert(outcome == "malformed" && after.Count == 9 && after["imdb"].GetProperty("fetchedAt").GetDateTime() == kept,
            $"A {mode} answer is malformed and keeps every stored value", new { outcome, sources = after.Count });
    }

    Assert((await Status()).GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32() == 3, "Each counts as a provider failure");
    boundary.TitleModes[9107] = "absent";
    Assert(await RefreshAndWait(extra[4], 9107) == "ok", "Values MDBList does not have (null, \"\", N/A) are absent, not malformed");
    var absent = await EntryRatings(admin, extra[4]);
    Assert(!absent.ContainsKey("imdb") && !absent.ContainsKey("tomatoes_critic") && absent["trakt"].GetProperty("value").GetDouble() == 77 &&
        absent["trakt"].GetProperty("votes").GetInt32() == 1200 && (await Status()).GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32() == 0,
        "Numbers sent as text are read; the answer resets the failure streak");
    boundary.TitleModes.Remove(9101, out _);
    boundary.TitleModes.Remove(9107, out _);

    // ---- In flight (review 2026-10-07, P2 2; round 2, P2 2-4). A settings save and the fetcher share one gate: the save waits
    // for a call already out, that call's answer is recorded first (about the key it used), and nothing starts afterwards with
    // what the save replaced. Each case holds a call at the boundary and saves while it is out.
    async Task<(HttpStatusCode Status, JsonElement Body)> SaveWhileHeld(object body, string what)
    {
        var saving = Patch(body);
        await Task.Delay(400);
        Assert(!saving.IsCompleted, $"The save waits while a call is out ({what})");
        boundary.Release.TrySetResult();
        return await saving.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // A run under way: turning ratings off, or lowering the budget, stops it; no call starts once the save has returned.
    foreach (var change in new[] { "off", "budget" })
    {
        boundary.HoldAny = true;
        callsBefore = boundary.Calls.Count;
        var running = Run();
        await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        revision = (await Settings()).GetProperty("revision").GetInt32();
        var used = (await Status()).GetProperty("budget").GetProperty("used").GetInt32();
        var changed = await SaveWhileHeld(change == "off" ? new { revision, enabled = (bool?)false, dailyBudget = (int?)null } :
            new { revision, enabled = (bool?)null, dailyBudget = (int?)used }, change == "off" ? "ratings off" : "budget lowered");
        var callsAtSave = boundary.Calls.Count;
        await running;
        boundary.ResetHold();
        var stop = (await Status()).GetProperty("lastRun").GetProperty("stopReason").GetString();
        Assert(changed.Status == HttpStatusCode.OK && callsAtSave == callsBefore + 1 && boundary.Calls.Count == callsBefore + 1 &&
            stop == (change == "off" ? "ratings_disabled" : "budget_spent"),
            change == "off" ? "Turning ratings off stops a running fetch: the call already out finishes, no other starts" :
                "Lowering the budget stops a running fetch at the new limit: no call starts after the save",
            new { calls = boundary.Calls.Count - callsBefore, stop });
        revision = (await Settings()).GetProperty("revision").GetInt32();
        await Patch(new { revision, enabled = true, dailyBudget = 500 });
    }

    // A run under way when the key is replaced: the call out used the old key, every later call the new one.
    boundary.HoldAny = true;
    boundary.AlsoAccept = Key2;
    var logBefore = boundary.CallLog.Count;
    var replacing = Run();
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await SaveWhileHeld(new { revision, apiKey = new { action = "replace", value = Key2 } }, "key replaced")).Status == HttpStatusCode.OK,
        "The key is replaced while a run's call is out");
    await replacing;
    boundary.ResetHold();
    var runLog = boundary.CallLog.Skip(logBefore).ToArray();
    Assert(runLog.Length >= 3 && runLog[0].KeyPrint == Boundary.Print(Key) && runLog.Skip(1).All(call => call.KeyPrint == Boundary.Print(Key2)),
        "The call out when the key was replaced used the old key; every call after the save used the new one (round 2, P2 2)",
        runLog.Select(call => call.KeyPrint == Boundary.Print(Key) ? "old" : call.KeyPrint == Boundary.Print(Key2) ? "new" : "other"));
    boundary.ExpectedKey = Key2;
    boundary.AlsoAccept = null;

    // The old key refused while its replacement is saved: recorded first, then lifted by the save.
    boundary.HoldTitle = 9102;
    boundary.TitleModes[9102] = "unauthorized";
    Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieB}/Ratings/Refresh")).Status == HttpStatusCode.Accepted, "A refresh is queued");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await SaveWhileHeld(new { revision, apiKey = new { action = "replace", value = Key } }, "old key refused")).Status == HttpStatusCode.OK,
        "The key is replaced while the old key's call is out");
    boundary.ResetHold();
    boundary.TitleModes.Remove(9102, out _);
    boundary.ExpectedKey = Key;
    Assert((await Attempt(9102)).Outcome == "unauthorized" && (await Status()).GetProperty("blocker").ValueKind == JsonValueKind.Null,
        "The old key's 401 is recorded before the save, and the save lifts the block: the new key is not blocked (round 2, P2 3)");
    Assert(await RefreshAndWait(movieB, 9102) == "ok", "The new key fetches at once");

    // The old key's quota spent while its replacement is saved: the breaker the 429 opened is the old key's, and the title is
    // not held back under the new key (round 2, P2 3-4).
    boundary.HoldTitle = 9102;
    boundary.TitleModes[9102] = "bothheaders";
    boundary.ResetAt = time.GetUtcNow().AddDays(3);
    Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{movieB}/Ratings/Refresh")).Status == HttpStatusCode.Accepted, "A refresh is queued");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await SaveWhileHeld(new { revision, apiKey = new { action = "replace", value = Key2 } }, "old key rate-limited")).Status == HttpStatusCode.OK,
        "The key is replaced while the old key's call is out");
    boundary.ResetHold();
    boundary.TitleModes.Remove(9102, out _);
    boundary.ExpectedKey = Key2;
    Assert((await Attempt(9102)).Outcome == "rate_limited" && !(await Status()).GetProperty("breaker").GetProperty("open").GetBoolean(),
        "The old key's 429 is recorded before the save, and the save closes the breaker it opened");
    callsBefore = boundary.Calls.Count;
    await Run();
    Assert(boundary.Calls.Skip(callsBefore).ToArray() is ["movie:9102"] && (await Attempt(9102)).Outcome == "ok",
        "The next run fetches that title with the new key at once: the old key's reset holds nothing back (round 2, P2 4)",
        boundary.Calls.Skip(callsBefore));

    // Test under way while a save comes in: the save waits, and the save's new revision is not marked verified.
    boundary.HoldTitle = MdbListClient.TestTmdbId;
    var testing = Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    Assert((await SaveWhileHeld(new { revision, refreshDays = 14 }, "Test out")).Status == HttpStatusCode.OK, "A save while Test's call is out");
    test = await testing;
    boundary.ResetHold();
    Assert(test.Body.GetProperty("ok").GetBoolean() && !(await Settings()).GetProperty("verified").GetBoolean(),
        "Test finishes first and marks only the revision it tested; the save after it is a new, untested revision");

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
        Assert(await database.RatingsFetches.AsNoTracking().AnyAsync(row => row.Outcome == "rate_limited"),
            "The attempt is recorded as rate_limited; the deadline lives on the breaker, the key's, not on the title");
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
    // A reset more than a year away is kept as given, not dropped for the end of the day (round 2, P3 7).
    boundary.Mode = "longretry";
    var askedAt = time.GetUtcNow().UtcDateTime;
    Assert(await RefreshAndWait(movieA, 9101) == "rate_limited", "A 429 asking to wait 40,000,000 seconds");
    until = (await Status()).GetProperty("breaker").GetProperty("until").GetDateTime().ToUniversalTime();
    Assert(until >= askedAt.AddSeconds(40000000 - 5) && until <= askedAt.AddSeconds(40000000 + 60),
        "The breaker stays open for the whole delay the provider gave, more than a year", new { until, askedAt });
    boundary.Mode = "full";
    revision = (await Settings()).GetProperty("revision").GetInt32();
    await Patch(new { revision, apiKey = new { action = "replace", value = Key } });
    // A delay too large for .NET's header parser saturates to the last representable moment instead of being dropped (round 3, P3 6).
    boundary.Mode = "hugeretry";
    Assert(await RefreshAndWait(movieA, 9101) == "rate_limited", "A 429 asking to wait 1,000,000,000,000 seconds");
    until = (await Status()).GetProperty("breaker").GetProperty("until").GetDateTime();
    Assert(until.Year == 9999, "The breaker stays open to the last representable moment, not to the end of the day", new { until });
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
            "Each malformed answer is recorded as such, once per title");
    boundary.Mode = "full";
    time.Offset += TimeSpan.FromHours(2);
    // One good answer ends the failure streak, so the next run's first failure is not the fifth in a row.
    Assert(await RefreshAndWait(movieA, 9101) == "ok" && (await Status()).GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32() == 0,
        "A good answer ends the failure streak");
    boundary.TitleModes[9103] = "slow";
    boundary.TitleModes[9104] = "notfound";
    boundary.TitleModes[9105] = "otherid";
    time.Offset += TimeSpan.FromDays(2);
    await Run();
    await using (var database = new ModDbContext(dbPath))
    {
        var outcomes = await database.RatingsFetches.AsNoTracking().Where(row => row.MediaType == "movie").ToDictionaryAsync(row => row.TmdbId, row => row.Outcome);
        Assert(outcomes[9103] == "timeout" && outcomes[9104] == "not_found" && outcomes[9105] == "malformed",
            "A slow provider times out, an unknown title is not_found, an answer about another title is malformed", outcomes);
    }

    Assert((await EntryRatings(admin, extra[1])).ContainsKey("imdb"), "not_found keeps the values the title already had");
    boundary.TitleModes.Clear();

    // ---- A killed run (R3): the claim commits before the call, so a restart never fetches that title again that day. While the
    // call is out the same title is added to another library and the entry being fetched is removed: the attempt is the
    // title's, so it survives that removal and still holds the new entry back (round 2, P2 5).
    time.Offset += TimeSpan.FromDays(20);
    boundary.HoldTitle = 9108;
    Guid killTwin;
    using (var kill = new CancellationTokenSource())
    {
        var scope = host.App.Services.CreateScope();
        var running = scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>().RunAsync(null, kill.Token);
        await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        killTwin = await Add("movie", 9108, world.Movies2.Id);
        Assert((await Send(admin, HttpMethod.Delete, $"JellyfinMod/Entries/{extra[5]}")).Status is HttpStatusCode.NoContent or HttpStatusCode.OK,
            "The entry being fetched is removed while its call is out");
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
            (await database.RatingsFetches.AsNoTracking().SingleAsync(row => row.MediaType == "movie" && row.TmdbId == 9108)).Outcome == "interrupted",
            "After a restart the left claim counts as an interrupted attempt, though the entry it was made for is gone");
    Assert(boundary.CallsFor("movie", 9108) == heldCalls && boundary.CallsFor("movie", 9101) >= 1,
        "The interrupted title is not fetched again that day; the others are");
    await Run();
    Assert(boundary.CallsFor("movie", 9108) == heldCalls,
        "The same title in another library does not skip the interrupted attempt's wait, even with the claimed entry removed (round 2, P2 5)");
    time.Offset += TimeSpan.FromDays(1.5);
    await Run();
    Assert(boundary.CallsFor("movie", 9108) == heldCalls + 1 && (await EntryRatings(admin, killTwin)).Count == 9,
        "A day later the interrupted title is fetched once, for the entry that holds it now");

    // ---- Persistence across the restart.
    settings = await Settings();
    Assert(settings.GetProperty("apiKeyConfigured").GetBoolean() && settings.GetProperty("dailyBudget").GetInt32() == 500 &&
        (await EntryRatings(admin, movieA)).Count == 9, "Settings, the key and the stored ratings survive a restart");

    // ---- Browse (R5, R7): no rating unless asked; one source when asked.
    var browseBase = new { mediaType = "movie", targetLibraryId = world.Movies.Id };
    var browse = await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", browseBase);
    Assert(browse.Status == HttpStatusCode.OK && browse.Body.GetProperty("items").GetArrayLength() >= 7 &&
        browse.Body.GetProperty("items").EnumerateArray().All(row => !row.TryGetProperty("rating", out _)),
        "Browse rows carry no rating unless the request asks for one (list payloads do not grow)");
    browse = await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", new { mediaType = "movie", targetLibraryId = world.Movies.Id, ratingSource = "imdb" });
    var withRating = browse.Body.GetProperty("items").EnumerateArray().Where(row => row.TryGetProperty("rating", out _)).ToArray();
    Assert(withRating.Length >= 7 && withRating.All(row => row.GetProperty("rating").GetProperty("source").GetString() == "imdb" &&
        row.GetProperty("rating").GetProperty("value").GetDouble() == 8.1), "With ratingSource each row carries that one source and nothing else");
    Assert((await Send(viewer, HttpMethod.Post, "JellyfinMod/Browse", new { mediaType = "movie", ratingSource = "google" })).Status == HttpStatusCode.BadRequest,
        "An unknown card source is 400");

    // ---- A ratings save waiting for a call out holds nothing other settings writes need, and the call's recording is bounded
    // when the database stays busy (review round 3, P2 1).
    boundary.HoldTitle = MdbListClient.TestTmdbId;
    var heldTest = Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    revision = (await Settings()).GetProperty("revision").GetInt32();
    var waitingSave = Patch(new { revision, refreshDays = 14 });
    await Task.Delay(300);
    var discovery = (await Get(admin, "JellyfinMod/Settings/Discovery")).Body;
    var otherWatch = Stopwatch.StartNew();
    var otherSave = await Send(admin, HttpMethod.Patch, "JellyfinMod/Settings/Discovery",
        new { revision = discovery.GetProperty("revision").GetInt32(), token = new { action = "unchanged" } });
    Assert(!waitingSave.IsCompleted && otherSave.Status == HttpStatusCode.OK && otherWatch.Elapsed < TimeSpan.FromSeconds(3),
        "While a ratings save waits for a call out, another settings save goes through at once", new { otherWatch.Elapsed.TotalSeconds, otherSave.Status });
    // Another writer holds SQLite's write lock as the held call answers: the recording gives up within its bound and frees the
    // gate, and the waiting save goes through once the database is free.
    await using (var blocker = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
    {
        await blocker.OpenAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync();
        }

        var answered = Stopwatch.StartNew();
        boundary.Release.TrySetResult();
        var busyTest = await heldTest.WaitAsync(TimeSpan.FromSeconds(60));
        var boundSeconds = answered.Elapsed.TotalSeconds;
        Assert(busyTest.Body.GetProperty("code").GetString() == "database_busy" && boundSeconds < RatingsRefreshRunner.DatabaseLimit.TotalSeconds + 5 &&
            !waitingSave.IsCompleted, "With the database held by another writer, Test gives up recording within its bound and says so",
            new { boundSeconds, code = busyTest.Body.GetProperty("code").GetString() });
        await using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    boundary.ResetHold();
    Assert((await waitingSave.WaitAsync(TimeSpan.FromSeconds(60))).Status == HttpStatusCode.OK, "The waiting ratings save goes through once the database is free");
    test = await Send(admin, HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("ok").GetBoolean(), "Test works again afterwards");

    // ---- The same bound on an ordinary manual refresh, at the claim and at the recording (review round 4, P2 1): a save's
    // transaction (BEGIN IMMEDIATE, COMMIT) runs on the connection with its own default timeout, which is bounded too.
    async Task<Microsoft.Data.Sqlite.SqliteConnection> HoldWriter()
    {
        var writer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await writer.OpenAsync();
        await using var begin = writer.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync();
        return writer;
    }

    // A pooled connection keeps an open transaction when it is closed, so the lock is ended explicitly.
    static async Task ReleaseWriter(Microsoft.Data.Sqlite.SqliteConnection writer)
    {
        await using var rollback = writer.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
    }

    async Task<double> SecondsUntilQueueEmpty(Stopwatch watch)
    {
        while (watch.Elapsed < TimeSpan.FromSeconds(90))
        {
            if ((await Status()).GetProperty("queued").GetInt32() == 0) return watch.Elapsed.TotalSeconds;
            await Task.Delay(100);
        }

        return watch.Elapsed.TotalSeconds;
    }

    var bound = RatingsRefreshRunner.DatabaseLimit.TotalSeconds + 5;
    var claimCalls = boundary.CallsFor("movie", 9104);
    var keptAt = (await EntryRatings(admin, extra[1]))["imdb"].GetProperty("fetchedAt").GetDateTime();
    var attemptBefore = (await Attempt(9104)).AttemptedAt;
    await using (var writer = await HoldWriter())
    {
        var watch = Stopwatch.StartNew();
        Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{extra[1]}/Ratings/Refresh")).Status == HttpStatusCode.Accepted,
            "A manual refresh is queued while another writer holds the database");
        var seconds = await SecondsUntilQueueEmpty(watch);
        Assert(seconds < bound && boundary.CallsFor("movie", 9104) == claimCalls,
            "The claim gives up within its bound and no call is made", new { seconds, bound });
        await ReleaseWriter(writer);
    }

    Assert((await Attempt(9104)).AttemptedAt == attemptBefore, "Nothing of the refused claim was written");
    boundary.HoldTitle = 9104;
    Assert((await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{extra[1]}/Ratings/Refresh")).Status == HttpStatusCode.Accepted, "A manual refresh is queued");
    await boundary.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await using (var writer = await HoldWriter())
    {
        var watch = Stopwatch.StartNew();
        boundary.Release.TrySetResult();
        var seconds = await SecondsUntilQueueEmpty(watch);
        Assert(seconds < bound && boundary.CallsFor("movie", 9104) == claimCalls + 1,
            "Its answer arrives while another writer holds the database: the recording gives up within its bound and frees the gate",
            new { seconds, bound });
        await ReleaseWriter(writer);
    }

    boundary.ResetHold();
    Assert((await Attempt(9104)).Outcome == "pending" && (await EntryRatings(admin, extra[1]))["imdb"].GetProperty("fetchedAt").GetDateTime() == keptAt,
        "Nothing of the answer was recorded: the claim stays pending (the next run counts it as interrupted) and the stored values are kept");
    settings = await Settings();

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

    // ---- Removing a title's entries removes their ratings (cascade); the title's attempt stays through its window, so the title
    // added again inside it is not fetched twice, and a run forgets it once the window has passed.
    await using (var database = new ModDbContext(dbPath))
    {
        // As if the title had just been fetched: its attempt is inside the refresh window.
        var recent = await database.RatingsFetches.SingleAsync(row => row.MediaType == "movie" && row.TmdbId == 9102);
        recent.AttemptedAt = time.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync();
    }

    foreach (var gone in new[] { movieB, bTwin })
        Assert((await Send(admin, HttpMethod.Delete, $"JellyfinMod/Entries/{gone}")).Status is HttpStatusCode.NoContent or HttpStatusCode.OK, "An entry is removed");
    await using (var database = new ModDbContext(dbPath))
        Assert(!await database.TitleRatings.AnyAsync(row => row.EntryId == movieB || row.EntryId == bTwin) &&
            await database.RatingsFetches.AnyAsync(row => row.MediaType == "movie" && row.TmdbId == 9102),
            "Its ratings go with it; the title's attempt stays");
    await Run();
    await using (var database = new ModDbContext(dbPath))
        Assert(await database.RatingsFetches.AnyAsync(row => row.MediaType == "movie" && row.TmdbId == 9102), "Inside the window a run keeps it");
    time.Offset += TimeSpan.FromDays(15);
    await Run();
    await using (var database = new ModDbContext(dbPath))
        Assert(!await database.RatingsFetches.AnyAsync(row => row.MediaType == "movie" && row.TmdbId == 9102) &&
            await database.RatingsFetches.AnyAsync(row => row.MediaType == "movie" && row.TmdbId == 9101),
            "Past the window a run forgets the attempt of a title no library holds, and keeps the others'");

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

// ---- Automatic fetching (user decision 8, 2026-10-08): soon after titles arrive and in full at setup, through the same claim,
// budget, breaker and credential gate as the daily run. A host of its own, on a fresh database, with the documented defaults
// for the arrival quiet window and the longest wait; only the pause between calls is shortened.
static async Task AutomaticAsync(string root, CapturingLoggerProvider logs)
{
    var folder = Path.Combine(root, "automatic");
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
    const string Key = "mdblist-automatic-key-0123456789abcdef";
    const string Key2 = "mdblist-automatic-replacement-fedcba98";
    boundary.ExpectedKey = Key;
    var hostLibrary = Stub<ILibraryManager>.Create((method, _) => method.Name switch
    {
        "GetLibraryOptions" => new LibraryOptions(),
        _ => method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null
    });
    var time = new ShiftedTimeProvider();
    var configuration = new PluginConfiguration { TmdbReadAccessToken = "tmdb-fixture-token" };
    var options = RatingsOptions.Default with { MinInterval = TimeSpan.FromMilliseconds(20) };
    Assert(options.Automatic && options.ArrivalQuiet == TimeSpan.FromSeconds(2) && options.ArrivalMaxWait == TimeSpan.FromSeconds(30),
        "Automatic fetching is on by default: a pass starts once arrivals have been quiet for 2 s, or after 30 s of steady arrivals");
    void Configure(IServiceCollection services)
    {
        services.AddSingleton(hostLibrary);
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(), () => configuration,
            provider.GetRequiredService<ILogger<TmdbClient>>(), null, null, new TmdbEndpoint(boundary.Address)));
        services.AddSingleton(new AcquisitionSecretStore(folder));
        RatingsServices.Add(services, () => configuration);
        services.AddSingleton(new RatingsEndpoint(boundary.Address));
        services.AddSingleton(options);
        RatingsServices.AddHostedServices(services);
        // The production reconciler, which binds a scanned or imported file to its entry (Phase 2, Phase 5).
        services.AddTransient<ReconciliationService>();
    }

    var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
    var admin = host.Client(world.Admin, true);
    var bodies = new List<string>();
    async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        bodies.Add(text);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType?.Contains("json") == true ? Json.Parse(text) : default);
    }

    async Task<JsonElement> Status() => (await Send(HttpMethod.Get, "JellyfinMod/Ratings/Status")).Body;
    async Task<int> Revision() => (await Send(HttpMethod.Get, "JellyfinMod/Settings/Ratings")).Body.GetProperty("revision").GetInt32();
    async Task<(HttpStatusCode Status, JsonElement Body)> Patch(object body) => await Send(HttpMethod.Patch, "JellyfinMod/Settings/Ratings", body);
    async Task<Guid> Add(int tmdbId, Guid library)
    {
        var response = await Send(HttpMethod.Post, "JellyfinMod/Entries", new { mediaType = "movie", tmdbId, targetLibraryId = library });
        Assert(response.Status == HttpStatusCode.OK, $"movie {tmdbId} is added through the real add path", response.Status);
        return response.Body.GetProperty("entry").GetProperty("id").GetString()!.ToGuid();
    }

    async Task<bool> HasMdbList(Guid entry)
    {
        var detail = await Send(HttpMethod.Get, $"JellyfinMod/Entries/{entry}");
        return detail.Body.GetProperty("ratings").EnumerateArray().Any(rating => rating.GetProperty("provider").GetString() == "mdblist");
    }

    // Quiet for longer than the arrival window and the 20 ms pause, so anything that was going to start has started and ended.
    async Task Settle()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await WaitFor(async () => (await Status()).GetProperty("running").ValueKind == JsonValueKind.Null, "no pass in progress");
    }

    int Arrivals() => logs.Lines.Count(line => line.Contains("Ratings run (arrivals)", StringComparison.Ordinal));

    // ---- No key: titles arrive and nothing happens — no call, no run recorded.
    var first = new List<Guid>();
    foreach (var id in new[] { 9301, 9302, 9303 })
    {
        first.Add(await Add(id, world.Movies.Id));
        await Task.Delay(15);
    }

    await Settle();
    var status = await Status();
    Assert(boundary.Calls.IsEmpty && status.GetProperty("lastRun").ValueKind == JsonValueKind.Null && status.GetProperty("running").ValueKind == JsonValueKind.Null,
        "Without a key, arriving titles make no call and record no run; they are left for later", status);

    // ---- A key saved but not yet verified starts nothing by itself (the setup run follows Test, or the next start).
    Assert((await Patch(new { revision = await Revision(), apiKey = new { action = "replace", value = Key } })).Status == HttpStatusCode.OK,
        "The administrator saves a key");
    await Settle();
    Assert(boundary.Calls.IsEmpty, "Saving a key alone makes no call");

    // ---- Startup with a key and no completed run: a full run at once, newest first.
    await host.DisposeAsync();
    var restarted = Stopwatch.StartNew();
    host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
    admin = host.Client(world.Admin, true);
    await WaitFor(async () => boundary.Calls.Count == 3 && (await Status()).GetProperty("lastRun").ValueKind == JsonValueKind.Object &&
        (await Status()).GetProperty("lastRun").GetProperty("finishedAt").ValueKind == JsonValueKind.String, "the startup run");
    status = await Status();
    Assert(boundary.Calls.SequenceEqual(["movie:9303", "movie:9302", "movie:9301"]) && status.GetProperty("lastRun").GetProperty("fetched").GetInt32() == 3 &&
        restarted.Elapsed < TimeSpan.FromSeconds(15) && await HasMdbList(first[0]),
        "A start with a key saved and no run ever completed fetches every title at once, newest first, and records the run",
        new { calls = boundary.Calls.ToArray(), restarted.Elapsed.TotalSeconds });
    var lastRunAt = status.GetProperty("lastRun").GetProperty("startedAt").GetDateTime();
    await host.DisposeAsync();
    host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
    admin = host.Client(world.Admin, true);
    await Settle();
    Assert(boundary.Calls.Count == 3 && (await Status()).GetProperty("lastRun").GetProperty("startedAt").GetDateTime() == lastRunAt,
        "Once a run has completed, a start leaves the rest to arrivals and the daily task");

    // ---- A new entry: its title is fetched within seconds, in the background.
    var arrivalsBefore = Arrivals();
    var added = Stopwatch.StartNew();
    var addWatch = Stopwatch.StartNew();
    var fresh = await Add(9304, world.Movies.Id);
    var addSeconds = addWatch.Elapsed.TotalSeconds;
    await WaitFor(async () => boundary.CallsFor("movie", 9304) == 1 && await HasMdbList(fresh), "the new title's ratings");
    var arrivedSeconds = added.Elapsed.TotalSeconds;
    Assert(arrivedSeconds < 6 && addSeconds < 2 && Arrivals() == arrivalsBefore + 1,
        "A title added to the catalog has its ratings within seconds (one arrivals pass), and the add itself did not wait for MDBList",
        new { arrivedSeconds, addSeconds });
    Console.WriteLine($"evidence - add answered in {addSeconds:F2}s; the title's ratings were stored {arrivedSeconds:F2}s after the add began");
    // The same title added to another library joins what its title already has: no call.
    var twin = await Add(9304, world.Movies2.Id);
    await WaitFor(() => HasMdbList(twin), "the twin adopts its title's ratings");
    Assert(boundary.CallsFor("movie", 9304) == 1, "The same title arriving in another library takes the stored values without a call");

    // ---- Ratings off: arrivals do nothing. Switched on again with the key saved: a full run at once.
    Assert((await Patch(new { revision = await Revision(), enabled = false })).Status == HttpStatusCode.OK, "The administrator turns ratings off");
    var offRun = (await Status()).GetProperty("lastRun").GetProperty("startedAt").GetDateTime();
    var whileOff = await Add(9305, world.Movies.Id);
    await Settle();
    Assert(boundary.CallsFor("movie", 9305) == 0 && (await Status()).GetProperty("lastRun").GetProperty("startedAt").GetDateTime() == offRun,
        "With ratings off an arriving title makes no call and records no run");
    var onWatch = Stopwatch.StartNew();
    var switchedOn = await Patch(new { revision = await Revision(), enabled = true });
    var onSeconds = onWatch.Elapsed.TotalSeconds;
    await WaitFor(async () => boundary.CallsFor("movie", 9305) == 1 && await HasMdbList(whileOff), "the setup run after switching on");
    Assert(switchedOn.Status == HttpStatusCode.OK && onSeconds < 2 && boundary.Calls.Count == 5,
        "Switching ratings on with a key saved starts a full run at once; the save answered without waiting for it", new { onSeconds });
    Console.WriteLine($"evidence - switching ratings on answered in {onSeconds:F2}s");

    // ---- Breaker open: arrivals do nothing; once it has closed, a file bound to the entry (the step an import completes
    // through) fetches the title.
    await using (var database = new ModDbContext(dbPath))
    {
        var state = await database.RatingsProviderStates.SingleAsync();
        (state.BreakerUntil, state.BreakerReason) = (time.GetUtcNow().UtcDateTime.AddHours(1), "failures");
        await database.SaveChangesAsync();
    }

    var behindBreaker = await Add(9306, world.Movies.Id);
    await Settle();
    Assert(boundary.CallsFor("movie", 9306) == 0, "With the breaker open an arriving title makes no call");
    time.Offset += TimeSpan.FromHours(2);
    await Settle();
    Assert(boundary.CallsFor("movie", 9306) == 0, "When the breaker closes nothing is fetched until a trigger or the daily run");
    async Task<ReconciliationResult> Bind(int tmdbId, string file)
    {
        var path = Path.Combine(world.Movies.Location, file);
        await File.WriteAllTextAsync(path, "fixture");
        using var scope = host.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReconciliationService>().ReconcileAsync(new NativeTitleSnapshot("movie", tmdbId,
            world.Movies.Id, $"JellyfinMod Ratings Movie {tmdbId}", 2026, null, null, null, null,
            [new NativeRepresentation(Guid.NewGuid(), world.Movies.Id, true, MediaPath: path)], []), CancellationToken.None);
    }

    var bound = Stopwatch.StartNew();
    var binding = await Bind(9306, "imported-9306.mkv");
    await WaitFor(async () => boundary.CallsFor("movie", 9306) == 1 && await HasMdbList(behindBreaker), "the bound title's ratings");
    Assert(binding.Outcome == ReconciliationOutcome.Updated && bound.Elapsed < TimeSpan.FromSeconds(6),
        "A file bound to an entry (an import completing, a scan finding it) fetches its title within seconds", new { binding.Outcome, bound.Elapsed.TotalSeconds });
    var scanned = await Bind(9307, "scanned-9307.mkv");
    await WaitFor(() => Task.FromResult(boundary.CallsFor("movie", 9307) == 1), "a title the scan created");
    Assert(scanned.Outcome == ReconciliationOutcome.Created, "A title new to the catalog from a library scan is fetched within seconds too");
    // A file bound to a title whose ratings are current is not fetched again: only due titles are.
    await Bind(9304, "second-copy-9304.mkv");
    await Settle();
    Assert(boundary.CallsFor("movie", 9304) == 1, "A file of a title fetched inside its window makes no call");

    // ---- A new episode file of a series already in the catalog (review 2026-10-08, P2 1): the series binding is unchanged, the
    // episode binding is new, and that is an arrival.
    var seriesItem = Guid.NewGuid();
    async Task<ReconciliationResult> BindSeries(params (int Season, int Number, Guid Item)[] files)
    {
        var episodes = new List<NativeEpisodeSnapshot>();
        foreach (var (season, number, item) in files)
        {
            var path = Path.Combine(world.Tv.Location, $"s{season:00}e{number:00}.mkv");
            await File.WriteAllTextAsync(path, "fixture");
            episodes.Add(new NativeEpisodeSnapshot(item, seriesItem, 9800 + number, season, number, true, MediaPath: path));
        }

        using var scope = host.App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReconciliationService>().ReconcileAsync(new NativeTitleSnapshot("series", 9701,
            world.Tv.Id, "JellyfinMod Ratings Series 9701", 2025, null, null, null, null,
            [new NativeRepresentation(seriesItem, world.Tv.Id, true, MediaPath: world.Tv.Location)], episodes), CancellationToken.None);
    }

    var firstEpisode = (1, 1, Guid.NewGuid());
    var seriesCreated = await BindSeries(firstEpisode);
    await WaitFor(() => Task.FromResult(boundary.CallsFor("show", 9701) == 1), "the scanned series' first fetch");
    Assert(seriesCreated.Outcome == ReconciliationOutcome.Created, "A series new to the catalog from a scan is fetched within seconds");
    await using (var database = new ModDbContext(dbPath))
    {
        // Its ratings are past the refresh window, so the next arrival may fetch it again.
        var attempt = await database.RatingsFetches.SingleAsync(row => row.MediaType == "series" && row.TmdbId == 9701);
        attempt.AttemptedAt = time.GetUtcNow().UtcDateTime.AddDays(-20);
        await database.SaveChangesAsync();
    }

    var unchanged = await BindSeries(firstEpisode);
    await Settle();
    Assert(unchanged.Outcome == ReconciliationOutcome.Unchanged && boundary.CallsFor("show", 9701) == 1,
        "The same scan again (nothing new) is not an arrival, even with the series' ratings due", unchanged.Outcome);
    var episodeArrived = Stopwatch.StartNew();
    var newEpisode = await BindSeries(firstEpisode, (1, 2, Guid.NewGuid()));
    await WaitFor(() => Task.FromResult(boundary.CallsFor("show", 9701) == 2), "the series after its new episode file");
    Assert(newEpisode.Outcome == ReconciliationOutcome.Updated && episodeArrived.Elapsed < TimeSpan.FromSeconds(6),
        "A new episode file of a known series (its series binding unchanged) fetches the series' due ratings within seconds (review 2026-10-08, P2 1)",
        new { newEpisode.Outcome, episodeArrived.Elapsed.TotalSeconds });

    // ---- A burst: 50 titles added back to back become one pass, one call at a time, inside the budget.
    var used = (await Status()).GetProperty("budget").GetProperty("used").GetInt32();
    Assert((await Patch(new { revision = await Revision(), dailyBudget = used + 20 })).Status == HttpStatusCode.OK, "The budget leaves room for 20 calls today");
    boundary.MaxInFlight = 0;
    arrivalsBefore = Arrivals();
    var callsBefore = boundary.Calls.Count;
    var burst = new List<Guid>();
    for (var id = 9400; id < 9450; id++) burst.Add(await Add(id, world.Movies.Id));
    await WaitFor(() => Task.FromResult(boundary.Calls.Count == callsBefore + 20), "the burst's pass");
    await Settle();
    status = await Status();
    var burstCalls = boundary.Calls.Skip(callsBefore).ToArray();
    Assert(burstCalls.Length == 20 && burstCalls.SequenceEqual(Enumerable.Range(0, 20).Select(index => $"movie:{9449 - index}")) &&
        Arrivals() == arrivalsBefore + 1 && boundary.MaxInFlight == 1 &&
        status.GetProperty("lastRun").GetProperty("fetched").GetInt32() == 20 && status.GetProperty("lastRun").GetProperty("stopReason").GetString() == "budget_spent",
        "50 titles added back to back are one pass: one call at a time, newest first, stopped by the budget after 20; the other 30 wait",
        new { calls = burstCalls.Length, passes = Arrivals() - arrivalsBefore, boundary.MaxInFlight });
    var withoutBefore = status.GetProperty("entriesWithoutRatings").GetInt32();
    Assert(withoutBefore == 30, "Status counts the 30 titles still without ratings", withoutBefore);

    // ---- A replaced key verified by Test: a full run at once (raising the budget alone starts nothing).
    Assert((await Patch(new { revision = await Revision(), dailyBudget = 500 })).Status == HttpStatusCode.OK, "The budget is raised again");
    await Settle();
    Assert(boundary.Calls.Count == callsBefore + 20, "Raising the budget starts nothing by itself");
    boundary.ExpectedKey = Key2;
    Assert((await Patch(new { revision = await Revision(), apiKey = new { action = "replace", value = Key2 } })).Status == HttpStatusCode.OK,
        "The administrator replaces the key");
    await Settle();
    Assert(boundary.Calls.Count == callsBefore + 20, "Replacing the key starts nothing until Test verifies it");
    var test = await Send(HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("ok").GetBoolean(), "Test verifies the new key", test.Body);
    await WaitFor(async () => (await Status()).GetProperty("entriesWithoutRatings").GetInt32() == 0, "the setup run after Test");
    await Settle();
    var setupCalls = boundary.Calls.Skip(callsBefore + 20).Where(call => call != "movie:278").ToArray();
    Assert(setupCalls.Length == 30 && setupCalls.Distinct().Count() == 30 && setupCalls[0] == "movie:9429" && boundary.WrongKeyCalls == 0,
        "A key verified by Test starts a full run at once: the 30 waiting titles, newest first, with the new key", setupCalls);

    // ---- A save during a run is fast, Status shows the pass, and a trigger during it waits for the next pass.
    time.Offset += TimeSpan.FromDays(15);
    boundary.Delay = TimeSpan.FromMilliseconds(150);
    boundary.MaxInFlight = 0;
    callsBefore = boundary.Calls.Count;
    test = await Send(HttpMethod.Post, "JellyfinMod/Settings/Ratings/Test");
    Assert(test.Body.GetProperty("ok").GetBoolean(), "Test passes again and starts another full run");
    JsonElement running = default;
    await WaitFor(async () => (running = (await Status()).GetProperty("running")).ValueKind == JsonValueKind.Object &&
        running.GetProperty("remaining").GetInt32() > 20, "a pass in progress");
    Assert(running.GetProperty("kind").GetString() == "setup" && running.GetProperty("startedAt").ValueKind == JsonValueKind.String,
        "Status shows the pass in progress: started by setup, with how many titles it still has", running);
    var patchWatch = Stopwatch.StartNew();
    var duringRun = await Patch(new { revision = await Revision(), refreshDays = 14 });
    var patchSeconds = patchWatch.Elapsed.TotalSeconds;
    var stillRunning = (await Status()).GetProperty("running");
    Assert(duringRun.Status == HttpStatusCode.OK && patchSeconds < 1.5 && stillRunning.ValueKind == JsonValueKind.Object,
        "A settings save during a run answers at once (it waits at most for the one call out) and the run carries on", new { patchSeconds });
    Console.WriteLine($"evidence - settings save during a run: {patchSeconds:F2}s; Status during the run: {running}");
    var lateArrival = await Add(9500, world.Movies.Id);
    await WaitFor(async () => boundary.CallsFor("movie", 9500) == 1 && await HasMdbList(lateArrival), "the arrival queued during the run");
    await Settle();
    var refreshCalls = boundary.Calls.Skip(callsBefore).Where(call => call != "movie:278").ToArray();
    Assert(boundary.MaxInFlight == 1 && refreshCalls.Distinct().Count() == refreshCalls.Length && refreshCalls[^1] == "movie:9500" &&
        refreshCalls.Length == 58 + 1, // 57 movies and the scanned series, then the late arrival
        "A title arriving during the run is fetched by the next pass, after it: one call at a time, no title twice",
        new { refreshCalls.Length, boundary.MaxInFlight, last = refreshCalls[^1] });

    // ---- Leak check for this host.
    Assert(bodies.All(body => !body.Contains(Key, StringComparison.Ordinal) && !body.Contains(Key2, StringComparison.Ordinal)) &&
        logs.Lines.All(line => !line.Contains(Key, StringComparison.Ordinal) && !line.Contains(Key2, StringComparison.Ordinal)),
        $"No response ({bodies.Count}) and no log line carries either automatic-run key");
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

#pragma warning disable EF1002 // a fixed table name, not input
static async Task<List<string>> ColumnsAsync(ModDbContext database, string table) =>
    await database.Database.SqlQueryRaw<string>($"SELECT name AS Value FROM pragma_table_info('{table}')").ToListAsync();
#pragma warning restore EF1002

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
