using System.Net.Http.Json;
using System.Text.Json;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A slow indexer behind Prowlarr (1337x through FlareSolverr, 2026-10-25): Prowlarr reads 1337x in four pages of twenty with a
/// delay between them and queues every client's query behind the one before, so one query takes 10 to 22 seconds and a title
/// that needs its fallback query takes two of them. With the production bounds such an indexer still answers in full. Real
/// Kestrel host, HTTP, serializer, migrations, SQLite and a Torznab boundary over real HTTP.
/// </summary>
internal static class SlowIndexerScenario
{
    private static readonly TimeSpan QueryTime = TimeSpan.FromSeconds(12);

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "slow-media");
        foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var world = new World
        {
            Admin = new Jellyfin.Database.Implementations.Entities.User("sadmin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new Jellyfin.Database.Implementations.Entities.User("sadmin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new Jellyfin.Database.Implementations.Entities.User("stv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new Jellyfin.Database.Implementations.Entities.User("sviewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
            Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
            Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
            Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
            Folder = folder
        };
        var dbPath = Path.Combine(folder, "slow-indexer.db");
        await using var torznab = new TorznabBoundary();
        await torznab.StartAsync();
        // Like 1337x: movie search by text only. Every query answers after the time Prowlarr takes; the title with its year finds
        // nothing, so the search goes on to the bare title, which finds the release.
        var feed = new IndexerScript
        {
            ApiKey = "slow-proxy-key", Caps = TorznabBoundary.Caps("q", "q"),
            Override = async context =>
            {
                if (context.Request.Query["t"] == "caps") return false;
                await Task.Delay(QueryTime, context.RequestAborted);
                if (!context.Request.Query["q"].ToString().Contains("2025", StringComparison.Ordinal)) return false;
                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync("""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><torznab:response offset="0" total="0"/></channel></rss>""");
                return true;
            }
        };
        torznab.Indexers["slowproxy"] = feed;
        feed.MovieItems.Add(new("Slow.Movie.2025.2160p.WEB-DL.DDP5.1.H.265-GRP", "slow-1", new Uri(torznab.Address, "/dl/none").ToString(),
            20_000_000_000, 40, new Dictionary<string, string>()));

        // The production bounds, not the shortened ones the rest of the suite uses.
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration(),
            services => services.AddSingleton(TorznabOptions.Default)))
        {
            using var admin = host.Client(world.Admin, true);
            var movieId = Guid.NewGuid();
            await using (var database = new ModDbContext(dbPath))
            {
                var metadata = JsonSerializer.Serialize(new TmdbMetadata("movie", 701, "Slow Movie", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, "tt0000701", null, false, null, 120, [], [], []));
                var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                    VALUES ({movieId}, 'movie', 701, 'tt0000701', 'Slow Movie', 2025, {metadata}, 0, 1, {added}, {world.Movies.Id}, 0)
                    """);
            }

            await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Slow proxy", baseUrl = new Uri(torznab.Address, "/slowproxy/api").ToString(), enabled = true, categories = new[] { 2000 },
                priority = 1, minIntervalSeconds = 0, apiKey = new { action = "replace", value = feed.ApiKey }
            }), 201, "The slow indexer is created");

            var started = DateTime.UtcNow;
            var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "The slow indexer's search answers");
            var took = DateTime.UtcNow - started;
            var outcome = search.GetProperty("indexers").EnumerateArray().Single();
            var queries = feed.Queries.Where(query => !query.Contains("t=caps", StringComparison.Ordinal)).ToArray();
            Assert(queries.Length == 2 && took > TimeSpan.FromSeconds(20),
                $"The slow indexer was asked twice and took longer than the old 20-second bound: {queries.Length} queries in {took.TotalSeconds:F1}s");
            Assert(outcome.GetProperty("status").GetString() == "ok" && outcome.GetProperty("resultCount").GetInt32() == 1 &&
                search.GetProperty("candidates").EnumerateArray().Single().GetProperty("rawTitle").GetString() == feed.MovieItems[0].Title,
                "An indexer that needs two slow queries still answers in full within the production bound: " + outcome.GetRawText());
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Console.WriteLine("PASS: a slow Prowlarr-proxied indexer that needs its fallback query answers in full within the production bound");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
