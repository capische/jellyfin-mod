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
        // A list the run can add to: the manual cleanup case adds a library reached through a second bind mount.
        var libraries = new List<TestLibrary> { movies, tv, farLibrary };
        var native = new NativeWorld
        {
            Libraries = libraries,
            // The administrator also sees the libraries the cleanup case adds through the second bind mount.
            UserLibraries = userId => userId == admin.Id ? [movies, tv, .. libraries.Where(library => library.Name.StartsWith("Alias", StringComparison.Ordinal))] :
                userId == ordinary.Id ? [movies, tv] : userId == tvAdmin.Id ? [tv] : userId == moviesOnly.Id ? [movies] : []
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

        // ---- I2 path mappings: validation names the rule; a valid mapping is verified by a real hardlink probe. Every
        // replacement names the version of the mappings it read (final review, finding 4).
        async Task<string> MappingsVersionOf(Guid id) => Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/DownloadClients"))
            .EnumerateArray().Single(client => client.GetProperty("id").AsGuid() == id).GetProperty("mappingsVersion").GetString()!;
        await ExpectAsync(admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[] { new { clientPathPrefix = "/data/inside", localPathPrefix = Path.Combine(media, "movies", "inside") } },
            mappingsVersion = await MappingsVersionOf(clientId)
        }), 400, "mapping_inside_library", "A mapping into a library folder is refused with the rule named");
        await ExpectAsync(admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[]
            {
                new { clientPathPrefix = "/other/torrents", localPathPrefix = Path.Combine(far, "downloads") },
                new { clientPathPrefix = "/other/torrents/", localPathPrefix = Path.Combine(far, "downloads") }
            },
            mappingsVersion = await MappingsVersionOf(clientId)
        }), 400, "duplicate_mapping", "Duplicate client prefixes are refused");
        Assert((await ordinary.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings",
            new { pathMappings = Array.Empty<object>() })).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot change mappings");
        var mappings = await ReadAsync(await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[] { new { clientPathPrefix = "/other/torrents", localPathPrefix = Path.Combine(far, "downloads") } },
            mappingsVersion = await MappingsVersionOf(clientId)
        }), 200, "Administrator saves a mapping");
        Assert(mappings.EnumerateArray().Single().GetProperty("verifiedAt").ValueKind == JsonValueKind.String,
            "A mapping whose folder can hardlink into a library records VerifiedAt");
        // Whole-review chunk 3b, P2 2: a replacement whose save fails part-way (here, inserting the new set) keeps the
        // existing mappings rather than leaving the client with none.
        await using (var trigger = new ModDbContext(dbPath))
            await trigger.Database.ExecuteSqlRawAsync("CREATE TRIGGER jfmod_fail_mapping BEFORE INSERT ON DownloadClientPathMappings " +
                "BEGIN SELECT RAISE(ABORT, 'injected: the replacement fails'); END;");
        HttpResponseMessage interruptedMapping;
        var versionBeforeInterrupt = await MappingsVersionOf(clientId);
        try
        {
            interruptedMapping = await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
            {
                pathMappings = new[] { new { clientPathPrefix = "/elsewhere/torrents", localPathPrefix = Path.Combine(far, "downloads") } },
                mappingsVersion = versionBeforeInterrupt
            });
        }
        finally
        {
            await using var drop = new ModDbContext(dbPath);
            await drop.Database.ExecuteSqlRawAsync("DROP TRIGGER jfmod_fail_mapping;");
        }

        var keptMappings = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings"));
        Assert(!interruptedMapping.IsSuccessStatusCode && keptMappings.GetArrayLength() == 1 &&
            keptMappings[0].GetProperty("clientPathPrefix").GetString() == "/other/torrents",
            "An interrupted mapping replacement keeps the existing mappings (whole-review c3bf2): " + keptMappings.GetRawText());
        Assert(!Directory.EnumerateFiles(far, ".jfmod-probe-*", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(media, ".jfmod-probe-*", SearchOption.AllDirectories).Any(), "The probe's dot-prefixed files are removed");
        // Codex delta review 6, P2: a mapping save does not advance the client's revision, so the mappings carry a version of
        // their own. Another administrator saves the same mappings again; a copy read before that is refused, and the mappings
        // stay as the other administrator saved them.
        var readMappings = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/DownloadClients")).EnumerateArray()
            .Single(client => client.GetProperty("id").AsGuid() == clientId);
        var staleVersion = readMappings.GetProperty("mappingsVersion").GetString();
        var revisionBefore = readMappings.GetProperty("revision").GetInt32();
        await ReadAsync(await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = new[] { new { clientPathPrefix = "/other/torrents", localPathPrefix = Path.Combine(far, "downloads") } },
            mappingsVersion = staleVersion
        }), 200, "Another administrator saves the mappings with the version they read");
        var staleSave = await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings", new
        {
            pathMappings = Array.Empty<object>(), revision = revisionBefore, mappingsVersion = staleVersion
        });
        var afterStale = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings"));
        Assert(staleSave.StatusCode == HttpStatusCode.Conflict && (await staleSave.Content.ReadAsStringAsync()).Contains("revision_conflict",
                StringComparison.Ordinal) && afterStale.GetArrayLength() == 1,
            $"A mapping save from a stale copy is refused even though the client's revision did not move (Codex delta review 6, P2): " +
            $"{(int)staleSave.StatusCode}, {afterStale.GetArrayLength()} mapping(s) left");
        // Final review, finding 4: a replacement that names no version is refused, and so is a client PATCH that replaces the
        // mappings from a stale copy; the mappings stay as they are.
        var unversioned = await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings",
            new { pathMappings = Array.Empty<object>() });
        var clientNow = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/DownloadClients")).EnumerateArray()
            .Single(client => client.GetProperty("id").AsGuid() == clientId);
        object ClientPatch(string? version) => new
        {
            name = clientNow.GetProperty("name").GetString(), kind = clientNow.GetProperty("kind").GetString(),
            baseUrl = clientNow.GetProperty("baseUrl").GetString(), username = clientNow.GetProperty("username").GetString() ?? "",
            password = new { action = "unchanged" }, enabled = clientNow.GetProperty("enabled").GetBoolean(),
            label = clientNow.GetProperty("label").GetString(), downloadDirectory = clientNow.GetProperty("downloadDirectory").GetString(),
            localDirectory = clientNow.GetProperty("localDirectory").GetString(), openUrl = (string?)null,
            pathMappings = Array.Empty<object>(), mappingsVersion = version, revision = clientNow.GetProperty("revision").GetInt32()
        };
        var stalePatch = await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}", ClientPatch(staleVersion));
        var afterPatch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/PathMappings"));
        Assert(unversioned.StatusCode == HttpStatusCode.Conflict && stalePatch.StatusCode == HttpStatusCode.Conflict &&
            (await stalePatch.Content.ReadAsStringAsync()).Contains("revision_conflict", StringComparison.Ordinal) && afterPatch.GetArrayLength() == 1,
            $"A mapping replacement without a version, or a client PATCH from a stale copy, is refused (final review, finding 4): " +
            $"{(int)unversioned.StatusCode}, {(int)stalePatch.StatusCode}, {afterPatch.GetArrayLength()} mapping(s) left");
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
        // The background monitor may already have read the torrent (0 % done): the progress is what the client reports, or none.
        Assert(importA.State == ImportStates.Waiting && importA.Progress is null or 0, $"The import waits with no invented progress: {importA.State}, {importA.Progress}");
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
        // Whole-review chunk 1, P2 4: the floor is raised above what the torrent has seeded. The goal met earlier no longer
        // counts: the library file is blocked again until the new goal is met, and lowering the floor frees it.
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(2, floorRatio: 5.0)), 200,
            "Administrator raises the seed floor");
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        var raised = PreviewItem(preview, completedA.BindingId!.Value);
        Assert(raised.GetProperty("state").GetString() == "blocked" && raised.GetProperty("reason").GetString() == "seed_goal_unmet",
            $"A goal met under the old floor does not override the raised one (whole-review c1f4): {raised.GetProperty("state")}/{raised.GetProperty("reason")}");
        await Tick();
        await using (var database = new ModDbContext(dbPath))
            Assert((await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id)).GoalMetAt is null,
                "The seed release forgets a goal that is no longer met");
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(3)), 200, "Administrator restores the floor");
        await Tick();
        preview = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"));
        Assert(PreviewItem(preview, completedA.BindingId!.Value).GetProperty("state").GetString() == "due",
            "With the goal met again under the current floor, the library file is due again");
        // Codex round 2 P2: what the torrent last showed is not enough. Before the next seed tick, the client reports the
        // torrent incomplete, then raises its own ratio goal above what it has seeded: the library file is blocked each time.
        var heldSeedA = transmission.Torrents[hashA];
        var seededBytes = heldSeedA.Files[0].Completed;
        heldSeedA.Files[0].Completed = seededBytes - 1;
        var incomplete = PreviewItem(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview")), completedA.BindingId!.Value);
        heldSeedA.Files[0].Completed = seededBytes;
        var (ratioMode, ratioLimit) = (heldSeedA.SeedRatioMode, heldSeedA.SeedRatioLimit);
        (heldSeedA.SeedRatioMode, heldSeedA.SeedRatioLimit) = (1, 3.0);
        var clientRaised = PreviewItem(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview")), completedA.BindingId!.Value);
        (heldSeedA.SeedRatioMode, heldSeedA.SeedRatioLimit) = (ratioMode, ratioLimit);
        Assert(incomplete.GetProperty("reason").GetString() == "seeding_incomplete" &&
            clientRaised.GetProperty("reason").GetString() == "seed_goal_unmet",
            $"Retention asks the client before a seed-shared file is due: {incomplete.GetProperty("state")}/{incomplete.GetProperty("reason")}, " +
            $"{clientRaised.GetProperty("state")}/{clientRaised.GetProperty("reason")}");
        var reclaim = await host.Service<RetentionExecutor>().ReclaimAsync(completedA.BindingId!.Value, CancellationToken.None);
        Assert(reclaim.State == "completed" && reclaim.PhysicalBytesReleased == 0 && !File.Exists(destinationA) &&
            inspector.TryInspect(sourceA, out var afterReclaim) && afterReclaim.HardlinkCount == 1,
            $"Retention unlinks only the library path and honestly reports 0 bytes released while the seeding copy remains: {reclaim.State}/{reclaim.Reason}");
        // Codex delta reviews 1 and 5, P1: right after the release reads the torrent under the gate, the client relocates it
        // into a library folder holding a file of the same name. The release deletes nothing at all: the client only forgets
        // the torrent, and the checked download stays on disk for the administrator's manual cleanup (user decision
        // 2026-10-02).
        transmission.Roots["/data/library"] = world.Movies.Location;
        var heldA = transmission.Torrents[hashA];
        var relocatedAFolder = Path.Combine(world.Movies.Location, "Relocated A");
        Directory.CreateDirectory(relocatedAFolder);
        var decoyA = Path.Combine(relocatedAFolder, heldA.Files[0].Name);
        await File.WriteAllBytesAsync(decoyA, new byte[2048]);
        // The tick reads the torrent first; the release then reads it again under the gate. The second read is the gated one.
        var readsOfA = 0;
        transmission.AfterGet = asked =>
        {
            if (asked is null || !asked.Contains(hashA) || ++readsOfA < 2) return Task.CompletedTask;
            heldA.DownloadDir = "/data/library/Relocated A";
            transmission.AfterGet = null;
            return Task.CompletedTask;
        };
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(4, seedRelease: true)), 200,
            "Administrator turns seed release on");
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id)).State ==
                SeedReleaseStates.Detached ? true : (bool?)null;
        }, "The seed release detaches the torrent after the late reclaim");
        transmission.AfterGet = null;
        Assert(File.Exists(decoyA) && File.Exists(sourceA) && !transmission.Torrents.ContainsKey(hashA) &&
            transmission.RemoveDeletedData.All(deleted => !deleted),
            $"A torrent relocated into a library right after the gated read is only forgotten by the client; nothing is deleted " +
            $"(Codex delta reviews 1 and 5): library file {File.Exists(decoyA)}, download {File.Exists(sourceA)}, " +
            $"delete-data requests {transmission.RemoveDeletedData.Count(deleted => deleted)}");
        File.Delete(decoyA);
        Directory.Delete(relocatedAFolder);
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id);
            var manifestA = TorrentDataRemoval.Deserialize(seed.CleanupManifest);
            Assert(seed.Reason == SeedReleaseReasons.CleanupPending && seed.PhysicalBytesReleased is null && seed.HardlinkCountBefore == 1 &&
                manifestA is { Count: 1 } && inspector.TryInspect(sourceA, out var keptA) && manifestA[0].Path == keptA.CanonicalPath &&
                manifestA[0].PhysicalIdentity == keptA.PhysicalIdentity && manifestA[0].Size == Size,
                $"The detached release keeps its checked file listed for the manual cleanup and claims nothing freed: {seed.Reason}, " +
                $"{seed.CleanupManifest}");
            var released = await database.History.AsNoTracking().SingleAsync(history => history.Id == seed.Id);
            Assert(released.EventType == "seeding_released" && released.Summary.Contains("Nothing was deleted", StringComparison.Ordinal),
                "History says seeding stopped and nothing was deleted: " + released.Summary);
            Assert((await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == grabA)).ActiveHash is null,
                "The grab gives its hash back once the client forgot the torrent");
        }

        // A detached release stays detached: later ticks, with the torrent long gone from the client, never complete it.
        await Tick();
        await Tick();
        await using (var database = new ModDbContext(dbPath))
            Assert((await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedA.Id)).State ==
                SeedReleaseStates.Detached && File.Exists(sourceA), "A detached release survives ticks with its files kept (Codex delta review 5, P2)");

        queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(!queue.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").AsGuid() == completedA.Id),
            "A detached seeding copy leaves the queue");

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

        // ================= Whole-review P1 5, 6 and 7: every removal with data checks the torrent as it is now.
        async Task<SeedReleaseOperation> SeedB()
        {
            await using var database = new ModDbContext(dbPath);
            return await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
        }

        var heldB = transmission.Torrents[hashB];
        var (downloadDirB, fileB) = (heldB.DownloadDir, heldB.Files[0]);
        var destinationB = completedB.DestinationPath!;
        // P1 5: the torrent is re-pointed at its library hardlink, the original download left where it was. Saved-path checks
        // pass, but the client would delete the files where the torrent is now: the library file.
        transmission.Roots["/data/library"] = world.Movies.Location;
        heldB.DownloadDir = "/data/library/" + Path.GetFileName(Path.GetDirectoryName(destinationB));
        heldB.Files[0] = new HeldFile { Name = Path.GetFileName(destinationB), Length = fileB.Length, Completed = fileB.Completed };
        heldB.UploadRatio = 1.5;
        await Tick();
        await Tick();
        var relocated = await SeedB();
        Assert(relocated.State == SeedReleaseStates.Blocked && relocated.Reason == SeedReleaseReasons.SeedingInsideLibrary &&
            File.Exists(destinationB) && transmission.Torrents.ContainsKey(hashB),
            $"A torrent now located in a library is never removed with its data (whole-review P1 5): {relocated.State}/{relocated.Reason}");
        heldB.DownloadDir = downloadDirB;
        heldB.Files[0] = fileB;

        // P1 6: the library link goes missing, and an older reclamation at the same pathname (another file) is on record.
        var awayB = destinationB + ".away";
        File.Move(destinationB, awayB);
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            database.RetentionOperations.Add(new RetentionOperation
            {
                ActionId = Guid.NewGuid(), BindingId = Guid.NewGuid(), EntryId = ids["movieB"], JellyfinItemId = Guid.NewGuid(),
                TargetLibraryId = world.Movies.Id, PolicyVersion = 1, MediaPath = seed.LibraryPath, StorageIdentity = "earlier",
                PhysicalIdentity = "an-earlier-file", LogicalBytes = Size, HardlinkCountBefore = 1, State = "completed", Reason = "reclaimed",
                PreparedAt = seed.PreparedAt.AddDays(-2), CompletedAt = seed.PreparedAt.AddDays(-2)
            });
            await database.SaveChangesAsync();
        }

        await Tick();
        await Tick();
        var stale = await SeedB();
        Assert(stale.State == SeedReleaseStates.Blocked && stale.Reason == SeedReleaseReasons.LibraryLinkUnexpected &&
            File.Exists(completedB.SourceLocalPath!) && transmission.Torrents.ContainsKey(hashB),
            $"A reclamation of an earlier file at the same pathname does not authorize deleting this download's last copy " +
            $"(whole-review P1 6): {stale.State}/{stale.Reason}");

        // P1 7: a removal left in progress (the client rejected it) is retried only after every safeguard is read again.
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedB.Id);
            seed.State = SeedReleaseStates.Removing;
            seed.Reason = null;
            seed.HardlinkCountBefore = 2;
            seed.RemovingAt = time.GetUtcNow().UtcDateTime;
            await database.SaveChangesAsync();
        }

        await Tick();
        await Tick();
        var retried = await SeedB();
        Assert(retried.State == SeedReleaseStates.Blocked && retried.Reason == SeedReleaseReasons.LibraryLinkUnexpected &&
            File.Exists(completedB.SourceLocalPath!) && transmission.Torrents.ContainsKey(hashB),
            $"A retried removal re-checks the library link and keeps the last copy (whole-review P1 7): {retried.State}/{retried.Reason}");
        File.Move(awayB, destinationB);
        await using (var database = new ModDbContext(dbPath))
        {
            await database.RetentionOperations.Where(value => value.PhysicalIdentity == "an-earlier-file").ExecuteDeleteAsync();
        }

        // Codex re-review P1-a: the release has read the client and waits for the retention gate; meanwhile the torrent is
        // re-pointed at its library hardlink. The removal must be decided on the torrent as it is after the wait. The tick's
        // own read is observed before the torrent moves (Codex delta review 1, P2).
        async Task WhileReleaseWaitsAsync(Action change)
        {
            var gateLease = await host.Service<RetentionExecutionGate>().AcquireAsync(CancellationToken.None);
            var callsBefore = transmission.GetCalls;
            var waitingTick = Tick();
            await WaitAsync(() => Task.FromResult(transmission.GetCalls > callsBefore ? true : (bool?)null),
                "The tick reads the client before the change");
            change();
            await gateLease.DisposeAsync();
            await waitingTick;
        }

        heldB.UploadRatio = 1.5;
        await WhileReleaseWaitsAsync(() =>
        {
            heldB.DownloadDir = "/data/library/" + Path.GetFileName(Path.GetDirectoryName(destinationB));
            heldB.Files[0] = new HeldFile { Name = Path.GetFileName(destinationB), Length = fileB.Length, Completed = fileB.Completed };
        });
        var movedDuringWait = await SeedB();
        Assert(File.Exists(destinationB) && transmission.Torrents.ContainsKey(hashB) && movedDuringWait.State == SeedReleaseStates.Blocked &&
            movedDuringWait.Reason == SeedReleaseReasons.SeedingInsideLibrary,
            $"A torrent moved into a library while its release waited for the gate is never removed with its data (Codex re-review P1-a): " +
            $"{movedDuringWait.State}/{movedDuringWait.Reason}, library file {File.Exists(destinationB)}");
        heldB.DownloadDir = downloadDirB;
        heldB.Files[0] = fileB;

        // Codex delta review 1, P2: the client raises the torrent's ratio goal while the release waits for the gate. The fresh
        // read decides: the release goes back to waiting and nothing is removed.
        await WhileReleaseWaitsAsync(() => (heldB.SeedRatioMode, heldB.SeedRatioLimit) = (1, 5.0));
        var raisedDuringWait = await SeedB();
        Assert(raisedDuringWait is { State: SeedReleaseStates.Waiting, Reason: SeedReleaseReasons.GoalUnmet } &&
            transmission.Torrents.ContainsKey(hashB) && File.Exists(completedB.SourceLocalPath!),
            $"A goal raised while the release waited sends it back to waiting (Codex delta review 1, P2): {raisedDuringWait.State}/{raisedDuringWait.Reason}");
        (heldB.SeedRatioMode, heldB.SeedRatioLimit) = (2, 2);

        // The client refuses the first removal; the retry finds the goal raised meanwhile and goes back to waiting. Two
        // refusals cover a tick of the import monitor's own that may land before the goal is raised.
        transmission.FailRemoves = 2;
        await Tick();
        var refused = await SeedB();
        (heldB.SeedRatioMode, heldB.SeedRatioLimit) = (1, 5.0);
        await Tick();
        var raisedBeforeRetry = await SeedB();
        Assert(refused.State == SeedReleaseStates.Removing && refused.Error is not null &&
            raisedBeforeRetry is { State: SeedReleaseStates.Waiting, Reason: SeedReleaseReasons.GoalUnmet } &&
            transmission.Torrents.ContainsKey(hashB) && File.Exists(completedB.SourceLocalPath!),
            $"A retried removal checks the goal again first (Codex delta review 1, P2): {refused.State} then {raisedBeforeRetry.State}/{raisedBeforeRetry.Reason}");

        // Codex delta review 1, P1: the client refuses again; on the retry, after its gated read and before it carries out the
        // request, it relocates the torrent onto the library file. The retry only has the client forget the torrent: the
        // library file and the download both stay. Both are armed while the goal is still unmet, so the import monitor's own
        // ticks, which run beside the test's, meet them in the same order.
        var removeCallsBeforeRetry = transmission.RemoveCalls;
        transmission.FailRemoves = 1;
        transmission.OnRemove = removed =>
        {
            if (!removed.Contains(hashB)) return Task.CompletedTask;
            transmission.OnRemove = null;
            heldB.DownloadDir = "/data/library/" + Path.GetFileName(Path.GetDirectoryName(destinationB));
            heldB.Files[0] = new HeldFile { Name = Path.GetFileName(destinationB), Length = fileB.Length, Completed = fileB.Completed };
            return Task.CompletedTask;
        };
        (heldB.SeedRatioMode, heldB.SeedRatioLimit) = (2, 2);
        var relocatedRetry = await WaitAsync(async () =>
        {
            await Tick();
            var seed = await SeedB();
            return seed.State == SeedReleaseStates.Detached ? seed : null;
        }, "The refused removal is retried and the client forgets the torrent");
        transmission.OnRemove = null;
        Assert(transmission.RemoveCalls - removeCallsBeforeRetry >= 2 && transmission.FailRemoves <= 0 &&
            File.Exists(destinationB) && File.Exists(completedB.SourceLocalPath!) && !transmission.Torrents.ContainsKey(hashB) &&
            transmission.RemoveDeletedData.All(deleted => !deleted),
            $"A retry whose torrent is relocated into a library after its gated read deletes nothing (Codex delta reviews 1 and 5): " +
            $"{transmission.RemoveCalls - removeCallsBeforeRetry} remove request(s), {relocatedRetry.State}/{relocatedRetry.Reason}, " +
            $"library file {File.Exists(destinationB)}");

        // The release detached with the relocated retry above: its accounting is checked as it stands.
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(seed.PhysicalBytesReleased is null && seed.CreditedRetentionOperationId is null && seed.Reason == SeedReleaseReasons.CleanupPending &&
                inspector.TryInspect(completedB.DestinationPath!, out var libraryB) && libraryB.HardlinkCount == 2 &&
                File.Exists(completedB.SourceLocalPath!), "The library file and its download link both stay, and nothing is claimed freed");
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
        // Whole-review chunk 3a, P2 5: an administrator limited to TV sees no movie's seed, hash or path.
        var tvSeeding = Json.Parse(await tvAdmin.GetStringAsync("/JellyfinMod/Seeding"));
        Assert(!tvSeeding.EnumerateArray().Any(item => item.GetProperty("importOperationId").AsGuid() == completedC.Id ||
                item.GetProperty("infoHash").GetString() == hashC),
            "A TV-only administrator's seeding list leaves out a movie's seed (whole-review c3af5): " + tvSeeding.GetRawText());
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
        var blockedK = await BlockedAsync(dbPath, grabK, Tick);
        Assert(blockedK.Reason == ImportReasons.NoVideoFile, "A torrent without video blocks as no_video_file");

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
        var archiveFolder = Path.GetDirectoryName(archiveData)!;
        var libraryFilesBeforeRemove = LibraryFiles(world);
        var inspectedArchive = inspector.TryInspect(archiveData, out var archiveBefore);
        // Codex delta reviews 3 and 5, P1: while the plugin has the client forget the torrent, the client really moves the
        // torrent's folder into a library, and another file appears at the archive's old name. Nothing is deleted at all: the
        // removal only records the checked files for the administrator's manual cleanup (user decision 2026-10-02).
        var relocatedG = Path.Combine(world.Movies.Location, "Relocated G");
        transmission.OnRemove = removed =>
        {
            if (!removed.Contains(hashG)) return Task.CompletedTask;
            transmission.OnRemove = null;
            Directory.Move(archiveFolder, relocatedG);
            Directory.CreateDirectory(archiveFolder);
            File.WriteAllBytes(archiveData, new byte[777]);
            return Task.CompletedTask;
        };
        var removedG = await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedG.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true, blocklist = true }) }), 200, "Remove with the client and a blocklist");
        transmission.OnRemove = null;
        var relocatedArchive = Path.Combine(relocatedG, "archive.rar");
        Assert(removedG.GetProperty("state").GetString() == "cancelled" && !transmission.Torrents.ContainsKey(hashG) &&
            File.Exists(relocatedArchive) && File.Exists(archiveData) && new FileInfo(archiveData).Length == 777 &&
            transmission.RemoveDeletedData.All(deleted => !deleted),
            $"A queue removal deletes nothing: neither what the client moved into a library nor a different file at a checked name " +
            $"(Codex delta reviews 3 and 5, P1): relocated {File.Exists(relocatedArchive)}, replacement {File.Exists(archiveData)}");
        await using (var database = new ModDbContext(dbPath))
        {
            var listedG = TorrentDataRemoval.Deserialize((await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == blockedG.Id))
                .CleanupManifest);
            Assert(inspectedArchive && listedG is { Count: 3 } && listedG.Any(file => file.Path == archiveBefore.CanonicalPath &&
                    file.PhysicalIdentity == archiveBefore.PhysicalIdentity && file.Size == Size),
                $"The removal records every checked file, as it was checked, for the manual cleanup: {listedG?.Count} listed");
        }

        Directory.Delete(relocatedG, true);
        Directory.Delete(archiveFolder, true);
        Assert(LibraryFiles(world).SequenceEqual(libraryFilesBeforeRemove), "The library is as it was");
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

        // Whole-review P1 9: an alias in the download folder points into a library. The mapped path looks like a download,
        // but the client would delete the library file through it.
        var victimFolder = Path.Combine(world.Movies.Location, "Alias Victim (2020)");
        Directory.CreateDirectory(victimFolder);
        var victim = Path.Combine(victimFolder, "Alias Victim (2020).mkv");
        await File.WriteAllBytesAsync(victim, new byte[1024]);
        var alias = transmission.Local("/data/torrents/jfmod/alias");
        File.CreateSymbolicLink(alias, victimFolder);
        var heldK = transmission.Torrents[hashK];
        var (downloadDirK, filesK) = (heldK.DownloadDir, heldK.Files.ToList());
        heldK.DownloadDir = "/data/torrents/jfmod/alias";
        heldK.Files.Clear();
        heldK.Files.Add(new HeldFile { Name = Path.GetFileName(victim), Length = 1024, Completed = 1024 });
        using (var aliasRemoval = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedK.Id}")
               { Content = JsonContent.Create(new { removeFromClient = true, blocklist = false }) }))
        {
            var aliasBody = await aliasRemoval.Content.ReadAsStringAsync();
            Assert(aliasRemoval.StatusCode == HttpStatusCode.Conflict && aliasBody.Contains(SeedReleaseReasons.SeedingInsideLibrary, StringComparison.Ordinal) &&
                File.Exists(victim) && transmission.Torrents.ContainsKey(hashK),
                $"Queue removal resolves symbolic links and never deletes library media through an alias (whole-review P1 9): " +
                $"{(int)aliasRemoval.StatusCode} {aliasBody} victim={File.Exists(victim)}");
        }

        heldK.DownloadDir = downloadDirK;
        heldK.Files.Clear();
        heldK.Files.AddRange(filesK);
        File.Delete(alias);
        Directory.Delete(victimFolder, true);

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
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Import", ImportSettings(5, seedRelease: true, visible: true)), 200,
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
        // Live finding 12: an import still scanning when the process stops lost the host's pending refresh with it.
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieS", "movie", 122, "Restart Scan Movie", 2021, "tt0000122", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        var fixtureS = TorrentFixture.Single("Restart.Scan.Movie.2021.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["movieS"] = fixtureS.Bytes;
        transmission.Register(fixtureS);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new("Restart.Scan.Movie.2021.1080p.WEB-DL-GRP", "guid-movieS", torznab.Download("movieS"), Size, 25,
                new() { ["imdbid"] = "0000122" }));
        var (grabS, hashS) = await GrabAsync(admin, ids["movieS"], null, fixtureS);
        transmission.Progress(hashS, 1.0);
        var scanningS = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabS);
            return operation.State == ImportStates.Scanning ? operation : null;
        }, "A third import reaches scanning and stays unbound across the restart");
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
        // No time passes on the shifted clock here, so without a repeated report this import would never bind.
        var repeatedS = await CompleteAsync(dbPath, grabS, Tick);
        Assert(repeatedS.Id == scanningS.Id && repeatedS.ScanAttempts == 1 &&
            world.Native.ScanRequests.Count(path => path == scanningS.DestinationPath) == 2 && repeatedS.ScanRequestedAt > scanningS.ScanRequestedAt,
            "A scan requested before a restart is reported once more after it and completes without escalating (live finding 12)");

        // Codex delta reviews 3 and 5, P2: a seed release records its checked files, the client forgets the torrent and drops the
        // connection before answering, and the process stops before the release learns the outcome. Everything here is the
        // production path: the list is the one the release wrote. After the restart the release detaches once, with one
        // event, every file of the torrent still on disk and still listed.
        var heldH = transmission.Torrents[hashH];
        var filesH = heldH.Files.Select(file => transmission.Local(heldH.DownloadDir + "/" + file.Name)).Where(File.Exists).ToList();
        heldH.UploadRatio = 10;
        heldH.SecondsSeeding = (long)TimeSpan.FromDays(30).TotalSeconds;
        transmission.DropRemoveAnswers = 1;
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id);
            return seed.State == SeedReleaseStates.Removing && seed.Error is not null && !transmission.Torrents.ContainsKey(hashH)
                ? true : (bool?)null;
        }, "The release records its files and asks the client, which forgets the torrent without answering");
        string? listedBeforeRestart;
        await using (var database = new ModDbContext(dbPath))
            listedBeforeRestart = (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id))
                .CleanupManifest;
        await StopAsync();
        await StartAsync();
        admin = host.Client(world.Admin, true);
        monitor = host.Service<ImportMonitor>();
        await WaitAsync(async () =>
        {
            await Tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id)).State ==
                SeedReleaseStates.Detached ? true : (bool?)null;
        }, "An interrupted detach is finished after a restart");
        await Tick();
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id);
            var listedH = TorrentDataRemoval.Deserialize(seed.CleanupManifest) ?? [];
            Assert(seed.Reason == SeedReleaseReasons.CleanupPending && seed.CleanupManifest == listedBeforeRestart && filesH.Count > 1 &&
                filesH.All(File.Exists) && filesH.All(path => inspector.TryInspect(path, out var file) && listedH.Any(item => item.Path == file.CanonicalPath)),
                $"After a restart the interrupted release keeps every file of the forgotten torrent on disk and listed (Codex delta reviews 3 " +
                $"and 5, P2): {seed.State}/{seed.Reason}, {filesH.Count(File.Exists)} of {filesH.Count} files on disk, {listedH.Count} listed");
        }

        Assert(await HistoryCount(dbPath, ids["movieH"], "seeding_released") == 1, "The interrupted release writes exactly one history event");

        // ================= Detach and keep (user decisions 2026-10-02: the client only forgets a torrent and nothing is deleted;
        // 0.1.0.0 has no cleanup tool, which is planned for a later version). Nothing above deleted a download. The recorded
        // files stay on disk, and automatic retention never unlinks one, also not through another name for it.
        // Codex delta review 7, P2 5: a release whose detach had recorded its files, sent back to waiting by a retry (its seed
        // goal rose), whose torrent then leaves the client outside JellyfinMod. It detaches again with its files still listed;
        // it does not complete with them unresolved.
        string? manifestB;
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedB.Id);
            manifestB = seed.CleanupManifest;
            Assert(manifestB is not null && !transmission.Torrents.ContainsKey(hashB), "Precondition: release B recorded its files and its torrent is gone");
            seed.State = SeedReleaseStates.Waiting;
            seed.Reason = SeedReleaseReasons.GoalUnmet;
            await database.SaveChangesAsync();
        }

        await Tick();
        await using (var database = new ModDbContext(dbPath))
        {
            var again = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(again is { State: SeedReleaseStates.Detached, Reason: SeedReleaseReasons.CleanupPending } && again.CleanupManifest == manifestB &&
                again.CompletedAt is null, $"A waiting release with recorded files whose torrent left the client detaches with them listed: " +
                $"{again.State}/{again.Reason}, list kept {again.CleanupManifest == manifestB}");
        }

        // An older build's release, detached without a list of its files, is shown for manual cleanup and never resolved
        // automatically (Codex delta review 5, P2). No path of this build leaves that state, so it is written as the older build
        // left it: removing, no list, the torrent gone.
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedB.Id);
            seed.State = SeedReleaseStates.Removing;
            seed.Reason = null;
            seed.CleanupManifest = null;
            await database.SaveChangesAsync();
        }

        await Tick();
        await using (var database = new ModDbContext(dbPath))
        {
            var legacy = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(legacy is { State: SeedReleaseStates.Detached, Reason: SeedReleaseReasons.ManualCleanup, CleanupManifest: null } &&
                File.Exists(completedB.SourceLocalPath!), $"A release without a trustworthy list is marked for manual cleanup: {legacy.State}/{legacy.Reason}");
        }


        // Every torrent's files must resolve for a seed-indexed reclaim: the foreign torrent's data exists.
        var foreignData = transmission.Local("/data/torrents/jfmod/Foreign.Torrent.mkv");
        if (!File.Exists(foreignData)) await File.WriteAllBytesAsync(foreignData, new byte[1000]);
        // Codex delta reviews 5 and 7, P1: a download folder that a second bind mount also shows inside a library. The queue
        // removal records the file (its path is not under any library path), and automatic retention must not unlink it through
        // the library's name for it. The Pi runner mounts one folder twice for this.
        var aliasA = Environment.GetEnvironmentVariable("JFMOD_ALIAS_A");
        var aliasB = Environment.GetEnvironmentVariable("JFMOD_ALIAS_B");
        Assert(aliasA is { Length: > 0 } && aliasB is { Length: > 0 } && Directory.Exists(aliasA) && Directory.Exists(aliasB),
            "The bind-mount case runs: JFMOD_ALIAS_A and JFMOD_ALIAS_B name two mounts of one folder (the Pi runner sets them)");
        // One host folder, mounted twice: its "dl" subfolder is the download folder through the first mount and a library
        // ("Alias Downloads") through the second. Its "lib" subfolder is an ordinary library on the download side's mount, so
        // the client's mapping for the folder verifies by hardlinking into it.
        var aliasRoot = "p5-" + Guid.NewGuid().ToString("N")[..8];
        var aliasFolder = Path.Combine(aliasB!, aliasRoot);
        Directory.CreateDirectory(Path.Combine(aliasB!, aliasRoot, "lib"));
        Directory.CreateDirectory(Path.Combine(aliasB!, aliasRoot, "dl"));
        var aliasLibraries = (List<TestLibrary>)world.Native.Libraries;
        aliasLibraries.Add(new TestLibrary
        {
            Id = Guid.NewGuid(), Name = "Alias Movies", CollectionType = Jellyfin.Data.Enums.CollectionType.movies,
            Location = Path.Combine(aliasB!, aliasRoot, "lib")
        });
        var aliasDownloads = new TestLibrary
        {
            Id = Guid.NewGuid(), Name = "Alias Downloads", CollectionType = Jellyfin.Data.Enums.CollectionType.movies,
            Location = Path.Combine(aliasA!, aliasRoot, "dl")
        };
        aliasLibraries.Add(aliasDownloads);
        transmission.Roots["/data/alias"] = Path.Combine(aliasB!, aliasRoot);
        // The client's own mapping for the aliased folder, saved through the API as an administrator would.
        var aliasClient = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/DownloadClients")).EnumerateArray().First();
        var aliasClientId = aliasClient.GetProperty("id").AsGuid();
        var aliasMappings = aliasClient.GetProperty("pathMappings").EnumerateArray()
            .Select(mapping => new { clientPathPrefix = mapping.GetProperty("clientPathPrefix").GetString(),
                localPathPrefix = mapping.GetProperty("localPathPrefix").GetString() })
            .Append(new { clientPathPrefix = (string?)"/data/alias/dl", localPathPrefix = (string?)Path.Combine(aliasB!, aliasRoot, "dl") }).ToArray();
        var mappedAlias = await ReadAsync(await admin.PutAsJsonAsync($"/JellyfinMod/Settings/DownloadClients/{aliasClientId}/PathMappings",
            new { pathMappings = aliasMappings, mappingsVersion = await MappingsVersionOf(aliasClientId) }), 200,
            "Administrator maps the aliased download folder");
        Assert(mappedAlias.EnumerateArray().Single(mapping => mapping.GetProperty("clientPathPrefix").GetString() == "/data/alias/dl")
            .GetProperty("verifiedAt").ValueKind == JsonValueKind.String, "The aliased download folder's mapping verifies");
        // The torrent's one file is a movie in its own folder, as the alias library will scan it.
        var heldAliasK = transmission.Torrents[hashK];
        heldAliasK.DownloadDir = "/data/alias/dl";
        heldAliasK.Files.Clear();
        heldAliasK.Files.Add(new HeldFile { Name = "Alias Movie (2021) [tmdbid-121]/Alias Movie (2021).mkv", Length = Size, Completed = Size });
        var aliasFile = transmission.Local("/data/alias/dl/" + heldAliasK.Files[0].Name);
        Directory.CreateDirectory(Path.GetDirectoryName(aliasFile)!);
        await File.WriteAllBytesAsync(aliasFile, new byte[Size]);
        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{blockedK.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true, blocklist = false }) }), 200, "Remove the aliased torrent from the queue");
        // The alias library scans the same directory entry under its own name, and the catalog binds it.
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieAlias", "movie", 121, "Alias Movie", 2021, "tt0000121", null, aliasDownloads.Id);
            await database.SaveChangesAsync();
        }

        var aliasLibraryPath = Path.Combine(aliasA!, aliasRoot, "dl", "Alias Movie (2021) [tmdbid-121]", "Alias Movie (2021).mkv");
        world.Native.Scan(aliasLibraryPath);
        var aliasBinding = await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EntryBindings.AsNoTracking().FirstOrDefaultAsync(value => value.EntryId == ids["movieAlias"]);
        }, "The alias library's file is bound");
        // As for the first title: retention sees it watched, then the window passes and it is due. The earlier torrent in an
        // unmapped folder would block every seed-indexed reclaim (the index is incomplete); it leaves the client for this step.
        var unmappedTorrents = transmission.Torrents.Where(pair => pair.Value.Files.Any(file => file.Name.Contains("Unmapped.", StringComparison.Ordinal)))
            .ToList();
        foreach (var (hash, _) in unmappedTorrents) transmission.Torrents.TryRemove(hash, out _);
        await host.Service<RetentionPolicyService>().SyncAsync(configuration, CancellationToken.None);
        await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        time.Offset += TimeSpan.FromDays(2);
        var aliasDue = PreviewItem(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview")), aliasBinding.Id);
        Assert(aliasDue.GetProperty("state").GetString() == "due",
            $"The alias library's file is due for retention: {aliasDue.GetProperty("state")}/{aliasDue.GetProperty("reason")}");
        var aliasReclaim = await host.Service<RetentionExecutor>().ReclaimAsync(aliasBinding.Id, CancellationToken.None);
        Assert(aliasReclaim.Reason == "retained_download" && File.Exists(aliasFile) && File.Exists(aliasLibraryPath),
            $"Automatic retention never unlinks a download kept for the cleanup through a library's other name for it (Codex delta " +
            $"review 7, P1 2): {aliasReclaim.State}/{aliasReclaim.Reason}, file kept {File.Exists(aliasFile)}");

        // Codex delta reviews 9, P1 2 and 11, P1 1: the cases retention once missed. Each title is a movie in its own folder of
        // the aliased download folder, scanned through the alias library and made due; its reclaim must be refused.
        async Task<RetentionExecutionResult> ReclaimThroughAliasAsync(string key, int tmdb, string title, int year)
        {
            await using (var database = new ModDbContext(dbPath))
            {
                AddEntry(database, ids, key, "movie", tmdb, title, year, $"tt{tmdb:D7}", null, aliasDownloads.Id);
                await database.SaveChangesAsync();
            }

            world.Native.Scan(Path.Combine(aliasA!, aliasRoot, "dl", $"{title} ({year}) [tmdbid-{tmdb}]", $"{title} ({year}).mkv"));
            var binding = await WaitAsync(async () =>
            {
                await using var database = new ModDbContext(dbPath);
                return await database.EntryBindings.AsNoTracking().FirstOrDefaultAsync(value => value.EntryId == ids[key]);
            }, $"{title} is bound through the alias library");
            await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
            time.Offset += TimeSpan.FromDays(2);
            var due = PreviewItem(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview")), binding.Id);
            Assert(due.GetProperty("state").GetString() == "due", $"{title} is due: {due.GetProperty("state")}/{due.GetProperty("reason")}");
            return await host.Service<RetentionExecutor>().ReclaimAsync(binding.Id, CancellationToken.None);
        }

        string DownloadSide(string title, int year, int tmdb)
        {
            var path = Path.Combine(aliasB!, aliasRoot, "dl", $"{title} ({year}) [tmdbid-{tmdb}]", $"{title} ({year}).mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[4096]);
            return path;
        }

        async Task SetManifestKAsync(Func<IReadOnlyList<VerifiedTorrentFile>, IReadOnlyList<VerifiedTorrentFile>> change)
        {
            await using var database = new ModDbContext(dbPath);
            var operation = await database.ImportOperations.SingleAsync(value => value.Id == blockedK.Id);
            operation.CleanupManifest = TorrentDataRemoval.Serialize(change(TorrentDataRemoval.Deserialize(operation.CleanupManifest) ?? []).ToList());
            await database.SaveChangesAsync();
        }

        // (1) A waiting release with no list of its files yet, whose torrent is removed from the client outside JellyfinMod
        // with its data kept (final review, finding 1): the data is still there, so the release is detached for a manual
        // cleanup, not completed, and its seeding file stays protected through the alias.
        var legacyDownload = DownloadSide("Legacy Alias", 2020, 123);
        string legacySeeding;
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(seed is { State: SeedReleaseStates.Detached, Reason: SeedReleaseReasons.ManualCleanup, CleanupManifest: null } &&
                !transmission.Torrents.ContainsKey(hashB), "Precondition: release B has no list and its torrent is gone");
            legacySeeding = seed.SeedingPath;
            seed.SeedingPath = inspector.TryCanonicalize(legacyDownload, out var canonicalLegacy) ? canonicalLegacy : legacyDownload;
            seed.State = SeedReleaseStates.Waiting;
            seed.Reason = SeedReleaseReasons.GoalUnmet;
            await database.SaveChangesAsync();
        }

        await Tick();
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedB.Id);
            Assert(seed is { State: SeedReleaseStates.Detached, Reason: SeedReleaseReasons.ManualCleanup } && File.Exists(legacyDownload),
                $"A waiting release whose torrent left the client with its data kept is detached for a manual cleanup, not completed: " +
                $"{seed.State}/{seed.Reason}");
        }

        var legacyReclaim = await ReclaimThroughAliasAsync("movieLegacyAlias", 123, "Legacy Alias", 2020);
        await using (var database = new ModDbContext(dbPath))
        {
            (await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedB.Id)).SeedingPath = legacySeeding;
            await database.SaveChangesAsync();
        }

        Assert(legacyReclaim.Reason == "retained_download" && File.Exists(legacyDownload),
            $"A release detached for a manual cleanup without a list keeps its seeding file from retention through an alias: " +
            $"{legacyReclaim.State}/{legacyReclaim.Reason}, kept {File.Exists(legacyDownload)}");

        // (2) A listed download replaced at the same path and scanned again through the alias: a new file, the same entry.
        var replacedDownload = DownloadSide("Replaced Alias", 2019, 124);
        Assert(inspector.TryInspect(replacedDownload, out var replacedBefore), "The replaced download is read before it is replaced");
        await SetManifestKAsync(files => [.. files, new VerifiedTorrentFile(replacedBefore.CanonicalPath, replacedBefore.PhysicalIdentity,
            Path.GetDirectoryName(Path.GetDirectoryName(replacedBefore.CanonicalPath)!)!, 4096)]);
        File.Delete(replacedDownload);
        await File.WriteAllBytesAsync(replacedDownload, new byte[4096]);
        Assert(inspector.TryInspect(replacedDownload, out var replacedAfter) && replacedAfter.PhysicalIdentity != replacedBefore.PhysicalIdentity,
            "Precondition: the download at the listed path is a different file now");
        var replacedReclaim = await ReclaimThroughAliasAsync("movieReplacedAlias", 124, "Replaced Alias", 2019);
        Assert(replacedReclaim.Reason == "retained_download" && File.Exists(replacedDownload),
            $"A listed download replaced at its path and scanned again through an alias is still kept from retention: " +
            $"{replacedReclaim.State}/{replacedReclaim.Reason}, kept {File.Exists(replacedDownload)}");

        // (3) A listed download whose folder cannot be read (its parent is closed to this process) and a library file of the
        // same name: whether they are the same entry cannot be established, so retention refuses (Codex delta review 11, P1 1).
        var lockedDownload = DownloadSide("Locked Alias", 2018, 125);
        var lockedParent = Path.Combine(world.Folder, "locked-parent");
        var lockedRecorded = Path.Combine(lockedParent, "dl", "Locked Alias (2018).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedRecorded)!);
        await SetManifestKAsync(files => [.. files, new VerifiedTorrentFile(lockedRecorded, "recorded-before-it-was-locked", lockedParent, 4096)]);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Phase 5 runs on Linux");
        File.SetUnixFileMode(lockedParent, UnixFileMode.None);
        Assert(inspector.Probe(Path.GetDirectoryName(lockedRecorded)!) == PathPresence.Unknown,
            "Precondition: the listed download's folder cannot be read (this case needs a test process that is not root)");
        var lockedReclaim = await ReclaimThroughAliasAsync("movieLockedAlias", 125, "Locked Alias", 2018);
        File.SetUnixFileMode(lockedParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert(lockedReclaim.Reason == "retained_download" && File.Exists(lockedDownload),
            $"A listed download whose folder cannot be read is never treated as absent: {lockedReclaim.State}/{lockedReclaim.Reason}, " +
            $"kept {File.Exists(lockedDownload)}");
        await SetManifestKAsync(files => [.. files.Where(file => file.Path != lockedRecorded && file.Path != replacedBefore.CanonicalPath)]);
        Directory.Delete(lockedParent, true);
        foreach (var (hash, held) in unmappedTorrents) transmission.Torrents[hash] = held;

        // Nothing in this build deletes a recorded download: every one is still on disk.
        Assert(File.Exists(sourceA) && filesH.All(File.Exists) && File.Exists(completedB.SourceLocalPath!) && File.Exists(aliasFile) &&
            transmission.RemoveDeletedData.All(deleted => !deleted),
            "Every recorded download is still on disk, and no request ever asked the client to delete data");
        await using (var database = new ModDbContext(dbPath))
        {
            var seedH = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == completedH.Id);
            Assert(seedH is { State: SeedReleaseStates.Detached, Reason: SeedReleaseReasons.CleanupPending } &&
                (TorrentDataRemoval.Deserialize(seedH.CleanupManifest) ?? []).Count == filesH.Count,
                $"A released torrent stays detached with every file it left listed: {seedH.State}/{seedH.Reason}");
        }

        // A database that ran the withdrawn cleanup-run migrations (only test instances did): at the next start their table and
        // history rows go, and the plugin starts normally (user decision 2026-10-02).
        await StopAsync();
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            await raw.OpenAsync();
            await using var command = raw.CreateCommand();
            command.CommandText = """
                CREATE TABLE "DownloadCleanupRuns" ("Id" TEXT NOT NULL CONSTRAINT "PK_DownloadCleanupRuns" PRIMARY KEY, "UserId" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL, "DeletedCount" INTEGER NOT NULL, "KeptCount" INTEGER NOT NULL, "FreedBytes" INTEGER NOT NULL,
                    "Data" TEXT NOT NULL, "State" TEXT NOT NULL DEFAULT 'completed');
                CREATE INDEX "IX_DownloadCleanupRuns_CreatedAt" ON "DownloadCleanupRuns" ("CreatedAt");
                INSERT INTO "DownloadCleanupRuns" VALUES ('8f5c2a8e-0000-4000-8000-000000000001', '8f5c2a8e-0000-4000-8000-000000000002',
                    '2026-10-02 06:00:00', 1, 0, 5, '{}', 'completed');
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20261002015135_WholeReviewDownloadCleanupRuns', '10.0.11');
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20261002031344_WholeReviewCleanupRunState', '10.0.11');
                """;
            await command.ExecuteNonQueryAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        admin = host.Client(world.Admin, true);
        monitor = host.Service<ImportMonitor>();
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            await raw.OpenAsync();
            await using var command = raw.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DownloadCleanupRuns'),
                       (SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" LIKE '20261002015135%' OR "MigrationId" LIKE '20261002031344%')
                """;
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert(reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0,
                $"The withdrawn cleanup-run table and migrations are gone after a start: table {reader.GetInt64(0)}, history rows {reader.GetInt64(1)}");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert((await admin.GetAsync("/JellyfinMod/Queue")).StatusCode == HttpStatusCode.OK && File.Exists(aliasFile),
            "The plugin starts normally on such a database, and nothing is deleted");

        aliasLibraries.Remove(aliasDownloads);
        aliasLibraries.RemoveAt(aliasLibraries.Count - 1);
        Directory.Delete(aliasFolder, true);

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
        // The host acts on a reported change only after its library monitor delay (60 s by default), so the escalation
        // waits past it: 70 s after the request nothing is re-requested yet.
        time.Offset += TimeSpan.FromSeconds(70);
        await Tick();
        Assert((await ImportFor(dbPath, grabL)).ScanAttempts == 1, "No escalation before the host's library monitor delay has passed");
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

        // ================= Review P2-o / live finding 8: a missing file after completion settles; a completion that regresses
        // goes back to watching, and the file is linked only once the client reports it complete again.
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieR", "movie", 121, "Regress Movie", 2021, "tt0000121", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        var fixtureR = TorrentFixture.Single("Regress.Movie.2021.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["movieR"] = fixtureR.Bytes;
        transmission.Register(fixtureR);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new("Regress.Movie.2021.1080p.WEB-DL-GRP", "guid-movieR", torznab.Download("movieR"), Size, 25,
                new() { ["imdbid"] = "0000121" }));
        var (grabR, hashR) = await GrabAsync(admin, ids["movieR"], null, fixtureR);
        transmission.Progress(hashR, 1.0);
        var heldR = transmission.Torrents[hashR];
        var fileR = heldR.Files.Single(file => file.Wanted);
        var localR = transmission.Local(heldR.DownloadDir + "/" + fileR.Name);
        File.Delete(localR); // reported complete, but not yet moved into place
        await Tick();
        var settlingR = await ImportFor(dbPath, grabR);
        Assert(settlingR.State == ImportStates.Identifying && settlingR.Reason is null && settlingR.CompletedDownloadAt is not null,
            "A file missing just after completion keeps the import identifying instead of blocking it");
        fileR.Completed = fileR.Length / 2; // a recheck finds missing pieces
        await Tick();
        var regressedR = await ImportFor(dbPath, grabR);
        Assert(regressedR.State == ImportStates.Waiting && regressedR.CompletedDownloadAt is null && regressedR.LinkedAt is null,
            "When the client stops reporting the download complete, the import goes back to watching and links nothing");
        await File.WriteAllBytesAsync(localR, new byte[fileR.Length / 2]); // a preallocated, incomplete file of half length
        using (var stream = new FileStream(localR, FileMode.Open, FileAccess.Write)) stream.SetLength(fileR.Length); // full length, still incomplete
        await Tick();
        Assert((await ImportFor(dbPath, grabR)).LinkedAt is null, "A full-length preallocated file is not linked while the client reports it incomplete");
        // Whole-review chunk 2b, P2 4: the process stopped after identification persisted `linking`, before the hardlink, and
        // a recheck meanwhile found missing pieces. Recovery must not link the incomplete file it identified before.
        Assert(inspector.TryInspect(localR, out var preallocated), "The preallocated file can be inspected");
        await using (var database = new ModDbContext(dbPath))
        {
            var interrupted = await database.ImportOperations.SingleAsync(value => value.GrabId == grabR);
            interrupted.State = ImportStates.Linking;
            interrupted.SourceLocalPath = preallocated.CanonicalPath;
            interrupted.SourcePhysicalIdentity = preallocated.PhysicalIdentity;
            interrupted.SourceLogicalBytes = (long)preallocated.LogicalBytes;
            interrupted.DestinationPath = null;
            await database.SaveChangesAsync();
        }

        await Tick();
        var recoveredR = await ImportFor(dbPath, grabR);
        Assert(recoveredR.LinkedAt is null && recoveredR.State == ImportStates.Waiting,
            $"Recovery from linking checks the client's completion again and links nothing incomplete (whole-review c2bf4): {recoveredR.State}");
        File.Delete(localR);
        transmission.Progress(hashR, 1.0);
        var doneR = await WaitAsync(async () =>
        {
            await Tick();
            var operation = await ImportFor(dbPath, grabR);
            return operation.State == ImportStates.Completed ? operation : null;
        }, "The import completes once the client reports completion again");
        Assert(doneR.Id == settlingR.Id && await HistoryCount(dbPath, ids["movieR"], "imported") == 1 &&
            await HistoryCount(dbPath, ids["movieR"], "import_blocked") == 0,
            "The same operation completes with one imported event and was never blocked");

        // ================= Review P2-n/P2-q: the escalation to a library scan is deferred only by reports on related refresh
        // paths (the host folds those into one refresher), and never past three waits after the import's own request. With the
        // host's default 60 s monitor delay the wait is 90 s and the cap 270 s. The host binds nothing on its own here.
        world.Native.AutoScan = false;
        await using (var database = new ModDbContext(dbPath))
        {
            foreach (var (key, tmdb, title, tvdb) in new[] { ("other", 201, "Other Show", 301), ("newOne", 202, "New Show One", 302),
                         ("newTwo", 203, "New Show Two", 303) })
            {
                AddEntry(database, ids, key, "series", tmdb, title, 2023, null, tvdb, world.Tv.Id);
                for (var number = 1; number <= 2; number++)
                {
                    var episode = new Episode { EntryId = ids[key], TmdbId = tmdb * 10 + number, SeasonNumber = 1, EpisodeNumber = number,
                        Title = "Episode " + number, RuntimeMinutes = 45, AirDate = new DateTime(2023, 1, number, 0, 0, 0, DateTimeKind.Utc) };
                    database.Episodes.Add(episode);
                    ids[key + number] = episode.Id;
                }
            }

            await database.SaveChangesAsync();
        }

        var otherFolder = Path.Combine(world.Tv.Location, "Other Show (2023)");
        Directory.CreateDirectory(Path.Combine(otherFolder, "Season 01"));
        var otherExisting = Path.Combine(otherFolder, "Season 01", "Other Show S01E01.mkv");
        await File.WriteAllBytesAsync(otherExisting, new byte[2048]);
        var otherSeries = new TestSeries { Id = Guid.NewGuid(), Name = "Other Show", Path = otherFolder };
        otherSeries.ProviderIds["Tmdb"] = "201";
        world.Native.Add(world.Tv, otherSeries);
        world.Native.Scan(otherExisting);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.AnyAsync(value => value.EpisodeId == ids["other1"]) ? true : (bool?)null;
        }, "The other series' existing episode is bound");

        async Task<(Guid Grab, ImportOperation Scanning)> ScanningEpisodeAsync(string entryKey, string episodeKey, string release, string tvdb)
        {
            var fixture = TorrentFixture.Single(release + ".mkv", Size);
            torznab.Torrents[episodeKey] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(release, "guid-" + episodeKey, torznab.Download(episodeKey), Size, 25, new() { ["tvdbid"] = tvdb }));
            var (grab, hash) = await GrabAsync(admin, ids[entryKey], ids[episodeKey], fixture);
            transmission.Progress(hash, 1.0);
            var scanning = await WaitAsync(async () =>
            {
                await Tick();
                var operation = await ImportFor(dbPath, grab);
                return operation.State == ImportStates.Scanning ? operation : null;
            }, release + " reaches scanning");
            return (grab, scanning);
        }

        // Moves the shifted clock to `seconds` after `origin`, however long the steps before took in real time.
        void At(DateTime origin, int seconds) => time.Offset += origin.AddSeconds(seconds) - time.GetUtcNow().UtcDateTime;
        async Task<int> AttemptsAsync(Guid grab) => (await ImportFor(dbPath, grab)).ScanAttempts;

        // Unrelated: episodes of two existing series, each in its own season folder.
        var (grabX, scanningX) = await ScanningEpisodeAsync("series", "e4", "Example.Show.S01E04.1080p.WEB-DL-GRP", "300");
        var originX = scanningX.ScanRequestedAt!.Value;
        Assert(scanningX.RefreshAnchorPath == scanningX.DestinationPath &&
            Path.GetDirectoryName(scanningX.DestinationPath) == Path.Combine(seriesFolder, "Season 01"),
            "An episode linked into an existing season folder anchors on its own file");
        At(originX, 50);
        var (grabU, scanningU) = await ScanningEpisodeAsync("other", "other2", "Other.Show.S01E02.1080p.WEB-DL-GRP", "301");
        Assert(Path.GetDirectoryName(scanningU.DestinationPath) == Path.Combine(otherFolder, "Season 01") &&
            (scanningU.ScanRequestedAt!.Value - originX).TotalSeconds is >= 50 and < 90,
            "Another series' episode reports its change inside the first import's wait");
        At(originX, 95);
        await Tick();
        Assert(await AttemptsAsync(grabX) == 2 && await AttemptsAsync(grabU) == 1,
            "A report 45 s ago in another series' folder does not postpone the escalation (review P2-q)");
        Assert(!logs.Lines.Any(line => line.Contains($"Import {scanningX.Id} defers its library scan", StringComparison.Ordinal)),
            "An escalation that is not deferred logs no deferral");

        // Related: two new series folders are siblings under the library root; later reports come from inside them.
        var (grabN1, scanningN1) = await ScanningEpisodeAsync("newOne", "newOne1", "New.Show.One.S01E01.1080p.WEB-DL-GRP", "302");
        var originN1 = scanningN1.ScanRequestedAt!.Value;
        var folderOne = Path.GetDirectoryName(Path.GetDirectoryName(scanningN1.DestinationPath)!)!;
        Assert(scanningN1.RefreshAnchorPath == folderOne && Path.GetDirectoryName(folderOne) == world.Tv.Location,
            "An episode of a new series anchors on the series folder its link created");
        At(originN1, 50);
        var (grabN2, scanningN2) = await ScanningEpisodeAsync("newTwo", "newTwo1", "New.Show.Two.S01E01.1080p.WEB-DL-GRP", "303");
        Assert(scanningN2.RefreshAnchorPath != folderOne && Path.GetDirectoryName(scanningN2.RefreshAnchorPath) == world.Tv.Location,
            "A second new series anchors on its own series folder, a sibling of the first");
        At(originN1, 95);
        await Tick();
        Assert(await AttemptsAsync(grabN1) == 1, "A sibling new series folder's report 45 s ago postpones the escalation (review P2-n)");
        Assert(logs.Lines.Any(line => line.StartsWith("Debug", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(line,
                    $@"Import {scanningN1.Id} defers its library scan 9[5-7] s after its request: a related scan request came [34][0-9] s ago \(wait 90 s, cap 270 s\)")),
            "The deferral past the first wait is logged for that operation, with its timing (review P2-x)");
        At(originN1, 130);
        var (grabN3, _) = await ScanningEpisodeAsync("newOne", "newOne2", "New.Show.One.S01E02.1080p.WEB-DL-GRP", "302");
        At(originN1, 210);
        var (grabN4, scanningN4) = await ScanningEpisodeAsync("newTwo", "newTwo2", "New.Show.Two.S01E02.1080p.WEB-DL-GRP", "303");
        At(originN1, 265);
        await Tick();
        Assert(await AttemptsAsync(grabN1) == 1 && (originN1.AddSeconds(265) - scanningN4.ScanRequestedAt!.Value).TotalSeconds < 90,
            "Related reports less than one wait apart keep deferring it");
        At(originN1, 275);
        await Tick();
        Assert(await AttemptsAsync(grabN1) == 2, "At the cap, three waits after its own request, it escalates although a related report is recent");

        world.Native.AutoScan = true;
        foreach (var grab in new[] { grabX, grabU, grabN1, grabN2, grabN3, grabN4 })
            world.Native.Scan((await ImportFor(dbPath, grab)).DestinationPath!);
        foreach (var grab in new[] { grabX, grabU, grabN1, grabN2, grabN3, grabN4 })
            await WaitAsync(async () =>
            {
                await Tick();
                return (await ImportFor(dbPath, grab)).State == ImportStates.Completed ? true : (bool?)null;
            }, "Every import of the escalation scenario completes once its file is scanned");

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

    private static object ImportSettings(int revision, string[]? extensions = null, bool seedRelease = false, bool visible = false,
        double floorRatio = 1.0) => new
    {
        importEnabled = true, seedReleaseEnabled = seedRelease, seedFloorRatio = floorRatio, seedFloorHours = (int?)null, importPollSeconds = 1,
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
