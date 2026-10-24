using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Russian and Ukrainian tracker names (P4.FU22, user 2026-10-25): a release is the title when the title stands in its name
/// as whole words and, for a film, the year follows it; quality is read from the whole name; full discs are refused; a
/// remux is a remux whatever other words it carries. Every title below is a real one, as RuTracker, Kinozal, Mazepa, 1337x
/// and Uindex returned it through Prowlarr on 2026-10-25, except the two marked as the tracker's own Cyrillic season form
/// (Prowlarr rewrites it to S1E1-4) and the one marked as a scene full-disc name. Real Kestrel host, HTTP, serializer,
/// migrations, SQLite and a text-only Torznab boundary over real HTTP.
/// </summary>
internal static class TrackerTitleScenarios
{
    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "tracker-media");
        foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var world = new World
        {
            Admin = new User("tadmin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new User("tadmin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new User("ttv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("tviewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
            Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
            Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
            Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
            Folder = folder
        };

        var dbPath = Path.Combine(folder, "tracker-titles.db");
        await using var torznab = new TorznabBoundary();
        await torznab.StartAsync();
        var inception = Guid.NewGuid();
        var matrix = Guid.NewGuid();
        var adolescence = Guid.NewGuid();
        var peepShow = Guid.NewGuid();
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            await using (var database = new ModDbContext(dbPath))
            {
                var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                string Metadata(string type, int tmdb, string title, int year, string? imdb, int runtime) => JsonSerializer.Serialize(new TmdbMetadata(
                    type, tmdb, title, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null, imdb, null, false, null, runtime, [], [], [],
                    OriginalTitle: title));
                foreach (var (id, tmdb, imdb, title, year, runtime) in new[]
                         {
                             (inception, 27205, "tt1375666", "Inception", 2010, 148), (matrix, 603, "tt0133093", "The Matrix", 1999, 136)
                         })
                {
                    var metadata = Metadata("movie", tmdb, title, year, imdb, runtime);
                    await database.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                        VALUES ({id}, 'movie', {tmdb}, {imdb}, {title}, {year}, {metadata}, 0, 1, {added}, {world.Movies.Id}, 0)
                        """);
                }

                foreach (var (id, tmdb, title, year, episodes) in new[] { (adolescence, 249042, "Adolescence", 2025, 4), (peepShow, 815, "Peep Show", 2003, 6) })
                {
                    var metadata = Metadata("series", tmdb, title, year, null, 45);
                    await database.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO Entries (Id, MediaType, TmdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                        VALUES ({id}, 'series', {tmdb}, {title}, {year}, {metadata}, 0, 1, {added}, {world.Tv.Id}, 0)
                        """);
                    var aired = new DateTime(year, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                    for (var episode = 1; episode <= episodes; episode++)
                        await database.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO Episodes (Id, EntryId, TmdbId, SeasonNumber, EpisodeNumber, Title, RuntimeMinutes, Monitored, State, AirDate)
                            VALUES ({Guid.NewGuid()}, {id}, {tmdb * 100 + episode}, 1, {episode}, 'Episode', 45, 1, 0, {aired})
                            """);
                }
            }

            // Text-only, as Prowlarr's RuTracker, Kinozal and Mazepa definitions are: no ids, so identity is the name alone.
            var feed = new IndexerScript { ApiKey = "tracker-key", Caps = TorznabBoundary.Caps("q", "q") };
            torznab.Indexers["trackers"] = feed;
            var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Trackers", baseUrl = new Uri(torznab.Address, "/trackers/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
                priority = 1, minIntervalSeconds = 0, apiKey = new { action = "replace", value = feed.ApiKey }
            }), 201, "The tracker scenario's indexer is created");
            Assert((await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200,
                "The tracker scenario's indexer is verified")).GetProperty("ok").GetBoolean(), "The tracker scenario's indexer answers its capabilities");

            FeedItem Row(string title, int category) => new(title, title, new Uri(torznab.Address, "/dl/none").ToString(), 20_000_000_000, 25, new(),
                Categories: [category]);

            async Task<Dictionary<string, JsonElement>> SearchAsync(string step, string query, List<FeedItem> items, params (string Title, int Category)[] rows)
            {
                items.Clear();
                items.AddRange(rows.Select(row => Row(row.Title, row.Category)));
                var search = await ReadAsync(await admin.GetAsync("/JellyfinMod/Releases?" + query), 200, step);
                var candidates = search.GetProperty("candidates").EnumerateArray().ToDictionary(row => row.GetProperty("rawTitle").GetString()!);
                Assert(candidates.Count == rows.Length, step + ": every row is evaluated: " + candidates.Count);
                return candidates;
            }

            static string Codes(JsonElement row) => string.Join(",", row.GetProperty("rejections").EnumerateArray().Select(item => item.GetProperty("code").GetString()));
            static void Eligible(Dictionary<string, JsonElement> rows, string title, string quality, string identity, string? rip = null)
            {
                var row = rows[title];
                var parsed = row.GetProperty("parsed");
                Assert(row.GetProperty("eligible").GetBoolean() && parsed.GetProperty("quality").GetString() == quality &&
                    row.GetProperty("match").GetProperty("identity").GetString() == identity && parsed.GetProperty("rip").GetString() == rip,
                    $"{title} is eligible as {quality} ({identity}, rip {rip ?? "none"}): {Codes(row)} {parsed.GetRawText()}");
            }

            static void Rejected(Dictionary<string, JsonElement> rows, string title, string code) => Assert(!rows[title].GetProperty("eligible").GetBoolean() &&
                rows[title].GetProperty("rejections").EnumerateArray().Any(item => item.GetProperty("code").GetString() == code),
                $"{title} is rejected as {code}: {Codes(rows[title])}");

            // ---- A film: the original title after the local one, the year after the title, quality inside the brackets.
            const string ruRemux = "Начало / Inception [2010, США, Великобритания, фантастика, боевик, триллер, драма, детектив, UHD BDRemux 2160p, HDR10, Dolby Vision] [Hybrid] Dub + 2x DVO + 2x AVO + VO + Dub (Ukr) + Original (Eng) + )";
            const string ruUhdRip = "Начало / Inception [2010, США, Великобритания, фантастика, боевик, триллер, драма, детектив, UHD BDRip 1080p, HDR10, Dolby Vision] [Hybrid] Dub + 3x DVO + 2x AVO + VO + 2x Ukr + Original (Eng) + )";
            const string ruDisc = "Начало / Inception [2010, США, Великобритания, фантастика, боевик, триллер, драма, UHD BR-DISK 2160p] Dub + Original Eng";
            const string uaRemux = "Початок / Inception (2010) UHD BDRemux 4K 2160p HDR H.265 2xUkr/Eng | Sub Ukr/Eng";
            const string uaRip = "Початок / Inception (2010) BDRip 1080p 2xUkr/Eng | Sub Ukr/Eng";
            const string uaTalk = "Початок / Inception - обговорення фільму (2015) HDTV";
            const string kzRip = "Inception 2010 DUB, 2 x DVO, Sub 4K, HEVC, SDR BDRip 2160p - RUSSIAN";
            const string kzShort = "Inception: The Cobol Job 2010 2 x VO, Sub BDRip 1080p - RUSSIAN";
            const string kzDisc = "Inception 2010 DUB, Sub 4K, HEVC, HDR  BR-DISK 2160p - RUSSIAN";
            const string uindexCut = "Inception 2010 UHD BluRay 2160p DTS-HD MA 5 1 DV HEVC HYBRID REMU...";
            const string fullRemux = "Inception 2010 UHD BluRay 2160p DTS HD MA 5 1 DV HEVC HYBRID REMUX-FraMeSToR";
            const string scene = "Inception 2010 1080p BluRay HEVC x265 5.1 BONE";
            // The one name here not seen on a tracker: the scene form of a full UHD disc.
            const string sceneDisc = "Inception.2010.COMPLETE.UHD.BLURAY-SURCODE";
            var rows = await SearchAsync("Inception from Russian, Ukrainian and scene trackers", $"entryId={inception}", feed.MovieItems,
                (ruRemux, 2045), (ruUhdRip, 2040), (ruDisc, 2045), (uaRemux, 2045), (uaRip, 2040), (uaTalk, 5000), (kzRip, 2000), (kzShort, 5000),
                (kzDisc, 2000), (uindexCut, 2000), (fullRemux, 2000), (scene, 2000), (sceneDisc, 2000));
            Eligible(rows, ruRemux, "remux-2160p", "title");
            Eligible(rows, ruUhdRip, "bluray-1080p", "title", "BDRip");
            Eligible(rows, uaRemux, "remux-2160p", "title");
            Eligible(rows, uaRip, "bluray-1080p", "title", "BDRip");
            Eligible(rows, kzRip, "bluray-2160p", "title", "BDRip");
            Eligible(rows, uindexCut, "remux-2160p", "title");
            Eligible(rows, fullRemux, "remux-2160p", "title");
            Eligible(rows, scene, "bluray-1080p", "title");
            Rejected(rows, kzShort, "title_mismatch");
            Rejected(rows, uaTalk, "title_mismatch");
            foreach (var disc in new[] { ruDisc, kzDisc, sceneDisc })
            {
                Rejected(rows, disc, "full_disc");
                Assert(rows[disc].GetProperty("parsed").GetProperty("source").GetString() == "disc" &&
                    rows[disc].GetProperty("parsed").GetProperty("quality").ValueKind == JsonValueKind.Null &&
                    rows[disc].GetProperty("rejections").EnumerateArray().Single(item => item.GetProperty("code").GetString() == "full_disc")
                        .GetProperty("message").GetString() == "Full disc releases cannot be imported.",
                    $"{disc} is a full disc with no quality tier, refused as one: {rows[disc].GetRawText()}");
            }

            Assert(rows[sceneDisc].GetProperty("rejections").EnumerateArray().All(item => item.GetProperty("code").GetString() != "media_type_mismatch"),
                "COMPLETE UHD BLURAY names a disc, not a complete-series pack");

            // ---- Sequels, collections and other works stay out; the film itself passes on every tracker.
            const string ruMatrix = "Матрица / The Matrix [1999, США, фантастика, BDRemux 1080p] Dub + 3х MVO + 3х DVO + 8х AVO + VO + Original (Eng) + )";
            const string ruReloaded = "Матрица: Перезагрузка / The Matrix Reloaded [2003, США, Австралия, фантастика, боевик, BDRemux 1080p] Dub + 2x MVO + 4x DVO + 5x AVO + Original (Eng) + )";
            const string ruResurrections = "Матрица: Воскрешение / The Matrix Resurrections [2021, США, фантастика, боевик, WEB-DL 2160p, HDR10] [Локализованный видеоряд] Dub + Original (Eng) + )";
            const string uaMatrix = "Матриця / The Matrix (1999) UHD BDRemux 2160p 4K HDR H.265 3xUkr/Eng | Sub Ukr/Eng";
            const string uaTrilogy = "Матриця: Трилогія / The Matrix: Trilogy (1999-2003) BDRemux 1080p [Remastered] 3xUkr/Eng | Sub Ukr/Eng";
            const string uaTrilogyShort = "Матриця / The Matrix: Trilogy (1999-2003) BDRip 720р Ukr/Eng | Sub Ukr/Eng";
            const string kzMatrix = "The Matrix (Remastered) 1999 DUB, 3 x MVO, 3 x DVO, 9 x AVO, Sub Blu-Ray Remux 1080p - RUSSIAN";
            const string kzCollection = "The Matrix 1999-2021 DUB HEVC, HDR BDRip 1080p - RUSSIAN";
            const string kzReloaded = "The Matrix: Reloaded (Remastered) 2003 DUB, 2 x MVO, 5 x DVO, 6 x AVO, Sub Blu-Ray Remux 1080p - RUSSIAN";
            const string uindexMatrix = "The Matrix 1999 UHD BluRay 2160p TrueHD Atmos 7 1 DV HEVC REMUX-F...";
            const string sceneMatrix = "The.Matrix.1999.1080p.UHD.BluRay.DD+.71.HDR.x265-TaurohtaR.mkv";
            const string song = "(Drum & Bass) [WEB] Cold FX - The Matrix - 2026, FLAC (tracks), lossless";
            const string pack = "[PACK] The Matrix 4K UHD Collection (1999 2003) (2160p HDR BDRip x265 10bit DTS) [4KLiGHT]";
            rows = await SearchAsync("The Matrix and its sequels", $"entryId={matrix}", feed.MovieItems, (ruMatrix, 2040), (ruReloaded, 2040),
                (ruResurrections, 2045), (uaMatrix, 2045), (uaTrilogy, 2040), (uaTrilogyShort, 2040), (kzMatrix, 2000), (kzCollection, 2000),
                (kzReloaded, 2000), (uindexMatrix, 2000), (sceneMatrix, 2000), (song, 3040), (pack, 2000));
            Eligible(rows, ruMatrix, "remux-1080p", "title");
            Eligible(rows, uaMatrix, "remux-2160p", "title");
            Eligible(rows, kzMatrix, "remux-1080p", "title");
            Eligible(rows, uindexMatrix, "remux-2160p", "title");
            Eligible(rows, sceneMatrix, "bluray-1080p", "title");
            foreach (var other in new[] { ruReloaded, ruResurrections, uaTrilogy, uaTrilogyShort, kzReloaded, pack })
                Rejected(rows, other, "title_mismatch");
            Rejected(rows, kzCollection, "year_mismatch");
            Assert(rows[kzCollection].GetProperty("rejections").EnumerateArray().Any(item =>
                    item.GetProperty("message").GetString() == "The release spans 1999–2021, a collection, not one film."),
                "A year range after the title is a collection: " + Codes(rows[kzCollection]));
            Assert(!rows[song].GetProperty("eligible").GetBoolean(), "A song called The Matrix is not the film: " + Codes(rows[song]));

            // ---- A season: S1E1-4 of 4 (RuTracker through Prowlarr), (S1) (Mazepa), Season 1 (1337x), and the tracker's own
            // "Сезон: 1 / Серии: 1-4 из 4" (the two names with it are that form, not seen through Prowlarr); a shorter range or one without its total stays several episodes, not a pack.
            const string ruSeason = "Переходный возраст / Adolescence / S1E1-4 of 4 [2025, Великобритания, Драма, триллер, WEB-DL 1080p] 4 x + Ukr Dub (Postmodern) + Original + )";
            const string uaSeason = "Юнацтво / Adolescence (S1) (2025) WEB-DL 1080p Ukr/Eng";
            const string enSeason = "[moon] Adolescence   Season 1 1080p WEBRip x265 AC3 MultiSubs";
            const string kzEpisodes = "Adolescence - S1E1-4 - 2025  4 x MVO, Sub WEBDL 1080p - RUSSIAN";
            const string ruOwnSeason = "Переходный возраст / Adolescence / Сезон: 1 / Серии: 1-4 из 4 [2025, Великобритания, драма, WEB-DL 1080p]";
            const string ruOwnPart = "Переходный возраст / Adolescence / Сезон: 1 / Серии: 1-3 из 4 [2025, Великобритания, драма, WEB-DL 1080p]";
            const string otherSeries = "Взрослеть на полную / Big Time Adolescence [2019, США, драма, комедия, WEB-DL 1080p] Dub (Пифагор (iTunes)) + Original Eng";
            rows = await SearchAsync("Adolescence season 1", $"entryId={adolescence}&scope=season&seasonNumber=1", feed.TvItems, (ruSeason, 5040),
                (uaSeason, 5040), (enSeason, 5000), (kzEpisodes, 5000), (ruOwnSeason, 5040), (ruOwnPart, 5040), (otherSeries, 2040));
            foreach (var (title, quality) in new[] { (ruSeason, "webdl-1080p"), (uaSeason, "webdl-1080p"), (enSeason, "webrip-1080p"), (ruOwnSeason, "webdl-1080p") })
            {
                Eligible(rows, title, quality, "title_without_year");
                var parsed = rows[title].GetProperty("parsed");
                Assert(parsed.GetProperty("seasonPack").GetBoolean() && parsed.GetProperty("seasonNumber").GetInt32() == 1,
                    $"{title} is a pack of season 1: {parsed.GetRawText()}");
            }

            Rejected(rows, kzEpisodes, "not_a_pack");
            Rejected(rows, ruOwnPart, "not_a_pack");
            Assert(rows[ruOwnPart].GetProperty("parsed").GetProperty("episodeNumbers").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([1, 2, 3]),
                "Серии: 1-3 из 4 names three episodes");
            Assert(!rows[otherSeries].GetProperty("eligible").GetBoolean(), "Big Time Adolescence is not Adolescence: " + Codes(rows[otherSeries]));

            // A pack of season 1 out of a run of seasons: the right season matches; another season, a partial one and an
            // episode marked "Серия: 3 из 6" do not.
            const string psSeasonOne = "Пип Шоу / Peep Show / S1E1-6 (6) (Jeremy Wooding) [2003, Великобритания, Комедия, SATRip]";
            const string psSeasonTwo = "Пип Шоу / Peep Show / S2E1-6 (6) (Tristram Shapeero) [2004, Великобритания, Комедия, SATRip]";
            const string psPartial = "Пип шоу / Peep Show / S1E1-4 (6) / [2003-2009, Великобритания, комедия, DVDRip]";
            const string psEpisode = "Пип шоу / Peep show / S3 / Серия: 3 из 6 (Tristram Shapeero) [2005, Великобритания, Ситком, HDTV-AVC] Original + rus )";
            rows = await SearchAsync("Peep Show season 1", $"entryId={peepShow}&scope=season&seasonNumber=1", feed.TvItems, (psSeasonOne, 5020),
                (psSeasonTwo, 5020), (psPartial, 5020), (psEpisode, 5020));
            // The title, the season and the pack all match; only SATRip (a satellite copy of no stated resolution) is unknown.
            Assert(Codes(rows[psSeasonOne]) == "quality_unknown" && rows[psSeasonOne].GetProperty("parsed").GetProperty("seasonPack").GetBoolean(),
                "Peep Show S1E1-6 (6) is the season 1 pack, refused only for its unknown quality: " + Codes(rows[psSeasonOne]));
            Rejected(rows, psSeasonTwo, "season_mismatch");
            Rejected(rows, psPartial, "not_a_pack");
            Rejected(rows, psEpisode, "not_a_pack");
            Assert(rows[psEpisode].GetProperty("parsed").GetProperty("seasonNumber").GetInt32() == 3 &&
                rows[psEpisode].GetProperty("parsed").GetProperty("episodeNumbers").EnumerateArray().Single().GetInt32() == 3,
                "S3 / Серия: 3 из 6 is episode 3 of season 3");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Console.WriteLine("PASS: Russian and Ukrainian tracker names: title as whole words, year after it, quality from the whole name, full discs refused, cut remux names");
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
