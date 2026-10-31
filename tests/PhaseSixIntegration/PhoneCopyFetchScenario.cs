using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Automation;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Codex review of Phase 12 (P1, P2): automation's copy for phones goes all the way. A movie that holds only a 2160p WEB-DL,
/// whose own profile would upgrade it to a 2160p Remux that no indexer has, and a season of two 2160p episodes, with "Also get a
/// 1080p copy for phones" on and episode upgrades off: one run grabs the movie's 1080p and the season's 1080p pack as Add; the
/// hold ends and both reach the download client; both are imported beside the files held, which stay byte for byte; the copy
/// for phones is no longer missing. Codex re-review of Phase 12 (P2): a second movie whose Remux exists but no release fits a
/// phone has its upgrade searched and grabbed in the same run after the empty search for phones; and the first episode, unmonitored
/// while the pack's client lookup is under way, is dropped from the pack, so only the second episode gets the extra version.
/// Then that episode's copy, named without its resolution, is removed with Remove This Version, and the next run never fetches
/// it again (phone_copy_removed). The Phase 5 host (real Kestrel, auth, MVC, EF migrations,
/// SQLite) with a Torznab and a Transmission boundary that downloads real files, real hardlinks and the production runner,
/// dispatcher and import monitor.
/// </summary>
internal static class PhoneCopyFetchScenario
{
    private const long Size = 200_000;

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "fetch-media");
        foreach (var directory in new[] { "movies", "tv", "downloads/jfmod" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var downloads = Path.Combine(media, "downloads", "jfmod");
        var movies = new TestLibrary { Id = Guid.NewGuid(), Name = "Movies", CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") };
        var tv = new TestLibrary { Id = Guid.NewGuid(), Name = "TV", CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") };
        var native = new NativeWorld { Libraries = [movies, tv], UserLibraries = _ => [movies, tv] };
        var previousLibrary = BaseItem.LibraryManager;
        BaseItem.LibraryManager = native.Library;
        var world = new World
        {
            Admin = new User("pfadmin", "auth", "reset") { Id = Guid.NewGuid() }, RestrictedAdmin = new User("pftv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("pfviewer", "auth", "reset") { Id = Guid.NewGuid() }, MoviesOnly = new User("pfmovies", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = movies, Tv = tv, Far = movies, Folder = folder, Native = native
        };

        var dbPath = Path.Combine(folder, "fetch.db");
        Guid movieId, upgradeMovieId, seriesId, e1, e2;
        var now = DateTime.UtcNow;
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Database.MigrateAsync();
            var movie = new Entry
            {
                MediaType = "movie", TmdbId = 1401, Title = "Phone Fetch", Year = 2021, ImdbId = "tt0001401", TargetLibraryId = movies.Id, Monitored = true,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 1401, "Phone Fetch", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                    null, null, "tt0001401", null, false, null, 100, [], [], []))
            };
            var series = new Entry
            {
                MediaType = "series", TmdbId = 1450, Title = "Phone Pack", Year = 2022, TargetLibraryId = tv.Id, Monitored = true,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 1450, "Phone Pack", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                    null, null, null, 7450, false, null, 45, [], [], [new TmdbSeason(1, "Season 1", 2, null, null)]))
            };
            // A movie whose Remux is out but no release fits a phone (Codex re-review, P2).
            var upgradeMovie = new Entry
            {
                MediaType = "movie", TmdbId = 1402, Title = "Phone Upgrade", Year = 2021, ImdbId = "tt0001402", TargetLibraryId = movies.Id, Monitored = true,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 1402, "Phone Upgrade", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                    null, null, "tt0001402", null, false, null, 100, [], [], []))
            };
            database.Entries.AddRange(movie, upgradeMovie, series);
            movieId = movie.Id;
            upgradeMovieId = upgradeMovie.Id;
            seriesId = series.Id;
            var episodes = new[] { 1, 2 }.Select(number => new Episode
            {
                EntryId = series.Id, TmdbId = 14500 + number, SeasonNumber = 1, EpisodeNumber = number, Title = "E" + number,
                AirDate = now.AddDays(-60 + number), RuntimeMinutes = 45, Monitored = true
            }).ToArray();
            database.Episodes.AddRange(episodes);
            (e1, e2) = (episodes[0].Id, episodes[1].Id);
            await database.SaveChangesAsync();
        }

        // The files held: a 2160p movie and two 2160p episodes, real files that Jellyfin's items point at.
        var movieFolder = Path.Combine(movies.Location, "Phone Fetch (2021)");
        Directory.CreateDirectory(movieFolder);
        var movieFile = Path.Combine(movieFolder, "Phone Fetch (2021) - 2160p WEB-DL.mkv");
        await File.WriteAllBytesAsync(movieFile, Enumerable.Repeat((byte)4, 8192).ToArray());
        var upgradeFolder = Path.Combine(movies.Location, "Phone Upgrade (2021)");
        Directory.CreateDirectory(upgradeFolder);
        var upgradeFile = Path.Combine(upgradeFolder, "Phone Upgrade (2021) - 2160p WEB-DL.mkv");
        await File.WriteAllBytesAsync(upgradeFile, Enumerable.Repeat((byte)6, 8192).ToArray());
        var seasonFolder = Path.Combine(tv.Location, "Phone Pack (2022) [tmdbid-1450]", "Season 01");
        Directory.CreateDirectory(seasonFolder);
        var episodeFiles = new[] { 1, 2 }.Select(number => Path.Combine(seasonFolder, $"Phone Pack (2022) S01E0{number} - 2160p WEB-DL.mkv")).ToArray();
        foreach (var file in episodeFiles) await File.WriteAllBytesAsync(file, Enumerable.Repeat((byte)5, 8192).ToArray());
        native.EpisodeTmdbId = file => file.Contains("S01E01", StringComparison.Ordinal) ? 14501 : file.Contains("S01E02", StringComparison.Ordinal) ? 14502 : null;
        var held = new[] { movieFile, upgradeFile }.Concat(episodeFiles).ToDictionary(path => path, Identity);

        await using var torznab = new TorznabBoundary();
        await using var transmission = new TransmissionBoundary();
        await torznab.StartAsync();
        await transmission.StartAsync();
        transmission.Roots[downloads] = downloads;
        var movieRelease = TorrentFixture.Single("Phone.Fetch.2021.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size);
        torznab.Torrents["movie1080"] = movieRelease.Bytes;
        transmission.Register(movieRelease);
        torznab.MovieItems.Add(new("Phone.Fetch.2021.1080p.WEB-DL.DDP5.1.H.264-GRP", "guid-movie1080", torznab.Download("movie1080"), Size, 30,
            new Dictionary<string, string> { ["imdbid"] = "0001401" }));
        // The Remux the second movie's profile wants; never completed here, so nothing replaces its file.
        var remuxRelease = TorrentFixture.Single("Phone.Upgrade.2021.2160p.BluRay.REMUX.HEVC.DTS-HD.MA.5.1-GRP.mkv", Size);
        torznab.Torrents["remux"] = remuxRelease.Bytes;
        transmission.Register(remuxRelease);
        torznab.MovieItems.Add(new("Phone.Upgrade.2021.2160p.BluRay.REMUX.HEVC.DTS-HD.MA.5.1-GRP", "guid-remux", torznab.Download("remux"), Size, 30,
            new Dictionary<string, string> { ["imdbid"] = "0001402" }));
        var pack = TorrentFixture.Multi("Phone.Pack.S01.1080p.WEB-DL.DDP5.1.H.264-GRP",
            ("Phone.Pack.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size), ("Phone.Pack.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size));
        torznab.Torrents["pack1080"] = pack.Bytes;
        transmission.Register(pack);
        torznab.TvItems.Add(new("Phone.Pack.S01.1080p.WEB-DL.DDP5.1.H.264-GRP", "guid-pack1080", torznab.Download("pack1080"), Size * 2, 30,
            new Dictionary<string, string> { ["tvdbid"] = "7450" }));

        var time = new ShiftedTimeProvider();
        var host = await PluginHost.StartAsync(world, dbPath, time, logs, new PluginConfiguration());
        try
        {
            var admin = host.Client(world.Admin, true);
            async Task Tick() => await host.Service<ImportMonitor>().TickOnceAsync(CancellationToken.None);
            async Task<AutomationRun> Run()
            {
                using var scope = host.App.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<AutomationRunner>().RunAsync("manual", CancellationToken.None)
                    ?? throw new InvalidOperationException("A manual run always runs");
            }

            native.AddMovie(movies, movieFile, 1401);
            native.AddMovie(movies, upgradeFile, 1402);
            native.Scan(Path.GetDirectoryName(seasonFolder)!);
            await WaitAsync(async () =>
            {
                await using var database = new ModDbContext(dbPath);
                return await database.EntryBindings.AnyAsync(value => value.EntryId == movieId) &&
                    await database.EntryBindings.AnyAsync(value => value.EntryId == upgradeMovieId) &&
                    await database.EpisodeBindings.CountAsync(value => value.EpisodeId == e1 || value.EpisodeId == e2) == 2 &&
                    await database.Episodes.CountAsync(value => (value.Id == e1 || value.Id == e2) && value.State == FileState.OnDisk) == 2 &&
                    (await database.Entries.SingleAsync(value => value.Id == movieId)).State == FileState.OnDisk ? "ok" : null;
            }, "The movie's and the episodes' 2160p files are bound");

            var any = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Any", qualities = new[] { "webdl-2160p", "webdl-1080p", "webdl-720p" }
            }), 201, "Profile Any");
            var phones = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Phones", qualities = new[] { "webdl-1080p", "webdl-720p" }, prefer = "everywhere"
            }), 201, "Profile for phones");
            // The movie's own profile wants a Remux that no indexer has: before the fix, its upgrade kept the copy for phones
            // from ever being searched (Codex review, P2).
            var remux = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
            {
                name = "Upgrade to Remux", qualities = new[] { "remux-2160p", "webdl-2160p" }, cutoff = "remux-2160p", upgradeAllowed = true,
                upgradeMode = "replace"
            }), 201, "A profile that upgrades to a Remux");
            await using (var database = new ModDbContext(dbPath))
            {
                foreach (var entry in await database.Entries.Where(value => value.Id == movieId || value.Id == upgradeMovieId).ToListAsync())
                    entry.QualityProfileId = remux.GetProperty("id").AsGuid();
                await database.SaveChangesAsync();
            }

            var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Boundary", baseUrl = new Uri(torznab.Address, "/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
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
            await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = true, downloadClientId = client.GetProperty("id").AsGuid(), defaultQualityProfileId = any.GetProperty("id").AsGuid(),
                phoneProfileId = phones.GetProperty("id").AsGuid(), revision = acquisition.GetProperty("revision").GetInt32()
            }), 200, "Acquisition enabled with a quality profile for phones");
            var automation = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Automation"), 200, "Automation settings");
            automation = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation",
                Settings(automation.GetProperty("revision").GetInt32())), 200, "Automation on, copies for phones on, episode upgrades off");
            Assert(automation.GetProperty("fetchPhoneCopies").GetBoolean() && !automation.GetProperty("episodeUpgradesEnabled").GetBoolean(),
                "The switches are as the scenario needs them");

            // ---- One run grabs both copies for phones, and the second movie's upgrade. The first episode is unmonitored while the
            // pack's client lookup is under way, after the first check of the hold and before the last.
            var unmonitoredDuringLookup = false;
            transmission.AfterGet = async asked =>
            {
                if (asked is not { Count: 1 } || !asked.Contains(pack.InfoHash)) return;
                transmission.AfterGet = null;
                using var unmonitor = await admin.PatchAsJsonAsync($"/JellyfinMod/Entries/{seriesId}/Episodes/{e1}", new { monitored = false });
                unmonitoredDuringLookup = unmonitor.IsSuccessStatusCode;
            };
            var run = await Run();
            GrabOperation movieGrab, packGrab;
            await using (var database = new ModDbContext(dbPath))
            {
                var grabs = await database.GrabOperations.AsNoTracking().ToListAsync();
                var decisions = await database.AutomationDecisions.AsNoTracking().Where(decision => decision.RunId == run.Id).ToListAsync();
                string Report() => string.Join("; ", grabs.Select(grab => $"{grab.EntryId} {grab.Scope} {grab.Mode} {grab.State} {grab.RawTitle}")) + " / " +
                    string.Join("; ", decisions.Select(decision => $"{decision.EntryId}/{decision.EpisodeId} {decision.Reason} {decision.Detail}"));
                movieGrab = grabs.SingleOrDefault(grab => grab.EntryId == movieId) ?? throw new InvalidOperationException(
                    "The movie whose upgrade finds no release still gets its copy for phones searched and grabbed: " + Report());
                packGrab = grabs.SingleOrDefault(grab => grab.EntryId == seriesId) ?? throw new InvalidOperationException("The season's pack for phones is grabbed: " + Report());
                Assert(movieGrab.Automatic && movieGrab.Mode == GrabModes.Add && movieGrab.UpgradeOperationId is null &&
                    movieGrab.ProfileId == phones.GetProperty("id").AsGuid() && packGrab.Scope == "season" && packGrab.Mode == GrabModes.Add,
                    "Both are automatic Adds with the profile for phones: " + Report());
                // Codex re-review, P2: no release fits the second movie's phones, and its upgrade is searched in the same run.
                var upgradeGrab = grabs.SingleOrDefault(grab => grab.EntryId == upgradeMovieId);
                Assert(decisions.Any(decision => decision.EntryId == upgradeMovieId && decision.Reason == AutomationReasons.NoEligibleCandidate) &&
                    upgradeGrab is { Automatic: true, UpgradeOperationId: not null } && upgradeGrab.RawTitle.Contains("REMUX", StringComparison.Ordinal),
                    "A movie whose copy for phones finds no release still has its upgrade searched and grabbed in the same run: " + Report());
            }

            // ---- The hold ends: both reach the download client (before the fix, dispatch refused them as no_longer_wanted).
            await WaitAsync(async () =>
            {
                await using var database = new ModDbContext(dbPath);
                var states = await database.GrabOperations.AsNoTracking().Where(grab => grab.Id == movieGrab.Id || grab.Id == packGrab.Id)
                    .Select(grab => new { grab.State, grab.FailureCode }).ToListAsync();
                if (states.Any(state => state.State == GrabStates.Failed))
                    throw new InvalidOperationException("A copy for phones failed at dispatch: " + string.Join(", ", states.Select(state => $"{state.State} {state.FailureCode}")));
                return transmission.Torrents.ContainsKey(movieRelease.InfoHash) && transmission.Torrents.ContainsKey(pack.InfoHash) ? "ok" : null;
            }, "Both copies for phones are submitted to the client");
            transmission.AfterGet = null;
            Assert(unmonitoredDuringLookup, "The first episode was unmonitored during the pack's client lookup");
            transmission.Progress(movieRelease.InfoHash, 1.0);
            transmission.Progress(pack.InfoHash, 1.0);
            var imports = await WaitAsync(async () =>
            {
                await Tick();
                await using var database = new ModDbContext(dbPath);
                var done = await database.ImportOperations.AsNoTracking().Where(operation => (operation.GrabId == movieGrab.Id || operation.GrabId == packGrab.Id) &&
                    operation.State == ImportStates.Completed).ToListAsync();
                return done.Count == 2 && done.Any(operation => operation.EpisodeId == e2) ? done : null;
            }, "The movie's copy and the second episode's copy are imported", 90);
            await using (var database = new ModDbContext(dbPath))
                Assert(!await database.ImportOperations.AnyAsync(operation => operation.GrabId == packGrab.Id && operation.EpisodeId == e1) &&
                    !await database.GrabClaims.AnyAsync(claim => claim.GrabId == packGrab.Id && claim.EpisodeId == e1),
                    "The episode unmonitored during the hold is dropped from the pack: no claim, no import (Codex re-review, P2)");

            // ---- Each title holds both files; the files held before are untouched.
            foreach (var (path, before) in held)
                Assert(File.Exists(path) && Identity(path) == before, $"{Path.GetFileName(path)} stays as it was: {before} → {(File.Exists(path) ? Identity(path) : "gone")}");
            var movieDetail = JsonDocument.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{movieId}")).RootElement;
            var movieVersions = movieDetail.GetProperty("versions").EnumerateArray().ToArray();
            string Rows(IEnumerable<JsonElement> rows) => string.Join("; ", rows.Select(row => $"{row.GetProperty("resolution")} {row.GetProperty("label")} phone={row.GetProperty("phoneCopy")}"));
            Assert(movieVersions.Length == 2 && movieVersions.Count(row => row.GetProperty("phoneCopy").GetBoolean()) == 1 &&
                movieVersions.Single(row => row.GetProperty("phoneCopy").GetBoolean()).GetProperty("resolution").GetString() == "1080p" &&
                !movieDetail.GetProperty("phoneCopyMissing").GetBoolean(),
                "The movie holds its 2160p and the 1080p for phones: " + Rows(movieVersions));
            var seriesDetail = JsonDocument.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{seriesId}")).RootElement;
            var episodeRows = seriesDetail.GetProperty("episodes").EnumerateArray().Where(row => row.GetProperty("seasonNumber").GetInt32() == 1).ToArray();
            var e1Row = episodeRows.Single(row => row.GetProperty("id").AsGuid() == e1);
            var e2Row = episodeRows.Single(row => row.GetProperty("id").AsGuid() == e2);
            Assert(e1Row.GetProperty("versions").GetArrayLength() == 1 && e2Row.GetProperty("versions").GetArrayLength() == 2 &&
                !e2Row.GetProperty("phoneCopyMissing").GetBoolean(),
                "The unmonitored episode holds only its 2160p, the other its 2160p and the pack's 1080p: " +
                string.Join(" | ", episodeRows.Select(row => Rows(row.GetProperty("versions").EnumerateArray()))));
            var e2Import = imports.Single(operation => operation.EpisodeId == e2);
            Assert(VersionQuality.Parse(e2Import.DestinationPath).Resolution is null,
                $"An episode's lower copy is named without its resolution: {Path.GetFileName(e2Import.DestinationPath)}");

            // ---- Remove This Version on that copy: the next run never fetches it again (Codex review, P2).
            Guid e2Copy;
            await using (var database = new ModDbContext(dbPath))
                e2Copy = await database.EpisodeBindings.AsNoTracking().Where(value => value.EpisodeId == e2 && value.MediaPath == e2Import.DestinationPath)
                    .Select(value => value.Id).SingleAsync();
            await ReadAsync(await admin.PostAsync($"/JellyfinMod/Entries/{seriesId}/Versions/{e2Copy}/Remove", null), 200, "Remove the episode's copy for phones");
            Assert(!File.Exists(e2Import.DestinationPath) && File.Exists(episodeFiles[1]), "Only the removed copy is gone; the episode's 2160p stays");
            await using (var database = new ModDbContext(dbPath))
            {
                foreach (var row in await database.AutomationTargets.ToListAsync()) row.NextSearchAt = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
                await database.SaveChangesAsync();
            }

            var grabsBefore = 0;
            await using (var database = new ModDbContext(dbPath))
                grabsBefore = await database.GrabOperations.CountAsync();
            var again = await Run();
            await using (var database = new ModDbContext(dbPath))
            {
                var decisions = await database.AutomationDecisions.AsNoTracking().Where(decision => decision.RunId == again.Id).ToListAsync();
                Assert(decisions.Any(decision => decision.EpisodeId == e2 && decision.Reason == AutomationReasons.PhoneCopyRemoved) &&
                    await database.GrabOperations.CountAsync() == grabsBefore,
                    "A removed copy for phones named without its resolution is never fetched again: " +
                    string.Join("; ", decisions.Select(decision => $"{decision.EntryId}/{decision.EpisodeId} {decision.Reason}")));
            }
        }
        finally
        {
            await host.DisposeAsync();
            BaseItem.LibraryManager = previousLibrary;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        Console.WriteLine("PASS: phase 12 copies for phones fetched end to end: dispatched, imported beside the files held, an unfound one not " +
            "starving the upgrade, an episode unmonitored in the hold dropped from the pack, a removed one never again");
    }

    /// <summary>A file's content and dates, to show a held file was not touched: its SHA-256, size and modification time.</summary>
    private static string Identity(string path)
    {
        var info = new FileInfo(path);
        return $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16]}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private static object Settings(int revision) => new
    {
        automationEnabled = true, automationIntervalHours = 1, automationBatchSize = 40, newEpisodeDelayMinutes = 120, dailyAutoGrabBudget = 6,
        maxConcurrentImports = 20, freeSpaceFloorPercent = 0, freeSpaceFloorBytes = 0L, decisionLogCap = 2000, episodeUpgradesEnabled = false,
        reacquireReclaimed = false, fetchPhoneCopies = true, revision
    };

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, string message, int seconds = 30) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(100);
        }

        throw new InvalidOperationException("Timed out: " + message);
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
