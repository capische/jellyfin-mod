using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

// Phase 5 import integration: a real Kestrel plugin host with authentication, authorization, MVC serialization, EF
// migrations and SQLite; a real HTTP Transmission RPC boundary that downloads by writing real files; a real Torznab
// boundary; and real hardlinks on the container's filesystems, including a second filesystem for EXDEV. The Jellyfin
// library manager and monitor are the one simulated host boundary; everything behind them is production code.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-five-" + Guid.NewGuid().ToString("N"));
var far = Path.Combine("/dev/shm", "jfmod-phase-five-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
Directory.CreateDirectory(far);
var logs = new CapturingLoggerProvider();
var secrets = new List<string>();
try
{
    try
    {
        await Phase5.RunAsync(folder, far, logs, secrets);
    }
    catch
    {
        // Plugin warnings and errors explain a failed step; secrets are asserted absent from these lines on success.
        foreach (var line in logs.Lines.Where(line => line.StartsWith("Warning", StringComparison.Ordinal) ||
                     line.StartsWith("Error", StringComparison.Ordinal)).TakeLast(40))
            Console.Error.WriteLine(line.Length > 600 ? line[..600] : line);
        throw;
    }

    foreach (var secret in secrets)
        Phase5.Assert(!logs.Lines.Any(line => line.Contains(secret, StringComparison.Ordinal)), "Logs never contain a credential");
    Console.WriteLine($"PASS: Phase 5 import, hardlinks, targeted scan, binding attribution, seed release, queue and recovery ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
    Directory.Delete(far, true);
}

internal static partial class Phase5
{
    private const long Size = 300_000;

    public static async Task RunAsync(string folder, string far, CapturingLoggerProvider logs, List<string> secrets)
    {
        var media = Path.Combine(folder, "media");
        foreach (var directory in new[] { "movies", "tv", "downloads/jfmod", "downloads/unmapped", "movies/inside" })
            Directory.CreateDirectory(Path.Combine(media, directory));
        Directory.CreateDirectory(Path.Combine(far, "library"));
        Directory.CreateDirectory(Path.Combine(far, "downloads", "jfmod"));
        var movies = new TestLibrary { Id = Guid.NewGuid(), Name = "Movies", CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") };
        var tv = new TestLibrary { Id = Guid.NewGuid(), Name = "TV", CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") };
        var farLibrary = new TestLibrary { Id = Guid.NewGuid(), Name = "Far", CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(far, "library") };
        var admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
        var tvAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() };
        var ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() };
        var moviesOnly = new User("moviefan", "auth", "reset") { Id = Guid.NewGuid() };
        var native = new NativeWorld
        {
            Libraries = [movies, tv, farLibrary],
            UserLibraries = userId => userId == admin.Id || userId == ordinary.Id ? [movies, tv] :
                userId == tvAdmin.Id ? [tv] : userId == moviesOnly.Id ? [movies] : []
        };
        BaseItem.LibraryManager = native.Library;
        var world = new World
        {
            Admin = admin, RestrictedAdmin = tvAdmin, Ordinary = ordinary, MoviesOnly = moviesOnly, Movies = movies, Tv = tv, Far = farLibrary,
            Folder = folder, Native = native
        };

        // ---- I2 migration: a Phase 4 database upgrades with its catalog and settings intact and documented defaults.
        var dbPath = Path.Combine(folder, "jellyfinmod.db");
        var ids = new Dictionary<string, Guid>();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.GetService<IMigrator>().MigrateAsync("20260919071544_PhaseFourAcquisition");
            // Written with SQL: the current model already has the Phase 5 columns the old schema lacks.
            var before = Metadata("movie", 999, "Before upgrade", 2020, "tt0000999", null);
            var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                VALUES ({Guid.NewGuid()}, 'movie', 999, 'tt0000999', 'Before upgrade', 2020, {before}, 0, 1, {added}, {movies.Id}, 0)
                """);
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AcquisitionSettings (Id, Enabled, Revision) VALUES ({AcquisitionSettings.SingletonId}, 0, 1)
                """);
            await database.Database.MigrateAsync();
            var applied = (await database.Database.GetAppliedMigrationsAsync()).ToList();
            Assert(applied.FindIndex(name => name.EndsWith("_PhaseFiveImport", StringComparison.Ordinal)) >
                applied.IndexOf("20260919071544_PhaseFourAcquisition") && applied.Contains("20260919071544_PhaseFourAcquisition"),
                "The Phase 5 migration applies after the Phase 4 migration");
            var upgraded = await database.AcquisitionSettings.AsNoTracking().SingleAsync();
            Assert(upgraded.ImportEnabled && !upgraded.SeedReleaseEnabled && upgraded.ImportPollSeconds == 15 && upgraded.SeedFloorRatio == 1.0 &&
                upgraded.SeedFloorHours == 168 && upgraded.StalledAfterHours == 24 && upgraded.ScanTimeoutMinutes == 10 &&
                !upgraded.QueueVisibleToUsers && upgraded.ImportRevision == 1 && upgraded.VideoExtensions.Contains("mkv", StringComparison.Ordinal),
                "An existing settings row gains the documented import defaults, with seed release off");
            Assert(await database.Entries.CountAsync() == 1, "Entries survive the upgrade");
            Assert(await Scalar(database, "PRAGMA integrity_check") == "ok" && await Scalar(database, "PRAGMA foreign_key_check") is null,
                "The upgraded database passes integrity and foreign-key checks");
            AddEntry(database, ids, "movieA", "movie", 100, "Example Movie", 2024, "tt0000100", null, movies.Id);
            AddEntry(database, ids, "movieB", "movie", 101, "Second Movie", 2023, "tt0000101", null, movies.Id);
            AddEntry(database, ids, "movieC", "movie", 102, "Third Movie", 2022, "tt0000102", null, movies.Id);
            AddEntry(database, ids, "movieD", "movie", 110, "Cross Movie", 2021, "tt0000110", null, movies.Id);
            AddEntry(database, ids, "movieE", "movie", 111, "Unmapped Movie", 2021, "tt0000111", null, movies.Id);
            AddEntry(database, ids, "movieF", "movie", 112, "Ambiguous Movie", 2021, "tt0000112", null, movies.Id);
            AddEntry(database, ids, "movieG", "movie", 113, "Archive Movie", 2021, "tt0000113", null, movies.Id);
            AddEntry(database, ids, "movieH", "movie", 114, "Sample Movie", 2021, "tt0000114", null, movies.Id);
            AddEntry(database, ids, "movieI", "movie", 115, "Crash Movie", 2021, "tt0000115", null, movies.Id);
            AddEntry(database, ids, "movieJ", "movie", 116, "Slow Scan Movie", 2021, "tt0000116", null, movies.Id);
            AddEntry(database, ids, "movieK", "movie", 117, "Nothing Movie", 2021, "tt0000117", null, movies.Id);
            AddEntry(database, ids, "series", "series", 200, "Example Show", 2023, null, 300, tv.Id);
            for (var number = 1; number <= 5; number++)
            {
                var episode = new Episode { EntryId = ids["series"], TmdbId = 2000 + number, SeasonNumber = 1, EpisodeNumber = number,
                    Title = "Episode " + number, RuntimeMinutes = 45, AirDate = new DateTime(2023, 1, number, 0, 0, 0, DateTimeKind.Utc) };
                database.Episodes.Add(episode);
                ids["e" + number] = episode.Id;
            }

            await database.SaveChangesAsync();
        }

        await using var torznab = new TorznabBoundary();
        await using var transmission = new TransmissionBoundary();
        await torznab.StartAsync();
        await transmission.StartAsync();
        secrets.Add(transmission.Password);
        secrets.Add(torznab.ApiKey);
        transmission.Roots["/data/torrents"] = Path.Combine(media, "downloads");
        transmission.Roots["/other/torrents"] = Path.Combine(far, "downloads");
        transmission.Roots["/unmapped"] = Path.Combine(media, "downloads", "unmapped");

        var fixtures = new Dictionary<string, TorrentFixture>
        {
            ["movieA"] = TorrentFixture.Single("Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size),
            ["movieB"] = TorrentFixture.Single("Second.Movie.2023.1080p.BluRay.x264-GRP.mkv", Size),
            ["movieC"] = TorrentFixture.Single("Third.Movie.2022.2160p.WEB-DL.DDP5.1.HEVC-GRP.mkv", Size),
            ["movieD"] = TorrentFixture.Single("Cross.Movie.2021.1080p.WEB-DL-GRP.mkv", Size),
            ["movieE"] = TorrentFixture.Single("Unmapped.Movie.2021.1080p.WEB-DL-GRP.mkv", Size),
            ["movieF"] = TorrentFixture.Multi("Ambiguous.Movie.2021.1080p.WEB-DL-GRP", ("part.one.mkv", Size), ("part.two.mkv", Size - 1000)),
            ["movieG"] = TorrentFixture.Multi("Archive.Movie.2021.1080p.WEB-DL-GRP", ("archive.rar", Size), ("archive.r00", Size), ("archive.nfo", 100)),
            ["movieH"] = TorrentFixture.Multi("Sample.Movie.2021.1080p.WEB-DL-GRP", ("Sample.Movie.2021.1080p.WEB-DL-GRP.mkv", Size),
                ("Sample/sample.mkv", 20_000), ("Sample.Movie.2021.nfo", 100)),
            ["movieI"] = TorrentFixture.Single("Crash.Movie.2021.1080p.WEB-DL-GRP.mkv", Size),
            ["movieJ"] = TorrentFixture.Single("Slow.Scan.Movie.2021.1080p.WEB-DL-GRP.mkv", Size),
            ["movieK"] = TorrentFixture.Multi("Nothing.Movie.2021.1080p.WEB-DL-GRP", ("readme.txt", 100), ("Nothing.Movie.2021.nfo", 100)),
            ["e2"] = TorrentFixture.Single("Example.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size),
            ["e3"] = TorrentFixture.Multi("Example.Show.S01E03.1080p.WEB-DL-GRP", ("Example.Show.S01E04.1080p.WEB-DL-GRP.mkv", Size)),
            ["e1"] = TorrentFixture.Single("Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size),
            ["e5"] = TorrentFixture.Single("Example.Show.S01E05.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", Size)
        };
        var imdb = new Dictionary<string, string>
        {
            ["movieA"] = "0000100", ["movieB"] = "0000101", ["movieC"] = "0000102", ["movieD"] = "0000110", ["movieE"] = "0000111",
            ["movieF"] = "0000112", ["movieG"] = "0000113", ["movieH"] = "0000114", ["movieI"] = "0000115", ["movieJ"] = "0000116",
            ["movieK"] = "0000117"
        };
        foreach (var (key, fixture) in fixtures)
        {
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            var title = fixture.Name.EndsWith(".mkv", StringComparison.Ordinal) ? fixture.Name[..^4] : fixture.Name;
            if (key.StartsWith("movie", StringComparison.Ordinal))
                torznab.MovieItems.Add(new(title, "guid-" + key, torznab.Download(key), fixture.Files.Sum(file => file.Length), 25,
                    new() { ["imdbid"] = imdb[key] }));
            else torznab.TvItems.Add(new(title, "guid-" + key, torznab.Download(key), Size, 25, new() { ["tvdbid"] = "300" }));
        }

        var time = new ShiftedTimeProvider();
        var configuration = new PluginConfiguration
        {
            RetentionEnabled = true, ReclaimAfterDays = 1, RetentionWatchedUserMode = WatchedUserMode.AnyUser, ExemptFavourites = true
        };
        var box = new HostBox { Current = await PluginHost.StartAsync(world, dbPath, time, logs, configuration) };
        try
        {
            await RunScenariosAsync(box, world, dbPath, time, logs, configuration, torznab, transmission, fixtures, ids, media, far);
        }
        finally
        {
            if (box.Current is { } running) await running.DisposeAsync();
        }
    }

    /// <summary>The host currently running; restarts replace it so a failure never disposes a stopped host twice.</summary>
    private sealed class HostBox
    {
        public PluginHost? Current { get; set; }
    }

    private static async Task RunScenariosAsync(HostBox box, World world, string dbPath, ShiftedTimeProvider time,
        CapturingLoggerProvider logs, PluginConfiguration configuration, TorznabBoundary torznab, TransmissionBoundary transmission,
        Dictionary<string, TorrentFixture> fixtures, Dictionary<string, Guid> ids, string media, string far)
    {
        var host = box.Current!;
        async Task RestartAsync()
        {
            box.Current = null;
            await host.DisposeAsync();
            host = box.Current = await PluginHost.StartAsync(world, dbPath, time, logs, configuration);
        }

        async Task StopAsync()
        {
            box.Current = null;
            await host.DisposeAsync();
        }

        async Task StartAsync() => host = box.Current = await PluginHost.StartAsync(world, dbPath, time, logs, configuration);

        var admin = host.Client(world.Admin, true);
        var ordinary = host.Client(world.Ordinary, false);
        var anonymous = host.Client(null, false);
        var moviesOnly = host.Client(world.MoviesOnly, false);
        var tvAdmin = host.Client(world.RestrictedAdmin, true);

        // ---- Health gates the web on capability names.
        var health = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Health"));
        Assert(Strings(health.GetProperty("Capabilities")).Intersect(["queue", "import", "seedRelease"]).Count() == 3,
            "Health advertises queue, import and seedRelease");

        // ---- I2/I7 authorization: the queue and import settings are administrator-only by default.
        Assert((await anonymous.GetAsync("/JellyfinMod/Queue")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous queue reads are refused");
        await ExpectAsync(ordinary.GetAsync("/JellyfinMod/Queue"), 403, "queue_admin_only", "Ordinary users do not see the queue by default");
        Assert((await ordinary.GetAsync("/JellyfinMod/Settings/Import")).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot read import settings");
        Assert((await anonymous.GetAsync("/JellyfinMod/Settings/Import")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous settings reads are refused");
        Assert((await ordinary.GetAsync("/JellyfinMod/Seeding")).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot read seeding");

        var importSettings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Import"));
        Assert(importSettings.GetProperty("importEnabled").GetBoolean() && !importSettings.GetProperty("seedReleaseEnabled").GetBoolean() &&
            importSettings.GetProperty("importPollSeconds").GetInt32() == 15, "Import settings read back their defaults");
        await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(1, ["mkv", "rar"])), 400,
            "invalid_video_extensions", "Archive extensions cannot be made importable");
        await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(9)), 409, "revision_conflict",
            "A stale import settings revision is refused");
        Assert((await ordinary.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(1))).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot change import settings");
        var saved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(1)), 200,
            "Administrator saves import settings");
        Assert(saved.GetProperty("importPollSeconds").GetInt32() == 1 && saved.GetProperty("seedFloorRatio").GetDouble() == 1.0 &&
            saved.GetProperty("seedFloorHours").ValueKind == JsonValueKind.Null && saved.GetProperty("revision").GetInt32() == 2,
            "Import settings persist the floor and cadence");

        // ---- Phase 4 configuration: profile, indexer, Transmission client (user decision 1) and enablement.
        var profile = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "Any HD", qualities = new[] { "webdl-2160p", "bluray-1080p", "webdl-1080p", "webrip-1080p", "webdl-720p" }
        }), 201, "Administrator creates a profile");
        var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Boundary", baseUrl = new Uri(torznab.Address, "/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
            apiKey = new { action = "replace", value = torznab.ApiKey }
        }), 201, "Administrator creates an indexer");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200, "Indexer test");
        var client = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", new
        {
            name = "Transmission (isolated)", kind = "transmission", baseUrl = transmission.Endpoint.ToString(), username = transmission.Username,
            password = new { action = "replace", value = transmission.Password }, enabled = true, label = "jellyfinmod-test",
            downloadDirectory = "/data/torrents/jfmod", localDirectory = Path.Combine(media, "downloads", "jfmod"),
            openUrl = "http://127.0.0.1:9/transmission/web/"
        }), 201, "Administrator creates the Transmission client");
        var clientId = client.GetProperty("id").AsGuid();
        Assert(client.GetProperty("pathMappings").GetArrayLength() == 0, "A new client has no explicit path mappings");
        Assert((await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/Test", null), 200, "Client test"))
            .GetProperty("ok").GetBoolean(), "The client connection is verified");
        var acquisition = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Acquisition"));
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
        {
            enabled = true, downloadClientId = clientId, defaultQualityProfileId = profile.GetProperty("id").AsGuid(),
            revision = acquisition.GetProperty("revision").GetInt32()
        }), 200, "Acquisition is enabled");

        // ---- I2 path mappings: validation names the rule; a valid mapping is verified by a real hardlink probe.
        await ExpectAsync(admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[] { new { clientPathPrefix = "/data/inside", localPathPrefix = Path.Combine(media, "movies", "inside") } }
        }), 400, "mapping_inside_library", "A mapping into a library folder is refused with the rule named");
        await ExpectAsync(admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[]
            {
                new { clientPathPrefix = "/other/torrents", localPathPrefix = Path.Combine(far, "downloads") },
                new { clientPathPrefix = "/other/torrents/", localPathPrefix = Path.Combine(far, "downloads") }
            }
        }), 400, "duplicate_mapping", "Duplicate client prefixes are refused");
        Assert((await ordinary.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings",
            new { pathMappings = Array.Empty<object>() })).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot change mappings");
        var mappings = await ReadAsync(await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[] { new { clientPathPrefix = "/other/torrents", localPathPrefix = Path.Combine(far, "downloads") } }
        }), 200, "Administrator saves a mapping");
        Assert(mappings.EnumerateArray().Single().GetProperty("verifiedAt").ValueKind == JsonValueKind.String,
            "A mapping whose folder can hardlink into a library records VerifiedAt");
        Assert(!Directory.EnumerateFiles(far, ".jfmod-probe-*", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(media, ".jfmod-probe-*", SearchOption.AllDirectories).Any(), "The probe's dot-prefixed files are removed");
        var unmappedTest = await ReadAsync(await admin.PostAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/TestImportPath",
            new { clientPath = "/nowhere/else" }), 200, "Import path test answers");
        Assert(!unmappedTest.GetProperty("ok").GetBoolean() && unmappedTest.GetProperty("code").GetString() == "path_unmapped",
            "An unmapped client path is reported as path_unmapped");
        var mappedTest = await ReadAsync(await admin.PostAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/TestImportPath",
            new { clientPath = "/data/torrents/jfmod" }), 200, "Import path test answers");
        var probes = mappedTest.GetProperty("libraries").EnumerateArray()
            .ToDictionary(item => item.GetProperty("libraryId").AsGuid(), item => item.GetProperty("linkProbe").GetString());
        Assert(mappedTest.GetProperty("ok").GetBoolean() && probes[world.Movies.Id] == "linked" && probes[world.Tv.Id] == "linked" &&
            probes[world.Far.Id] == "not_same_mount", "The probe links into both same-mount libraries and reports the other mount");
        Assert(!Directory.EnumerateFiles(media, ".jfmod-probe-*", SearchOption.AllDirectories).Any(), "Probe files never remain");

        var monitor = host.Service<ImportMonitor>();
        async Task Tick() => await monitor.TickOnceAsync(CancellationToken.None);

        // ================= Movie A: grab → download → import → bind → seed (I3–I5).
        var (grabA, hashA) = await GrabAsync(admin, ids["movieA"], null, fixtures["movieA"]);
        var importA = await WaitAsync(async () => await ImportFor(dbPath, grabA), "An accepted grab gets an import operation");
        Assert(importA.State == ImportStates.Waiting && importA.Progress is null, "The import waits with no invented progress");
        var queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        var rowA = Row(queue, importA.Id);
        Assert(rowA.GetProperty("state").GetString() == "queued" &&
            (rowA.GetProperty("progress").ValueKind == JsonValueKind.Null || rowA.GetProperty("progress").GetDouble() == 0) &&
            rowA.GetProperty("client").GetProperty("openUrl").GetString() == "http://127.0.0.1:9/transmission/web/",
            "Before the client reports progress the row is queued with null progress and an Open in client link");
        var entryA = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["movieA"]}"));
        Assert(entryA.GetProperty("entry").GetProperty("state").GetString() == "grabbed", "A file-less entry projects grabbed");

        transmission.Progress(hashA, 0.25);
        await Tick();
        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        rowA = Row(queue, importA.Id);
        Assert(rowA.GetProperty("state").GetString() == "downloading" && Math.Abs(rowA.GetProperty("progress").GetDouble() - 0.25) < 0.001 &&
            rowA.GetProperty("sizeBytes").GetInt64() == Size, "The queue shows real download progress: " + rowA);
        var listed = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Entries?mediaType=movie&state=downloading"));
        var listedA = listed.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == ids["movieA"]);
        Assert(listedA.GetProperty("progress").GetInt32() == 25 && listed.GetProperty("totalRecordCount").GetInt32() == 1,
            "The File filter finds the downloading title with the same progress as its queue row");
        Assert((await ReadEntry(dbPath, ids["movieA"])).State == FileState.None, "Progress is projected without writing the entry");

        // Write volume: an unchanged download writes nothing; a change writes one observation.
        var observedAt = (await ImportFor(dbPath, grabA)).ObservedAt;
        for (var index = 0; index < 3; index++) await Tick();
        Assert((await ImportFor(dbPath, grabA)).ObservedAt == observedAt, "Ticks without change leave the client columns untouched");
        transmission.Progress(hashA, 0.5);
        await Tick();
        Assert((await ImportFor(dbPath, grabA)).ObservedAt > observedAt, "A progress change is written once");

        // Client outage: the row is unknown with its last progress, never 0 %, and no operation is created or failed.
        transmission.Offline = true;
        await Tick();
        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        rowA = Row(queue, importA.Id);
        Assert(!queue.GetProperty("clientStatus").GetProperty("reachable").GetBoolean() && rowA.GetProperty("state").GetString() == "unknown" &&
            Math.Abs(rowA.GetProperty("progress").GetDouble() - 0.5) < 0.001 && rowA.GetProperty("observedAt").ValueKind == JsonValueKind.String,
            "An unreachable client shows unknown with the last progress and when it was observed");
        transmission.Offline = false;
        await Tick();
        Assert((await ImportFor(dbPath, grabA)).State == ImportStates.Waiting && await CountImports(dbPath, grabA) == 1,
            "The client coming back resumes the same operation");

        // Torrent removed by hand: blocked, never failed, one history event, and a re-add resumes the same operation.
        transmission.Torrents.TryRemove(hashA, out var removedA);
        await Tick();
        await Tick();
        var blockedA = await ImportFor(dbPath, grabA);
        Assert(blockedA.State == ImportStates.Blocked && blockedA.Reason == ImportReasons.TorrentMissing &&
            await HistoryCount(dbPath, ids["movieA"], "import_blocked") == 1, "A missing torrent blocks once with one history event");
        transmission.Torrents[hashA] = removedA!;
        await Tick();
        Assert((await ImportFor(dbPath, grabA)).State == ImportStates.Waiting && (await ImportFor(dbPath, grabA)).Id == importA.Id,
            "Re-adding the torrent resumes the same operation");

        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Entries/{ids["movieA"]}"), 409, "grab_active",
            "A title that is still downloading keeps its grab and cannot be removed");

        transmission.Progress(hashA, 1.0);
        var completedA = await CompleteAsync(dbPath, grabA, Tick);
        var destinationA = Path.Combine(world.Movies.Location, "Example Movie (2024) [tmdbid-100]",
            "Example Movie (2024) [tmdbid-100] - 1080p WEB-DL.mkv");
        var sourceA = transmission.Local("/data/torrents/jfmod/" + fixtures["movieA"].Name);
        Assert(completedA.DestinationPath == destinationA && completedA.VersionLabel == "1080p WEB-DL",
            "The movie lands at the documented folder and multi-version file name");
        var inspector = new UnixFileInspector();
        Assert(inspector.TryInspect(destinationA, out var libraryFileA) && inspector.TryInspect(sourceA, out var seedingFileA) &&
            libraryFileA.PhysicalIdentity == seedingFileA.PhysicalIdentity && libraryFileA.HardlinkCount == 2 &&
            completedA.HardlinkCountAfter == 2, "The library file is a hardlink of the download: same inode, link count 2, no copy");
        Assert(Directory.EnumerateFiles(world.Movies.Location, "*", SearchOption.AllDirectories).Count() == 1,
            "Exactly one file was added to the library");
        Assert(world.Native.ScanRequests.Contains(destinationA), "A targeted scan was requested for the new file only");
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["movieA"]);
            var binding = await database.EntryBindings.AsNoTracking().SingleAsync(value => value.EntryId == ids["movieA"]);
            Assert(entry.State == FileState.OnDisk && entry.JellyfinItemId == binding.JellyfinItemId && completedA.BindingId == binding.Id &&
                completedA.NativeItemId == binding.JellyfinItemId, "Reconciliation bound the imported file and the import attributes that binding");
            Assert(await database.History.CountAsync(history => history.EntryId == ids["movieA"] && history.EventType == "imported") == 1 &&
                await database.History.AnyAsync(history => history.Id == completedA.Id && history.EventType == "imported") &&
                !await database.History.AnyAsync(history => history.EventType == "media_missing" || history.EventType == "episode_media_missing"),
                "One imported event keyed by the operation, and no media_missing");
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(value => value.TargetId == ids["movieA"]);
            Assert(evaluation.RequiresFreshCompletion && evaluation.BaselineAt >= completedA.LinkedAt!.Value.AddSeconds(-1),
                "The imported representation starts retention from the import (P3.T7 baseline)");
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == grabA);
            Assert(grab.ActiveTarget is null && grab.ActiveHash is not null, "The target is free again while the seeding copy is still owned");
            Assert(await database.SeedReleaseOperations.AsNoTracking().CountAsync(value => value.ImportOperationId == completedA.Id &&
                value.State == SeedReleaseStates.Waiting) == 1, "A completed import owns one waiting seed release");
        }

        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Entries/{ids["movieA"]}"), 409, "import_active",
            "A title whose seeding copy the plugin still owns cannot be removed");
        await Tick();
        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        rowA = Row(queue, completedA.Id);
        Assert(rowA.GetProperty("state").GetString() == "seeding" &&
            rowA.GetProperty("seeding").GetProperty("reason").GetString() == SeedReleaseReasons.GoalUnmet &&
            Strings(rowA.GetProperty("seeding").GetProperty("waitingFor")).SequenceEqual(["ratio"]) &&
            rowA.GetProperty("seeding").GetProperty("libraryLinkPresent").GetBoolean(),
            "After import the row continues as seeding, waiting for the ratio floor");
        Assert(rowA.GetProperty("admin").GetProperty("destinationPath").GetString() == destinationA, "Administrators see physical paths");
        var importDto = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Imports/{completedA.Id}"));
        Assert(importDto.GetProperty("state").GetString() == "completed" && importDto.GetProperty("nativeItemId").ValueKind == JsonValueKind.String,
            "GET /Imports returns the completed operation");

        // ================= I6 case 2: retention reclaims the library link first; the seed release later frees the bytes.
        await host.Service<RetentionPolicyService>().SyncAsync(configuration, CancellationToken.None);
        var preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        var previewA = PreviewItem(preview, completedA.BindingId!.Value);
        Assert(previewA.GetProperty("state").GetString() is "scheduled" or "waiting", "Freshly imported media is not due");
        time.Offset += TimeSpan.FromDays(2);
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        previewA = PreviewItem(preview, completedA.BindingId!.Value);
        Assert(previewA.GetProperty("state").GetString() == "blocked" && previewA.GetProperty("reason").GetString() == "seed_goal_unmet",
            "While its seed goal is unmet, the library file is blocked from reclaim with a seed reason");
        transmission.Torrents[hashA].UploadRatio = 1.2;
        await Tick();
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id);
            Assert(seed.GoalMetAt is not null && seed.Reason == SeedReleaseReasons.Disabled && transmission.Torrents.ContainsKey(hashA) &&
                transmission.RemoveCalls == 0, "With seed release off nothing is removed, and the row says why");
        }

        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(Row(queue, completedA.Id).GetProperty("seeding").GetProperty("reason").GetString() == SeedReleaseReasons.Disabled &&
            !queue.GetProperty("seedReleaseEnabled").GetBoolean(), "The queue explains that seed release is off");
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        Assert(PreviewItem(preview, completedA.BindingId!.Value).GetProperty("state").GetString() == "due",
            "Once the plugin's effective goal is met, the library file is due");
        var reclaim = await host.Service<RetentionExecutor>().ReclaimAsync(completedA.BindingId!.Value, CancellationToken.None);
        Assert(reclaim.State == "completed" && reclaim.PhysicalBytesReleased == 0 && !File.Exists(destinationA) &&
            inspector.TryInspect(sourceA, out var afterReclaim) && afterReclaim.HardlinkCount == 1,
            "Retention unlinks only the library path and honestly reports 0 bytes released while the seeding copy remains");
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(2, seedRelease: true)), 200,
            "Administrator turns seed release on");
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id)).State ==
                SeedReleaseStates.Completed ? true : (bool?)null;
        }, "The seed release completes after the late reclaim");
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id);
            var retention = await database.RetentionOperations.AsNoTracking().SingleAsync(value => value.Id == reclaim.OperationId);
            Assert(seed.PhysicalBytesReleased == Size && seed.CreditedRetentionOperationId == retention.Id && seed.HardlinkCountBefore == 1 &&
                !File.Exists(sourceA) && !transmission.Torrents.ContainsKey(hashA),
                "Removing the last link frees the logical size and credits the earlier reclaim");
            var released = await database.History.AsNoTracking().SingleAsync(history => history.Id == seed.Id);
            Assert(released.EventType == "seeding_released" && released.Summary.Contains("previously reported as 0 B", StringComparison.Ordinal),
                "History shows the freed bytes that were reported as 0 B earlier");
            Assert((await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == grabA)).ActiveHash is null,
                "The grab gives its hash back once the seeding copy is gone");
        }

        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(!queue.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").AsGuid() == completedA.Id),
            "A released seeding copy leaves the queue");

        // ================= I6 case 1: seed goal first; the library file survives with link count 1.
        var foreignHash = new string('f', 40);
        transmission.Torrents[foreignHash] = new HeldTorrent
        {
            Hash = foreignHash, Name = "Foreign.Torrent.mkv", DownloadDir = "/data/torrents/jfmod", Labels = ["jellyfinmod-test"],
            Files = [new HeldFile { Name = "Foreign.Torrent.mkv", Length = 1000, Completed = 1000 }], UploadRatio = 9
        };
        var (grabB, hashB) = await GrabAsync(admin, ids["movieB"], null, fixtures["movieB"]);
        transmission.Progress(hashB, 1.0);
        var completedB = await CompleteAsync(dbPath, grabB, Tick);
        Assert(completedB.VersionLabel == "1080p BluRay", "The version label uses resolution and source");
        transmission.Torrents[hashB].UploadRatio = 1.5;
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id)).State ==
                SeedReleaseStates.Completed ? true : (bool?)null;
        }, "The seed release completes once the ratio is met");
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(seed.PhysicalBytesReleased == 0 && seed.CreditedRetentionOperationId is null && seed.Reason == SeedReleaseReasons.Released &&
                inspector.TryInspect(completedB.DestinationPath!, out var libraryB) && libraryB.HardlinkCount == 1 &&
                !File.Exists(completedB.SourceLocalPath!), "The library file survives with link count 1 and 0 bytes are claimed");
            Assert(await database.History.CountAsync(history => history.EntryId == ids["movieB"] && history.EventType == "seeding_released") == 1,
                "One seeding_released event");
            Assert((await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["movieB"])).State == FileState.OnDisk,
                "The title stays on disk");
        }

        Assert(transmission.Torrents.ContainsKey(foreignHash), "A torrent the plugin did not add, in the same label, is never removed");

        // ================= An existing native file gains a second version; the indexer's 14 days outlast the floor.
        // Jellyfin keeps one item for a movie folder and attaches the new file as a further media source of it, so
        // the import only finishes if each media source is bound on its own file (P6.M6).
        var thirdFolder = Path.Combine(world.Movies.Location, "Third Movie (2022)");
        Directory.CreateDirectory(thirdFolder);
        var thirdExisting = Path.Combine(thirdFolder, "Third Movie (2022).mkv");
        await File.WriteAllBytesAsync(thirdExisting, new byte[4096]);
        var thirdMovie = world.Native.AddMovie(world.Movies, thirdExisting, 102);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.AnyAsync(value => value.EntryId == ids["movieC"]) ? true : (bool?)null;
        }, "The existing file is bound by reconciliation");
        Guid firstBindingC;
        await using (var database = new ModDbContext(dbPath))
            firstBindingC = (await database.EntryBindings.AsNoTracking().SingleAsync(value => value.EntryId == ids["movieC"])).Id;
        var (grabC, hashC) = await GrabAsync(admin, ids["movieC"], null, fixtures["movieC"]);
        transmission.Progress(hashC, 1.0);
        var completedC = await CompleteAsync(dbPath, grabC, Tick);
        Assert(completedC.DestinationPath == Path.Combine(thirdFolder, "Third Movie (2022) - 2160p WEB-DL.mkv"),
            "A second version lands in the existing folder with the folder name as its prefix; nothing is renamed");
        Assert(world.Native.Items.OfType<MediaBrowser.Controller.Entities.Movies.Movie>()
                .Count(movie => movie.ProviderIds.GetValueOrDefault("Tmdb") == "102") == 1 &&
            thirdMovie.LocalAlternateVersions.SequenceEqual([completedC.DestinationPath!]),
            "Jellyfin indexed the second file as another media source of the same item, not as a second item");
        await using (var database = new ModDbContext(dbPath))
        {
            var bindings = await database.EntryBindings.AsNoTracking().Where(value => value.EntryId == ids["movieC"]).ToListAsync();
            Assert(bindings.Count == 2 && bindings.Select(value => value.VersionGroupId).Distinct().Count() == 1 && File.Exists(thirdExisting),
                "Both versions are bound in one native version group and the existing file is untouched");
            var owning = bindings.Single(value => value.OwnerItemId is null);
            var source = bindings.Single(value => value.OwnerItemId is not null);
            Assert(owning.Id == firstBindingC && owning.JellyfinItemId == thirdMovie.Id && owning.MediaPath == thirdExisting &&
                source.OwnerItemId == thirdMovie.Id && source.JellyfinItemId != thirdMovie.Id &&
                source.MediaPath == completedC.DestinationPath && completedC.BindingId == source.Id &&
                completedC.NativeItemId == source.JellyfinItemId,
                "Each media source is bound to its own file and the import completes against its own destination, " +
                $"leaving the first version's binding untouched (first {owning.MediaPath}; second {source.MediaPath})");
            Assert((await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["movieC"])).JellyfinItemId == thirdMovie.Id,
                "The entry still points at the item a client can open, never at a media source of it");
            var rows = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["movieC"]}"))
                .GetProperty("versions").EnumerateArray().ToArray();
            Assert(rows.Length == 2 && rows.All(row => row.GetProperty("jellyfinItemId").AsGuid() == thirdMovie.Id) &&
                rows.Select(row => row.GetProperty("mediaSourceId").GetString()).Distinct().Count() == 2 &&
                rows.Any(row => row.GetProperty("mediaSourceId").GetString() == source.JellyfinItemId.ToString("N") &&
                    row.GetProperty("label").GetString() == "2160p WEB-DL"),
                "The selector plays the chosen media source of the one item: " + string.Join("; ", rows.Select(row => row.GetRawText())));
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedC.Id);
            // The indexer snapshot requires 14 days: simulated by setting the recorded snapshot, documented in PHASE5 I9.
            seed.IndexerSeconds = (long)TimeSpan.FromDays(14).TotalSeconds;
            await database.SaveChangesAsync();
        }

        transmission.Torrents[hashC].UploadRatio = 5;
        transmission.Torrents[hashC].SecondsSeeding = (long)TimeSpan.FromDays(7).TotalSeconds;
        await Tick();
        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        var seedingC = Row(queue, completedC.Id).GetProperty("seeding");
        Assert(seedingC.GetProperty("goalSeconds").GetInt64() == (long)TimeSpan.FromDays(14).TotalSeconds &&
            Strings(seedingC.GetProperty("waitingFor")).SequenceEqual(["time"]) && transmission.Torrents.ContainsKey(hashC),
            "An indexer's 14-day requirement is not shortened by a met ratio floor");
        var seedingList = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Seeding"));
        Assert(seedingList.EnumerateArray().Any(item => item.GetProperty("importOperationId").AsGuid() == completedC.Id &&
            item.GetProperty("goalSecondsSource").GetString() == "indexer"), "GET /Seeding names where each goal comes from");
        transmission.Torrents[hashC].SecondsSeeding = (long)TimeSpan.FromDays(14).TotalSeconds;
        await WaitAsync(async () =>
        {
            await Tick();
            return transmission.Torrents.ContainsKey(hashC) ? null : true;
        }, "After 14 days of seeding the torrent is released");

        // ================= Failure cases: each ends in its documented state and writes nothing to the library.
        var libraryFilesBefore = LibraryFiles(world);

        // EXDEV: the data sits on another filesystem; the mapping is verified, but the library is on a different mount.
        var (grabD, hashD) = await GrabAsync(admin, ids["movieD"], null, fixtures["movieD"]);
        transmission.Torrents[hashD].DownloadDir = "/other/torrents/jfmod";
        transmission.Progress(hashD, 1.0);
        var blockedD = await BlockedAsync(dbPath, grabD, Tick);
        Assert(blockedD.Reason == ImportReasons.CrossFilesystem && LibraryFiles(world).SequenceEqual(libraryFilesBefore) &&
            !Directory.Exists(Path.Combine(world.Movies.Location, "Cross Movie (2021) [tmdbid-110]")),
            "A download on another mount is blocked as cross_filesystem; nothing is copied or created in the library");
        // Restoring the single-mount layout and pressing Retry succeeds.
        transmission.Torrents[hashD].DownloadDir = "/data/torrents/jfmod";
        transmission.Progress(hashD, 1.0);
        Assert((await ordinary.PostAsync($"/JellyfinMod/Imports/{blockedD.Id}/Retry", null)).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot retry");
        await ReadAsync(await admin.PostAsync($"/JellyfinMod/Imports/{blockedD.Id}/Retry", null), 202, "Administrator retries");
        var completedD = await CompleteAsync(dbPath, grabD, Tick);
        Assert(completedD.Id == blockedD.Id, "Retrying a blocked import resumes the same operation");

        var (grabE, hashE) = await GrabAsync(admin, ids["movieE"], null, fixtures["movieE"]);
        transmission.Torrents[hashE].DownloadDir = "/unmapped/jfmod";
        transmission.Progress(hashE, 1.0);
        Assert((await BlockedAsync(dbPath, grabE, Tick)).Reason == ImportReasons.PathUnmapped, "An unmapped client path blocks as path_unmapped");

        var (grabF, hashF) = await GrabAsync(admin, ids["movieF"], null, fixtures["movieF"]);
        transmission.Progress(hashF, 1.0);
        var blockedF = await BlockedAsync(dbPath, grabF, Tick);
        Assert(blockedF.Reason == ImportReasons.AmbiguousFiles, "Two files of similar size block as ambiguous_files");

        var (grabG, hashG) = await GrabAsync(admin, ids["movieG"], null, fixtures["movieG"]);
        transmission.Progress(hashG, 1.0);
        var blockedG = await BlockedAsync(dbPath, grabG, Tick);
        Assert(blockedG.Reason == ImportReasons.ArchiveUnsupported, "An archive blocks as archive_unsupported and is never extracted");

        var (grabK, hashK) = await GrabAsync(admin, ids["movieK"], null, fixtures["movieK"]);
        transmission.Progress(hashK, 1.0);
        Assert((await BlockedAsync(dbPath, grabK, Tick)).Reason == ImportReasons.NoVideoFile, "A torrent without video blocks as no_video_file");

        var (grabH, hashH) = await GrabAsync(admin, ids["movieH"], null, fixtures["movieH"]);
        transmission.Progress(hashH, 1.0);
        var completedH = await CompleteAsync(dbPath, grabH, Tick);
        Assert(completedH.SourceClientPath!.EndsWith("/Sample.Movie.2021.1080p.WEB-DL-GRP/Sample.Movie.2021.1080p.WEB-DL-GRP.mkv", StringComparison.Ordinal),
            "The sample and the NFO are ignored and the feature file is imported");

        // I7 removal: without the client, with the client and data, and with a blocklist.
        Assert((await ordinary.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedF.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true, blocklist = false }) })).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot remove queue rows");
        Assert((await anonymous.DeleteAsync($"/JellyfinMod/Queue/{blockedF.Id}")).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous removal is refused");
        var removedF = await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedF.Id}")
            { Content = JsonContent.Create(new { removeFromClient = false, blocklist = false }) }), 200, "Remove without the client");
        Assert(removedF.GetProperty("state").GetString() == "cancelled" && transmission.Torrents.ContainsKey(hashF) &&
            File.Exists(transmission.Local("/data/torrents/jfmod/Ambiguous.Movie.2021.1080p.WEB-DL-GRP/part.one.mkv")),
            "Removing without the client leaves the torrent and its data");
        var archiveData = transmission.Local("/data/torrents/jfmod/Archive.Movie.2021.1080p.WEB-DL-GRP/archive.rar");
        var libraryFilesBeforeRemove = LibraryFiles(world);
        var removedG = await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedG.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true, blocklist = true }) }), 200, "Remove with the client and a blocklist");
        Assert(removedG.GetProperty("state").GetString() == "cancelled" && !transmission.Torrents.ContainsKey(hashG) && !File.Exists(archiveData) &&
            LibraryFiles(world).SequenceEqual(libraryFilesBeforeRemove), "The torrent and its data are removed; the library is untouched");
        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(!queue.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").AsGuid() == blockedF.Id ||
            item.GetProperty("id").AsGuid() == blockedG.Id), "Removed rows leave the queue");
        var search = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["movieG"]}"));
        var blocklisted = search.GetProperty("candidates").EnumerateArray().Single(candidate => candidate.GetProperty("rawTitle").GetString() ==
            "Archive.Movie.2021.1080p.WEB-DL-GRP");
        Assert(!blocklisted.GetProperty("eligible").GetBoolean() && blocklisted.GetProperty("rejections").EnumerateArray()
            .Any(rejection => rejection.GetProperty("code").GetString() == "blocklisted"), "A blocklisted release is rejected by a fresh search");
        Assert(await HistoryCount(dbPath, ids["movieG"], "blocklisted") == 1 && await HistoryCount(dbPath, ids["movieG"], "queue_removed") == 1,
            "Removal and blocklisting are recorded in history");

        // ================= Episodes: only the imported episode becomes available; mismatches, existing files and collisions block.
        var seriesFolder = Path.Combine(world.Tv.Location, "Example Show (2023)");
        Directory.CreateDirectory(Path.Combine(seriesFolder, "Season 01"));
        var existingEpisode = Path.Combine(seriesFolder, "Season 01", "Example Show S01E01.mkv");
        await File.WriteAllBytesAsync(existingEpisode, new byte[2048]);
        var existingSeries = new TestSeries { Id = Guid.NewGuid(), Name = "Example Show", Path = seriesFolder };
        existingSeries.ProviderIds["Tmdb"] = "200";
        world.Native.Add(world.Tv, existingSeries);
        world.Native.Scan(existingEpisode);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.AnyAsync(value => value.EpisodeId == ids["e1"]) ? true : (bool?)null;
        }, "The existing episode is bound");
        var (grabE2, hashE2) = await GrabAsync(admin, ids["series"], ids["e2"], fixtures["e2"]);
        transmission.Progress(hashE2, 0.4);
        await Tick();
        var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["series"]}"));
        var episodeTwo = detail.GetProperty("episodes").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == ids["e2"]);
        Assert(episodeTwo.GetProperty("state").GetString() == "downloading" && episodeTwo.GetProperty("progress").GetInt32() == 40 &&
            episodeTwo.GetProperty("availability").GetString() == "missing" &&
            detail.GetProperty("entry").GetProperty("state").GetString() == "onDisk",
            "A downloading episode projects its own progress; the series keeps its native state");
        transmission.Progress(hashE2, 1.0);
        var completedE2 = await CompleteAsync(dbPath, grabE2, Tick);
        Assert(completedE2.DestinationPath == Path.Combine(seriesFolder, "Season 01", "Example Show (2023) S01E02.mkv") &&
            completedE2.VersionLabel is null, "An episode lands in the bound series folder's season folder with no version label");
        detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["series"]}"));
        var availability = detail.GetProperty("episodes").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").AsGuid(), item => item.GetProperty("availability").GetString());
        Assert(availability[ids["e1"]] == "onDisk" && availability[ids["e2"]] == "onDisk" && availability[ids["e3"]] == "missing" &&
            availability[ids["e4"]] == "missing", "Importing one episode makes only that episode available");

        var (grabE3, hashE3) = await GrabAsync(admin, ids["series"], ids["e3"], fixtures["e3"]);
        transmission.Progress(hashE3, 1.0);
        Assert((await BlockedAsync(dbPath, grabE3, Tick)).Reason == ImportReasons.EpisodeMismatch,
            "A file numbered as another episode blocks as episode_mismatch");
        var (grabE1, hashE1) = await GrabAsync(admin, ids["series"], ids["e1"], fixtures["e1"]);
        transmission.Progress(hashE1, 1.0);
        Assert((await BlockedAsync(dbPath, grabE1, Tick)).Reason == ImportReasons.TargetExists,
            "An episode that already has a file is not imported again (PHASE5 open question 4)");
        var collision = Path.Combine(seriesFolder, "Season 01", "Example Show (2023) S01E05.mkv");
        await File.WriteAllBytesAsync(collision, [1, 2, 3]);
        var (grabE5, hashE5) = await GrabAsync(admin, ids["series"], ids["e5"], fixtures["e5"]);
        transmission.Progress(hashE5, 1.0);
        Assert((await BlockedAsync(dbPath, grabE5, Tick)).Reason == ImportReasons.DestinationCollision &&
            (await File.ReadAllBytesAsync(collision)).SequenceEqual(new byte[] { 1, 2, 3 }),
            "A colliding name blocks as destination_collision and leaves the existing file untouched");

        // ================= I7 visibility and polling cost.
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(3, seedRelease: true, visible: true)), 200,
            "Administrator lets users see the queue");
        var userQueue = Json.Parse(await ordinary.GetStringAsync("/JellyfinMod/Queue"));
        Assert(userQueue.GetProperty("items").GetArrayLength() > 0 && userQueue.GetProperty("items").EnumerateArray()
            .All(item => item.GetProperty("client").ValueKind == JsonValueKind.Null && !item.TryGetProperty("admin", out _)),
            "Ordinary users see rows without the client link or physical detail");
        var movieUserQueue = Json.Parse(await moviesOnly.GetStringAsync("/JellyfinMod/Queue"));
        Assert(movieUserQueue.GetProperty("items").EnumerateArray().All(item => item.GetProperty("entry").GetProperty("mediaType").GetString() == "movie"),
            "A user without TV access sees no TV rows");
        var tvOperation = await ImportFor(dbPath, grabE3);
        Assert((await moviesOnly.GetAsync($"/JellyfinMod/Imports/{tvOperation.Id}")).StatusCode == HttpStatusCode.NotFound,
            "An inaccessible import answers with the concealed 404");
        Assert((await tvAdmin.GetAsync($"/JellyfinMod/Imports/{blockedD.Id}")).StatusCode == HttpStatusCode.NotFound,
            "An administrator without the library's access gets the concealed 404 too");
        await Tick();
        var cache = host.Service<ClientSnapshotCache>();
        var readsBefore = cache.Reads;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? admin : ordinary).GetStringAsync("/JellyfinMod/Queue")));
        Assert(cache.Reads - readsBefore <= 1, $"Twenty queue polls cost at most one client read, not one per request ({cache.Reads - readsBefore})");

        // ================= Recovery: restart the host with operations left in every interruptible state.
        world.Native.AutoScan = false;
        var (grabI, hashI) = await GrabAsync(admin, ids["movieI"], null, fixtures["movieI"]);
        transmission.Progress(hashI, 1.0);
        var scanningI = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabI);
            return operation.State == ImportStates.Scanning ? operation : null;
        }, "The import reaches scanning while the scan is held back");
        var (grabJ, hashJ) = await GrabAsync(admin, ids["movieJ"], null, fixtures["movieJ"]);
        transmission.Progress(hashJ, 1.0);
        var scanningJ = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabJ);
            return operation.State == ImportStates.Scanning ? operation : null;
        }, "A second import reaches scanning");
        await StopAsync();
        // As if the process died between creating the link and recording it, and between linking and scanning.
        await using (var database = new ModDbContext(dbPath))
        {
            var interruptedI = await database.ImportOperations.SingleAsync(value => value.Id == scanningI.Id);
            interruptedI.State = ImportStates.Linking;
            interruptedI.LinkedAt = null;
            interruptedI.ScanAttempts = 0;
            var interruptedJ = await database.ImportOperations.SingleAsync(value => value.Id == scanningJ.Id);
            interruptedJ.State = ImportStates.Linked;
            interruptedJ.ScanAttempts = 0;
            await database.SaveChangesAsync();
        }

        world.Native.AutoScan = true;
        await StartAsync();
        admin = host.Client(world.Admin, true);
        monitor = host.Service<ImportMonitor>();
        var afterRestart = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Import"));
        Assert(afterRestart.GetProperty("seedReleaseEnabled").GetBoolean() && afterRestart.GetProperty("importPollSeconds").GetInt32() == 1 &&
            afterRestart.GetProperty("queueVisibleToUsers").GetBoolean(), "Import settings survive a restart unchanged");
        var recoveredI = await CompleteAsync(dbPath, grabI, Tick);
        var recoveredJ = await CompleteAsync(dbPath, grabJ, Tick);
        Assert(recoveredI.Id == scanningI.Id && recoveredJ.Id == scanningJ.Id && recoveredI.DestinationPath == scanningI.DestinationPath &&
            Directory.EnumerateFiles(Path.GetDirectoryName(recoveredI.DestinationPath!)!).Count() == 1 &&
            Directory.EnumerateFiles(Path.GetDirectoryName(recoveredJ.DestinationPath!)!).Count() == 1 &&
            inspector.TryInspect(recoveredI.DestinationPath!, out var linkI) && linkI.HardlinkCount == 2,
            "After a restart each interrupted import completes once, with one hardlink and no duplicate file");
        Assert(await HistoryCount(dbPath, ids["movieI"], "imported") == 1 && await HistoryCount(dbPath, ids["movieJ"], "imported") == 1,
            "Recovery writes one imported event each");

        // A seed release that died between asking the client and inspecting the result ends once, with one event.
        await StopAsync();
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedH.Id);
            seed.State = SeedReleaseStates.Removing;
            seed.HardlinkCountBefore = 2;
            seed.RemovingAt = time.GetUtcNow().UtcDateTime;
            await database.SaveChangesAsync();
        }

        // The client did remove the torrent and its data before the process died.
        transmission.Torrents.TryRemove(hashH, out var removedH);
        foreach (var file in removedH!.Files)
        {
            var data = transmission.Local(removedH.DownloadDir + "/" + file.Name);
            if (File.Exists(data)) File.Delete(data);
        }

        await StartAsync();
        admin = host.Client(world.Admin, true);
        monitor = host.Service<ImportMonitor>();
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id)).State ==
                SeedReleaseStates.Completed ? true : (bool?)null;
        }, "An interrupted removal is inspected and finished after a restart");
        await Tick();
        Assert(await HistoryCount(dbPath, ids["movieH"], "seeding_released") == 1, "The interrupted release writes exactly one history event");

        // ================= Scan timeout: re-requested once, then binding_not_observed; a later scan still completes it.
        world.Native.AutoScan = false;
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieL", "movie", 118, "Late Bind Movie", 2021, "tt0000118", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        var fixtureL = TorrentFixture.Single("Late.Bind.Movie.2021.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["movieL"] = fixtureL.Bytes;
        transmission.Register(fixtureL);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new("Late.Bind.Movie.2021.1080p.WEB-DL-GRP", "guid-movieL", torznab.Download("movieL"), Size, 25,
                new() { ["imdbid"] = "0000118" }));
        var (grabL, hashL) = await GrabAsync(admin, ids["movieL"], null, fixtureL);
        transmission.Progress(hashL, 1.0);
        var scanningL = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabL);
            return operation.State == ImportStates.Scanning ? operation : null;
        }, "The late import reaches scanning");
        time.Offset += TimeSpan.FromMinutes(11);
        await Tick();
        Assert((await ImportFor(dbPath, grabL)).ScanAttempts == 2, "A timed-out scan is requested once more");
        time.Offset += TimeSpan.FromMinutes(11);
        await Tick();
        var notObserved = await ImportFor(dbPath, grabL);
        Assert(notObserved.State == ImportStates.Blocked && notObserved.Reason == ImportReasons.BindingNotObserved &&
            File.Exists(notObserved.DestinationPath!), "Then it blocks as binding_not_observed and keeps the hardlink");
        world.Native.Scan(notObserved.DestinationPath!);
        var lateL = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabL);
            return operation.State == ImportStates.Completed ? operation : null;
        }, "The blocked import completes once a later scan binds its file");
        Assert(lateL.Id == scanningL.Id, "A later scan still binds the file and completes the same operation");
        world.Native.AutoScan = true;

        // ================= P4.A6 (b): the Phase 3 seed reader reads the same daemon and sees its paths through the mappings.
        configuration.TransmissionRpcUrl = transmission.Endpoint.ToString();
        configuration.TransmissionUsername = transmission.Username;
        configuration.TransmissionPassword = transmission.Password;
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieM", "movie", 119, "Reader Movie", 2021, "tt0000119", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        var fixtureM = TorrentFixture.Single("Reader.Movie.2021.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["movieM"] = fixtureM.Bytes;
        transmission.Register(fixtureM);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new("Reader.Movie.2021.1080p.WEB-DL-GRP", "guid-movieM", torznab.Download("movieM"), Size, 25,
                new() { ["imdbid"] = "0000119" }));
        var removeCallsBeforeM = transmission.RemoveCalls;
        var (grabM, hashM) = await GrabAsync(admin, ids["movieM"], null, fixtureM);
        transmission.Progress(hashM, 1.0);
        var completedM = await CompleteAsync(dbPath, grabM, Tick);
        Assert(transmission.Torrents[hashM].DownloadDir == "/data/torrents/jfmod" && !File.Exists("/data/torrents/jfmod/" + fixtureM.Name) &&
            File.Exists(completedM.SourceLocalPath!), "The daemon reports a path that exists only through the client's mapping");
        // The administrator drops the row but keeps the torrent seeding; the plugin no longer tracks a release for it.
        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{completedM.Id}")
            { Content = JsonContent.Create(new { removeFromClient = false, blocklist = false }) }), 200, "Remove the seeding row without the client");
        Assert(transmission.Torrents.ContainsKey(hashM), "The torrent keeps seeding in the client");
        Guid thirdBindingId;
        await using (var database = new ModDbContext(dbPath))
            thirdBindingId = (await database.EntryBindings.AsNoTracking().SingleAsync(value => value.EntryId == ids["movieC"] &&
                value.MediaPath == thirdExisting)).Id;
        _ = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        time.Offset += TimeSpan.FromDays(2);
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        var previewM = PreviewItem(preview, completedM.BindingId!.Value);
        Assert(previewM.GetProperty("state").GetString() == "blocked" && previewM.GetProperty("torrentManaged").ValueKind == JsonValueKind.True &&
            previewM.GetProperty("reason").GetString() == "seed_goal_unmet" && previewM.GetProperty("ratioGoal").GetDouble() == 1.0,
            "The reader finds the mapped seeding copy and holds it to the plugin's floor, not an unbounded goal: " + previewM);
        Assert(transmission.Torrents[hashM].SeedRatioMode == 2 && transmission.Torrents[hashM].SeedIdleMode == 2,
            "The client itself still has no stopping condition for the plugin's torrent");
        var previewThird = PreviewItem(preview, thirdBindingId);
        Assert(preview.GetProperty("seedIndexUnresolvedFiles").GetInt32() > 0 && previewThird.GetProperty("state").GetString() == "blocked" &&
            previewThird.GetProperty("reason").GetString() == "seed_index_incomplete",
            "Torrent data that no mapping resolves still blocks non-torrent media (fail closed): " + previewThird);
        transmission.Torrents[hashM].UploadRatio = 0.6;
        var earlyM = await host.Service<RetentionExecutor>().ReclaimAsync(completedM.BindingId!.Value, CancellationToken.None);
        Assert(earlyM.State != "completed" && File.Exists(completedM.DestinationPath!),
            $"Nothing is deleted before the seed goal is met ({earlyM.State} {earlyM.Reason})");
        transmission.Torrents[hashM].UploadRatio = 1.2;
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        previewM = PreviewItem(preview, completedM.BindingId!.Value);
        Assert(previewM.GetProperty("state").GetString() == "due" && previewM.GetProperty("torrentManaged").ValueKind == JsonValueKind.True,
            "Once the plugin's goal is met, the library file of a plugin torrent is due: " + previewM);
        var reclaimM = await host.Service<RetentionExecutor>().ReclaimAsync(completedM.BindingId!.Value, CancellationToken.None);
        Assert(reclaimM.State == "completed" && reclaimM.PhysicalBytesReleased == 0 && !File.Exists(completedM.DestinationPath!) &&
            inspector.TryInspect(completedM.SourceLocalPath!, out var seedingM) && seedingM.HardlinkCount == 1 &&
            transmission.Torrents.ContainsKey(hashM) && transmission.RemoveCalls == removeCallsBeforeM,
            "Retention reclaims only the library link, reports 0 bytes and leaves the seeding torrent alone");

        await RestartAsync();
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(!await database.History.AnyAsync(history => history.EventType == "media_missing" || history.EventType == "episode_media_missing"),
                "No media_missing was written during the whole run");
            Assert(await Scalar(database, "PRAGMA integrity_check") == "ok" && await Scalar(database, "PRAGMA foreign_key_check") is null,
                "The database passes integrity and foreign-key checks after the run");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static object ImportSettings(int revision, string[]? extensions = null, bool seedRelease = false, bool visible = false) => new
    {
        importEnabled = true, seedReleaseEnabled = seedRelease, seedFloorRatio = 1.0, seedFloorHours = (int?)null, importPollSeconds = 1,
        videoExtensions = extensions ?? ["mkv", "mp4"], stalledAfterHours = 24, scanTimeoutMinutes = 10, queueVisibleToUsers = visible,
        revision
    };

    private static void AddEntry(ModDbContext database, Dictionary<string, Guid> ids, string key, string mediaType, int tmdbId, string title,
        int year, string? imdbId, int? tvdbId, Guid libraryId)
    {
        var entry = new Entry
        {
            MediaType = mediaType, TmdbId = tmdbId, Title = title, Year = year, ImdbId = imdbId, TargetLibraryId = libraryId,
            MetadataJson = Metadata(mediaType, tmdbId, title, year, imdbId, tvdbId)
        };
        database.Entries.Add(entry);
        ids[key] = entry.Id;
    }

    private static string Metadata(string mediaType, int tmdbId, string title, int year, string? imdbId, int? tvdbId) =>
        JsonSerializer.Serialize(new TmdbMetadata(mediaType, tmdbId, title, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null,
            imdbId, tvdbId, false, null, 100, [], [], mediaType == "series" ? [new TmdbSeason(1, "Season 1", 5, null, null)] : []));

    private static async Task<(Guid GrabId, string Hash)> GrabAsync(HttpClient admin, Guid entryId, Guid? episodeId, TorrentFixture fixture)
    {
        var search = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}" + (episodeId is { } id ? $"&episodeId={id}" : "")));
        var title = fixture.Name.EndsWith(".mkv", StringComparison.Ordinal) ? fixture.Name[..^4] : fixture.Name;
        var candidate = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == title);
        Assert(candidate.GetProperty("eligible").GetBoolean(), "The fixture release is eligible: " + candidate.GetRawText());
        var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = search.GetProperty("searchId").AsGuid(), releaseId = candidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-" + Guid.NewGuid().ToString("N")
        }), 202, "Administrator grabs " + title);
        var grabId = grab.GetProperty("id").AsGuid();
        await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{grabId}")).GetProperty("state").GetString() ==
            "accepted" ? true : (bool?)null, "The grab is accepted by the Transmission boundary");
        return (grabId, fixture.InfoHash);
    }

    private static async Task<ImportOperation> CompleteAsync(string dbPath, Guid grabId, Func<Task> tick)
    {
        ImportOperation? last = null;
        try
        {
            return await WaitAsync(async () =>
            {
                await tick();
                var operation = last = await ImportFor(dbPath, grabId);
                Assert(operation.State is not (ImportStates.Blocked or ImportStates.Failed),
                    $"Import of grab {grabId} stopped: {operation.State} {operation.Reason} {operation.Error}");
                return operation.State == ImportStates.Completed ? operation : null;
            }, "The import completes");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Timed out", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{error.Message}: last {last?.State} {last?.Reason} {last?.Error} scans {last?.ScanAttempts} " +
                $"destination {last?.DestinationPath}");
        }
    }

    private static async Task<ImportOperation> BlockedAsync(string dbPath, Guid grabId, Func<Task> tick) =>
        await WaitAsync(async () =>
        {
            await tick();
            var operation = await ImportFor(dbPath, grabId);
            return operation.State == ImportStates.Blocked ? operation : null;
        }, "The import blocks");

    private static async Task<ImportOperation> ImportFor(string dbPath, Guid grabId)
    {
        await using var database = new ModDbContext(dbPath);
        return await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId)
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync() ?? throw new InvalidOperationException("No import yet");
    }

    private static async Task<int> CountImports(string dbPath, Guid grabId)
    {
        await using var database = new ModDbContext(dbPath);
        return await database.ImportOperations.CountAsync(value => value.GrabId == grabId);
    }

    private static async Task<Entry> ReadEntry(string dbPath, Guid entryId)
    {
        await using var database = new ModDbContext(dbPath);
        return await database.Entries.AsNoTracking().SingleAsync(value => value.Id == entryId);
    }

    private static async Task<int> HistoryCount(string dbPath, Guid entryId, string eventType)
    {
        await using var database = new ModDbContext(dbPath);
        return await database.History.CountAsync(history => history.EntryId == entryId && history.EventType == eventType);
    }

    private static string[] LibraryFiles(World world) => Directory.EnumerateFiles(world.Movies.Location, "*", SearchOption.AllDirectories)
        .Concat(Directory.EnumerateFiles(world.Tv.Location, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal).ToArray();

    private static JsonElement Row(JsonElement queue, Guid id) =>
        queue.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == id);

    private static JsonElement PreviewItem(JsonElement preview, Guid bindingId) =>
        preview.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("bindingId").AsGuid() == bindingId);

    private static async Task<string?> Scalar(ModDbContext database, string sql)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    public static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, string message, int seconds = 30) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await probe() is { } value) return value;
            }
            catch (InvalidOperationException error) when (error.Message == "No import yet")
            {
                last = error;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("Timed out: " + message + (last is null ? string.Empty : " (" + last.Message + ")"));
    }

    public static async Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30)
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
        return Json.Parse(body);
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, int status, string type, string message)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status && body.Contains($"\"{type}\"", StringComparison.Ordinal),
            $"{message}: expected {status} {type}, got {(int)response.StatusCode} {body}");
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
