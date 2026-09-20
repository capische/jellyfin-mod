using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Automation;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

// Phase 6 automation integration: the Phase 5 host (real Kestrel, auth, MVC, EF migrations, SQLite) with two real HTTP
// Torznab boundaries that count every query, a Transmission RPC boundary that downloads real files, real hardlinks, and
// the production scheduler, upgrade and retention services.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-six-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var logs = new CapturingLoggerProvider();
try
{
    try
    {
        await Phase6.RunAsync(folder, logs);
    }
    catch
    {
        foreach (var line in logs.Lines.Where(line => line.StartsWith("Warning", StringComparison.Ordinal) ||
                     line.StartsWith("Error", StringComparison.Ordinal)).TakeLast(40))
            Console.Error.WriteLine(line.Length > 800 ? line[..800] : line);
        throw;
    }

    Console.WriteLine($"PASS: Phase 6 scheduler, budgets, breaker, new episodes, upgrades, added versions, per-version retention and versions API ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

internal static class Phase6
{
    private const long Size = 200_000;

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "media");
        foreach (var directory in new[] { "movies", "tv", "downloads/jfmod" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var downloads = Path.Combine(media, "downloads", "jfmod");
        var movies = new TestLibrary { Id = Guid.NewGuid(), Name = "Movies", CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") };
        var tv = new TestLibrary { Id = Guid.NewGuid(), Name = "TV", CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") };
        var admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
        var ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() };
        var native = new NativeWorld { Libraries = [movies, tv], UserLibraries = _ => [movies, tv] };
        BaseItem.LibraryManager = native.Library;
        var world = new World
        {
            Admin = admin, RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() }, Ordinary = ordinary,
            MoviesOnly = new User("moviefan", "auth", "reset") { Id = Guid.NewGuid() }, Movies = movies, Tv = tv, Far = movies,
            Folder = folder, Native = native
        };

        // ---- M2 migration: a Phase 5 database gains automation with the master switch off and documented defaults.
        var dbPath = Path.Combine(folder, "jellyfinmod.db");
        var ids = new Dictionary<string, Guid>();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.GetService<IMigrator>().MigrateAsync("20260919090423_PhaseFiveImport");
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AcquisitionSettings (Id, Enabled, Revision, ImportEnabled, SeedReleaseEnabled, SeedFloorRatio, SeedFloorHours,
                    ImportPollSeconds, VideoExtensions, StalledAfterHours, ScanTimeoutMinutes, QueueVisibleToUsers, ImportRevision)
                VALUES ({AcquisitionSettings.SingletonId}, 0, 1, 1, 0, 1.0, 168, 15, 'mkv', 24, 10, 0, 1)
                """);
            await database.Database.MigrateAsync();
            var upgraded = await database.AcquisitionSettings.AsNoTracking().SingleAsync();
            Assert(!upgraded.AutomationEnabled && upgraded.AutomationIntervalHours == 6 && upgraded.AutomationBatchSize == 40 &&
                upgraded.NewEpisodeDelayMinutes == 120 && upgraded.DailyAutoGrabBudget == 6 && upgraded.MaxConcurrentImports == 3 &&
                upgraded.FreeSpaceFloorPercent == 10 && upgraded.FreeSpaceFloorBytes == 25_000_000_000 && upgraded.DecisionLogCap == 2000 &&
                !upgraded.EpisodeUpgradesEnabled && !upgraded.ReacquireReclaimed,
                "The migration leaves automation off and applies the documented defaults to an existing settings row");
            Assert(await Scalar(database, "PRAGMA integrity_check") == "ok" && await Scalar(database, "PRAGMA foreign_key_check") is null,
                "The upgraded database passes integrity and foreign-key checks");
            // A run left "running" by a killed process is marked interrupted by the next run (the T9 pattern).
            database.AutomationRuns.Add(new AutomationRun { StartedAt = DateTime.UtcNow.AddHours(-3), Status = AutomationRunStatuses.Running });
            foreach (var (key, number) in new[] { ("m1", 1), ("m2", 2), ("m3", 3), ("m4", 4), ("m5", 5) })
                AddMovie(database, ids, movies.Id, key, 400 + number, "Auto Movie " + number, 2020, $"tt{900400 + number}", monitored: true);
            AddMovie(database, ids, movies.Id, "unmonitored", 450, "Quiet Movie", 2020, "tt0900450", monitored: false);
            AddMovie(database, ids, movies.Id, "up", 460, "Up Movie", 2020, "tt0900460", monitored: true);
            AddMovie(database, ids, movies.Id, "kept", 461, "Kept Movie", 2020, "tt0900461", monitored: true);
            AddMovie(database, ids, movies.Id, "order", 462, "Order Movie", 2020, "tt0900462", monitored: false);
            // Two titles whose only releases live on a tracker that advertises nothing but a text search.
            AddMovie(database, ids, movies.Id, "textauto", 463, "Text Only Movie", 2021, "tt0900463", monitored: true);
            AddMovie(database, ids, movies.Id, "textmanual", 464, "Text Manual Movie", 2019, "tt0900464", monitored: true);
            var series = new Entry
            {
                MediaType = "series", TmdbId = 500, Title = "Auto Show", Year = 2024, TargetLibraryId = tv.Id, Monitored = true,
                MetadataJson = Metadata("series", 500, "Auto Show", 2024, null, 700)
            };
            database.Entries.Add(series);
            ids["series"] = series.Id;
            var now = DateTime.UtcNow;
            foreach (var (key, season, number, airDate) in new[]
                     {
                         ("s1e1", 1, 1, now.AddDays(-30)), ("s1e2", 1, 2, now.AddDays(30)), ("s0e1", 0, 1, now.AddDays(-10))
                     })
            {
                var episode = new Episode
                {
                    EntryId = series.Id, TmdbId = 5000 + season * 10 + number, SeasonNumber = season, EpisodeNumber = number,
                    Title = key, AirDate = airDate, RuntimeMinutes = 45, Monitored = true
                };
                database.Episodes.Add(episode);
                ids[key] = episode.Id;
            }

            await database.SaveChangesAsync();
        }

        await using var torznab = new TorznabBoundary();
        await using var flaky = new TorznabBoundary();
        // A public tracker as most of them are: a text search and no id search at all (user decision 2026-09-20).
        await using var publicTracker = new TorznabBoundary { Caps = TextOnlyCaps };
        await using var transmission = new TransmissionBoundary();
        await torznab.StartAsync();
        await flaky.StartAsync();
        await publicTracker.StartAsync();
        await transmission.StartAsync();
        // No path mapping here: Transmission reports the same folder Jellyfin sees, which the Phase 3 seed reader needs.
        transmission.Roots[downloads] = downloads;

        var fixtures = new Dictionary<string, TorrentFixture>();
        void Release(TorznabBoundary indexer, string key, string title, string? imdb = null, string? tvdb = null)
        {
            var fixture = TorrentFixture.Single(title + ".mkv", Size);
            fixtures[key] = fixture;
            indexer.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            var attributes = new Dictionary<string, string>();
            if (imdb is not null) attributes["imdbid"] = imdb;
            if (tvdb is not null) attributes["tvdbid"] = tvdb;
            lock (indexer.MovieItems)
                (tvdb is null ? indexer.MovieItems : indexer.TvItems).Add(new(title, "guid-" + key, indexer.Download(key), Size, 30, attributes));
        }

        Release(torznab, "m1", "Auto.Movie.1.2020.1080p.WEB-DL-GRP", "0900401");
        Release(torznab, "m2", "Auto.Movie.2.2020.1080p.WEB-DL-GRP", "0900402");
        Release(torznab, "unmonitored", "Quiet.Movie.2020.1080p.WEB-DL-GRP", "0900450");
        Release(torznab, "s1e1", "Auto.Show.S01E01.1080p.WEB-DL-GRP", tvdb: "700");
        Release(torznab, "s1e2", "Auto.Show.S01E02.1080p.WEB-DL-GRP", tvdb: "700");

        var time = new ShiftedTimeProvider();
        var configuration = new PluginConfiguration
        {
            RetentionEnabled = true, ReclaimAfterDays = 1, RetentionWatchedUserMode = WatchedUserMode.AnyUser, ExemptFavourites = true,
            TransmissionRpcUrl = transmission.Endpoint.ToString(), TransmissionUsername = transmission.Username,
            TransmissionPassword = transmission.Password
        };
        IServiceProvider? services = null;
        Func<IServiceProvider, ITaskManager> taskManager = provider => Stub<ITaskManager>.Create((method, arguments) =>
        {
            if (method.Name == "QueueScheduledTask")
                Task.Run(async () =>
                {
                    try
                    {
                        // The real task entry point, as Jellyfin's task manager invokes it.
                        await new AutomationSearchTask(services!.GetRequiredService<IServiceScopeFactory>())
                            .ExecuteAsync(new Progress<double>(), CancellationToken.None);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine("Queued automation run failed: " + error);
                    }
                });
            return null;
        });
        var host = await PluginHost.StartAsync(world, dbPath, time, logs, configuration, taskManager);
        services = host.App.Services;
        try
        {
            host = await ScenariosAsync(host, world, dbPath, time, logs, configuration, torznab, flaky, publicTracker, transmission, fixtures, ids,
                taskManager, Release, downloads, value => services = value);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>What a public tracker advertises: a text search, and no imdbid, tmdbid or tvdbid search.</summary>
    private const string TextOnlyCaps = """
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <server title="JellyfinMod text-only boundary"/>
          <limits max="100" default="100"/>
          <searching>
            <search available="yes" supportedParams="q"/>
            <tv-search available="yes" supportedParams="q"/>
            <movie-search available="yes" supportedParams="q"/>
          </searching>
          <categories>
            <category id="2000" name="Movies"/>
            <category id="5000" name="TV"/>
          </categories>
        </caps>
        """;

    private static async Task<PluginHost> ScenariosAsync(PluginHost host, World world, string dbPath, ShiftedTimeProvider time,
        CapturingLoggerProvider logs, PluginConfiguration configuration, TorznabBoundary torznab, TorznabBoundary flaky,
        TorznabBoundary publicTracker, TransmissionBoundary transmission, Dictionary<string, TorrentFixture> fixtures,
        Dictionary<string, Guid> ids, Func<IServiceProvider, ITaskManager> taskManager,
        Action<TorznabBoundary, string, string, string?, string?> release, string downloads, Action<IServiceProvider> setServices)
    {
        var admin = host.Client(world.Admin, true);
        var ordinary = host.Client(world.Ordinary, false);
        var anonymous = host.Client(null, false);
        async Task Tick() => await host.Service<ImportMonitor>().TickOnceAsync(CancellationToken.None);
        async Task<AutomationRun> Run()
        {
            using var scope = host.App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AutomationRunner>().RunAsync("manual", CancellationToken.None)
                ?? throw new InvalidOperationException("A manual run always runs");
        }

        var health = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Health"));
        Assert(Strings(health.GetProperty("Capabilities")).Intersect(["automation", "versions"]).Count() == 2,
            "Health advertises automation and versions");

        // ---- M2 settings and authorization.
        Assert((await anonymous.GetAsync("/JellyfinMod/Automation/Status")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous status is refused");
        foreach (var path in new[] { "Automation/Status", "Automation/Decisions", "Settings/Automation", $"Automation/Targets?entryId={ids["m1"]}" })
            Assert((await ordinary.GetAsync("/JellyfinMod/" + path)).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot read " + path);
        Assert((await ordinary.PostAsync("/JellyfinMod/Automation/Run", null)).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot start a run");
        var automation = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"));
        Assert(!automation.GetProperty("automationEnabled").GetBoolean() && automation.GetProperty("revision").GetInt32() == 1,
            "Automation settings read back off");
        await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(9, enabled: true)), 409,
            "revision_conflict", "A stale automation revision is refused");
        Assert((await ordinary.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(1, enabled: true))).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot change automation");

        // Acquisition configuration, with a cutoff that must be one of the allowed qualities.
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Bad cutoff", qualities = new[] { "webdl-1080p" }, cutoff = "bluray-2160p", upgradeAllowed = true
        }), 400, "invalid_cutoff", "A cutoff outside the allowed qualities is refused with the rule named");
        var any = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Any", qualities = new[] { "webdl-2160p", "webdl-1080p", "webdl-720p" }
        }), 201, "Profile Any");
        var upgrade = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Upgrade to 1080p", qualities = new[] { "webdl-1080p", "webdl-720p" }, cutoff = "webdl-1080p", upgradeAllowed = true,
            upgradeMode = "replace"
        }), 201, "Profile with a cutoff");
        Assert(upgrade.GetProperty("cutoff").GetString() == "webdl-1080p" && upgrade.GetProperty("upgradeAllowed").GetBoolean() &&
            upgrade.GetProperty("upgradeMode").GetString() == "replace", "Cutoff, upgrade switch and mode are stored");
        var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Boundary", baseUrl = new Uri(torznab.Address, "/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
            apiKey = new { action = "replace", value = torznab.ApiKey }, minIntervalSeconds = 0, dailyQueryBudget = 100
        }), 201, "Indexer");
        var indexerId = indexer.GetProperty("id").AsGuid();
        Assert(indexer.GetProperty("minIntervalSeconds").GetInt32() == 0 && indexer.GetProperty("dailyQueryBudget").GetInt32() == 100,
            "Per-indexer limits are stored");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexerId}/Test", null), 200, "Indexer test");
        var client = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", new
        {
            name = "Transmission (isolated)", kind = "transmission", baseUrl = transmission.Endpoint.ToString(), username = transmission.Username,
            password = new { action = "replace", value = transmission.Password }, enabled = true, label = "jellyfinmod-test",
            downloadDirectory = downloads, localDirectory = downloads
        }), 201, "Client");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{client.GetProperty("id").AsGuid()}/Test", null), 200, "Client test");
        var acquisition = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Acquisition"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
        {
            enabled = true, downloadClientId = client.GetProperty("id").AsGuid(), defaultQualityProfileId = any.GetProperty("id").AsGuid(),
            revision = acquisition.GetProperty("revision").GetInt32()
        }), 200, "Acquisition enabled");
        var importSettings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Import"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", new
        {
            importEnabled = true, seedReleaseEnabled = false, seedFloorRatio = 1.0, seedFloorHours = (int?)null, importPollSeconds = 1,
            videoExtensions = new[] { "mkv" }, stalledAfterHours = 24, scanTimeoutMinutes = 10, queueVisibleToUsers = false,
            revision = importSettings.GetProperty("revision").GetInt32()
        }), 200, "Import settings");

        // ---- M3: master switch off → a disabled run touches no indexer.
        var queriesBefore = torznab.SearchQueries;
        await ReadAsync(await admin.PostAsync("/JellyfinMod/Automation/Run", null), 202, "Manual run is queued");
        var disabled = await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.AutomationRuns.AsNoTracking().Where(run => run.Status == AutomationRunStatuses.Disabled)
                .FirstOrDefaultAsync();
        }, "A disabled run is recorded");
        Assert(disabled.Trigger == "manual" && torznab.SearchQueries == queriesBefore, "With the master switch off the native task touches no indexer");
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.AutomationRuns.AnyAsync(run => run.Status == AutomationRunStatuses.Interrupted),
                "A run left running by a stopped process is marked interrupted");
        var queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(!queue.GetProperty("automation").GetProperty("enabled").GetBoolean() &&
            Strings(queue.GetProperty("automation").GetProperty("pausedReasons")).Contains("disabled"),
            "The queue carries an automation-paused banner while the master switch is off");

        // ---- M3: batches, budgets and backoff, counted against the boundary's own query log.
        var saved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(1, enabled: true,
            batch: 3, grabs: 1)), 200, "Administrator turns automation on with a batch of 3 and one grab a day");
        var automationRevision = saved.GetProperty("revision").GetInt32();
        // The five monitored movies are due in a known order.
        await using (var database = new ModDbContext(dbPath))
        {
            var start = time.GetUtcNow().UtcDateTime;
            foreach (var (key, offset) in new[] { ("m1", 5), ("m2", 4), ("m3", 3), ("m4", 2), ("m5", 1) })
                database.AutomationTargets.Add(new AutomationTargetState
                {
                    TargetId = ids[key], EntryId = ids[key], NextSearchAt = start.AddMinutes(-offset), ProfileRevisionSeen = 1
                });
            // Episodes and the other movies are pushed out so these runs are about the five.
            foreach (var key in new[] { "s1e1", "up", "kept", "textauto", "textmanual" })
                database.AutomationTargets.Add(new AutomationTargetState
                {
                    TargetId = ids[key], EntryId = key == "s1e1" ? ids["series"] : ids[key], EpisodeId = key == "s1e1" ? ids[key] : null,
                    NextSearchAt = start.AddDays(30), ProfileRevisionSeen = 1,
                    AirDateSeen = key == "s1e1" ? (await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == ids["s1e1"])).AirDate : null
                });
            await database.SaveChangesAsync();
        }

        queriesBefore = torznab.SearchQueries;
        var runA = await Run();
        // Budgets are checked before searching: once the day's grab is made, the remaining targets are not searched.
        Assert(runA.Status == AutomationRunStatuses.Completed && runA.Searched == 1 && runA.Grabbed == 1 && runA.Skipped == 2,
            $"Run A: the batch of 3 grabs once, then the grab budget skips the next targets unsearched ({Describe(runA)}; " +
            $"{await DecisionsAsync(dbPath, runA.Id, ids)})");
        Assert(torznab.SearchQueries - queriesBefore == 1 && QueriesOf(runA) == 1,
            "The boundary counted exactly the queries the run summary reports");
        Assert(!torznab.Queries.Any(query => query.Contains("0900450", StringComparison.Ordinal)), "An unmonitored title is never searched");
        await using (var database = new ModDbContext(dbPath))
        {
            var decisions = await database.AutomationDecisions.AsNoTracking().Where(decision => decision.RunId == runA.Id).ToListAsync();
            Assert(decisions.Count == 3 && decisions.Any(decision => decision.TargetId == ids["m1"] && decision.Kind == "grabbed") &&
                decisions.Count(decision => decision.Reason == "budget_grabs") == 2,
                "One decision per target with its stable reason");
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["m1"]);
            Assert(grab.Automatic && grab.RequestedBy == JellyfinMod.Services.Acquisition.GrabService.AutomationUserId,
                "The grab went through the Phase 4 grab path as an automatic grab");
        }

        await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["m1"]}"))
            .GetProperty("history").EnumerateArray().Any(item => item.GetProperty("eventType").GetString() == "auto_grabbed") ? true : null,
            "The accepted automatic grab writes auto_grabbed");
        // A larger grab budget lets the next runs search again.
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(automationRevision++, enabled: true,
            batch: 3, grabs: 20)), 200, "Administrator raises the grab budget");
        queriesBefore = torznab.SearchQueries;
        var runB = await Run();
        Assert(runB.Searched == 2 && torznab.SearchQueries - queriesBefore == 2 && QueriesOf(runB) == 2,
            $"Run B searches the two targets not reached yet ({Describe(runB)}; {await DecisionsAsync(dbPath, runB.Id, ids)})");
        await using (var database = new ModDbContext(dbPath))
        {
            var m4 = await database.AutomationTargets.AsNoTracking().SingleAsync(value => value.TargetId == ids["m4"]);
            Assert(m4.ConsecutiveEmpty == 1 && Math.Abs((m4.NextSearchAt - time.GetUtcNow().UtcDateTime).TotalHours - 12) < 0.1,
                "An empty search backs off 12 hours");
        }

        time.Offset += TimeSpan.FromHours(12.1);
        await ForceDueAsync(dbPath, time, ids["m4"]);
        var runC = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var m4 = await database.AutomationTargets.AsNoTracking().SingleAsync(value => value.TargetId == ids["m4"]);
            Assert(m4.ConsecutiveEmpty == 2 && Math.Abs((m4.NextSearchAt - time.GetUtcNow().UtcDateTime).TotalHours - 24) < 0.1,
                $"The second empty search backs off 24 hours ({Describe(runC)})");
        }

        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["m4"]}", new { searchNow = true }), 200, "searchNow");
        await using (var database = new ModDbContext(dbPath))
        {
            var m4 = await database.AutomationTargets.AsNoTracking().SingleAsync(value => value.TargetId == ids["m4"]);
            Assert(m4.ConsecutiveEmpty == 0 && m4.SearchNowRequestedAt is not null, "searchNow resets the backoff");
        }

        var runD = await Run();
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.AutomationDecisions.AnyAsync(decision => decision.RunId == runD.Id && decision.TargetId == ids["m4"] &&
                decision.Reason == "no_eligible_candidate"), "The requested target is searched on the next run despite its backoff");

        // Daily indexer budget: one more query allowed, then budget_indexer.
        await using (var database = new ModDbContext(dbPath))
        {
            var used = (await database.IndexerBudgets.AsNoTracking().SingleAsync(value => value.IndexerId == indexerId)).QueriesUsed;
            var row = await database.AcquisitionIndexers.SingleAsync(value => value.Id == indexerId);
            row.DailyQueryBudget = used + 1;
            await database.SaveChangesAsync();
        }

        await ForceDueAsync(dbPath, time, ids["m3"], ids["m4"], ids["m5"]);

        queriesBefore = torznab.SearchQueries;
        var runE = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var decisions = await database.AutomationDecisions.AsNoTracking().Where(decision => decision.RunId == runE.Id).ToListAsync();
            Assert(torznab.SearchQueries - queriesBefore == 1 && decisions.Count(decision => decision.Reason == "budget_indexer") == 2,
                $"A daily query budget stops the run's searches with budget_indexer decisions ({Describe(runE)})");
            (await database.AcquisitionIndexers.SingleAsync(value => value.Id == indexerId)).DailyQueryBudget = 1000;
            await database.SaveChangesAsync();
        }

        // ---- M3 breaker: five failures in a row open it; the healthy indexer keeps answering.
        var flakyIndexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Flaky", baseUrl = new Uri(flaky.Address, "/api").ToString(), enabled = true, categories = new[] { 2000 },
            apiKey = new { action = "replace", value = flaky.ApiKey }, minIntervalSeconds = 0, dailyQueryBudget = 1000
        }), 201, "Second indexer");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{flakyIndexer.GetProperty("id").AsGuid()}/Test", null), 200, "Test");
        flaky.FailSearchesWith = 500;
        for (var attempt = 0; attempt < 5; attempt++)
            await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["m4"]}"), 200, "Manual search counts towards the breaker");
        var status = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Automation/Status"));
        var flakyStatus = status.GetProperty("indexers").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Flaky");
        Assert(flakyStatus.GetProperty("breakerOpenUntil").ValueKind == JsonValueKind.String, "The status shows the open breaker");
        var flakyQueries = flaky.SearchQueries;
        var healthyQueries = torznab.SearchQueries;
        var afterBreaker = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["m4"]}"));
        Assert(flaky.SearchQueries == flakyQueries && torznab.SearchQueries == healthyQueries + 1 &&
            afterBreaker.GetProperty("indexers").EnumerateArray().Any(item => item.GetProperty("status").GetString() == "breaker_open"),
            "A broken indexer is skipped while the other keeps being queried");
        await ReadAsync(await admin.DeleteAsync($"/JellyfinMod/Settings/Indexers/{flakyIndexer.GetProperty("id").AsGuid()}"), 204, "Remove flaky");

        // ---- M3 free-space floor: nothing is searched or grabbed below it.
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(automationRevision++,
            enabled: true, batch: 3, grabs: 20, floorBytes: long.MaxValue / 2)), 200, "Floor above the free space");
        await ForceDueAsync(dbPath, time, ids["m5"]);
        queriesBefore = torznab.SearchQueries;
        var runF = await Run();
        await using (var database = new ModDbContext(dbPath))
            Assert(torznab.SearchQueries == queriesBefore && await database.AutomationDecisions.AnyAsync(decision => decision.RunId == runF.Id &&
                decision.Reason == "free_space_floor") && runF.Grabbed == 0, "Below the free-space floor nothing is searched or grabbed");
        status = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Automation/Status"));
        Assert(Strings(status.GetProperty("pausedReasons")).Contains("free_space_floor") &&
            status.GetProperty("budgets").GetProperty("freeBytes").GetInt64() > 0, "The status reads the mount's free space and names the floor");

        // ---- M3 client outage pauses the run.
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(automationRevision++,
            enabled: true, batch: 3, grabs: 20)), 200, "Floor restored");
        transmission.Offline = true;
        var runG = await Run();
        transmission.Offline = false;
        Assert(runG.Status == AutomationRunStatuses.Paused && runG.Detail == "client_unreachable" && runG.Searched == 0,
            "An unreachable client pauses automatic grabs for the run");

        // ---- M4: a new episode is searched only after its delay; specials never.
        await using (var database = new ModDbContext(dbPath))
        {
            // The episode airs now, in shifted time: half an hour ago, inside the two-hour delay.
            (await database.Episodes.SingleAsync(value => value.Id == ids["s1e2"])).AirDate = time.GetUtcNow().UtcDateTime.AddMinutes(-30);
            foreach (var row in await database.AutomationTargets.Where(value => value.EpisodeId == null).ToListAsync())
                row.NextSearchAt = time.GetUtcNow().UtcDateTime.AddDays(30);
            var episodeRow = await database.AutomationTargets.SingleAsync(value => value.TargetId == ids["s1e1"]);
            episodeRow.NextSearchAt = time.GetUtcNow().UtcDateTime.AddDays(30);
            await database.SaveChangesAsync();
        }

        var runH = await Run();
        await using (var database = new ModDbContext(dbPath))
            Assert(!await database.AutomationDecisions.AnyAsync(decision => decision.RunId == runH.Id &&
                    (decision.TargetId == ids["s1e2"] || decision.TargetId == ids["s0e1"])),
                "An episode inside its delay and a special are not searched");
        time.Offset += TimeSpan.FromHours(2);
        var runI = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking()
                .SingleAsync(value => value.RunId == runI.Id && value.TargetId == ids["s1e2"]);
            Assert(decision.Kind == "grabbed", $"After the delay the new episode is grabbed once ({decision.Reason})");
            Assert(!torznab.Queries.Any(query => query.Contains("season=0", StringComparison.Ordinal)), "Specials are never searched");
        }

        var runJ = await Run();
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.GrabOperations.CountAsync(value => value.EpisodeId == ids["s1e2"]) == 1 &&
                !await database.AutomationDecisions.AnyAsync(decision => decision.RunId == runJ.Id && decision.TargetId == ids["s1e2"] &&
                    decision.Kind == "grabbed"), "The episode is not grabbed a second time");

        // ---- M5 upgrade with replacement provenance.
        var upFolder = Path.Combine(world.Movies.Location, "Up Movie (2020) [tmdbid-460]");
        Directory.CreateDirectory(upFolder);
        var upOld = Path.Combine(upFolder, "Up Movie (2020) [tmdbid-460] - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(upOld, new byte[4096]);
        world.Native.AddMovie(world.Movies, upOld, 460);
        var keptFolder = Path.Combine(world.Movies.Location, "Kept Movie (2020) [tmdbid-461]");
        Directory.CreateDirectory(keptFolder);
        var keptOld = Path.Combine(keptFolder, "Kept Movie (2020) [tmdbid-461] - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(keptOld, new byte[4096]);
        world.Native.AddMovie(world.Movies, keptOld, 461);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.CountAsync(value => value.EntryId == ids["up"] || value.EntryId == ids["kept"]) == 2 ? true : null;
        }, "Both 720p files are bound");
        foreach (var key in new[] { "up", "kept" })
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids[key]}", new { qualityProfileId = upgrade.GetProperty("id").AsGuid() }),
                200, "Assign the upgrade profile");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Entries/{ids["kept"]}/Keep", null), 200, "Keep the second title");
        release(torznab, "up1080", "Up.Movie.2020.1080p.WEB-DL-GRP", "0900460", null);
        release(torznab, "kept1080", "Kept.Movie.2020.1080p.WEB-DL-GRP", "0900461", null);
        var targets = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Automation/Targets?entryId={ids["up"]}"));
        Assert(targets.EnumerateArray().Single().GetProperty("upgradeEligible").GetBoolean() &&
            targets.EnumerateArray().Single().GetProperty("heldBestQuality").GetString() == "webdl-720p",
            "A held 720p below a 1080p cutoff is upgrade-eligible");
        await ForceDueAsync(dbPath, time, ids["up"], ids["kept"]);
        var runK = await Run();
        Assert(runK.UpgradesPlanned == 2, $"Both below-cutoff titles get an upgrade grab ({Describe(runK)})");
        foreach (var key in new[] { "up1080", "kept1080" }) await WaitForTorrentAsync(transmission, fixtures[key].InfoHash);
        transmission.Progress(fixtures["up1080"].InfoHash, 1.0);
        transmission.Progress(fixtures["kept1080"].InfoHash, 1.0);
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return await database.UpgradeOperations.CountAsync(value => value.State == UpgradeStates.Completed) == 2 ? true : null;
        }, "Both upgrades finish", 60);
        await using (var database = new ModDbContext(dbPath))
        {
            var up = await database.UpgradeOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["up"]);
            var replaced = await database.RetentionOperations.AsNoTracking().SingleAsync(value => value.Id == up.ReplacementRetentionOperationId);
            Assert(up.Reason == "replaced" && replaced.Provenance == RetentionProvenances.UpgradeReplaced && replaced.UpgradeOperationId == up.Id &&
                replaced.State == "completed" && !File.Exists(upOld) &&
                await database.EntryBindings.CountAsync(value => value.EntryId == ids["up"]) == 1,
                "The 720p file is removed by a retention operation with upgrade_replaced provenance once the 1080p is bound");
            var history = await database.History.AsNoTracking().Where(value => value.EntryId == ids["up"]).OrderBy(value => value.CreatedAt)
                .Select(value => value.EventType).ToListAsync();
            Assert(history.IndexOf("upgrade_added") >= 0 && history.IndexOf("upgrade_replaced") > history.IndexOf("upgrade_added") &&
                !history.Contains("media_missing"), "History shows upgrade_added then upgrade_replaced and no media_missing");
            Assert((await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["up"])).State == FileState.OnDisk,
                "The upgraded title stays on disk");
            var kept = await database.UpgradeOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["kept"]);
            Assert(kept.Reason == AutomationReasons.KeptEntry && File.Exists(keptOld) &&
                await database.EntryBindings.CountAsync(value => value.EntryId == ids["kept"]) == 2 &&
                await database.AutomationDecisions.AnyAsync(value => value.EntryId == ids["kept"] && value.Reason == AutomationReasons.KeptEntry),
                "Keep lets the title gain the 1080p version but never removes its 720p");
        }

        // ---- M6 get another quality on purpose; M8 versions API.
        await ExpectAsync(ordinary.GetAsync($"/JellyfinMod/Releases?entryId={ids["up"]}&intent=addVersion"), 403, "", "Ordinary users cannot add a version");
        await ExpectAsync(admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["m5"]}&intent=addVersion"), 409, "no_playable_version",
            "A file-less title cannot take another version");
        release(torznab, "up2160", "Up.Movie.2020.2160p.WEB-DL-GRP", "0900460", null);
        var addSearch = Json.Parse(await admin.GetStringAsync(
            $"/JellyfinMod/Releases?entryId={ids["up"]}&intent=addVersion&profileId={any.GetProperty("id").AsGuid()}"));
        var held = addSearch.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == "Up.Movie.2020.1080p.WEB-DL-GRP");
        var uhd = addSearch.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == "Up.Movie.2020.2160p.WEB-DL-GRP");
        Assert(addSearch.GetProperty("intent").GetString() == "addVersion" && held.GetProperty("heldQuality").GetBoolean() &&
            !uhd.GetProperty("heldQuality").GetBoolean(), "The picker marks the held quality");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = addSearch.GetProperty("searchId").AsGuid(), releaseId = held.GetProperty("releaseId").GetString(), idempotencyKey = "p6-held-1080"
        }), 409, "held_quality", "A second copy of a held quality is refused");
        var addGrab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = addSearch.GetProperty("searchId").AsGuid(), releaseId = uhd.GetProperty("releaseId").GetString(), idempotencyKey = "p6-add-2160"
        }), 202, "Administrator grabs another quality");
        await WaitForTorrentAsync(transmission, fixtures["up2160"].InfoHash);
        transmission.Progress(fixtures["up2160"].InfoHash, 1.0);
        var addImport = await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.GrabId == addGrab.GetProperty("id").AsGuid() &&
                value.State == ImportStates.Completed);
        }, "The added version is imported");
        Assert(addImport.Intent == "addVersion" && addImport.DestinationPath!.EndsWith(" - 2160p WEB-DL.mkv", StringComparison.Ordinal),
            "The added version lands beside the first with its own label");
        var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}"));
        var versions = detail.GetProperty("versions").EnumerateArray().ToArray();
        Assert(versions.Length == 2 && versions.Select(item => item.GetProperty("resolution").GetString()).Order().SequenceEqual(["1080p", "2160p"]) &&
            versions.Count(item => item.GetProperty("isDefault").GetBoolean()) == 1 && versions.All(item => item.GetProperty("sizeBytes").GetInt64() == Size) &&
            versions.All(item => item.GetProperty("mediaSourceId").GetString()!.Length == 32),
            "Entry detail lists both versions with resolution, label, size and one default");
        Assert(detail.GetProperty("upgrade").GetProperty("blockedReason").GetString() == "already_held_at_cutoff",
            "The administrator sees why no further upgrade happens");
        var viewerDetail = Json.Parse(await ordinary.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}"));
        Assert(viewerDetail.GetProperty("versions").GetArrayLength() == 2 && viewerDetail.GetProperty("upgrade").ValueKind == JsonValueKind.Null,
            "Ordinary users see the versions but not the upgrade state");

        // ---- M7: within a due title the lowest quality goes first; a seeding higher version does not stop it.
        await host.Service<RetentionPolicyService>().SyncAsync(configuration, CancellationToken.None);
        // The first evaluation records each user's finished state; the next one schedules from it (the stub's play time is "now").
        time.Offset += TimeSpan.FromMinutes(1);
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        DateTime? scheduledDeadline;
        await using (var database = new ModDbContext(dbPath))
        {
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(value => value.TargetId == ids["up"]);
            scheduledDeadline = evaluation.Deadline;
            var observations = await database.CompletionObservations.AsNoTracking().Where(value => value.TargetId == ids["up"]).ToListAsync();
            Assert(evaluation.State == "scheduled", $"The upgraded title is scheduled after it was watched: {evaluation.State} {evaluation.Reason} " +
                $"{evaluation.Deadline:o} baseline {evaluation.BaselineAt:o} fresh {evaluation.RequiresFreshCompletion}; observations " +
                string.Join(", ", observations.Select(value => $"{value.UserId} played {value.Played} at {value.CompletedAt:o} last {value.LastPlayedAt:o} evidence {value.EvidenceAvailable}")));
        }

        time.Offset += TimeSpan.FromDays(3);
        var up2160Hash = fixtures["up2160"].InfoHash;
        transmission.Torrents[fixtures["up1080"].InfoHash].UploadRatio = 5;
        await Tick();
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), CancellationToken.None);
        await using (var database = new ModDbContext(dbPath))
        {
            var bindings = await database.EntryBindings.AsNoTracking().Where(value => value.EntryId == ids["up"]).ToListAsync();
            var preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
            var rows = string.Join("; ", preview.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("entryId").AsGuid() == ids["up"])
                .Select(item => item.GetRawText()));
            var lastRun = await database.RetentionRuns.AsNoTracking().OrderByDescending(value => value.StartedAt).FirstAsync();
            Assert(bindings.Count == 1 && bindings[0].MediaPath!.Contains("2160p", StringComparison.Ordinal) &&
                (await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["up"])).State == FileState.OnDisk,
                $"The 1080p version is reclaimed while the seeding 2160p stays; the title stays on disk (bindings {bindings.Count}; " +
                $"preview {rows}; run {lastRun.Status} {lastRun.Detail} eligible {lastRun.Eligible} blocked {lastRun.Blocked} reclaimed {lastRun.Reclaimed})");
        }

        string afterRunEvaluation;
        await using (var database = new ModDbContext(dbPath))
        {
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(value => value.TargetId == ids["up"]);
            afterRunEvaluation = $"{evaluation.State} {evaluation.Reason} {evaluation.Deadline:o} baseline {evaluation.BaselineAt:o}";
        }

        var afterFirst = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}")).GetProperty("versions").EnumerateArray().Single();
        Assert(afterFirst.GetProperty("retention").GetProperty("reason").GetString() == "seed_goal_unmet",
            "The remaining version shows it waits for seeding");
        // Completion is re-read from the remaining version once its sibling is gone; the title's window must not restart.
        time.Offset += TimeSpan.FromMinutes(5);
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        await using (var database = new ModDbContext(dbPath))
        {
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(value => value.TargetId == ids["up"]);
            var observations = await database.CompletionObservations.AsNoTracking().Where(value => value.TargetId == ids["up"]).ToListAsync();
            Assert(evaluation.State == "scheduled" && scheduledDeadline is not null && evaluation.Deadline == scheduledDeadline,
                $"A reclaimed sibling version does not restart the remaining version's window: {evaluation.State} {evaluation.Reason} " +
                $"deadline {evaluation.Deadline:o} was {scheduledDeadline:o} eligible {evaluation.EligibleAt:o} basis {evaluation.CompletionBasisAt:o} " +
                $"baseline {evaluation.BaselineAt:o} fresh {evaluation.RequiresFreshCompletion} after-run {afterRunEvaluation}; history " +
                string.Join(", ", await database.History.AsNoTracking().Where(value => value.EntryId == ids["up"]).OrderBy(value => value.CreatedAt)
                    .Select(value => value.EventType + "@" + value.CreatedAt.ToString("o")).ToListAsync()) + "; observations " + string.Join(", ", observations.Select(value =>
                    $"{value.JellyfinItemId} played {value.Played} at {value.CompletedAt:o} last {value.LastPlayedAt:o}")));
        }

        transmission.Torrents[up2160Hash].UploadRatio = 5;
        await Tick();
        // No further waiting: the deadline already passed, so the seed goal alone held the last version back.
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), CancellationToken.None);
        await using (var database = new ModDbContext(dbPath))
        {
            var preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
            var rows = string.Join("; ", preview.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("entryId").AsGuid() == ids["up"]).Select(item => item.GetRawText()));
            var operations = string.Join("; ", (await database.RetentionOperations.AsNoTracking().Where(value => value.EntryId == ids["up"])
                .ToListAsync()).Select(value => $"{value.State} {value.Reason} {value.Provenance} {value.MediaPath}"));
            var seeds = string.Join("; ", (await database.SeedReleaseOperations.AsNoTracking().Where(value => value.EntryId == ids["up"])
                .ToListAsync()).Select(value => $"{value.State} {value.Reason} met {value.GoalMetAt:o} ratio {value.ObservedRatio}"));
            Assert((await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["up"])).State == FileState.Reclaimed &&
                !await database.EntryBindings.AnyAsync(value => value.EntryId == ids["up"]),
                $"After its seeding goal the last version is reclaimed and the title becomes reclaimed (preview {rows}; operations {operations}; seeds {seeds})");
        }

        // Two plain versions, both due: the lower resolution is processed first within one run.
        var orderFolder = Path.Combine(world.Movies.Location, "Order Movie (2020) [tmdbid-462]");
        Directory.CreateDirectory(orderFolder);
        var order1080 = Path.Combine(orderFolder, "Order Movie (2020) [tmdbid-462] - 1080p WEB-DL.mkv");
        var order720 = Path.Combine(orderFolder, "Order Movie (2020) [tmdbid-462] - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(order1080, new byte[3000]);
        world.Native.AddMovie(world.Movies, order1080, 462);
        await File.WriteAllBytesAsync(order720, new byte[2000]);
        world.Native.AddMovie(world.Movies, order720, 462);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.CountAsync(value => value.EntryId == ids["order"]) == 2 ? true : null;
        }, "Both plain versions are bound");
        time.Offset += TimeSpan.FromMinutes(1);
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        time.Offset += TimeSpan.FromDays(3);
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), CancellationToken.None);
        await using (var database = new ModDbContext(dbPath))
        {
            var operations = await database.RetentionOperations.AsNoTracking().Where(value => value.EntryId == ids["order"])
                .OrderBy(value => value.PreparedAt).ToListAsync();
            Assert(operations.Count == 2 && operations[0].MediaPath.Contains("720p", StringComparison.Ordinal) &&
                operations[1].MediaPath.Contains("1080p", StringComparison.Ordinal) && operations.All(value => value.Provenance == "retention"),
                "The executor processes the 720p before the 1080p of the same due title");
        }

        // ---- M3 title matches: a release a public tracker could only match by title and year is grabbable by hand,
        // but automation takes one only from an indexer trusted for it (user decision 2026-09-20).
        var textOnly = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Public tracker", baseUrl = new Uri(publicTracker.Address, "/api").ToString(), enabled = true, categories = new[] { 2000 },
            apiKey = new { action = "replace", value = publicTracker.ApiKey }, minIntervalSeconds = 0, dailyQueryBudget = 1000
        }), 201, "Text-only indexer");
        var textOnlyId = textOnly.GetProperty("id").AsGuid();
        Assert(!textOnly.GetProperty("automateTitleMatches").GetBoolean(), "A new indexer is not trusted for title matches");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{textOnlyId}/Test", null), 200, "Text-only indexer test");
        var verified = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Indexers")).EnumerateArray()
            .Single(item => item.GetProperty("id").AsGuid() == textOnlyId);
        Assert(Strings(verified.GetProperty("capabilities").GetProperty("movieSearch")).SequenceEqual(["q"]),
            "The tracker advertises a text search and no id search: " + verified.GetProperty("capabilities").GetRawText());
        release(publicTracker, "textauto", "Text.Only.Movie.2021.1080p.WEB-DL-GRP", null, null);
        release(publicTracker, "textmanual", "Text.Manual.Movie.2019.1080p.WEB-DL-GRP", null, null);

        // The picker shows the release as eligible and matched by title alone, so nothing else can hide it from automation.
        var textSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["textauto"]}"));
        var textCandidate = textSearch.GetProperty("candidates").EnumerateArray()
            .Single(item => item.GetProperty("rawTitle").GetString() == "Text.Only.Movie.2021.1080p.WEB-DL-GRP");
        Assert(textCandidate.GetProperty("eligible").GetBoolean() &&
            textCandidate.GetProperty("match").GetProperty("identity").GetString() == "title",
            "The tracker's only candidate is eligible and matched by title and year alone: " + textCandidate.GetRawText());

        await ForceDueAsync(dbPath, time, ids["textauto"]);
        var runL = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking()
                .SingleAsync(value => value.RunId == runL.Id && value.TargetId == ids["textauto"]);
            Assert(runL.Searched == 1 && runL.Grabbed == 0 && decision.Kind == AutomationDecisionKinds.Skipped &&
                decision.Reason == AutomationReasons.NoEligibleCandidate &&
                !await database.GrabOperations.AnyAsync(value => value.EntryId == ids["textauto"]) &&
                !await database.ImportOperations.AnyAsync(value => value.EntryId == ids["textauto"]),
                $"An untrusted indexer's title match is searched but never grabbed automatically ({Describe(runL)}; " +
                $"{decision.Kind} {decision.Reason} {decision.Detail})");
        }

        // The manual path is untouched by the rule: an administrator grabs a title match from the same untrusted indexer.
        var manualSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["textmanual"]}"));
        var manualCandidate = manualSearch.GetProperty("candidates").EnumerateArray()
            .Single(item => item.GetProperty("rawTitle").GetString() == "Text.Manual.Movie.2019.1080p.WEB-DL-GRP");
        Assert(manualCandidate.GetProperty("match").GetProperty("identity").GetString() == "title", "The manual candidate is a title match");
        await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = manualSearch.GetProperty("searchId").AsGuid(), releaseId = manualCandidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p6-title-manual"
        }), 202, "Administrator grabs a title match by hand");
        await WaitForTorrentAsync(transmission, fixtures["textmanual"].InfoHash);
        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["textmanual"]);
            Assert(!grab.Automatic && grab.IndexerId == textOnlyId,
                "The by-hand grab of a title match reaches the client from the untrusted indexer");
        }

        // Trusting the indexer for title matches lets the next run grab the release the previous one refused.
        var trusted = await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{textOnlyId}", new
        {
            name = "Public tracker", baseUrl = new Uri(publicTracker.Address, "/api").ToString(), enabled = true, automateTitleMatches = true,
            categories = new[] { 2000 }, minIntervalSeconds = 0, dailyQueryBudget = 1000, revision = textOnly.GetProperty("revision").GetInt32()
        }), 200, "Administrator trusts the indexer for title matches");
        Assert(trusted.GetProperty("automateTitleMatches").GetBoolean() && !trusted.GetProperty("verified").GetBoolean(),
            "The trust setting reads back and the changed indexer must prove its capabilities again");
        await ForceDueAsync(dbPath, time, ids["textauto"]);
        var runM = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["textauto"]);
            Assert(runM.Grabbed == 1 && grab.Automatic && grab.IndexerId == textOnlyId &&
                grab.RequestedBy == JellyfinMod.Services.Acquisition.GrabService.AutomationUserId,
                $"Once the indexer is trusted the same title match is grabbed automatically from it ({Describe(runM)}; " +
                $"{await DecisionsAsync(dbPath, runM.Id, ids)})");
        }

        await WaitForTorrentAsync(transmission, fixtures["textauto"].InfoHash);

        // ---- Settings survive a restart; the whole run wrote no media_missing.
        await host.DisposeAsync();
        host = await PluginHost.StartAsync(world, dbPath, time, logs, configuration, taskManager);
        setServices(host.App.Services);
        admin = host.Client(world.Admin, true);
        var reread = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"));
        Assert(reread.GetProperty("automationEnabled").GetBoolean() && reread.GetProperty("automationBatchSize").GetInt32() == 3 &&
            reread.GetProperty("dailyAutoGrabBudget").GetInt32() == 20, "Automation settings survive a restart");
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(!await database.History.AnyAsync(value => value.EventType == "media_missing" || value.EventType == "episode_media_missing"),
                "No media_missing was written during the run");
            Assert(await Scalar(database, "PRAGMA integrity_check") == "ok" && await Scalar(database, "PRAGMA foreign_key_check") is null,
                "The database is intact after the run");
        }

        return host;
    }

    private static object AutomationSettings(int revision, bool enabled, int batch = 40, int grabs = 6, long floorBytes = 0) => new
    {
        automationEnabled = enabled, automationIntervalHours = 1, automationBatchSize = batch, newEpisodeDelayMinutes = 120,
        dailyAutoGrabBudget = grabs, maxConcurrentImports = 20, freeSpaceFloorPercent = 0, freeSpaceFloorBytes = floorBytes,
        decisionLogCap = 2000, episodeUpgradesEnabled = false, reacquireReclaimed = false, revision
    };

    private static async Task ForceDueAsync(string dbPath, ShiftedTimeProvider time, params Guid[] targetIds)
    {
        await using var database = new ModDbContext(dbPath);
        foreach (var row in await database.AutomationTargets.Where(value => value.EpisodeId == null || value.EpisodeId != null).ToListAsync())
            row.NextSearchAt = targetIds.Contains(row.TargetId) ? time.GetUtcNow().UtcDateTime.AddMinutes(-1) : time.GetUtcNow().UtcDateTime.AddDays(30);
        foreach (var id in targetIds.Where(id => !database.AutomationTargets.Local.Any(row => row.TargetId == id)))
            database.AutomationTargets.Add(new AutomationTargetState
            {
                TargetId = id, EntryId = id, NextSearchAt = time.GetUtcNow().UtcDateTime.AddMinutes(-1), ProfileRevisionSeen = 0
            });
        await database.SaveChangesAsync();
    }

    private static async Task WaitForTorrentAsync(TransmissionBoundary transmission, string hash) =>
        await WaitAsync(() => Task.FromResult(transmission.Torrents.ContainsKey(hash) ? true : (bool?)null), "The client holds the grabbed torrent");

    private static async Task<string> DecisionsAsync(string dbPath, Guid runId, Dictionary<string, Guid> ids)
    {
        await using var database = new ModDbContext(dbPath);
        var names = ids.ToDictionary(pair => pair.Value, pair => pair.Key);
        return string.Join("; ", (await database.AutomationDecisions.AsNoTracking().Where(decision => decision.RunId == runId)
                .OrderBy(decision => decision.CreatedAt).ToListAsync())
            .Select(decision => $"{names.GetValueOrDefault(decision.TargetId)} {decision.Kind} {decision.Reason} {decision.Detail}"));
    }

    private static int QueriesOf(AutomationRun run) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(run.QueriesByIndexerJson)!.Values.Sum();

    private static string Describe(AutomationRun run) =>
        $"status {run.Status}, considered {run.TargetsConsidered}, searched {run.Searched}, grabbed {run.Grabbed}, upgrades {run.UpgradesPlanned}, skipped {run.Skipped}, detail {run.Detail}";

    private static void AddMovie(ModDbContext database, Dictionary<string, Guid> ids, Guid libraryId, string key, int tmdbId, string title,
        int year, string imdb, bool monitored)
    {
        var entry = new Entry
        {
            MediaType = "movie", TmdbId = tmdbId, Title = title, Year = year, ImdbId = imdb, TargetLibraryId = libraryId, Monitored = monitored,
            MetadataJson = Metadata("movie", tmdbId, title, year, imdb, null)
        };
        database.Entries.Add(entry);
        ids[key] = entry.Id;
    }

    private static string Metadata(string mediaType, int tmdbId, string title, int year, string? imdbId, int? tvdbId) =>
        JsonSerializer.Serialize(new TmdbMetadata(mediaType, tmdbId, title, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null,
            imdbId, tvdbId, false, null, 100, [], [], mediaType == "series" ? [new TmdbSeason(1, "Season 1", 5, null, null)] : []));

    private static async Task<string?> Scalar(ModDbContext database, string sql)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, string message, int seconds = 30) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(100);
        }

        throw new InvalidOperationException("Timed out: " + message);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() == true) return true;
            await Task.Delay(100);
        }

        throw new InvalidOperationException("Timed out: " + message);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return body.Length == 0 ? default : Json.Parse(body);
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, int status, string type, string message)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status && (type.Length == 0 || body.Contains($"\"{type}\"", StringComparison.Ordinal)),
            $"{message}: expected {status} {type}, got {(int)response.StatusCode} {body}");
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
