using System.Net.Http.Json;
using System.Text.Json;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The release picker's Sources (P4.FU25): each indexer keeps one colour of eight from when it is first saved, through a restart,
/// in the settings list, in a search's indexers, its progress lines and its rows; a removed indexer's colour goes to the next one
/// added, a ninth wraps, and indexers added at the same moment never share a free colour. A search given <c>indexerIds</c> asks
/// only those indexers. Real Kestrel host, HTTP, serializer, migrations, SQLite and a Torznab boundary over real HTTP.
/// </summary>
internal static class SourcesScenario
{
    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "sources-media");
        foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var world = new World
        {
            Admin = new Jellyfin.Database.Implementations.Entities.User("cadmin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new Jellyfin.Database.Implementations.Entities.User("cadmin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new Jellyfin.Database.Implementations.Entities.User("ctv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new Jellyfin.Database.Implementations.Entities.User("cviewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
            Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
            Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
            Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
            Folder = folder
        };
        var dbPath = Path.Combine(folder, "sources.db");
        await using var torznab = new TorznabBoundary();
        await torznab.StartAsync();
        var feeds = new[] { "alpha", "beta", "gamma" }.ToDictionary(name => name, name =>
        {
            var feed = new IndexerScript { ApiKey = name + "-key", Caps = TorznabBoundary.Caps("q", "q") };
            feed.MovieItems.Add(new($"Sources.Movie.2025.2160p.WEB-DL.DDP5.1.H.265-{name.ToUpperInvariant()}", name + "-1",
                new Uri(torznab.Address, "/dl/none").ToString(), 20_000_000_000, 40, new Dictionary<string, string>()));
            torznab.Indexers[name] = feed;
            return feed;
        });
        int Searches(string name) => feeds[name].Queries.Count(query => !query.Contains("t=caps", StringComparison.Ordinal));

        var colours = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            var movieId = Guid.NewGuid();
            await using (var database = new ModDbContext(dbPath))
            {
                var metadata = JsonSerializer.Serialize(new TmdbMetadata("movie", 702, "Sources Movie", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, "tt0000702", null, false, null, 120, [], [], []));
                var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                    VALUES ({movieId}, 'movie', 702, 'tt0000702', 'Sources Movie', 2025, {metadata}, 0, 1, {added}, {world.Movies.Id}, 0)
                    """);
            }

            async Task<JsonElement> AddAsync(string name, string feed, int priority) => await ReadAsync(await admin.PostAsJsonAsync(
                "/JellyfinMod/Settings/Indexers", new
                {
                    name, baseUrl = new Uri(torznab.Address, $"/{feed}/api").ToString(), enabled = true, categories = new[] { 2000 },
                    priority, minIntervalSeconds = 0, apiKey = new { action = "replace", value = feeds.GetValueOrDefault(feed)?.ApiKey ?? "none" }
                }), 201, $"{name} is created");

            foreach (var (name, priority) in new[] { ("alpha", 1), ("beta", 2), ("gamma", 3) })
            {
                var created = await AddAsync("Sources " + name, name, priority);
                ids[name] = created.GetProperty("id").AsGuid();
                colours[name] = created.GetProperty("colorIndex").GetInt32();
            }

            Assert(colours["alpha"] == 0 && colours["beta"] == 1 && colours["gamma"] == 2,
                "The first indexers take the palette in the order they were added: " + string.Join(", ", colours));

            // Only the chosen indexer is asked; its colour is on its outcome, its progress line and its rows.
            var progressKey = "sources-" + Guid.NewGuid().ToString("N");
            var only = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&indexerIds={ids["beta"]}&progressKey={progressKey}"),
                200, "A search of one chosen indexer answers");
            var outcomes = only.GetProperty("indexers").EnumerateArray().ToArray();
            Assert(Searches("beta") > 0 && Searches("alpha") == 0 && Searches("gamma") == 0,
                $"Only the chosen indexer was searched: alpha {Searches("alpha")}, beta {Searches("beta")}, gamma {Searches("gamma")}");
            Assert(outcomes.Length == 1 && outcomes[0].GetProperty("indexerId").AsGuid() == ids["beta"]
                && outcomes[0].GetProperty("colorIndex").GetInt32() == 1 && outcomes[0].GetProperty("status").GetString() == "ok",
                "The search's indexers name only the chosen one, with its colour: " + only.GetProperty("indexers").GetRawText());
            var rows = only.GetProperty("candidates").EnumerateArray().ToArray();
            Assert(rows.Length == 1 && rows[0].GetProperty("indexerColorIndex").GetInt32() == 1,
                "The chosen indexer's row carries its colour: " + only.GetProperty("candidates").GetRawText());
            var progress = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases/Progress/{progressKey}"), 200, "The search's progress is readable");
            var lines = progress.GetProperty("indexers").EnumerateArray().ToArray();
            Assert(lines.Length == 1 && lines[0].GetProperty("indexerId").AsGuid() == ids["beta"] && lines[0].GetProperty("colorIndex").GetInt32() == 1,
                "The progress has one line, the chosen indexer's, with its colour: " + progress.GetProperty("indexers").GetRawText());

            // Without indexerIds every enabled indexer is asked, as before.
            var all = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "A search of every indexer answers");
            var allColours = all.GetProperty("indexers").EnumerateArray()
                .ToDictionary(outcome => outcome.GetProperty("indexerId").AsGuid(), outcome => outcome.GetProperty("colorIndex").GetInt32());
            Assert(allColours.Count == 3 && ids.All(pair => allColours[pair.Value] == colours[pair.Key]) && Searches("alpha") > 0 && Searches("gamma") > 0,
                "Without indexerIds every indexer is asked, each with its colour: " + all.GetProperty("indexers").GetRawText());

            await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&indexerIds={Guid.NewGuid()}"), 409,
                "A search naming no enabled indexer is refused");

            // A removed indexer's colour goes to the next one added; the eight are used before any repeats, and the ninth wraps.
            Assert((await admin.DeleteAsync($"/JellyfinMod/Settings/Indexers/{ids["beta"]}")).IsSuccessStatusCode, "beta is removed");
            ids.Remove("beta");
            colours.Remove("beta");
            var again = await AddAsync("Sources beta again", "beta", 2);
            ids["beta again"] = again.GetProperty("id").AsGuid();
            colours["beta again"] = again.GetProperty("colorIndex").GetInt32();
            Assert(colours["beta again"] == 1, "A removed indexer's colour is the first given to the next one added: " + colours["beta again"]);
            for (var extra = 3; extra <= 8; extra++)
            {
                var created = await AddAsync("Sources extra " + extra, "none", 10 + extra);
                ids["extra " + extra] = created.GetProperty("id").AsGuid();
                colours["extra " + extra] = created.GetProperty("colorIndex").GetInt32();
            }

            Assert(Enumerable.Range(3, 5).All(extra => colours["extra " + extra] == extra) && colours["extra 8"] == 0,
                "Indexers four to eight take the remaining colours and the ninth wraps to the first: " + string.Join(", ", colours));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // After a restart every indexer keeps its colour.
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            var listed = (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Indexers"), 200, "The indexers are listed after a restart"))
                .EnumerateArray().ToDictionary(indexer => indexer.GetProperty("id").AsGuid(), indexer => indexer.GetProperty("colorIndex").GetInt32());
            Assert(listed.Count == ids.Count && ids.All(pair => listed[pair.Value] == colours[pair.Key]),
                "Every indexer keeps its colour across a restart: " + string.Join(", ", listed.Values));

            // Seven indexers added at the same moment: each takes its own free colour (one of each is in use, the first twice), never
            // the same one twice (P4.FU25 review).
            var created = await Task.WhenAll(Enumerable.Range(1, 7).Select(async index =>
            {
                var response = await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
                {
                    name = "Sources together " + index, baseUrl = new Uri(torznab.Address, "/none/api").ToString(), enabled = false,
                    categories = new[] { 2000 }, priority = 50 + index, minIntervalSeconds = 0, apiKey = new { action = "replace", value = "none" }
                });
                return (await ReadAsync(response, 201, "A concurrent indexer is created")).GetProperty("colorIndex").GetInt32();
            }));
            Assert(created.Order().SequenceEqual(Enumerable.Range(1, 7)),
                "Indexers added together take the seven free colours, none twice: " + string.Join(", ", created));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Console.WriteLine("PASS: indexer colours are given once and kept, and a search with indexerIds asks only those indexers");
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
