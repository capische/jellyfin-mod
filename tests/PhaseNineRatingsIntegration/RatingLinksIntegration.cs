using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Ratings;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class RatingLinksIntegration
{
    public static async Task RunAsync(string root, CapturingLoggerProvider logs)
    {
        var folder = Path.Combine(root, "links");
        Directory.CreateDirectory(folder);
        TestLibrary Library(string name, Jellyfin.Data.Enums.CollectionType kind = Jellyfin.Data.Enums.CollectionType.movies)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(path);
            return new TestLibrary { Id = Guid.NewGuid(), CollectionType = kind, Location = path };
        }
        var world = new World
        {
            Admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new User("admin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = Library("movies"), Movies2 = Library("movies2"), Far = Library("far"),
            Tv = Library("tv", Jellyfin.Data.Enums.CollectionType.tvshows), Folder = folder
        };
        await using var boundary = await Boundary.StartAsync();
        const string Key = "links-fixture-key-0123456789abcdef";
        boundary.ExpectedKey = Key;
        boundary.TitleModes[9601] = "links";
        var configuration = new PluginConfiguration { TmdbReadAccessToken = "tmdb-fixture-token" };
        var time = new ShiftedTimeProvider();
        var dbPath = Path.Combine(folder, "links.db");
        var native = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "The Matrix" };
        var library = Stub<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "GetItemById" => native,
            "GetLibraryOptions" => new LibraryOptions(),
            _ => method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null
        });
        void Configure(IServiceCollection services)
        {
            services.AddSingleton(library);
            services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(), () => configuration,
                provider.GetRequiredService<ILogger<TmdbClient>>(), null, null, new TmdbEndpoint(boundary.Address)));
            services.AddSingleton(new AcquisitionSecretStore(folder));
            RatingsServices.Add(services, () => configuration);
            services.AddSingleton(new RatingsEndpoint(boundary.Address));
            services.AddSingleton(new WikidataRatingsEndpoint(new Uri(boundary.Address, "sparql")));
            services.AddSingleton(RatingsOptions.Default with { MinInterval = TimeSpan.FromMilliseconds(20), Automatic = false });
            RatingsServices.AddHostedServices(services);
        }
        await using var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), configuration, Configure);
        using var admin = host.Client(world.Admin, true);
        using var viewer = host.Client(world.Ordinary, false);
        async Task<JsonElement> Send(HttpClient caller, HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await caller.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            if (text.Contains(Key, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The API returned a fixture key");
            return Json.Parse(text);
        }
        async Task Patch(object fields) => await Send(admin, HttpMethod.Patch, "JellyfinMod/Settings/Ratings", fields);
        async Task<int> Revision() => (await Send(admin, HttpMethod.Get, "JellyfinMod/Settings/Ratings")).GetProperty("revision").GetInt32();
        async Task<JsonElement> Status() => await Send(admin, HttpMethod.Get, "JellyfinMod/Ratings/Status");
        await Patch(new { revision = await Revision(), apiKey = new { action = "replace", value = Key } });
        var added = await Send(admin, HttpMethod.Post, "JellyfinMod/Entries", new { mediaType = "movie", tmdbId = 9601, targetLibraryId = world.Movies.Id });
        var entryId = added.GetProperty("entry").GetProperty("id").GetString()!.ToGuid();
        await using (var db = new ModDbContext(dbPath))
        {
            var entry = await db.Entries.SingleAsync(row => row.Id == entryId);
            entry.JellyfinItemId = native.Id;
            await db.SaveChangesAsync();
        }
        async Task Refresh()
        {
            await Send(admin, HttpMethod.Post, $"JellyfinMod/Entries/{entryId}/Ratings/Refresh");
            for (var count = 0; ; count++)
            {
                if ((await Status()).GetProperty("queued").GetInt32() == 0) return;
                if (count == 300) throw new InvalidOperationException("Ratings refresh did not complete");
                await Task.Delay(25);
            }
        }
        async Task<Dictionary<string, JsonElement>> Read() => (await Send(viewer, HttpMethod.Get, $"JellyfinMod/Ratings/Items/{native.Id}"))
            .GetProperty("ratings").EnumerateArray().ToDictionary(row => row.GetProperty("source").GetString()!, row => row.Clone());
        async Task NoLinks(string message)
        {
            var ratings = await Read();
            await using var db = new ModDbContext(dbPath);
            Check(ratings.Values.All(row => !row.TryGetProperty("url", out _)) &&
                !await db.TitleRatings.AnyAsync(row => row.Url != null), message);
        }

        await Refresh();
        var ratings = await Read();
        Check(ratings["tomatoes_critic"].GetProperty("url").GetString() == "https://www.rottentomatoes.com/m/the_matrix" &&
            ratings["metacritic_user"].GetProperty("url").GetString() == "https://www.metacritic.com/movie/the-matrix/" &&
            ratings["rogerebert"].GetProperty("url").GetString() == "https://www.rogerebert.com/reviews/the-matrix-1999" &&
            boundary.WikidataCalls.IsEmpty, "MDBList title links are rebuilt and served by the native-item HTTP API without a Wikidata call");
        await using (var db = new ModDbContext(dbPath))
            Check(await db.TitleRatings.CountAsync(row => row.Url != null) == 6, "All six rebuilt MDBList links are stored in SQLite");

        await using (var db = new ModDbContext(dbPath))
        {
            var row = await db.TitleRatings.SingleAsync(row => row.EntryId == entryId && row.Source == "tomatoes_critic");
            row.Url = "https://evil.example/matrix";
            await db.SaveChangesAsync();
        }
        Check(!(await Read())["tomatoes_critic"].TryGetProperty("url", out _),
            "The read boundary revalidates a stored URL before exposing it");

        boundary.TitleModes[9601] = "badlinks";
        boundary.WikidataMode = "missing";
        await Refresh();
        await NoLinks("Foreign hosts, query, fragment, encoded paths, traversal and a key in a valid slug are dropped before storage");
        Check(boundary.WikidataCalls.Count == 1 && boundary.WikidataCalls.Single().Imdb == "tt0133093" &&
            boundary.WikidataCalls.Single().UserAgent.Contains("JellyfinMod/", StringComparison.Ordinal),
            "Missing links use one IMDb query for all sources with a polite user agent");

        boundary.TitleModes[9601] = "full";
        boundary.WikidataMode = "links";
        await Refresh();
        ratings = await Read();
        Check(ratings["tomatoes_critic"].GetProperty("url").GetString() == "https://www.rottentomatoes.com/m/matrix" &&
            ratings["tomatoes_audience"].GetProperty("url").GetString() == "https://www.rottentomatoes.com/m/matrix" &&
            ratings["metacritic"].GetProperty("url").GetString() == "https://www.metacritic.com/movie/the-matrix/" &&
            ratings["letterboxd"].GetProperty("url").GetString() == "https://letterboxd.com/film/the-matrix/",
            "Wikidata P1258, P1712 and P6127 become per-source title links through the HTTP API");
        await using (var db = new ModDbContext(dbPath))
            Check(await db.TitleRatings.CountAsync(row => row.Url != null) == 5, "Wikidata links are persisted for both RT and Metacritic scores and Letterboxd");
        var beforeRead = (boundary.Calls.Count, boundary.WikidataCalls.Count);
        await Read();
        await Send(viewer, HttpMethod.Get, $"JellyfinMod/Entries/{entryId}");
        Check(beforeRead == (boundary.Calls.Count, boundary.WikidataCalls.Count), "Repeated detail reads never call either provider");

        var twin = await Send(admin, HttpMethod.Post, "JellyfinMod/Entries", new { mediaType = "movie", tmdbId = 9601, targetLibraryId = world.Movies2.Id });
        var twinId = twin.GetProperty("entry").GetProperty("id").GetString()!.ToGuid();
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>().RunAsync(null, CancellationToken.None);
        await using (var db = new ModDbContext(dbPath))
            Check(await db.TitleRatings.CountAsync(row => row.EntryId == twinId && row.Url != null) == 5 &&
                beforeRead == (boundary.Calls.Count, boundary.WikidataCalls.Count),
                "A second library adopts the title's stored links without another provider call");

        boundary.WikidataMode = "foreign";
        await Refresh();
        await NoLinks("Invalid Wikidata identifiers are dropped rather than passed through");

        boundary.WikidataMode = "keypath";
        await Refresh();
        await NoLinks("Even title-shaped Wikidata paths containing the credential are dropped");

        // One slot left: the scores may refresh, but a link lookup must not overspend it.
        var used = (await Status()).GetProperty("budget").GetProperty("used").GetInt32();
        await Patch(new { revision = await Revision(), dailyBudget = used + 1 });
        var wikiBefore = boundary.WikidataCalls.Count;
        await Refresh();
        Check(boundary.WikidataCalls.Count == wikiBefore &&
            (await Status()).GetProperty("budget").GetProperty("used").GetInt32() == used + 1,
            "Wikidata shares the daily budget and is skipped when MDBList uses the last slot");
        await Patch(new { revision = await Revision(), dailyBudget = 500 });

        boundary.WikidataMode = "fail";
        for (var count = 0; count < 5; count++) await Refresh();
        Check((await Status()).GetProperty("breaker").GetProperty("open").GetBoolean() &&
            (await Read())["imdb"].GetProperty("value").GetDouble() == 8.1,
            "Five Wikidata HTTP failures open the existing breaker and keep the newly refreshed scores");
        var beforeBreaker = (boundary.Calls.Count, boundary.WikidataCalls.Count);
        using (var refused = await admin.PostAsync($"JellyfinMod/Entries/{entryId}/Ratings/Refresh", null))
            Check(refused.StatusCode == System.Net.HttpStatusCode.Conflict && beforeBreaker == (boundary.Calls.Count, boundary.WikidataCalls.Count),
                "An open breaker stops calls to both providers");
        time.Offset += TimeSpan.FromHours(2);
        boundary.WikidataMode = "ratelimited";
        await Refresh();
        var breaker = (await Status()).GetProperty("breaker");
        Check(breaker.GetProperty("open").GetBoolean() && breaker.GetProperty("reason").GetString() == "rate_limited" &&
            breaker.GetProperty("until").GetDateTime() > time.GetUtcNow().UtcDateTime.AddHours(47),
            "Wikidata 429 honors Retry-After through the existing breaker");
        Check(logs.Lines.All(line => !line.Contains(Key, StringComparison.OrdinalIgnoreCase)), "Link lookups and rejected URLs leak no key in logs");
        Console.WriteLine("PASS: rating links — MDBList/Wikidata HTTP, SQLite, native-item API, validation, shared budget and breaker");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("ok - " + message);
    }
}
