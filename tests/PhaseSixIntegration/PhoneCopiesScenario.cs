using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Automation;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Phase 12 M36: "Also get a 1080p copy for phones". With the switch off, automation searches nothing for titles that hold files;
/// with it on, a movie whose only file is 4K is searched with the quality profile for phones and the 1080p is grabbed as Add; a
/// movie that already holds a 1080p is not a target; a movie from which a 1080p was removed (Remove This Version) is never
/// fetched again; a save that leaves the switch out keeps it. The Phase 5 host (real Kestrel, auth, MVC, EF migrations, SQLite)
/// with a Torznab and a Transmission boundary over real HTTP and the production automation runner.
/// </summary>
internal static class PhoneCopiesScenario
{
    private const long Size = 200_000;

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "phones-media");
        foreach (var directory in new[] { "movies", "tv", "downloads/jfmod" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var downloads = Path.Combine(media, "downloads", "jfmod");
        var movies = new TestLibrary { Id = Guid.NewGuid(), Name = "Movies", CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") };
        var tv = new TestLibrary { Id = Guid.NewGuid(), Name = "TV", CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") };
        var native = new NativeWorld { Libraries = [movies, tv], UserLibraries = _ => [movies, tv] };
        var previousLibrary = BaseItem.LibraryManager;
        BaseItem.LibraryManager = native.Library;
        var world = new World
        {
            Admin = new User("phadmin", "auth", "reset") { Id = Guid.NewGuid() }, RestrictedAdmin = new User("phtv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("phviewer", "auth", "reset") { Id = Guid.NewGuid() }, MoviesOnly = new User("phmovies", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = movies, Tv = tv, Far = movies, Folder = folder, Native = native
        };

        var dbPath = Path.Combine(folder, "phones.db");
        var ids = new Dictionary<string, Guid>();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Database.MigrateAsync();
            // The later titles start unmonitored, so the switched-off run sees only the first three; they are monitored before the
            // switched-on run.
            foreach (var (key, tmdb, title, imdb) in new[]
                     {
                         ("uhd", 1301, "Phone Uhd", "tt0001301"), ("both", 1302, "Phone Both", "tt0001302"), ("removed", 1303, "Phone Removed", "tt0001303"),
                         ("reclaimed", 1304, "Phone Retained", "tt0001304"), ("race1", 1305, "Phone Race One", "tt0001305"),
                         ("race2", 1306, "Phone Race Two", "tt0001306"), ("upgrade", 1307, "Phone Upgrade", "tt0001307")
                     })
            {
                var entry = new Entry
                {
                    MediaType = "movie", TmdbId = tmdb, Title = title, Year = 2021, ImdbId = imdb, TargetLibraryId = movies.Id,
                    Monitored = key is "uhd" or "both" or "removed",
                    State = FileState.OnDisk,
                    MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", tmdb, title, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                        null, null, imdb, null, false, null, 100, [], [], []))
                };
                database.Entries.Add(entry);
                ids[key] = entry.Id;
                string FileOf(string label) => Path.Combine(movies.Location, $"{title} (2021)", $"{title} (2021) - {label}.mkv");
                var labels = key switch { "both" => new[] { "2160p WEB-DL", "1080p WEB-DL" }, "upgrade" => ["720p WEB-DL"], _ => ["2160p WEB-DL"] };
                foreach (var label in labels)
                {
                    var item = Guid.NewGuid();
                    database.EntryBindings.Add(new EntryBinding
                    {
                        EntryId = entry.Id, JellyfinItemId = item, VersionGroupId = item, TargetLibraryId = movies.Id, MediaPath = FileOf(label)
                    });
                }

                if (key is "removed" or "reclaimed")
                    database.RetentionOperations.Add(new RetentionOperation
                    {
                        ActionId = Guid.NewGuid(), BindingId = Guid.NewGuid(), EntryId = entry.Id, JellyfinItemId = Guid.NewGuid(), TargetLibraryId = movies.Id,
                        MediaPath = FileOf("1080p WEB-DL"), StorageIdentity = "test", PhysicalIdentity = "1:2", LogicalBytes = Size, HardlinkCountBefore = 1,
                        State = "completed", PreparedAt = DateTime.UtcNow.AddDays(-2), CompletedAt = DateTime.UtcNow.AddDays(-2),
                        Provenance = key == "removed" ? RetentionProvenances.VersionRemoved : RetentionProvenances.Retention
                    });
            }

            // A series whose aired season 1 holds only 4K episodes: a season pack is added for phones.
            var series = new Entry
            {
                MediaType = "series", TmdbId = 1350, Title = "Phone Show", Year = 2022, TargetLibraryId = tv.Id, Monitored = false, State = FileState.OnDisk,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 1350, "Phone Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                    null, null, null, 7350, false, null, 45, [], [], [new TmdbSeason(1, "Season 1", 2, null, null)]))
            };
            database.Entries.Add(series);
            ids["series"] = series.Id;
            var seriesItem = Guid.NewGuid();
            foreach (var number in new[] { 1, 2 })
            {
                var episode = new Episode
                {
                    EntryId = series.Id, TmdbId = 13500 + number, SeasonNumber = 1, EpisodeNumber = number, Title = "E" + number,
                    AirDate = DateTime.UtcNow.AddDays(-60 + number), RuntimeMinutes = 45, Monitored = true, State = FileState.OnDisk
                };
                database.Episodes.Add(episode);
                ids["e" + number] = episode.Id;
                var item = Guid.NewGuid();
                database.EpisodeBindings.Add(new EpisodeBinding
                {
                    EpisodeId = episode.Id, JellyfinItemId = item, SeriesItemId = seriesItem, TargetLibraryId = tv.Id,
                    MediaPath = Path.Combine(tv.Location, "Phone Show (2022)", "Season 01", $"Phone Show (2022) S01E0{number} - 2160p WEB-DL.mkv")
                });
            }

            await database.SaveChangesAsync();
        }

        await using var torznab = new TorznabBoundary();
        await using var transmission = new TransmissionBoundary();
        await torznab.StartAsync();
        await transmission.StartAsync();
        transmission.Roots[downloads] = downloads;
        void Release(string key, string title, string imdb)
        {
            var fixture = TorrentFixture.Single(title + ".mkv", Size);
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.MovieItems.Add(new(title, "guid-" + key, torznab.Download(key), Size, 30, new Dictionary<string, string> { ["imdbid"] = imdb }));
        }

        void Pack(string key, string title)
        {
            var fixture = TorrentFixture.Multi(title, ("Phone.Show.S01E01.1080p.WEB-DL.mkv", Size), ("Phone.Show.S01E02.1080p.WEB-DL.mkv", Size));
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.TvItems)
                torznab.TvItems.Add(new(title, "guid-" + key, torznab.Download(key), Size * 2, 30, new Dictionary<string, string> { ["tvdbid"] = "7350" }));
        }

        Release("uhd2160", "Phone.Uhd.2021.2160p.WEB-DL.DV.DDP5.1.H.265-GRP", "0001301");
        Release("uhd1080hdr", "Phone.Uhd.2021.1080p.WEB-DL.HDR10.DTS.5.1.H.265-GRP", "0001301");
        Release("uhd1080", "Phone.Uhd.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "0001301");
        Release("removed1080", "Phone.Removed.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "0001303");
        Release("reclaimed1080", "Phone.Retained.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "0001304");
        Release("race11080", "Phone.Race.One.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "0001305");
        Release("race21080", "Phone.Race.Two.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "0001306");
        Release("upgrade2160", "Phone.Upgrade.2021.2160p.WEB-DL.DDP5.1.H.265-GRP", "0001307");
        Pack("pack1080", "Phone.Show.S01.1080p.WEB-DL.DDP5.1.H.264-GRP");

        var time = new ShiftedTimeProvider();
        var host = await PluginHost.StartAsync(world, dbPath, time, logs, new PluginConfiguration());
        try
        {
            var admin = host.Client(world.Admin, true);
            async Task<AutomationRun> Run()
            {
                using var scope = host.App.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<AutomationRunner>().RunAsync("manual", CancellationToken.None)
                    ?? throw new InvalidOperationException("A manual run always runs");
            }

            var any = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Any", qualities = new[] { "webdl-2160p", "webdl-1080p", "webdl-720p" }
            }), 201, "Profile Any");
            var phones = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Phones", qualities = new[] { "webdl-1080p", "webdl-720p" }, prefer = "everywhere"
            }), 201, "Profile for phones");
            var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Boundary", baseUrl = new Uri(torznab.Address, "/api").ToString(), enabled = true, categories = new[] { 2000 },
                apiKey = new { action = "replace", value = torznab.ApiKey }, minIntervalSeconds = 0, dailyQueryBudget = 100
            }), 201, "Indexer");
            await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200, "Indexer test");
            var client = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", new
            {
                name = "Transmission (isolated)", kind = "transmission", baseUrl = transmission.Endpoint.ToString(), username = transmission.Username,
                password = new { action = "replace", value = transmission.Password }, enabled = true, label = "jellyfinmod-test",
                downloadDirectory = downloads, localDirectory = downloads
            }), 201, "Client");
            await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{client.GetProperty("id").AsGuid()}/Test", null), 200, "Client test");
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings");
            acquisition = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = true, downloadClientId = client.GetProperty("id").AsGuid(), defaultQualityProfileId = any.GetProperty("id").AsGuid(),
                phoneProfileId = phones.GetProperty("id").AsGuid(), revision = acquisition.GetProperty("revision").GetInt32()
            }), 200, "Acquisition enabled with a quality profile for phones");
            Assert(acquisition.GetProperty("phoneProfileId").AsGuid() == phones.GetProperty("id").AsGuid(), "The profile for phones is stored");

            // ---- Switch off (the default): nothing that holds a file is searched.
            var automation = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Automation"), 200, "Automation settings");
            Assert(automation.TryGetProperty("fetchPhoneCopies", out var fetch) && !fetch.GetBoolean(), "Also get a 1080p copy for phones is off by default");
            automation = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation",
                Settings(automation.GetProperty("revision").GetInt32(), fetchPhoneCopies: null)), 200, "Automation on, copies for phones left as they are");
            Assert(!automation.GetProperty("fetchPhoneCopies").GetBoolean(), "A save that leaves the switch out keeps it off");
            var off = await Run();
            await using (var database = new ModDbContext(dbPath))
            {
                Assert(off.Status == AutomationRunStatuses.Completed && torznab.SearchQueries == 0 && !await database.GrabOperations.AnyAsync() &&
                    !await database.AutomationDecisions.AnyAsync(decision => ids.Values.Contains(decision.EntryId!.Value)),
                    $"With the switch off, no title that holds a file is searched or decided ({off.Status}, {torznab.SearchQueries} queries)");
            }

            // ---- Switch on. Every title is monitored now, an upgrade profile is given to the 720p title, and a 1080p lands on one
            // race title while the other is searched (phone_copy_held).
            var upgradeProfile = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Upgrade to 4K", qualities = new[] { "webdl-2160p", "webdl-1080p", "webdl-720p" }, cutoff = "webdl-2160p", upgradeAllowed = true,
                upgradeMode = "replace"
            }), 201, "A profile that upgrades by replacing");
            await using (var database = new ModDbContext(dbPath))
            {
                foreach (var entry in await database.Entries.ToListAsync()) entry.Monitored = true;
                (await database.Entries.SingleAsync(entry => entry.Id == ids["upgrade"])).QualityProfileId = upgradeProfile.GetProperty("id").AsGuid();
                await database.SaveChangesAsync();
            }

            var raced = new Dictionary<string, string>();
            torznab.OnSearch = async query =>
            {
                foreach (var (searched, other) in new[] { ("race1", "race2"), ("race2", "race1") })
                {
                    if (raced.Count > 0 || !query.Contains(searched == "race1" ? "0001305" : "0001306", StringComparison.Ordinal)) continue;
                    raced[other] = searched;
                    await using var database = new ModDbContext(dbPath);
                    var item = Guid.NewGuid();
                    var title = other == "race1" ? "Phone Race One" : "Phone Race Two";
                    database.EntryBindings.Add(new EntryBinding
                    {
                        EntryId = ids[other], JellyfinItemId = item, VersionGroupId = item, TargetLibraryId = movies.Id,
                        MediaPath = Path.Combine(movies.Location, $"{title} (2021)", $"{title} (2021) - 1080p WEB-DL.mkv")
                    });
                    await database.SaveChangesAsync();
                }
            };

            automation = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation",
                Settings(automation.GetProperty("revision").GetInt32(), fetchPhoneCopies: true)), 200, "Also get a 1080p copy for phones on");
            Assert(automation.GetProperty("fetchPhoneCopies").GetBoolean(), "The switch saves");
            automation = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation",
                Settings(automation.GetProperty("revision").GetInt32(), fetchPhoneCopies: null)), 200, "A save without the switch");
            Assert(automation.GetProperty("fetchPhoneCopies").GetBoolean(), "A save that leaves the switch out keeps it on");
            var on = await Run();
            torznab.OnSearch = null;
            await using (var database = new ModDbContext(dbPath))
            {
                var grabs = await database.GrabOperations.AsNoTracking().ToListAsync();
                var decisions = await database.AutomationDecisions.AsNoTracking().ToListAsync();
                string Grabs() => string.Join("; ", grabs.Select(item => $"{item.EntryId} {item.Scope} {item.Intent} {item.Mode} {item.RawTitle}"));
                string Decisions() => string.Join("; ", decisions.Select(decision => $"{decision.EntryId}/{decision.EpisodeId} {decision.Reason} {decision.Detail}"));
                var uhd = grabs.SingleOrDefault(item => item.EntryId == ids["uhd"]);
                Assert(on.Status == AutomationRunStatuses.Completed && uhd is not null && uhd.Automatic && uhd.Intent == "addVersion" &&
                    uhd.Mode == GrabModes.Add && uhd.ProfileId == phones.GetProperty("id").AsGuid() &&
                    uhd.RawTitle == "Phone.Uhd.2021.1080p.WEB-DL.DDP5.1.H.264-GRP",
                    "The 4K-only movie's 1080p (Plays Everywhere: H.264 and DD+ before HEVC HDR10 DTS) is grabbed as Add with the profile for phones: " + Grabs());
                Assert(decisions.Any(decision => decision.EntryId == ids["uhd"] && decision.Reason == AutomationReasons.Grabbed &&
                        decision.Detail!.StartsWith("Copy for phones: ", StringComparison.Ordinal)) &&
                    !decisions.Any(decision => decision.EntryId == ids["both"]),
                    "The run records the copy for phones and leaves the title that holds a 1080p alone: " + Decisions());
                foreach (var key in new[] { "removed", "reclaimed" })
                    Assert(decisions.Any(decision => decision.EntryId == ids[key] && decision.Reason == AutomationReasons.PhoneCopyRemoved) &&
                        grabs.All(item => item.EntryId != ids[key]),
                        $"A 1080p removed by {(key == "removed" ? "Remove This Version" : "retention")} is never fetched again (phone_copy_removed): " + Decisions());
                Assert(torznab.Queries.All(query => !query.Contains("0001302", StringComparison.Ordinal) && !query.Contains("0001303", StringComparison.Ordinal) &&
                        !query.Contains("0001304", StringComparison.Ordinal)),
                    "Neither the title with a 1080p nor the ones whose 1080p was removed are searched: " + string.Join(" | ", torznab.Queries));

                // The race: the title that gained a 1080p while the other was searched is not searched and records phone_copy_held.
                Assert(raced.Count == 1, "One race title gained a 1080p during the other's search");
                var (late, searched) = raced.Single();
                Assert(decisions.Any(decision => decision.EntryId == ids[late] && decision.Reason == AutomationReasons.PhoneCopyHeld) &&
                    grabs.All(item => item.EntryId != ids[late]) && grabs.Any(item => item.EntryId == ids[searched]),
                    $"A title that gained a 1080p before its turn records phone_copy_held and is not grabbed ({late}): " + Decisions());

                // A season of 4K-only episodes gets one 1080p pack, added beside them, with the profile for phones.
                var pack = grabs.SingleOrDefault(item => item.EntryId == ids["series"]);
                if (pack is null)
                {
                    var manual = await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["series"]}&scope=season&seasonNumber=1");
                    throw new InvalidOperationException("No pack was grabbed; the season search answers: " + manual[..Math.Min(400, manual.Length)] + " LOG: " +
                        string.Join(" | ", logs.Lines.Where(line => line.Contains("pack", StringComparison.OrdinalIgnoreCase))));
                }
                Assert(pack is not null && pack.Scope == "season" && pack.SeasonNumber == 1 && pack.Mode == GrabModes.Add &&
                    pack.ProfileId == phones.GetProperty("id").AsGuid() && pack.RawTitle == "Phone.Show.S01.1080p.WEB-DL.DDP5.1.H.264-GRP" &&
                    decisions.Any(decision => decision.EntryId == ids["series"] && decision.Detail == "Season 1 pack for phones: " + pack.RawTitle),
                    "A season of 4K-only episodes gets its 1080p as one pack, added: " + Grabs() + " / " + Decisions());
                Assert(await database.GrabClaims.CountAsync(claim => claim.GrabId == pack!.Id) == 2,
                    "The pack claims both episodes for another version");

                // While copies for phones are fetched, an upgrade from 720p to 4K adds instead of replacing, so the 720p stays for phones.
                var upgrade = await database.UpgradeOperations.AsNoTracking().SingleOrDefaultAsync(row => row.EntryId == ids["upgrade"]);
                Assert(upgrade is not null && upgrade.Mode == "add" && upgrade.SupersededQuality == "webdl-720p" && upgrade.NewQuality == "webdl-2160p",
                    $"An upgrade of the phones' 720p to 4K adds while the switch is on, though its profile replaces ({upgrade?.Mode}): " + Grabs());
            }

            // ---- A second run makes no second grab: the copies for phones are on their way.
            var grabCount = 0;
            await using (var database = new ModDbContext(dbPath))
                grabCount = await database.GrabOperations.CountAsync();
            await ForceDueAsync(dbPath, time, ids["uhd"]);
            await Run();
            await using (var database = new ModDbContext(dbPath))
                Assert(await database.GrabOperations.CountAsync() == grabCount, "A second run does not grab a copy for phones again");
        }
        finally
        {
            await host.DisposeAsync();
            BaseItem.LibraryManager = previousLibrary;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        Console.WriteLine("PASS: phase 12 automation: also get a 1080p copy for phones, off by default, Add with the profile for phones, removed copies never again");
    }

    private static object Settings(int revision, bool? fetchPhoneCopies) => new
    {
        automationEnabled = true, automationIntervalHours = 1, automationBatchSize = 40, newEpisodeDelayMinutes = 120, dailyAutoGrabBudget = 6,
        maxConcurrentImports = 20, freeSpaceFloorPercent = 0, freeSpaceFloorBytes = 0L, decisionLogCap = 2000, episodeUpgradesEnabled = true,
        reacquireReclaimed = false, fetchPhoneCopies, revision
    };

    private static async Task ForceDueAsync(string dbPath, ShiftedTimeProvider time, Guid targetId)
    {
        await using var database = new ModDbContext(dbPath);
        foreach (var row in await database.AutomationTargets.Where(value => value.TargetId == targetId).ToListAsync())
            row.NextSearchAt = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await database.SaveChangesAsync();
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
