using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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
        var tvAdminId = Guid.NewGuid();
        // The TV administrator is limited to the TV library (whole-review chunk 3b, P2 5).
        var native = new NativeWorld { Libraries = [movies, tv], UserLibraries = id => id == tvAdminId ? [tv] : [movies, tv] };
        BaseItem.LibraryManager = native.Library;
        var world = new World
        {
            Admin = admin, RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = tvAdminId }, Ordinary = ordinary,
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
                // V1 turns episode upgrades on in existing databases too (user, 2026-09-28).
                upgraded.EpisodeUpgradesEnabled && !upgraded.ReacquireReclaimed,
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
            // RET2-R8: a title whose older file an administrator kept by itself (PHASE10 Q3) while an upgrade replaces (Q10).
            AddMovie(database, ids, movies.Id, "filekept", 465, "File Kept Movie", 2020, "tt0900465", monitored: true);
            AddMovie(database, ids, movies.Id, "order", 462, "Order Movie", 2020, "tt0900462", monitored: false);
            // A title whose versions carry no resolution in their names, so their tier is read from the probed video size.
            AddMovie(database, ids, movies.Id, "scope", 467, "Scope Movie", 2020, "tt0900467", monitored: false);
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
        // V1's migration turns episode upgrades on and moves the revision past any form opened before it.
        var initialRevision = automation.GetProperty("revision").GetInt32();
        Assert(!automation.GetProperty("automationEnabled").GetBoolean() && initialRevision >= 1 &&
            automation.GetProperty("episodeUpgradesEnabled").GetBoolean(), "Automation settings read back off");
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
        var saved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(initialRevision, enabled: true,
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
            foreach (var key in new[] { "s1e1", "up", "kept", "filekept", "textauto", "textmanual" })
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
        status = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Automation/Status"));
        Assert(!Strings(status.GetProperty("pausedReasons")).Contains("breaker_open"),
            "One open breaker does not pause automation while another enabled indexer answers");
        await using (var database = new ModDbContext(dbPath))
        {
            (await database.AcquisitionIndexers.SingleAsync(value => value.Id == indexerId)).Enabled = false;
            await database.SaveChangesAsync();
        }
        status = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Automation/Status"));
        Assert(Strings(status.GetProperty("pausedReasons")).Contains("breaker_open"),
            "With every enabled indexer behind an open breaker, the status and queue banner report breaker_open (P7.S11)");
        await using (var database = new ModDbContext(dbPath))
        {
            (await database.AcquisitionIndexers.SingleAsync(value => value.Id == indexerId)).Enabled = true;
            await database.SaveChangesAsync();
        }
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
        var fileKeptFolder = Path.Combine(world.Movies.Location, "File Kept Movie (2020) [tmdbid-465]");
        Directory.CreateDirectory(fileKeptFolder);
        var fileKeptOld = Path.Combine(fileKeptFolder, "File Kept Movie (2020) [tmdbid-465] - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(fileKeptOld, new byte[4096]);
        world.Native.AddMovie(world.Movies, fileKeptOld, 465);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.CountAsync(value => value.EntryId == ids["up"] || value.EntryId == ids["kept"] ||
                value.EntryId == ids["filekept"]) == 3 ? true : null;
        }, "The three 720p files are bound");
        Guid fileKeptBinding;
        await using (var database = new ModDbContext(dbPath))
            fileKeptBinding = await database.EntryBindings.Where(value => value.EntryId == ids["filekept"]).Select(value => value.Id).SingleAsync();
        await ExpectAsync(ordinary.PostAsync($"/JellyfinMod/Entries/{ids["filekept"]}/Versions/{fileKeptBinding}/Keep", null), 403, "",
            "Ordinary users cannot keep a file");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Entries/{ids["filekept"]}/Versions/{fileKeptBinding}/Keep", null), 200,
            "Keep the third title's 720p file by itself");
        foreach (var key in new[] { "up", "kept", "filekept" })
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids[key]}", new { qualityProfileId = upgrade.GetProperty("id").AsGuid() }),
                200, "Assign the upgrade profile");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Entries/{ids["kept"]}/Keep", null), 200, "Keep the second title");
        release(torznab, "up1080", "Up.Movie.2020.1080p.WEB-DL-GRP", "0900460", null);
        release(torznab, "kept1080", "Kept.Movie.2020.1080p.WEB-DL-GRP", "0900461", null);
        release(torznab, "filekept1080", "File.Kept.Movie.2020.1080p.WEB-DL-GRP", "0900465", null);
        var targets = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Automation/Targets?entryId={ids["up"]}"));
        Assert(targets.EnumerateArray().Single().GetProperty("upgradeEligible").GetBoolean() &&
            targets.EnumerateArray().Single().GetProperty("heldBestQuality").GetString() == "webdl-720p",
            "A held 720p below a 1080p cutoff is upgrade-eligible");
        await ForceDueAsync(dbPath, time, ids["up"], ids["kept"], ids["filekept"]);
        var runK = await Run();
        Assert(runK.UpgradesPlanned == 3, $"The three below-cutoff titles get an upgrade grab ({Describe(runK)})");
        foreach (var key in new[] { "up1080", "kept1080", "filekept1080" }) await WaitForTorrentAsync(transmission, fixtures[key].InfoHash);
        transmission.Progress(fixtures["up1080"].InfoHash, 1.0);
        transmission.Progress(fixtures["kept1080"].InfoHash, 1.0);
        transmission.Progress(fixtures["filekept1080"].InfoHash, 1.0);
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return await database.UpgradeOperations.CountAsync(value => value.State == UpgradeStates.Completed) == 2 &&
                await database.UpgradeOperations.AnyAsync(value => value.EntryId == ids["filekept"] && value.State == UpgradeStates.Blocked)
                ? true : null;
        }, "Two upgrades finish and the file-kept one is blocked", 60);
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
            // Q10 with a per-file Keep (RET2-R8): the upgrade adds the 1080p and the kept 720p stays, blocked as version_kept.
            var fileKept = await database.UpgradeOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["filekept"]);
            Assert(fileKept.State == UpgradeStates.Blocked && fileKept.Reason == "version_kept" && File.Exists(fileKeptOld) &&
                await database.EntryBindings.CountAsync(value => value.EntryId == ids["filekept"]) == 2 &&
                !await database.RetentionOperations.AnyAsync(value => value.EntryId == ids["filekept"] && value.State == "completed"),
                $"A per-file Keep on the older version stops the replacement while the new version is added ({fileKept.State}/{fileKept.Reason})");
        }

        // V1 review P1-3: the upgrade service found the file-kept title's new 1080p playable; then, before its replacement step
        // ran, that 1080p went (as Remove this version takes it: the file, its binding and its native item). The replacement
        // must not remove the 720p, now the title's only copy.
        await ReadAsync(await admin.DeleteAsync($"/JellyfinMod/Entries/{ids["filekept"]}/Versions/{fileKeptBinding}/Keep"), 200,
            "Stop keeping the third title's 720p");

        // V1 review P1-9: a file at the new version's path is not the new version. Its bytes rewritten in place (the inode the
        // import linked, other content), then another file put at its path: the replacement keeps the 720p both times.
        await using (var database = new ModDbContext(dbPath))
        {
            var pending = await database.UpgradeOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["filekept"]);
            var import = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == pending.NewImportOperationId);
            var successorPath = (await database.EntryBindings.AsNoTracking().SingleAsync(value => value.Id == import.BindingId)).MediaPath!;
            // Re-review coverage: the same size, other bytes, written in place (the inode and size the import linked; only the
            // content and its modification time differ).
            var linkedSize = new FileInfo(successorPath).Length;
            await using (var sameSize = new FileStream(successorPath, FileMode.Open, FileAccess.Write))
                await sameSize.WriteAsync(Enumerable.Repeat((byte)9, 4096).ToArray());
            var sameSized = await host.Service<RetentionExecutor>().ReplaceAsync(pending.SupersededBindingId, pending.Id, default);
            Assert(new FileInfo(successorPath).Length == linkedSize && sameSized.State != "completed" &&
                sameSized.Reason == "successor_unavailable" && File.Exists(fileKeptOld),
                $"The replacement refuses a new version rewritten in place at its own size, and the 720p stays ({sameSized.State}/{sameSized.Reason})");
            await using (var rewrite = new FileStream(successorPath, FileMode.Append, FileAccess.Write))
                await rewrite.WriteAsync(new byte[] { 1 });
            var rewritten = await host.Service<RetentionExecutor>().ReplaceAsync(pending.SupersededBindingId, pending.Id, default);
            Assert(rewritten.State != "completed" && rewritten.Reason == "successor_unavailable" && File.Exists(fileKeptOld),
                $"The replacement refuses a new version rewritten in place, and the 720p stays ({rewritten.State}/{rewritten.Reason})");
            File.Delete(successorPath);
            await File.WriteAllBytesAsync(successorPath, Enumerable.Repeat((byte)7, (int)Size).ToArray());
            var swapped = await host.Service<RetentionExecutor>().ReplaceAsync(pending.SupersededBindingId, pending.Id, default);
            Assert(swapped.State != "completed" && swapped.Reason == "successor_unavailable" && File.Exists(fileKeptOld),
                $"The replacement refuses another file at the new version's path, and the 720p stays ({swapped.State}/{swapped.Reason})");
        }

        UpgradeOperation waitingUpgrade;
        await using (var database = new ModDbContext(dbPath))
        {
            waitingUpgrade = await database.UpgradeOperations.AsNoTracking().SingleAsync(value => value.EntryId == ids["filekept"]);
            var import = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == waitingUpgrade.NewImportOperationId);
            var successor = await database.EntryBindings.SingleAsync(value => value.Id == import.BindingId);
            File.Delete(successor.MediaPath!);
            database.EntryBindings.Remove(successor);
            await database.SaveChangesAsync();
            world.Native.Remove(host.Service<MediaBrowser.Controller.Library.ILibraryManager>().GetItemById(import.NativeItemId!.Value)!);
        }

        var raced = await host.Service<RetentionExecutor>().ReplaceAsync(waitingUpgrade.SupersededBindingId, waitingUpgrade.Id, default);
        Assert(raced.State != "completed" && File.Exists(fileKeptOld),
            $"The replacement refuses once its successor is gone, and the 720p stays ({raced.State}/{raced.Reason})");

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
        // Jellyfin holds both files as media sources of one item, so the rows differ by media source, not by item.
        Assert(versions.Select(item => item.GetProperty("jellyfinItemId").AsGuid()).Distinct().Count() == 1 &&
            versions.Select(item => item.GetProperty("mediaSourceId").GetString()).Distinct().Count() == 2 &&
            versions.Select(item => item.GetProperty("bindingId").AsGuid()).Distinct().Count() == 2,
            "Both versions play through the same item and their own media source: " +
            string.Join("; ", versions.Select(item => item.GetRawText())));
        Assert(detail.GetProperty("upgrade").GetProperty("blockedReason").GetString() == "already_held_at_cutoff",
            "The administrator sees why no further upgrade happens");
        var viewerDetail = Json.Parse(await ordinary.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}"));
        Assert(viewerDetail.GetProperty("versions").GetArrayLength() == 2 && viewerDetail.GetProperty("upgrade").ValueKind == JsonValueKind.Null,
            "Ordinary users see the versions but not the upgrade state");

        // Whole-review chunk 2b, P2 5: a movie whose file lies directly in the library root takes no other version. A new file
        // in a folder of its own would be a second movie to Jellyfin, not a version of this one.
        await using (var database = new ModDbContext(dbPath))
        {
            AddMovie(database, ids, world.Movies.Id, "rootheld", 466, "Root Movie", 2020, "tt0900466", monitored: false);
            await database.SaveChangesAsync();
        }

        var rootOld = Path.Combine(world.Movies.Location, "Root Movie (2020) - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(rootOld, new byte[4096]);
        world.Native.AddMovie(world.Movies, rootOld, 466);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.AnyAsync(value => value.EntryId == ids["rootheld"]) ? true : null;
        }, "The root-level file is bound");
        release(torznab, "root2160", "Root.Movie.2020.2160p.WEB-DL-GRP", "0900466", null);
        var rootSearch = Json.Parse(await admin.GetStringAsync(
            $"/JellyfinMod/Releases?entryId={ids["rootheld"]}&intent=addVersion&profileId={any.GetProperty("id").AsGuid()}"));
        var rootCandidate = rootSearch.GetProperty("candidates").EnumerateArray()
            .Single(item => item.GetProperty("rawTitle").GetString() == "Root.Movie.2020.2160p.WEB-DL-GRP");
        var rootGrab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = rootSearch.GetProperty("searchId").AsGuid(), releaseId = rootCandidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p6-root-2160"
        }), 202, "Administrator grabs another quality of the root-level movie");
        await WaitForTorrentAsync(transmission, fixtures["root2160"].InfoHash);
        transmission.Progress(fixtures["root2160"].InfoHash, 1.0);
        var rootImport = await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.GrabId == rootGrab.GetProperty("id").AsGuid() &&
                value.State != ImportStates.Waiting && value.State != ImportStates.Identifying);
        }, "The root-level movie's added version leaves identification");
        Assert(rootImport.State == ImportStates.Blocked && rootImport.Reason == ImportReasons.VersionsNotGrouped &&
            rootImport.DestinationPath is null && !Directory.EnumerateDirectories(world.Movies.Location, "Root Movie*").Any(),
            $"Another version of a movie held in the library root is refused as versions_not_grouped and nothing is linked " +
            $"(whole-review c2bf5): {rootImport.State}/{rootImport.Reason} {rootImport.DestinationPath}");
        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{rootImport.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true, blocklist = false }) }), 200, "The refused import leaves the queue");

        // V1 review P2-12: the 2160p is a further version of the readable 1080p main item. With its own tag blocked for a user,
        // Jellyfin's rule hides that version from them: the entry's rows leave it out for the ordinary user and the
        // administrator, and Remove answers the administrator as for a version that does not exist. Nothing is removed.
        var uhdRow = versions.Single(item => item.GetProperty("resolution").GetString() == "2160p");
        var hiddenVersion = world.Native.Items.Single(item => item.Id == Guid.Parse(uhdRow.GetProperty("mediaSourceId").GetString()!));
        Assert(hiddenVersion is Video { PrimaryVersionId: not null }, "The 2160p is a further version of the title's main item");
        hiddenVersion.Tags = ["jfmod-v1-hidden"];
        foreach (var restricted in new[] { world.Admin, world.Ordinary })
            restricted.SetPreference(PreferenceKind.BlockedTags, ["jfmod-v1-hidden"]);
        var hiddenForViewer = Json.Parse(await ordinary.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}")).GetProperty("versions");
        var hiddenForAdmin = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}")).GetProperty("versions");
        var hiddenRemove = await admin.PostAsync($"/JellyfinMod/Entries/{ids["up"]}/Versions/{uhdRow.GetProperty("bindingId").AsGuid()}/Remove", null);
        var hiddenRemoveStatus = hiddenRemove.StatusCode;
        foreach (var restricted in new[] { world.Admin, world.Ordinary })
            restricted.SetPreference(PreferenceKind.BlockedTags, []);
        hiddenVersion.Tags = [];
        Assert(hiddenForViewer.GetArrayLength() == 1 && hiddenForViewer[0].GetProperty("resolution").GetString() == "1080p" &&
            hiddenForAdmin.GetArrayLength() == 1 && hiddenRemoveStatus == HttpStatusCode.NotFound && File.Exists(hiddenVersion.Path),
            $"A further version hidden by its own tag is not listed ({hiddenForViewer.GetArrayLength()} rows for the viewer, " +
            $"{hiddenForAdmin.GetArrayLength()} for the administrator) and Remove conceals it ({(int)hiddenRemoveStatus}, file present " +
            $"{File.Exists(hiddenVersion.Path)})");
        Assert(Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}")).GetProperty("versions").GetArrayLength() == 2,
            "Without the blocked tag both versions are listed again");

        // V1 re-review P-3: the further version's item cannot be read, so whether this user may see it is unknown. It is left
        // out of the rows, and Remove and Keep answer as for a version that does not exist. Nothing is removed or kept.
        world.Native.FailingRead = id => id == hiddenVersion.Id;
        var unreadableRows = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["up"]}")).GetProperty("versions");
        var unreadableRemove = (await admin.PostAsync(
            $"/JellyfinMod/Entries/{ids["up"]}/Versions/{uhdRow.GetProperty("bindingId").AsGuid()}/Remove", null)).StatusCode;
        var unreadableKeep = (await admin.PostAsync(
            $"/JellyfinMod/Entries/{ids["up"]}/Versions/{uhdRow.GetProperty("bindingId").AsGuid()}/Keep", null)).StatusCode;
        world.Native.FailingRead = null;
        await using (var database = new ModDbContext(dbPath))
            Assert(unreadableRows.GetArrayLength() == 1 && unreadableRows[0].GetProperty("resolution").GetString() == "1080p" &&
                unreadableRemove == HttpStatusCode.NotFound && unreadableKeep == HttpStatusCode.NotFound && File.Exists(hiddenVersion.Path!) &&
                !await database.VersionKeeps.AnyAsync(value => value.EntryId == ids["up"]),
                $"A further version whose item cannot be read is not listed ({unreadableRows.GetArrayLength()} rows) and Remove " +
                $"({(int)unreadableRemove}) and Keep ({(int)unreadableKeep}) conceal it");

        // ---- M7: within a due title the lowest quality goes first; a seeding higher version does not stop it.
        await host.Service<RetentionPolicyService>().SyncAsync(configuration, CancellationToken.None);
        // The first evaluation records each user's finished state; the next one schedules from it (the stub's play time is "now").
        time.Offset += TimeSpan.FromMinutes(1);
        // Every import restarts the title (P3.T7) and a new file restarts it too (RET2-R1), so evidence read before the 2160p
        // import finished is older than the baseline; the evidence repair reads the watch again, as a playback event would.
        await host.Service<RetentionCompletionService>().RefreshAllAsync(new Progress<double>(), CancellationToken.None);
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
                    $"{value.JellyfinItemId} played {value.Played} at {value.CompletedAt:o} last {value.LastPlayedAt:o} " +
                    $"evidence {value.EvidenceAvailable}")) + "; bindings " + string.Join(", ",
                    (await database.EntryBindings.AsNoTracking().Where(value => value.EntryId == ids["up"]).ToListAsync())
                        .Select(value => $"{value.JellyfinItemId} owner {value.OwnerItemId} " +
                            $"alive {world.Native.Items.Any(item => item.Id == value.JellyfinItemId)} {value.MediaPath}")));
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

        // A version's tier is the larger of its width and height tiers, as Jellyfin's own media info reads it: a scope-ratio
        // 3840x1600 file is 2160p, not the 1080p its height alone gives. The versions API reads the probed streams, and
        // retention ranks the same sizes from the native items, so a 4:3 1440x1080 file is 1080p, not 720p by its width.
        var scopeFolder = Path.Combine(world.Movies.Location, "Scope Movie (2020) [tmdbid-467]");
        Directory.CreateDirectory(scopeFolder);
        var scopeSizes = new (string Label, int Width, int Height, string Resolution)[]
        {
            ("Wide", 3840, 1600, "2160p"), ("Cinema", 1920, 800, "1080p"), ("Flat", 1280, 536, "720p"), ("Academy", 1440, 1080, "1080p")
        };
        foreach (var size in scopeSizes)
        {
            var file = Path.Combine(scopeFolder, $"Scope Movie (2020) [tmdbid-467] - {size.Label}.mkv");
            await File.WriteAllBytesAsync(file, new byte[1000]);
            world.Native.AddMovie(world.Movies, file, 467);
            var item = (Video)world.Native.Items.Single(value => value.Path == file);
            item.Width = size.Width;
            item.Height = size.Height;
            world.Native.Streams[item.Id] =
            [
                new MediaBrowser.Model.Entities.MediaStream
                {
                    Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 0, Codec = "hevc", Width = size.Width, Height = size.Height
                }
            ];
        }

        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.CountAsync(value => value.EntryId == ids["scope"]) == scopeSizes.Length ? true : null;
        }, "Every scope-ratio version is bound");
        var scopeRows = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["scope"]}")).GetProperty("versions")
            .EnumerateArray().ToArray();
        Assert(scopeRows.Length == scopeSizes.Length && scopeSizes.All(size => scopeRows.Any(row =>
                row.GetProperty("label").GetString() == size.Label && row.GetProperty("width").GetInt32() == size.Width &&
                row.GetProperty("height").GetInt32() == size.Height && row.GetProperty("resolution").GetString() == size.Resolution)),
            "Each version's resolution is its width or height tier, whichever is larger: " +
            string.Join("; ", scopeRows.Select(row => $"{row.GetProperty("label")} {row.GetProperty("width")}x{row.GetProperty("height")} " +
                $"{row.GetProperty("resolution")}")));
        time.Offset += TimeSpan.FromMinutes(1);
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        time.Offset += TimeSpan.FromDays(3);
        await using (var scope = host.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), CancellationToken.None);
        await using (var database = new ModDbContext(dbPath))
        {
            // 720p, then the two 1080p files by path, then 2160p; a width-only rank put the 1440x1080 file first.
            var order = (await database.RetentionOperations.AsNoTracking().Where(value => value.EntryId == ids["scope"])
                    .OrderBy(value => value.PreparedAt).ToListAsync())
                .Select(value => Path.GetFileNameWithoutExtension(value.MediaPath))
                .Select(name => name[(name.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..]).ToArray();
            Assert(order.SequenceEqual(["Flat", "Academy", "Cinema", "Wide"]),
                "Retention processes the scope-ratio versions lowest tier first: " + string.Join(", ", order));
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

        // V1 D1: an episode's first file is named `Series (Year) SNNENN` with no quality label, so its name tells nothing; its
        // held quality is the quality of the release the plugin imported it from, and it is not held_quality_unknown.
        await WaitForTorrentAsync(transmission, fixtures["s1e2"].InfoHash);
        transmission.Progress(fixtures["s1e2"].InfoHash, 1.0);
        var episodeImport = await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.EpisodeId == ids["s1e2"] &&
                value.State == ImportStates.Completed);
        }, "The new episode is imported", 60);
        var episodeTarget = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Automation/Targets?entryId={ids["series"]}"))
            .EnumerateArray().Single(item => item.GetProperty("targetId").AsGuid() == ids["s1e2"]);
        Assert(Path.GetFileName(episodeImport.DestinationPath) == "Auto Show (2024) S01E02.mkv" &&
            episodeTarget.GetProperty("heldBestQuality").GetString() == "webdl-1080p" &&
            episodeTarget.GetProperty("blockedReason").GetString() != AutomationReasons.HeldQualityUnknown,
            $"An unlabelled episode file holds the quality of the release it was imported from ({Path.GetFileName(episodeImport.DestinationPath)}: " +
            episodeTarget.GetRawText() + ")");

        // V1 re-review coverage of P2-12 for an episode: S01E02 gains a further version, which its own tag hides from a user
        // who can read the episode. The episode's rows leave it out, and Remove and Keep answer as for a version that does not
        // exist; without the blocked tag it is listed again.
        var mainEpisode = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Single(item => item.Path == episodeImport.DestinationPath);
        var episodeVersionPath = Path.Combine(Path.GetDirectoryName(episodeImport.DestinationPath)!, "Auto Show (2024) S01E02 - 720p WEB-DL.mkv");
        await File.WriteAllBytesAsync(episodeVersionPath, new byte[4096]);
        var episodeVersion = world.Native.AddEpisodeVersion(mainEpisode, episodeVersionPath);
        var episodeVersionBinding = await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.AsNoTracking().SingleOrDefaultAsync(value => value.EpisodeId == ids["s1e2"] &&
                value.JellyfinItemId == episodeVersion.Id);
        }, "The episode's further version is bound");
        async Task<int> EpisodeRowsAsync(HttpClient client) => Json.Parse(await client.GetStringAsync($"/JellyfinMod/Entries/{ids["series"]}"))
            .GetProperty("episodes").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == ids["s1e2"])
            .GetProperty("versions").GetArrayLength();
        var visibleRows = await EpisodeRowsAsync(admin);
        episodeVersion.Tags = ["jfmod-v1-hidden"];
        foreach (var restricted in new[] { world.Admin, world.Ordinary })
            restricted.SetPreference(PreferenceKind.BlockedTags, ["jfmod-v1-hidden"]);
        var hiddenEpisodeViewer = await EpisodeRowsAsync(ordinary);
        var hiddenEpisodeAdmin = await EpisodeRowsAsync(admin);
        var hiddenEpisodeRemove = (await admin.PostAsync(
            $"/JellyfinMod/Entries/{ids["series"]}/Versions/{episodeVersionBinding.Id}/Remove", null)).StatusCode;
        var hiddenEpisodeKeep = (await admin.PostAsync(
            $"/JellyfinMod/Entries/{ids["series"]}/Versions/{episodeVersionBinding.Id}/Keep", null)).StatusCode;
        foreach (var restricted in new[] { world.Admin, world.Ordinary })
            restricted.SetPreference(PreferenceKind.BlockedTags, []);
        episodeVersion.Tags = [];
        Assert(visibleRows == 2 && hiddenEpisodeViewer == 1 && hiddenEpisodeAdmin == 1 && hiddenEpisodeRemove == HttpStatusCode.NotFound &&
            hiddenEpisodeKeep == HttpStatusCode.NotFound && File.Exists(episodeVersionPath) && await EpisodeRowsAsync(admin) == 2,
            $"An episode's further version hidden by its own tag is not listed ({visibleRows} rows visible, then {hiddenEpisodeViewer} " +
            $"for the viewer and {hiddenEpisodeAdmin} for the administrator) and Remove ({(int)hiddenEpisodeRemove}) and Keep " +
            $"({(int)hiddenEpisodeKeep}) conceal it");

        // ---- Whole-review chunk 2c: automation re-checks what it grabs, counts what will become imports, keeps a grab with
        // its records, waits for a real free-space measurement, looks past an under-seeded best release, and starts afresh
        // when a title is switched to another profile or monitored again. Each title here is its own target, made due alone.
        var reviewKeys = new[] { "seedy", "unwatch", "capx", "capy", "failsave", "nospace", "switch" };
        await using (var database = new ModDbContext(dbPath))
        {
            foreach (var (key, index) in reviewKeys.Select((key, index) => (key, index)))
                AddMovie(database, ids, world.Movies.Id, key, 470 + index, "Review " + key, 2020, $"tt09004{70 + index}", monitored: true);
            // Scheduled far out, with the default profile's revision seen, so no run takes them until each is made due alone.
            var defaultRevision = (await database.AcquisitionQualityProfiles.AsNoTracking().SingleAsync(value => value.Name == "Any")).Revision;
            foreach (var key in reviewKeys)
                database.AutomationTargets.Add(new AutomationTargetState
                {
                    TargetId = ids[key], EntryId = ids[key], NextSearchAt = time.GetUtcNow().UtcDateTime.AddDays(30), ProfileRevisionSeen = defaultRevision
                });
            await database.SaveChangesAsync();
        }

        foreach (var key in reviewKeys.Where(key => key is not ("seedy" or "switch")))
            release(torznab, key, $"Review.{key}.2020.1080p.WEB-DL-GRP", $"09004{70 + Array.IndexOf(reviewKeys, key)}", null);
        // Two releases of one title: the better-scored one has too few seeders, the other has plenty.
        foreach (var (title, seeders) in new[] { ("Review.seedy.2020.2160p.WEB-DL-GRP", 2), ("Review.seedy.2020.1080p.WEB-DL-GRP", 40) })
        {
            var key = "seedy-" + seeders;
            var fixture = TorrentFixture.Single(title + ".mkv", Size);
            fixtures[key] = fixture;
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.MovieItems.Add(new(title, "guid-" + key, torznab.Download(key), Size, seeders,
                    new Dictionary<string, string> { ["imdbid"] = "0900470" }));
        }

        // P2 6: the 2160p release scores higher but has 2 seeders; the 1080p one meets the profile's 10 and is grabbed.
        var seeded = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Seeded", qualities = new[] { "webdl-2160p", "webdl-1080p" }, minimumSeeders = 10
        }), 201, "Profile with a seeders minimum");
        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["seedy"]}", new { qualityProfileId = seeded.GetProperty("id").AsGuid() }),
            200, "The title searches with the seeded profile");
        await ForceDueAsync(dbPath, time, ids["seedy"]);
        var runSeedy = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.EntryId == ids["seedy"]);
            Assert(grab is not null && grab.RawTitle == "Review.seedy.2020.1080p.WEB-DL-GRP",
                $"An under-seeded best release does not hide one that meets the thresholds (whole-review c2cf6; {Describe(runSeedy)}; " +
                $"{await DecisionsAsync(dbPath, runSeedy.Id, ids)})");
        }

        // P2 1: the title is unmonitored while its search is under way; the grab is not made.
        torznab.OnSearch = async query =>
        {
            if (!query.Contains("0900471", StringComparison.Ordinal)) return;
            await using var database = new ModDbContext(dbPath);
            (await database.Entries.SingleAsync(value => value.Id == ids["unwatch"])).Monitored = false;
            await database.SaveChangesAsync();
        };
        await ForceDueAsync(dbPath, time, ids["unwatch"]);
        var runUnwatch = await Run();
        torznab.OnSearch = null;
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runUnwatch.Id &&
                value.TargetId == ids["unwatch"]);
            Assert(!await database.GrabOperations.AnyAsync(value => value.EntryId == ids["unwatch"]) &&
                decision.Reason == AutomationReasons.NoLongerWanted,
                $"A title unmonitored during its search is not grabbed (whole-review c2cf1): {decision.Kind} {decision.Reason} {decision.Detail}");
        }

        // Codex round 2 P2: monitored again, the title is unmonitored while its torrent is fetched, after the search and its
        // check. Admission reads the title again with the insert, so nothing is grabbed.
        await using (var database = new ModDbContext(dbPath))
            await database.Entries.Where(value => value.Id == ids["unwatch"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Monitored, true));
        torznab.OnDownload = async key =>
        {
            if (key != "unwatch") return;
            using var unmonitor = await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["unwatch"]}", new { monitored = false });
        };
        await ForceDueAsync(dbPath, time, ids["unwatch"]);
        var runFetched = await Run();
        torznab.OnDownload = null;
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runFetched.Id &&
                value.TargetId == ids["unwatch"]);
            Assert(!await database.GrabOperations.AnyAsync(value => value.EntryId == ids["unwatch"]) &&
                decision.Reason == AutomationReasons.NoLongerWanted,
                $"A title unmonitored while its torrent is fetched is not grabbed (Codex round 2 P2): {decision.Kind} {decision.Reason} {decision.Detail}");
        }

        // Codex round 2 P2 and delta review 2: an automatic grab whose title an administrator unmonitors while the grab's
        // hold ends, and the client is being asked about the torrent, is never sent.
        var heldAutomaticId = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Entries.Where(value => value.Id == ids["unwatch"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Monitored, true));
            var template = await database.GrabOperations.AsNoTracking().FirstAsync(value => value.EntryId == ids["seedy"]);
            database.GrabOperations.Add(new GrabOperation
            {
                Id = heldAutomaticId, RequestedBy = JellyfinMod.Services.Acquisition.GrabService.AutomationUserId,
                IdempotencyKey = "review-held-unwatch", RequestFingerprint = "review", EntryId = ids["unwatch"], Automatic = true,
                ActiveTarget = JellyfinMod.Services.Acquisition.GrabService.TargetKey(ids["unwatch"], null), SearchId = template.SearchId,
                ReleaseId = "review-unwatch", IndexerId = template.IndexerId, IndexerName = template.IndexerName, SourceGuid = "review-unwatch",
                RawTitle = "Review.unwatch.2020.1080p.WEB-DL-GRP", ProfileId = template.ProfileId, DownloadClientId = template.DownloadClientId,
                DownloadClientRevision = template.DownloadClientRevision, InfoHash = fixtures["unwatch"].InfoHash, Label = template.Label,
                DownloadDirectory = template.DownloadDirectory, State = GrabStates.Pending, CreatedAt = time.GetUtcNow().UtcDateTime,
                UpdatedAt = time.GetUtcNow().UtcDateTime, HoldUntil = time.GetUtcNow().UtcDateTime
            });
            await database.SaveChangesAsync();
        }

        host.Service<JellyfinMod.Services.Acquisition.GrabPayloadVault>().Put(heldAutomaticId,
            new JellyfinMod.Services.Acquisition.TorrentLocator(fixtures["unwatch"].InfoHash, fixtures["unwatch"].Bytes, null, null, null));
        var unmonitoredDuringLookup = false;
        transmission.AfterGet = async asked =>
        {
            if (asked is not { Count: 1 } || !asked.Contains(fixtures["unwatch"].InfoHash)) return;
            transmission.AfterGet = null;
            using var unmonitor = await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["unwatch"]}", new { monitored = false });
            unmonitoredDuringLookup = unmonitor.IsSuccessStatusCode;
        };
        using (var scope = host.Service<IServiceScopeFactory>().CreateScope())
            await scope.ServiceProvider.GetRequiredService<JellyfinMod.Services.Acquisition.GrabService>().DispatchAsync(heldAutomaticId, default);
        transmission.AfterGet = null;
        await using (var database = new ModDbContext(dbPath))
        {
            var heldAutomatic = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == heldAutomaticId);
            Assert(unmonitoredDuringLookup && heldAutomatic is { State: GrabStates.Failed, FailureCode: "no_longer_wanted" } &&
                !transmission.Torrents.ContainsKey(fixtures["unwatch"].InfoHash),
                $"A held automatic grab of a title no longer monitored is not sent (Codex round 2 P2): {heldAutomatic.State} {heldAutomatic.FailureCode}");
        }

        // Final review, finding 3 (whole-review c2a-F2): automation turned off while a held automatic grab's client lookup is under
        // way stops that grab; it is never sent. The switch is put back as it was afterwards.
        var automationBefore = System.Text.Json.Nodes.JsonNode.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"))!.AsObject();
        async Task<HttpResponseMessage> SetAutomationAsync(bool enabled)
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"))!.AsObject();
            body["automationEnabled"] = enabled;
            return await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", body);
        }

        if (!automationBefore["automationEnabled"]!.GetValue<bool>())
            Assert((await SetAutomationAsync(true)).IsSuccessStatusCode, "Automation is on for the held-grab case");
        var heldOffId = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Entries.Where(value => value.Id == ids["unwatch"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Monitored, true));
            var template = await database.GrabOperations.AsNoTracking().FirstAsync(value => value.EntryId == ids["seedy"]);
            database.GrabOperations.Add(new GrabOperation
            {
                Id = heldOffId, RequestedBy = JellyfinMod.Services.Acquisition.GrabService.AutomationUserId,
                IdempotencyKey = "review-held-automation-off", RequestFingerprint = "review", EntryId = ids["unwatch"], Automatic = true,
                ActiveTarget = JellyfinMod.Services.Acquisition.GrabService.TargetKey(ids["unwatch"], null), SearchId = template.SearchId,
                ReleaseId = "review-automation-off", IndexerId = template.IndexerId, IndexerName = template.IndexerName, SourceGuid = "review-automation-off",
                RawTitle = "Review.unwatch.2020.1080p.WEB-DL-GRP", ProfileId = template.ProfileId, DownloadClientId = template.DownloadClientId,
                DownloadClientRevision = template.DownloadClientRevision, InfoHash = fixtures["unwatch"].InfoHash, Label = template.Label,
                DownloadDirectory = template.DownloadDirectory, State = GrabStates.Pending, CreatedAt = time.GetUtcNow().UtcDateTime,
                UpdatedAt = time.GetUtcNow().UtcDateTime, HoldUntil = time.GetUtcNow().UtcDateTime
            });
            await database.SaveChangesAsync();
        }

        host.Service<JellyfinMod.Services.Acquisition.GrabPayloadVault>().Put(heldOffId,
            new JellyfinMod.Services.Acquisition.TorrentLocator(fixtures["unwatch"].InfoHash, fixtures["unwatch"].Bytes, null, null, null));
        var turnedOffDuringLookup = false;
        transmission.AfterGet = async asked =>
        {
            if (asked is not { Count: 1 } || !asked.Contains(fixtures["unwatch"].InfoHash)) return;
            transmission.AfterGet = null;
            using var off = await SetAutomationAsync(false);
            turnedOffDuringLookup = off.IsSuccessStatusCode;
        };
        using (var scope = host.Service<IServiceScopeFactory>().CreateScope())
            await scope.ServiceProvider.GetRequiredService<JellyfinMod.Services.Acquisition.GrabService>().DispatchAsync(heldOffId, default);
        transmission.AfterGet = null;
        await using (var database = new ModDbContext(dbPath))
        {
            var heldOff = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == heldOffId);
            Assert(turnedOffDuringLookup && heldOff is { State: GrabStates.Failed, FailureCode: "automation_disabled" } &&
                !transmission.Torrents.ContainsKey(fixtures["unwatch"].InfoHash),
                $"A held automatic grab is not sent when automation is turned off during its client lookup (final review 3): " +
                $"{heldOff.State} {heldOff.FailureCode}");
        }

        Assert((await SetAutomationAsync(automationBefore["automationEnabled"]!.GetValue<bool>())).IsSuccessStatusCode,
            "The automation switch is put back");

        // Final review 2, finding 1: the switch for a held automatic grab's kind of grab is read again when it commits. An episode
        // upgrade stops when episode upgrades are turned off during its client lookup, and a reacquisition of a reclaimed title
        // when reacquisition is turned off. Each switch, and the target's file state, is put back afterwards.
        async Task<HttpResponseMessage> SetAutomationSwitchAsync(string name, bool value)
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"))!.AsObject();
            body[name] = value;
            return await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", body);
        }

        async Task<(GrabOperation Grab, bool SwitchedOff)> HeldSwitchCaseAsync(string key, Guid entryId, Guid? episodeId, Guid? upgradeOperationId,
            string switchName)
        {
            var id = Guid.NewGuid();
            await using (var database = new ModDbContext(dbPath))
            {
                var target = JellyfinMod.Services.Acquisition.GrabService.TargetKey(entryId, episodeId);
                Assert(!await database.GrabOperations.AnyAsync(value => value.ActiveTarget == target), $"No grab is active for the {key} case");
                var template = await database.GrabOperations.AsNoTracking().FirstAsync(value => value.EntryId == ids["seedy"]);
                database.GrabOperations.Add(new GrabOperation
                {
                    Id = id, RequestedBy = JellyfinMod.Services.Acquisition.GrabService.AutomationUserId,
                    IdempotencyKey = "review-held-" + key, RequestFingerprint = "review", EntryId = entryId, EpisodeId = episodeId, Automatic = true,
                    UpgradeOperationId = upgradeOperationId, ActiveTarget = target, SearchId = template.SearchId,
                    ReleaseId = "review-" + key, IndexerId = template.IndexerId, IndexerName = template.IndexerName, SourceGuid = "review-" + key,
                    RawTitle = "Review." + key + ".2020.2160p.WEB-DL-GRP", ProfileId = template.ProfileId, DownloadClientId = template.DownloadClientId,
                    DownloadClientRevision = template.DownloadClientRevision, InfoHash = fixtures["unwatch"].InfoHash, Label = template.Label,
                    DownloadDirectory = template.DownloadDirectory, State = GrabStates.Pending, CreatedAt = time.GetUtcNow().UtcDateTime,
                    UpdatedAt = time.GetUtcNow().UtcDateTime, HoldUntil = time.GetUtcNow().UtcDateTime
                });
                await database.SaveChangesAsync();
            }

            host.Service<JellyfinMod.Services.Acquisition.GrabPayloadVault>().Put(id,
                new JellyfinMod.Services.Acquisition.TorrentLocator(fixtures["unwatch"].InfoHash, fixtures["unwatch"].Bytes, null, null, null));
            var switchedOff = false;
            transmission.AfterGet = async asked =>
            {
                if (asked is not { Count: 1 } || !asked.Contains(fixtures["unwatch"].InfoHash)) return;
                transmission.AfterGet = null;
                using var off = await SetAutomationSwitchAsync(switchName, false);
                switchedOff = off.IsSuccessStatusCode;
            };
            using (var scope = host.Service<IServiceScopeFactory>().CreateScope())
                await scope.ServiceProvider.GetRequiredService<JellyfinMod.Services.Acquisition.GrabService>().DispatchAsync(id, default);
            transmission.AfterGet = null;
            await using (var database = new ModDbContext(dbPath))
                return (await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == id), switchedOff);
        }

        var switchesBefore = System.Text.Json.Nodes.JsonNode.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"))!.AsObject();
        Assert((await SetAutomationAsync(true)).IsSuccessStatusCode && (await SetAutomationSwitchAsync("episodeUpgradesEnabled", true)).IsSuccessStatusCode
            && (await SetAutomationSwitchAsync("reacquireReclaimed", true)).IsSuccessStatusCode, "Automation, episode upgrades and reacquisition are on");
        FileState episodeStateBefore, movieStateBefore;
        await using (var database = new ModDbContext(dbPath))
        {
            episodeStateBefore = (await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == ids["s1e1"])).State;
            movieStateBefore = (await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["unwatch"])).State;
            await database.Episodes.Where(value => value.Id == ids["s1e1"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, FileState.OnDisk));
            await database.Entries.Where(value => value.Id == ids["unwatch"]).ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.State, FileState.Reclaimed).SetProperty(value => value.Monitored, true));
        }

        var (heldUpgrade, upgradesOff) = await HeldSwitchCaseAsync("episode-upgrade", ids["series"], ids["s1e1"], Guid.NewGuid(), "episodeUpgradesEnabled");
        Assert(upgradesOff && heldUpgrade is { State: GrabStates.Failed, FailureCode: "episode_upgrades_disabled" } &&
            !transmission.Torrents.ContainsKey(fixtures["unwatch"].InfoHash),
            $"A held automatic episode upgrade is not sent when episode upgrades are turned off during its client lookup (final review 2, finding 1): " +
            $"{heldUpgrade.State} {heldUpgrade.FailureCode}");
        // Pi review 1, finding 3: the grab read names the switch that stopped it and says nothing was sent.
        async Task<string?> GrabMessageAsync(Guid id) =>
            Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{id}")).GetProperty("message").GetString();
        var upgradeMessage = await GrabMessageAsync(heldUpgrade.Id);
        Assert(upgradeMessage == "Episode upgrades were turned off during the hold; nothing was sent.",
            $"The stopped episode upgrade explains itself (Pi review 1, finding 3): {upgradeMessage}");
        var (reacquire, reacquireOff) = await HeldSwitchCaseAsync("reacquire", ids["unwatch"], null, null, "reacquireReclaimed");
        Assert(reacquireOff && reacquire is { State: GrabStates.Failed, FailureCode: "reacquire_disabled" } &&
            !transmission.Torrents.ContainsKey(fixtures["unwatch"].InfoHash),
            $"A held automatic reacquisition is not sent when reacquisition is turned off during its client lookup (final review 2, finding 1): " +
            $"{reacquire.State} {reacquire.FailureCode}");
        var reacquireMessage = await GrabMessageAsync(reacquire.Id);
        Assert(reacquireMessage == "Reacquiring reclaimed titles was turned off during the hold; nothing was sent.",
            $"The stopped reacquisition explains itself (Pi review 1, finding 3): {reacquireMessage}");
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Episodes.Where(value => value.Id == ids["s1e1"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, episodeStateBefore));
            await database.Entries.Where(value => value.Id == ids["unwatch"]).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, movieStateBefore));
        }

        Assert((await SetAutomationSwitchAsync("episodeUpgradesEnabled", switchesBefore["episodeUpgradesEnabled"]!.GetValue<bool>())).IsSuccessStatusCode
            && (await SetAutomationSwitchAsync("reacquireReclaimed", switchesBefore["reacquireReclaimed"]!.GetValue<bool>())).IsSuccessStatusCode
            && (await SetAutomationAsync(automationBefore["automationEnabled"]!.GetValue<bool>())).IsSuccessStatusCode,
            "The automation switches are put back");

        // P2 2: a held grab will become an import. With the cap at the open imports plus one, it fills the cap.
        int openImports;
        await using (var database = new ModDbContext(dbPath))
        {
            openImports = await database.ImportOperations.CountAsync(operation => ImportStates.Open.Contains(operation.State));
            var template = await database.GrabOperations.AsNoTracking().FirstAsync(value => value.EntryId == ids["seedy"]);
            database.GrabOperations.Add(new GrabOperation
            {
                RequestedBy = world.Admin.Id, IdempotencyKey = "review-held-capx", RequestFingerprint = "review", EntryId = ids["capx"],
                ActiveTarget = JellyfinMod.Services.Acquisition.GrabService.TargetKey(ids["capx"], null), SearchId = template.SearchId,
                ReleaseId = "review-capx", IndexerId = template.IndexerId, IndexerName = template.IndexerName, SourceGuid = "review-capx",
                RawTitle = "Review.capx.2020.1080p.WEB-DL-GRP", ProfileId = template.ProfileId, DownloadClientId = template.DownloadClientId,
                State = GrabStates.Pending, CreatedAt = time.GetUtcNow().UtcDateTime, UpdatedAt = time.GetUtcNow().UtcDateTime,
                HoldUntil = time.GetUtcNow().UtcDateTime.AddHours(1)
            });
            await database.SaveChangesAsync();
        }

        var capSettings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(capSettings.GetProperty("revision").GetInt32(),
            enabled: true, batch: 3, grabs: 20, maxImports: openImports + 1)), 200, "The import cap is the open imports plus one");
        await ForceDueAsync(dbPath, time, ids["capy"]);
        var queriesBeforeCap = torznab.SearchQueries;
        var runCap = await Run();
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runCap.Id &&
                value.TargetId == ids["capy"]);
            Assert(decision.Reason == AutomationReasons.TooManyOpenImports && torznab.SearchQueries == queriesBeforeCap &&
                !await database.GrabOperations.AnyAsync(value => value.EntryId == ids["capy"]),
                $"A held grab counts towards the concurrent-import cap across runs (whole-review c2cf2): {decision.Reason}");
            var heldGrab = await database.GrabOperations.SingleAsync(value => value.IdempotencyKey == "review-held-capx");
            heldGrab.State = GrabStates.Cancelled;
            heldGrab.ActiveTarget = null;
            await database.SaveChangesAsync();
        }

        // Codex round 2 P2 and delta review 2: one slot is left when automation starts. An administrator grabs another
        // title by hand while automation searches; automation counts that grab again at its own insert and does not take
        // the slot twice.
        int busyBefore;
        await using (var database = new ModDbContext(dbPath))
            busyBefore = await database.ImportOperations.CountAsync(operation => ImportStates.Open.Contains(operation.State)) +
                await database.GrabOperations.CountAsync(grab => grab.State == GrabStates.Pending || grab.State == GrabStates.Submitting ||
                    grab.State == GrabStates.Unknown || grab.State == GrabStates.Accepted && grab.ActiveTarget != null &&
                    !database.ImportOperations.Any(operation => operation.GrabId == grab.Id));
        capSettings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(capSettings.GetProperty("revision").GetInt32(),
            enabled: true, batch: 3, grabs: 20, maxImports: busyBefore + 1)), 200, "The import cap is what is open now plus one");
        Guid? manualCapGrab = null;
        string? manualCapStatus = null;
        torznab.OnSearch = async query =>
        {
            if (!query.Contains("0900473", StringComparison.Ordinal) || manualCapStatus is not null) return;
            manualCapStatus = "searching";
            var manualSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["capx"]}"));
            var manualCandidate = manualSearch.GetProperty("candidates").EnumerateArray()
                .First(candidate => candidate.GetProperty("eligible").GetBoolean());
            using var manual = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = manualSearch.GetProperty("searchId").AsGuid(), releaseId = manualCandidate.GetProperty("releaseId").GetString(),
                idempotencyKey = "p6-manual-capx"
            });
            manualCapStatus = ((int)manual.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (manual.IsSuccessStatusCode) manualCapGrab = Json.Parse(await manual.Content.ReadAsStringAsync()).GetProperty("id").AsGuid();
        };
        await ForceDueAsync(dbPath, time, ids["capy"]);
        var queriesBeforeAdmission = torznab.SearchQueries;
        var runCapDuring = await Run();
        torznab.OnSearch = null;
        if (manualCapGrab is { } manualId)
            await admin.PostAsync($"/JellyfinMod/Grabs/{manualId}/Cancel", null);
        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runCapDuring.Id &&
                value.TargetId == ids["capy"]);
            Assert(manualCapGrab is not null && decision.Reason == AutomationReasons.TooManyOpenImports &&
                torznab.SearchQueries > queriesBeforeAdmission && !await database.GrabOperations.AnyAsync(value => value.EntryId == ids["capy"]),
                $"A grab admitted by hand during automation's search counts towards the import cap at automation's insert (Codex round 2 " +
                $"P2): manual grab {manualCapStatus}, decision {decision.Reason}/{decision.Detail}, {busyBefore} open before");
        }

        capSettings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(capSettings.GetProperty("revision").GetInt32(),
            enabled: true, batch: 3, grabs: 20)), 200, "The import cap is restored");

        // P2 3: the run step that made a grab cannot be saved; the grab is cancelled during its hold and never sent.
        await using (var trigger = new ModDbContext(dbPath))
            await trigger.Database.ExecuteSqlRawAsync("CREATE TRIGGER jfmod_fail_decision BEFORE INSERT ON AutomationDecisions " +
                "BEGIN SELECT RAISE(ABORT, 'injected: the run step fails'); END;");
        await ForceDueAsync(dbPath, time, ids["failsave"]);
        try
        {
            await Run();
        }
        catch (Exception error) when (error is DbUpdateException or InvalidOperationException)
        {
        }
        finally
        {
            await using var drop = new ModDbContext(dbPath);
            await drop.Database.ExecuteSqlRawAsync("DROP TRIGGER jfmod_fail_decision;");
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.EntryId == ids["failsave"]);
            Assert(grab is { State: GrabStates.Cancelled } && !transmission.Torrents.ContainsKey(fixtures["failsave"].InfoHash),
                $"A grab whose run step could not be saved is cancelled, never sent (whole-review c2cf3): {grab?.State}");
        }

        // P2 4: the library's mount cannot be measured (here, its root is briefly gone); nothing is searched or grabbed.
        var away = world.Movies.Location + "-away";
        await ForceDueAsync(dbPath, time, ids["nospace"]);
        var queriesBeforeSpace = torznab.SearchQueries;
        Directory.Move(world.Movies.Location, away);
        AutomationRun runSpace;
        try
        {
            runSpace = await Run();
        }
        finally
        {
            Directory.Move(away, world.Movies.Location);
        }

        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runSpace.Id &&
                value.TargetId == ids["nospace"]);
            Assert(decision.Reason == AutomationReasons.FreeSpaceFloor && torznab.SearchQueries == queriesBeforeSpace &&
                !await database.GrabOperations.AnyAsync(value => value.EntryId == ids["nospace"]),
                $"Free space that cannot be read stops automatic grabs (whole-review c2cf4): {decision.Reason} {decision.Detail}");
        }

        // Codex round 2 P2: the mount is measured once and found below the floor, and its root is gone by any later read.
        // The one measurement decides: the title is not grabbed on a list that no longer names the root.
        await ForceDueAsync(dbPath, time, ids["nospace"]);
        long floorBytes;
        await using (var database = new ModDbContext(dbPath))
        {
            floorBytes = (await database.AcquisitionSettings.AsNoTracking().SingleAsync()).FreeSpaceFloorBytes;
            await database.AcquisitionSettings.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.FreeSpaceFloorBytes, long.MaxValue / 2));
        }

        world.Native.VirtualFoldersHook = folders => Environment.StackTrace.Contains(".FreeSpace(", StringComparison.Ordinal)
            ? folders.Select(folder => new MediaBrowser.Model.Entities.VirtualFolderInfo { ItemId = folder.ItemId, Name = folder.Name,
                CollectionType = folder.CollectionType, Locations = [folder.Locations[0] + "-gone"] }).ToList()
            : folders;
        AutomationRun runRemeasured;
        try
        {
            runRemeasured = await Run();
        }
        finally
        {
            world.Native.VirtualFoldersHook = null;
            await using var database = new ModDbContext(dbPath);
            await database.AcquisitionSettings.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.FreeSpaceFloorBytes, floorBytes));
        }

        await using (var database = new ModDbContext(dbPath))
        {
            var decision = await database.AutomationDecisions.AsNoTracking().SingleAsync(value => value.RunId == runRemeasured.Id &&
                value.TargetId == ids["nospace"]);
            Assert(decision.Reason == AutomationReasons.FreeSpaceFloor &&
                !await database.GrabOperations.AnyAsync(value => value.EntryId == ids["nospace"]),
                $"A free-space check decides on one measurement of every root (Codex round 2 P2): {decision.Reason} {decision.Detail}");
        }

        // P2 7: an empty search backs the title off; switching it to another profile, or monitoring it again, starts afresh.
        await ForceDueAsync(dbPath, time, ids["switch"]);
        await Run();
        async Task<AutomationTargetState> SwitchRowAsync()
        {
            await using var database = new ModDbContext(dbPath);
            return await database.AutomationTargets.AsNoTracking().SingleAsync(value => value.TargetId == ids["switch"]);
        }

        var backedOff = await SwitchRowAsync();
        var other = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Other", qualities = new[] { "webdl-1080p", "webdl-720p" }
        }), 201, "Another profile");
        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["switch"]}", new { qualityProfileId = other.GetProperty("id").AsGuid() }),
            200, "The title switches profile");
        var switched = await SwitchRowAsync();
        await using (var database = new ModDbContext(dbPath))
        {
            var row = await database.AutomationTargets.SingleAsync(value => value.TargetId == ids["switch"]);
            row.ConsecutiveEmpty = 3;
            row.NextSearchAt = time.GetUtcNow().UtcDateTime.AddDays(7);
            await database.SaveChangesAsync();
        }

        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["switch"]}", new { monitored = false }), 200, "Unmonitor");
        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{ids["switch"]}", new { monitored = true }), 200, "Monitor again");
        var remonitored = await SwitchRowAsync();
        var nowShifted = time.GetUtcNow().UtcDateTime;
        Assert(backedOff.ConsecutiveEmpty == 1 && backedOff.NextSearchAt > nowShifted &&
            switched.ConsecutiveEmpty == 0 && switched.NextSearchAt <= nowShifted &&
            remonitored.ConsecutiveEmpty == 0 && remonitored.NextSearchAt <= nowShifted,
            $"Switching profile and monitoring again reset the backoff (whole-review c2cf7): backed off {backedOff.ConsecutiveEmpty}/" +
            $"{backedOff.NextSearchAt:O}, switched {switched.ConsecutiveEmpty}/{switched.NextSearchAt:O}, re-monitored " +
            $"{remonitored.ConsecutiveEmpty}/{remonitored.NextSearchAt:O}, now {nowShifted:O}");

        // Whole-review chunk 3b, P2 5: an administrator limited to TV reads no decision about a movie, and the targets of a
        // series leave out an episode a blocked tag hides.
        var tvAdmin = host.Client(world.RestrictedAdmin, true);
        var adminDecisions = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Automation/Decisions?limit=200"));
        var tvDecisions = Json.Parse(await tvAdmin.GetStringAsync("/JellyfinMod/Automation/Decisions?limit=200"));
        var movieIds = ids.Where(pair => pair.Key != "series" && !pair.Key.StartsWith('s')).Select(pair => pair.Value).ToHashSet();
        bool AboutMovie(JsonElement item) => item.GetProperty("entryId").ValueKind == JsonValueKind.String &&
            movieIds.Contains(item.GetProperty("entryId").AsGuid());
        Assert(adminDecisions.GetProperty("items").EnumerateArray().Any(AboutMovie) &&
            !tvDecisions.GetProperty("items").EnumerateArray().Any(AboutMovie) &&
            tvDecisions.GetProperty("totalRecordCount").GetInt32() < adminDecisions.GetProperty("totalRecordCount").GetInt32() &&
            (await tvAdmin.GetAsync($"/JellyfinMod/Automation/Targets?entryId={ids["m1"]}")).StatusCode == HttpStatusCode.NotFound,
            "A TV-only administrator reads no automation decision or target of a movie");
        var hiddenEpisodeItem = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Single(item => item.Path == episodeImport.DestinationPath);
        async Task<HashSet<Guid>> TargetIdsAsync() => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Automation/Targets?entryId={ids["series"]}"))
            .EnumerateArray().Select(item => item.GetProperty("targetId").AsGuid()).ToHashSet();
        async Task<int> EpisodeHistoryAsync() => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["series"]}"))
            .GetProperty("history").EnumerateArray().Count(item => item.GetProperty("episodeId").ValueKind == JsonValueKind.String &&
                item.GetProperty("episodeId").AsGuid() == ids["s1e2"]);
        var allTargets = await TargetIdsAsync();
        var episodeEvents = await EpisodeHistoryAsync();
        // Codex round 2 P2: blocklistings recorded before they carried an episode name only their operation. One names the
        // hidden episode's import; the other's operation is gone.
        await using (var database = new ModDbContext(dbPath))
        {
            database.History.AddRange(
                new HistoryRecord { EntryId = ids["series"], EventType = "blocklisted", Summary = "Blocklisted JfmodHiddenRelease",
                    Data = JsonSerializer.Serialize(new { operationId = episodeImport.Id, InfoHash = "aa" }) },
                new HistoryRecord { EntryId = ids["series"], EventType = "blocklisted", Summary = "Blocklisted JfmodGoneRelease",
                    Data = JsonSerializer.Serialize(new { operationId = Guid.NewGuid(), InfoHash = "bb" }) });
            // Codex delta review 2: a grab of this series detached from its episode when that row was deleted.
            var template = await database.GrabOperations.AsNoTracking().FirstAsync(value => value.EntryId == ids["seedy"]);
            var detachedGrab = new GrabOperation
            {
                RequestedBy = world.Admin.Id, IdempotencyKey = "review-detached", RequestFingerprint = "review", EntryId = ids["series"],
                SearchId = template.SearchId, ReleaseId = "review-detached", IndexerId = template.IndexerId, IndexerName = template.IndexerName,
                SourceGuid = "review-detached", RawTitle = "JfmodDetachedRelease", ProfileId = template.ProfileId,
                DownloadClientId = template.DownloadClientId, State = GrabStates.Cancelled, CreatedAt = time.GetUtcNow().UtcDateTime,
                UpdatedAt = time.GetUtcNow().UtcDateTime, HoldUntil = time.GetUtcNow().UtcDateTime
            };
            database.GrabOperations.Add(detachedGrab);
            database.History.Add(new HistoryRecord { EntryId = ids["series"], EventType = "grab_cancelled",
                Summary = "Cancelled grab of JfmodDetachedRelease", Data = JsonSerializer.Serialize(new { operationId = detachedGrab.Id }) });
            await database.SaveChangesAsync();
        }

        async Task<string[]> SeriesSummariesAsync() => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["series"]}"))
            .GetProperty("history").EnumerateArray().Select(item => item.GetProperty("summary").GetString() ?? string.Empty).ToArray();
        var visibleSummaries = await SeriesSummariesAsync();
        hiddenEpisodeItem.Tags = ["jfmod-review-hidden"];
        world.Admin.SetPreference(PreferenceKind.BlockedTags, ["jfmod-review-hidden"]);
        var taggedTargets = await TargetIdsAsync();
        var hiddenEpisodeEvents = await EpisodeHistoryAsync();
        var hiddenSummaries = await SeriesSummariesAsync();
        world.Admin.SetPreference(PreferenceKind.BlockedTags, []);
        hiddenEpisodeItem.Tags = [];
        Assert(visibleSummaries.Contains("Blocklisted JfmodHiddenRelease") && visibleSummaries.Contains("Blocklisted JfmodGoneRelease") &&
            visibleSummaries.Contains("Cancelled grab of JfmodDetachedRelease") &&
            !hiddenSummaries.Any(summary => summary.Contains("JfmodHiddenRelease", StringComparison.Ordinal) ||
                summary.Contains("JfmodGoneRelease", StringComparison.Ordinal) ||
                summary.Contains("JfmodDetachedRelease", StringComparison.Ordinal)) && hiddenSummaries.Contains("Blocklisted a release"),
            "An event naming only its operation follows that operation's episode, and one that cannot be placed names nothing " +
            "(Codex round 2 P2): " + string.Join(" | ", hiddenSummaries.Where(summary => summary.StartsWith("Blocklisted", StringComparison.Ordinal))));
        // Whole-review chunk 3a, P2 2: the title's history leaves out the hidden episode's import and grab events.
        Assert(episodeEvents > 0 && hiddenEpisodeEvents == 0,
            $"A title's history leaves out events about an episode hidden from the requester (whole-review c3af2): {episodeEvents} then {hiddenEpisodeEvents}");
        Assert(allTargets.Contains(ids["s1e2"]) && !taggedTargets.Contains(ids["s1e2"]) && taggedTargets.Contains(ids["s1e1"]),
            $"The targets of a series leave out an episode hidden from the administrator (whole-review c3bf5): {allTargets.Count} then {taggedTargets.Count}");

        // Whole-review chunk 3b, P2 3: two automation changes from the same revision at once; one is saved, the other is 409.
        for (var round = 0; round < 6; round++)
        {
            var current = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation")).GetProperty("revision").GetInt32();
            var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(variant => admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation",
                AutomationSettings(current, enabled: true, batch: 3, grabs: 20 + variant))));
            var codes = answers.Select(answer => (int)answer.StatusCode).OrderBy(code => code).ToArray();
            var after = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation")).GetProperty("revision").GetInt32();
            Assert(codes.SequenceEqual([200, 409]) && after == current + 1,
                $"Of two automation changes from revision {current} one is saved and the other answers 409 (whole-review c3bf3): " +
                $"{string.Join(",", codes)}, revision now {after}");
        }

        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", AutomationSettings(
            Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation")).GetProperty("revision").GetInt32(), enabled: true, batch: 3,
            grabs: 20)), 200, "Automation settings back to the run's values");

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

    private static object AutomationSettings(int revision, bool enabled, int batch = 40, int grabs = 6, long floorBytes = 0,
        int maxImports = 20) => new
    {
        automationEnabled = enabled, automationIntervalHours = 1, automationBatchSize = batch, newEpisodeDelayMinutes = 120,
        dailyAutoGrabBudget = grabs, maxConcurrentImports = maxImports, freeSpaceFloorPercent = 0, freeSpaceFloorBytes = floorBytes,
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
