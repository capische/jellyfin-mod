using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Http;

// Phase 4 acquisition integration: a real Kestrel plugin host with authentication, authorization, MVC
// serialization, EF migrations and SQLite, talking real HTTP to a Torznab boundary server and a Transmission RPC
// boundary server. No plugin client is mocked; the boundaries are the external systems.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-four-" + Guid.NewGuid().ToString("N"));
var far = Path.Combine("/dev/shm", "jfmod-phase-four-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
Directory.CreateDirectory(far);
// A third filesystem (the container's /dev tmpfs) that holds no movie or TV library root.
var foreign = Path.Combine("/dev", "jfmod-phase-four-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(foreign);
var logs = new CapturingLoggerProvider();
var secrets = new List<string>();
try
{
    await RunAsync(folder, far, foreign, logs, secrets);
    // No credential, API key or tracker passkey may appear in any log line the plugin wrote.
    foreach (var secret in secrets)
        Assert(!logs.Lines.Any(line => line.Contains(secret, StringComparison.Ordinal)), "Logs never contain a credential or passkey");
    Console.WriteLine($"PASS: Phase 4 acquisition settings, Torznab search, scoring, held Transmission handoff, recovery and redaction ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
    Directory.Delete(far, true);
    Directory.Delete(foreign, true);
}

static async Task RunAsync(string folder, string far, string foreign, CapturingLoggerProvider logs, List<string> secrets)
{
    var media = Path.Combine(folder, "media");
    foreach (var directory in new[] { "movies", "movies2", "tv", "downloads", "movies/inside" })
        Directory.CreateDirectory(Path.Combine(media, directory));
    var world = new World
    {
        Admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() },
        SecondAdmin = new User("admin2", "auth", "reset") { Id = Guid.NewGuid() },
        RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() },
        Ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() },
        Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
        Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
        Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
        Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = far },
        Folder = folder
    };

    // ---- Migration: an existing Phase 3 database upgrades with entries and episodes intact (P4.A2).
    var dbPath = Path.Combine(folder, "jellyfinmod.db");
    var movieId = Guid.NewGuid();
    var copyId = Guid.NewGuid();
    var seriesId = Guid.NewGuid();
    var episodeIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20260919043933_PhaseThreeNativeVisibility");
        var movieMetadata = Metadata("movie", 100, "Example Movie", 2024, "tt0000100", null, 118);
        var seriesMetadata = Metadata("series", 200, "Example Show", 2023, null, 300, null);
        var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
            VALUES ({movieId}, 'movie', 100, 'tt0000100', 'Example Movie', 2024, {movieMetadata}, 0, 1, {added}, {world.Movies.Id}, 0)
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
            VALUES ({copyId}, 'movie', 100, 'tt0000100', 'Example Movie', 2024, {movieMetadata}, 5, 1, {added}, {world.Movies2.Id}, 0)
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Entries (Id, MediaType, TmdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
            VALUES ({seriesId}, 'series', 200, 'Example Show', 2023, {seriesMetadata}, 0, 1, {added}, {world.Tv.Id}, 0)
            """);
        var numbers = new[] { (1, 1), (1, 2), (1, 3), (0, 1) };
        for (var index = 0; index < numbers.Length; index++)
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Episodes (Id, EntryId, TmdbId, SeasonNumber, EpisodeNumber, Title, RuntimeMinutes, Monitored, State)
                VALUES ({episodeIds[index]}, {seriesId}, {2000 + index}, {numbers[index].Item1}, {numbers[index].Item2}, 'Episode', 45, 1, 0)
                """);
        await database.Database.MigrateAsync();
        var applied = (await database.Database.GetAppliedMigrationsAsync()).ToList();
        // Later phases append migrations; the Phase 4 one must still follow the latest Phase 3 migration.
        Assert(applied.IndexOf("20260919071544_PhaseFourAcquisition") > applied.IndexOf("20260919043933_PhaseThreeNativeVisibility") &&
            applied.IndexOf("20260919043933_PhaseThreeNativeVisibility") >= 0,
            "The Phase 4 migration applies after the latest Phase 3 migration");
        Assert(await database.Entries.CountAsync() == 3 && await database.Episodes.CountAsync() == 4 &&
            await database.Entries.AllAsync(entry => entry.QualityProfileId == null) &&
            (await database.Entries.SingleAsync(entry => entry.Id == copyId)).State == FileState.Reclaimed &&
            !await database.GrabOperations.AnyAsync(),
            "Upgrade preserves entries, episodes and reclaimed provenance, and new entries inherit the default profile");
    }

    await using var torznab = new TorznabBoundary();
    await using var canary = new CanaryBoundary();
    await using var transmission = new TransmissionBoundary();
    await torznab.StartAsync();
    await canary.StartAsync();
    await transmission.StartAsync();
    secrets.Add(transmission.Password);

    // ---- Controlled fixtures: self-created v1 torrents whose announce URLs carry a tracker passkey.
    var passkey = "pk" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
    secrets.Add(passkey);
    var announce = $"http://tracker.invalid/announce?passkey={passkey}";
    var movieTorrent = TorrentFixture.Create("Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", 4_000_000_000, announce);
    var webripTorrent = TorrentFixture.Create("Example.Movie.2024.720p.WEBRip.x264-GRP.mkv", 1_200_000_000, announce);
    var episodeTorrent = TorrentFixture.Create("Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", 1_500_000_000, announce);
    var episodeTwoTorrent = TorrentFixture.Create("Example.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", 1_500_000_000, announce);
    var episodeThreeTorrent = TorrentFixture.Create("Example.Show.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP.mkv", 1_500_000_000, announce);
    var hybridTorrent = TorrentFixture.Create("Example.Movie.2024.1080p.BluRay.x265-HYB.mkv", 5_000_000_000, announce, hybrid: true);
    foreach (var (id, fixture) in new[] { ("movie", movieTorrent), ("webrip", webripTorrent), ("e1", episodeTorrent),
                 ("e2", episodeTwoTorrent), ("e3", episodeThreeTorrent), ("hybrid", hybridTorrent) })
    {
        torznab.Torrents[id] = fixture.Bytes;
        transmission.MetainfoHashes[Convert.ToBase64String(fixture.Bytes)] = fixture.InfoHash;
    }

    torznab.Redirects["bounce"] = new Uri(canary.Address, "/steal").ToString();
    var dl = (string id) => new Uri(torznab.Address, $"/dl/{id}?passkey={passkey}").ToString();
    var properHash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("proper")));
    var properBase32 = Base32(Convert.FromHexString(properHash));
    var good = new IndexerScript
    {
        ApiKey = "good-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), PageSize = 2,
        Caps = TorznabBoundary.Caps("q,imdbid,tmdbid", "q,tvdbid,season,ep")
    };
    secrets.Add(good.ApiKey);
    Dictionary<string, string> Attributes(params (string, string)[] values) => values.ToDictionary(value => value.Item1, value => value.Item2);
    good.MovieItems.AddRange([
        new("Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP", "good-a", dl("movie"), 4_000_000_000, 50, Attributes(("imdbid", "0000100"))),
        new("Example.Movie.2024.2160p.BluRay.REMUX.HEVC.TrueHD.Atmos-GRP", "good-b", dl("missing"), 60_000_000_000, 20, []),
        new("Example.Movie.2024.720p.WEBRip.x264-GRP", "good-c", dl("webrip"), 1_200_000_000, 5, Attributes(("downloadvolumefactor", "0"))),
        new("Example.Movie.2024.HDCAM.x264-BAD", "good-d", dl("missing"), 1_000_000_000, 900, []),
        new("Other.Film.2019.1080p.WEB-DL-GRP", "good-e", dl("missing"), 4_000_000_000, 10, Attributes(("imdbid", "9999999"))),
        new("Example.Movie.2024.1080p.WEB-DL-EVIL", "good-f", new Uri(canary.Address, "/torrent").ToString(), 4_000_000_000, 10, []),
        new("Example.Movie.2024.1080p.BluRay.x264-REDIR", "good-g", dl("bounce"), 8_000_000_000, 10, []),
        new("Example.Movie.2024.1080p.WEB-DL.PROPER-GRP2", "good-h", null, 4_100_000_000, 30,
            Attributes(("magneturl", $"magnet:?xt=urn:btih:{properBase32}&dn=proper"))),
        new("Example.Movie.2024.1080p.WEB-DL-V2", "good-i", null, 4_000_000_000, 30,
            Attributes(("magneturl", "magnet:?xt=urn:btmh:1220" + new string('a', 64)))),
        new("Example.Movie.2024.1080p.BluRay.x265-HYB", "good-j", dl("hybrid"), 5_000_000_000, 40, [])
    ]);
    good.TvItems.AddRange([
        new("Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP", "tv-1", dl("e1"), 1_500_000_000, 40, Attributes(("tvdbid", "300"))),
        new("Example.Show.S01E01E02.1080p.WEB-DL-GRP", "tv-2", dl("missing"), 3_000_000_000, 10, []),
        new("Example.Show.S01.1080p.WEB-DL-GRP", "tv-3", dl("missing"), 20_000_000_000, 10, []),
        new("Example.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP", "tv-4", dl("e2"), 1_500_000_000, 35, []),
        new("Example Show - 101 [1080p WEB-DL]", "tv-5", dl("missing"), 1_500_000_000, 10, []),
        new("Example.Show.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP", "tv-6", dl("e3"), 1_500_000_000, 30, []),
        new("Example.Show.S00E01.1080p.WEB-DL-GRP", "tv-7", dl("missing"), 1_500_000_000, 30, [])
    ]);
    torznab.Indexers["good"] = good;
    var queryOnly = new IndexerScript { ApiKey = "q-key", Caps = TorznabBoundary.Caps("q", "q") };
    queryOnly.MovieItems.Add(new("Example.Movie.2024.1080p.WEB-DL-QONLY", "q-a", dl("movie"), 4_000_000_000, 60, []));
    // Same title, wrong year: a text-only match must still be refused on the year (user decision 2026-09-20).
    queryOnly.MovieItems.Add(new("Example.Movie.1998.1080p.WEB-DL-QOLD", "q-b", dl("movie"), 4_000_000_000, 60, []));
    torznab.Indexers["qonly"] = queryOnly;
    torznab.Indexers["slow"] = new IndexerScript
    {
        ApiKey = "slow-key", Caps = TorznabBoundary.Caps("q,imdbid", "q"),
        Override = async context =>
        {
            if (context.Request.Query["t"] == "caps") return false;
            await Task.Delay(TimeSpan.FromSeconds(6));
            return false;
        }
    };
    torznab.Indexers["malformed"] = new IndexerScript
    {
        ApiKey = "m-key", Caps = TorznabBoundary.Caps("q,imdbid", "q"),
        Override = async context =>
        {
            if (context.Request.Query["t"] == "caps") return false;
            await context.Response.WriteAsync("<rss><channel><item><title>broken");
            return true;
        }
    };
    var rateLimited = new IndexerScript
    {
        ApiKey = "r-key", Caps = TorznabBoundary.Caps("q,imdbid", "q"),
        Override = context =>
        {
            if (context.Request.Query["t"] == "caps") return Task.FromResult(false);
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "120";
            return Task.FromResult(true);
        }
    };
    torznab.Indexers["ratelimit"] = rateLimited;
    torznab.Indexers["xxe"] = new IndexerScript
    {
        ApiKey = "x-key",
        Caps = $"""<?xml version="1.0"?><!DOCTYPE caps [<!ENTITY leak SYSTEM "{new Uri(canary.Address, "/xxe")}">]><caps><server title="&leak;"/></caps>"""
    };
    var big = new IndexerScript { ApiKey = "b-key", PageSize = 2, ReportedTotal = 1000, Caps = TorznabBoundary.Caps("q,imdbid", "q", max: 2) };
    for (var index = 0; index < 20; index++)
        big.MovieItems.Add(new($"Example.Movie.2024.1080p.WEB-DL-BIG{index}", $"big-{index}", dl("missing"), 4_000_000_000, 1, []));
    torznab.Indexers["big"] = big;

    var time = new ShiftedTimeProvider();
    var configuration = new PluginConfiguration();
    var hold = TimeSpan.FromSeconds(2);
    var host = await PluginHost.StartAsync(world, dbPath, time, logs, hold, configuration);
    try
    {
        using var anonymous = host.Client(null, false);
        using var ordinary = host.Client(world.Ordinary, false);
        using var admin = host.Client(world.Admin, true);
        using var admin2 = host.Client(world.SecondAdmin, true);
        using var tvAdmin = host.Client(world.RestrictedAdmin, true);

        // ---- A2: authorization, validation, redaction and reference integrity.
        Assert((await anonymous.GetAsync("/JellyfinMod/Settings/Indexers")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous settings read is rejected");
        foreach (var path in new[] { "Settings/Indexers", "Settings/DownloadClients", "Settings/QualityProfiles", "Settings/Acquisition", "Grabs",
                     $"Releases?entryId={movieId}" })
            Assert((await ordinary.GetAsync("/JellyfinMod/" + path)).StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot use " + path);
        Assert((await ordinary.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new { name = "x", qualities = new[] { "webdl-1080p" } }))
            .StatusCode == HttpStatusCode.Forbidden, "Ordinary users cannot change settings");

        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new { name = "Bad", qualities = new[] { "webdl-9000p" } }),
            400, "invalid_qualities", "Unknown qualities are rejected");
        Assert((await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles",
                new { name = "Bad", qualities = new[] { "webdl-1080p" }, preferFreeleech = true })).StatusCode == HttpStatusCode.BadRequest,
            "Unknown profile fields are rejected");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles",
            new { name = "Bad", qualities = new[] { "webdl-1080p" }, minimumBytesPerHour = 10, maximumBytesPerHour = 5 }), 400, "invalid_size_range",
            "Minimum size must not exceed maximum");
        var hd = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "HD", qualities = new[] { "webdl-1080p", "bluray-1080p", "webrip-1080p", "webdl-720p", "webrip-720p" },
            minimumBytesPerHour = 500_000_000L, maximumBytesPerHour = 20_000_000_000L
        }), 201, "Administrator creates a quality profile");
        var hdId = hd.GetProperty("id").AsGuid();
        var sd = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles",
            new { name = "Small", qualities = new[] { "webrip-720p" } }), 201, "Administrator creates a second profile");
        var sdId = sd.GetProperty("id").AsGuid();
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new { name = "HD", qualities = new[] { "webdl-1080p" } }),
            409, "name_in_use", "Profile names are unique");

        var goodIndexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Good", baseUrl = new Uri(torznab.Address, "/good/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
            priority = 1, minimumSeedRatio = 1.0, minimumSeedMinutes = 2880,
            apiKey = new { action = "replace", value = good.ApiKey }
        }), 201, "Administrator creates an indexer with a write-only key");
        var goodId = goodIndexer.GetProperty("id").AsGuid();
        Assert(goodIndexer.GetProperty("apiKeyConfigured").GetBoolean() && !goodIndexer.GetRawText().Contains(good.ApiKey, StringComparison.Ordinal),
            "The indexer response reports a configured key without returning it");
        var listed = await admin.GetStringAsync("/JellyfinMod/Settings/Indexers");
        Assert(!listed.Contains(good.ApiKey, StringComparison.Ordinal) && !listed.Contains("sec_", StringComparison.Ordinal),
            "Indexer reads expose neither the key nor its secret reference");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Creds", baseUrl = "http://user:pass@127.0.0.1/api", categories = new[] { 2000 }
        }), 400, "invalid_indexer_url", "Credentials embedded in an indexer URL are refused");
        await ExpectAsync(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{goodId}", new
        {
            name = "Good", baseUrl = new Uri(torznab.Address, "/good/api").ToString(), enabled = true, categories = new[] { 2000, 5000 }, revision = 9
        }), 409, "revision_conflict", "A stale indexer revision is refused");
        var patchedIndexer = await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{goodId}", new
        {
            name = "Good", baseUrl = new Uri(torznab.Address, "/good/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
            priority = 1, minimumSeedRatio = 1.0, minimumSeedMinutes = 2880, revision = 1, apiKey = new { action = "unchanged" }
        }), 200, "Administrator updates an indexer without re-entering its key");
        Assert(patchedIndexer.GetProperty("revision").GetInt32() == 2 && patchedIndexer.GetProperty("apiKeyConfigured").GetBoolean() &&
            !patchedIndexer.GetProperty("verified").GetBoolean(), "An indexer change keeps the key but requires new verification");
        var test = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{goodId}/Test", null), 200, "Indexer test answers");
        Assert(test.GetProperty("ok").GetBoolean() && good.Queries.Any(query => query.Contains("t=caps", StringComparison.Ordinal) &&
                query.Contains("apikey=" + good.ApiKey, StringComparison.Ordinal)),
            "Capabilities are fetched over real HTTP with the stored key");
        var verified = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Indexers")).EnumerateArray().Single();
        Assert(verified.GetProperty("verified").GetBoolean() &&
            verified.GetProperty("capabilities").GetProperty("movieSearch").EnumerateArray().Select(value => value.GetString()).Contains("imdbid"),
            "Verified capabilities are recorded against the indexer revision");

        var wrongKey = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Wrong key", baseUrl = new Uri(torznab.Address, "/good/api").ToString(), enabled = false, categories = new[] { 2000 },
            apiKey = new { action = "replace", value = "wrong-key-value" }
        }), 201, "An indexer with a wrong key can be saved");
        var wrongTest = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{wrongKey.GetProperty("id").AsGuid()}/Test", null), 200,
            "Wrong-key test answers");
        Assert(!wrongTest.GetProperty("ok").GetBoolean() && wrongTest.GetProperty("code").GetString() == "auth_failed",
            "A Torznab credential error delivered with HTTP 200 is reported as an authentication failure, not an empty result");

        await using (var database = new ModDbContext(dbPath))
            Assert(await database.AcquisitionIndexers.AllAsync(indexer => indexer.ApiKeySecretRef!.StartsWith("sec_")),
                "SQLite rows hold only opaque secret references");
        var secretFile = Path.Combine(folder, "acquisition-secrets.json");
        Assert(!OperatingSystem.IsWindows() && File.GetUnixFileMode(secretFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) &&
            File.ReadAllText(secretFile).Contains(good.ApiKey, StringComparison.Ordinal),
            "Secret values live only in the plugin-owned 0600 secret file");

        // Download client: the destination must share one filesystem and mount with the library roots (decision 6).
        object Client(string localDirectory, string label = "jellyfinmod-test", string kind = "transmission", string? password = null) => new
        {
            name = "Transmission " + Guid.NewGuid().ToString("N")[..6], kind, baseUrl = transmission.Endpoint.ToString(), username = transmission.Username,
            password = new { action = "replace", value = password ?? transmission.Password }, enabled = true, label,
            downloadDirectory = "/downloads/jellyfinmod", localDirectory, openUrl = "http://127.0.0.1:9/transmission/web/"
        };
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(Path.Combine(media, "absent"))), 400,
            "destination_missing", "A missing download folder is refused");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(Path.Combine(media, "movies", "inside"))), 400,
            "destination_inside_library", "A download folder inside a watched library is refused");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(foreign)), 400,
            "destination_not_same_filesystem", "A download folder on a filesystem without any movie/TV root is refused");
        Directory.CreateDirectory(far + "-downloads");
        var shmClient = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(far + "-downloads")), 201,
            "A download folder beside a library on another filesystem is accepted");
        Assert(shmClient.GetProperty("sameFilesystemLibraryIds").EnumerateArray().Select(value => value.AsGuid()).SequenceEqual([world.Far.Id]),
            "Only libraries whose roots share the folder's statx device and mount are recorded");
        // The very first client is selected on creation (P7.S11), and a selected client cannot be deleted.
        var shmId = shmClient.GetProperty("id").AsGuid();
        var firstSelection = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
        Assert(firstSelection.GetProperty("downloadClientId").AsGuid() == shmId, "The first client created is selected when none exists");
        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Settings/DownloadClients/{shmId}"), 409, "download_client_selected",
            "The selected client cannot be deleted");
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition",
            new { enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = (Guid?)null,
                revision = firstSelection.GetProperty("revision").GetInt32() }), 200, "An administrator can leave no client selected");
        Assert((await admin.DeleteAsync($"/JellyfinMod/Settings/DownloadClients/{shmId}")).StatusCode ==
            HttpStatusCode.NoContent, "An unused client can be deleted");
        Directory.Delete(far + "-downloads");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(Path.Combine(media, "downloads"), label: "jfmod-owned")),
            400, "invalid_label", "The per-operation ownership label prefix is reserved");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(Path.Combine(media, "downloads"), kind: "qbittorrent")),
            400, "unsupported_client_kind", "Only registered driver kinds are accepted");
        var clientDto = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", Client(Path.Combine(media, "downloads"))),
            201, "Administrator creates the Transmission client");
        var clientId = clientDto.GetProperty("id").AsGuid();
        // Again the only client, so selected; clear it so readiness below starts from nothing selected.
        var secondSelection = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
        Assert(secondSelection.GetProperty("downloadClientId").AsGuid() == clientId, "A client created when none exists is selected");
        await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition",
            new { enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = (Guid?)null,
                revision = secondSelection.GetProperty("revision").GetInt32() }), 200, "The selection is cleared");
        var sameFilesystem = clientDto.GetProperty("sameFilesystemLibraryIds").EnumerateArray().Select(value => value.AsGuid()).ToHashSet();
        Assert(sameFilesystem.SetEquals([world.Movies.Id, world.Movies2.Id, world.Tv.Id]) &&
            clientDto.GetProperty("passwordConfigured").GetBoolean() && !clientDto.GetRawText().Contains(transmission.Password, StringComparison.Ordinal),
            "The client records which libraries share its filesystem and never returns its password");
        transmission.RpcVersion = 16;
        var oldRpc = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/Test", null), 200, "Client test answers");
        Assert(!oldRpc.GetProperty("ok").GetBoolean() && oldRpc.GetProperty("code").GetString() == "client_unsupported_version",
            "A Transmission without label support fails the connection test");
        transmission.RpcVersion = 17;
        var clientTest = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/Test", null), 200, "Client test answers");
        Assert(clientTest.GetProperty("ok").GetBoolean() && clientTest.GetProperty("version").GetString() == "4.0.6 (boundary)" &&
            clientTest.GetProperty("apiVersion").GetString() == "17" && transmission.HandshakeRejections > 0 && transmission.AddCalls == 0,
            "The client test completes the session-id handshake over real HTTP and adds nothing");
        var badPassword = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients",
            Client(Path.Combine(media, "downloads"), password: "not-the-password")), 201, "A client with a wrong password can be saved");
        var noneSelected = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
        Assert(noneSelected.GetProperty("downloadClientId").ValueKind == JsonValueKind.Null,
            "Creating a client while others exist never selects it, even with none selected");
        var acquisitionRevision = noneSelected.GetProperty("revision").GetInt32();
        var badTest = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{badPassword.GetProperty("id").AsGuid()}/Test", null),
            200, "Wrong password test answers");
        Assert(!badTest.GetProperty("ok").GetBoolean() && badTest.GetProperty("code").GetString() == "client_auth_failed",
            "Refused client credentials are reported safely");

        var settings = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
        Assert(!settings.GetProperty("ready").GetBoolean() && Strings(settings.GetProperty("blockers")).SequenceEqual(["no_download_client", "no_default_profile"]),
            "Readiness lists every missing prerequisite");
        var notReady = await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new { enabled = true, revision = acquisitionRevision });
        Assert(notReady.StatusCode == HttpStatusCode.Conflict && (await notReady.Content.ReadAsStringAsync()).Contains("acquisition_not_ready"),
            "Grabs cannot be enabled before a verified indexer, client and default profile exist");
        settings = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition",
            new { enabled = true, downloadClientId = clientId, defaultQualityProfileId = hdId, revision = acquisitionRevision }), 200, "Administrator enables grabs");
        Assert(settings.GetProperty("enabled").GetBoolean() && settings.GetProperty("ready").GetBoolean() &&
            settings.GetProperty("holdSeconds").GetInt32() == 2 && settings.GetProperty("seedProtectionMatchesClient").GetBoolean(),
            "Enabled acquisition reports its hold, and with no XML endpoint seed protection reads the selected client (P7.S7 default 12)");
        configuration.TransmissionRpcUrl = transmission.Endpoint.ToString();
        settings = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
        Assert(settings.GetProperty("seedProtectionMatchesClient").GetBoolean(), "Seed protection reading the same Transmission is reported: " + settings.GetRawText());
        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Settings/QualityProfiles/{hdId}"), 409, "profile_is_default",
            "The default profile cannot be deleted");
        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}"), 409, "download_client_selected",
            "The selected client cannot be deleted");

        // ---- A3/A4: capability-first search, partial failures, hostile answers and explicit rejections.
        var noisy = new Dictionary<string, Guid>();
        foreach (var name in new[] { "qonly", "slow", "malformed", "ratelimit", "xxe", "big" })
        {
            var created = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name, baseUrl = new Uri(torznab.Address, $"/{name}/api").ToString(), enabled = true, categories = new[] { 2000, 5000 }, priority = 5,
                apiKey = new { action = "replace", value = torznab.Indexers[name].ApiKey }
            }), 201, "Indexer " + name + " created");
            noisy[name] = created.GetProperty("id").AsGuid();
        }

        var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Movie release search answers");
        var outcomes = search.GetProperty("indexers").EnumerateArray().ToDictionary(value => value.GetProperty("name").GetString()!);
        Assert(outcomes["Good"].GetProperty("status").GetString() == "ok" && outcomes["Good"].GetProperty("resultCount").GetInt32() == 10,
            "The verified indexer returns every row across its pages");
        Assert(outcomes["slow"].GetProperty("status").GetString() == "timeout", "A slow indexer times out without failing the search");
        Assert(outcomes["malformed"].GetProperty("status").GetString() == "malformed_response", "Malformed XML is an error, not an empty result");
        Assert(outcomes["ratelimit"].GetProperty("status").GetString() == "rate_limited" &&
            outcomes["ratelimit"].GetProperty("retryAfterSeconds").GetInt32() == 120, "Rate-limit guidance is reported");
        Assert(outcomes["xxe"].GetProperty("status").GetString() == "malformed_response" && canary.Hits == 0,
            "A DTD/external entity in caps is refused and never resolved");
        Assert(outcomes["big"].GetProperty("truncated").GetBoolean() && outcomes["big"].GetProperty("resultCount").GetInt32() == 10 &&
            search.GetProperty("truncated").GetBoolean() && search.GetProperty("partial").GetBoolean(),
            "Pagination stops at its bound and says the result is truncated and partial");
        var goodQueries = good.Queries.Where(query => query.Contains("t=movie", StringComparison.Ordinal)).ToArray();
        Assert(goodQueries.Length == 5 && goodQueries.All(query => query.Contains("imdbid=0000100", StringComparison.Ordinal) &&
                query.Contains("cat=2000%2C5000", StringComparison.Ordinal) && query.Contains("limit=100", StringComparison.Ordinal)) &&
            Enumerable.Range(0, 5).All(page => goodQueries.Any(query => query.Contains($"offset={page * 2}", StringComparison.Ordinal))),
            "Movie search uses the advertised imdbid parameter and offset pagination");
        Assert(queryOnly.Queries.Any(query => query.Contains("t=movie", StringComparison.Ordinal) && query.Contains("q=Example%20Movie%202024", StringComparison.Ordinal)),
            "An indexer without provider ids receives only a text query");
        var rows = search.GetProperty("candidates").EnumerateArray().ToArray();
        var byTitle = rows.ToDictionary(row => row.GetProperty("rawTitle").GetString()!);
        Assert(!search.GetRawText().Contains(passkey, StringComparison.Ordinal) && !search.GetRawText().Contains("/dl/", StringComparison.Ordinal),
            "Search responses never contain download locators or passkeys");
        var best = byTitle["Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP"];
        var bestParsed = best.GetProperty("parsed");
        Assert(best.GetProperty("eligible").GetBoolean() && bestParsed.GetProperty("quality").GetString() == "webdl-1080p" &&
            bestParsed.GetProperty("codec").GetString() == "h264" && bestParsed.GetProperty("audio").GetString() == "DD+" &&
            bestParsed.GetProperty("group").GetString() == "GRP" && bestParsed.GetProperty("year").GetInt32() == 2024 &&
            best.GetProperty("match").GetProperty("identity").GetString() == "verified" &&
            best.GetProperty("seedRatio").GetDouble() == 1.0 && best.GetProperty("seedMinutes").GetInt32() == 2880 &&
            best.GetProperty("freeleech").ValueKind == JsonValueKind.Null,
            "An eligible release exposes parsed fields, verified identity, seed requirements and unknown flags as null");
        Assert(best.GetProperty("contributions").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "quality_rank" &&
            item.GetProperty("points").GetInt32() == 500), "Score contributions are exposed");
        void Rejected(string title, string code) => Assert(!byTitle[title].GetProperty("eligible").GetBoolean() &&
            byTitle[title].GetProperty("rejections").EnumerateArray().Any(item => item.GetProperty("code").GetString() == code),
            $"{title} is rejected as {code}");
        Rejected("Example.Movie.2024.2160p.BluRay.REMUX.HEVC.TrueHD.Atmos-GRP", "quality_not_allowed");
        Rejected("Example.Movie.2024.HDCAM.x264-BAD", "quality_forbidden");
        Rejected("Other.Film.2019.1080p.WEB-DL-GRP", "identity_mismatch");
        Rejected("Example.Movie.2024.1080p.WEB-DL-EVIL", "download_host_not_allowed");
        Rejected("Example.Movie.2024.1080p.WEB-DL-V2", "unsupported_hash");
        // Most public trackers advertise no id search, so a title-and-year match is the only identity they can
        // give. It is grabbable by hand and marked as such; automation ignores it unless the indexer is trusted.
        var titleMatched = byTitle["Example.Movie.2024.1080p.WEB-DL-QONLY"];
        Assert(titleMatched.GetProperty("eligible").GetBoolean() &&
            titleMatched.GetProperty("match").GetProperty("identity").GetString() == "title" &&
            titleMatched.GetProperty("match").GetProperty("method").GetString()!.StartsWith("q", StringComparison.Ordinal),
            "A text-only indexer's release matching title and year is eligible and reported as a title match: " +
            titleMatched.GetRawText());
        Rejected("Example.Movie.1998.1080p.WEB-DL-QOLD", "year_mismatch");
        var freeleech = byTitle["Example.Movie.2024.720p.WEBRip.x264-GRP"];
        Assert(freeleech.GetProperty("eligible").GetBoolean() && freeleech.GetProperty("freeleech").GetBoolean() &&
            freeleech.GetProperty("contributions").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "freeleech"),
            "Freeleech is reported and scored");
        var proper = byTitle["Example.Movie.2024.1080p.WEB-DL.PROPER-GRP2"];
        Assert(proper.GetProperty("eligible").GetBoolean() && proper.GetProperty("proper").GetBoolean() &&
            proper.GetProperty("infoHash").GetString() == properHash, "A base32 magnet is normalized to a lower-case v1 hash");
        var eligible = rows.TakeWhile(row => row.GetProperty("eligible").GetBoolean()).ToArray();
        Assert(eligible.Length == search.GetProperty("eligibleCount").GetInt32() &&
            rows.Skip(eligible.Length).All(row => !row.GetProperty("eligible").GetBoolean()) &&
            eligible.Zip(eligible.Skip(1)).All(pair => pair.First.GetProperty("score").GetInt32() >= pair.Second.GetProperty("score").GetInt32()),
            "Eligible releases come first, sorted by descending score");
        var rateQueries = rateLimited.Queries.Count;
        var again = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Repeated search answers");
        Assert(rateLimited.Queries.Count == rateQueries &&
            again.GetProperty("indexers").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "ratelimit")
                .GetProperty("status").GetString() == "rate_limited",
            "A rate-limited indexer is not asked again before its Retry-After");
        Assert(again.GetProperty("candidates").EnumerateArray().Where(row => row.GetProperty("indexerName").GetString() == "Good")
                .Select(row => row.GetProperty("releaseId").GetString())
                .SequenceEqual(rows.Where(row => row.GetProperty("indexerName").GetString() == "Good").Select(row => row.GetProperty("releaseId").GetString())),
            "Scoring and ordering are deterministic across searches");

        // Picker profile changes rescore one search without touching the entry's saved profile.
        var small = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&profileId={sdId}"), 200, "Rescored search");
        var smallRows = small.GetProperty("candidates").EnumerateArray().ToDictionary(row => row.GetProperty("rawTitle").GetString()!);
        Assert(small.GetProperty("profile").GetProperty("id").AsGuid() == sdId && !small.GetProperty("profile").GetProperty("inherited").GetBoolean() &&
            !smallRows["Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP"].GetProperty("eligible").GetBoolean() &&
            smallRows["Example.Movie.2024.720p.WEBRip.x264-GRP"].GetProperty("eligible").GetBoolean(),
            "A picker profile produces a new evaluated snapshot");
        var movieDetail = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Entries/{movieId}"), 200, "Entry detail");
        Assert(movieDetail.GetProperty("entry").GetProperty("qualityProfileId").ValueKind == JsonValueKind.Null,
            "Searching never changes the entry's saved profile");

        foreach (var id in noisy.Values)
        {
            var current = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Indexers")).EnumerateArray()
                .Single(value => value.GetProperty("id").AsGuid() == id);
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{id}", new
            {
                name = current.GetProperty("name").GetString(), baseUrl = current.GetProperty("baseUrl").GetString(), enabled = false,
                categories = new[] { 2000, 5000 }, priority = 5, revision = current.GetProperty("revision").GetInt32()
            }), 200, "Noisy indexer disabled");
        }

        // Episodes: one stable episode per search; packs, multi-episode, absolute numbering and specials are refused.
        await ExpectAsync(admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}"), 400, "episode_required", "Series searches need an episode");
        Assert((await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&episodeId={episodeIds[0]}")).StatusCode == HttpStatusCode.BadRequest,
            "A movie search refuses an episode");
        Assert((await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound,
            "A foreign episode id is concealed");
        Assert((await tvAdmin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}")).StatusCode == HttpStatusCode.NotFound,
            "An administrator without access to the library gets the concealed 404");
        var episodeSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[0]}"), 200,
            "Episode search answers");
        Assert(good.Queries.Any(query => query.Contains("t=tvsearch", StringComparison.Ordinal) && query.Contains("tvdbid=300", StringComparison.Ordinal) &&
            query.Contains("season=1", StringComparison.Ordinal) && query.Contains("ep=1", StringComparison.Ordinal)),
            "Episode search uses advertised tvdbid, season and ep");
        var tvRows = episodeSearch.GetProperty("candidates").EnumerateArray().ToDictionary(row => row.GetProperty("rawTitle").GetString()!);
        byTitle = tvRows;
        Assert(tvRows["Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP"].GetProperty("eligible").GetBoolean(), "The matching episode is eligible");
        Rejected("Example.Show.S01E01E02.1080p.WEB-DL-GRP", "multi_episode");
        Rejected("Example.Show.S01.1080p.WEB-DL-GRP", "season_pack");
        Rejected("Example.Show.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP", "episode_mismatch");
        Rejected("Example Show - 101 [1080p WEB-DL]", "absolute_numbering");
        var special = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[3]}"), 200, "Special search");
        Assert(special.GetProperty("eligibleCount").GetInt32() == 0 && special.GetProperty("candidates").EnumerateArray()
                .All(row => row.GetProperty("rejections").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "ambiguous_special")),
            "Specials are refused with a visible reason");

        // ---- A5: forged, foreign and rejected grabs never persist anything.
        search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Movie search for grabbing");
        var searchId = search.GetProperty("searchId").AsGuid();
        rows = search.GetProperty("candidates").EnumerateArray().ToArray();
        string Release(JsonElement result, string title) => result.GetProperty("candidates").EnumerateArray()
            .Single(row => row.GetProperty("rawTitle").GetString() == title).GetProperty("releaseId").GetString()!;
        var bestRelease = Release(search, "Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP");
        object Grab(Guid id, string release, string key) => new { searchId = id, releaseId = release, idempotencyKey = key };
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(Guid.NewGuid(), bestRelease, "forged-0001")), 404,
            "search_not_found", "A forged search id is refused");
        await ExpectAsync(admin2.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, bestRelease, "foreign-0001")), 404,
            "search_not_found", "Another administrator cannot grab from someone else's search");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
                Grab(searchId, Release(search, "Example.Movie.2024.2160p.BluRay.REMUX.HEVC.TrueHD.Atmos-GRP"), "rejected-0001")), 409,
            "release_rejected", "Rejected releases cannot be grabbed");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, "not-a-release", "missing-0001")), 404,
            "release_not_found", "Unknown release ids are refused");
        Assert((await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId, releaseId = bestRelease, idempotencyKey = "extra-0001", downloadUrl = "http://attacker.invalid/x.torrent"
            })).StatusCode == HttpStatusCode.BadRequest, "A browser-supplied download URL is refused");
        Assert((await ordinary.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, bestRelease, "ordinary-0001"))).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot grab");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
                Grab(searchId, Release(search, "Example.Movie.2024.1080p.BluRay.x265-HYB"), "hybrid-0001")), 502,
            "unsupported_hash", "A hybrid v1/v2 torrent is refused before anything persists");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
                Grab(searchId, Release(search, "Example.Movie.2024.1080p.BluRay.x264-REDIR"), "redirect-0001")), 502,
            "redirect_rejected", "A torrent link redirecting to another host is refused");
        Assert(canary.Hits == 0, "The redirect target was never contacted");
        await using (var database = new ModDbContext(dbPath))
            Assert(!await database.GrabOperations.AnyAsync(), "No refused grab persisted an operation");

        // ---- Decision 2: a held grab is cancellable, idempotently, and nothing reaches Transmission.
        var held = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, bestRelease, "cancel-0001")), 202,
            "Grab is accepted into its hold");
        var heldId = held.GetProperty("id").AsGuid();
        Assert(held.GetProperty("state").GetString() == "pending" && held.GetProperty("cancellable").GetBoolean() &&
            held.GetProperty("holdUntil").GetDateTime() > DateTime.UtcNow && held.GetProperty("infoHash").GetString() == movieTorrent.InfoHash,
            "A held grab carries its verified torrent identity and hold deadline");
        Assert((await ordinary.PostAsync($"/JellyfinMod/Grabs/{heldId}/Cancel", null)).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot cancel");
        var cancelled = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Grabs/{heldId}/Cancel", null), 200, "Cancel during hold");
        var cancelledAgain = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Grabs/{heldId}/Cancel", null), 200, "Repeated cancel");
        Assert(cancelled.GetProperty("state").GetString() == "cancelled" && cancelledAgain.GetProperty("state").GetString() == "cancelled",
            "Cancel is idempotent");
        await Task.Delay(hold + TimeSpan.FromSeconds(1.5));
        Assert(transmission.AddCalls == 0 && (await ReadAsync(await admin.GetAsync($"/JellyfinMod/Grabs/{heldId}"), 200, "Grab read"))
            .GetProperty("state").GetString() == "cancelled", "A cancelled grab is never sent after its hold");
        Assert(await HistoryCountAsync(admin, movieId, "grab_cancelled") == 1, "Cancel writes exactly one history event");
        var replay = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, bestRelease, "cancel-0001")), 200,
            "Same key and payload replays");
        Assert(replay.GetProperty("id").AsGuid() == heldId, "An idempotent replay returns the same operation");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(searchId, Release(search, "Example.Movie.2024.720p.WEBRip.x264-GRP"), "cancel-0001")), 409, "idempotency_conflict",
            "A reused key with a different payload conflicts");

        // ---- Concurrent grabs: one operation, one torrent.
        var sameKey = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, bestRelease, "movie-grab-0001"))));
        var sameKeyIds = new HashSet<Guid>();
        foreach (var response in sameKey)
        {
            Assert(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, "Concurrent same-key grabs converge: " + response.StatusCode);
            sameKeyIds.Add(Json.Parse(await response.Content.ReadAsStringAsync()).GetProperty("id").AsGuid());
        }

        Assert(sameKeyIds.Count == 1, "Concurrent same-key grabs return one operation");
        var movieGrabId = sameKeyIds.Single();
        var racers = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", Grab(searchId, Release(search, "Example.Movie.2024.720p.WEBRip.x264-GRP"), $"race-000{index}"))));
        foreach (var response in racers)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert(response.StatusCode == HttpStatusCode.Conflict && body.Contains("grab_active") && body.Contains(movieGrabId.ToString("N")),
                "A second grab for an active target is refused and names the owner");
        }

        var accepted = await WaitForStateAsync(admin, movieGrabId, "accepted");
        Assert(transmission.AddCalls == 1 && transmission.Torrents.TryGetValue(movieTorrent.InfoHash, out var added) &&
            added.DownloadDir == "/downloads/jellyfinmod" &&
            added.Labels.SequenceEqual(["jellyfinmod-test", "jfmod-" + movieGrabId.ToString("N")]) &&
            added.SeedRatioMode == 2 && added.SeedIdleMode == 2,
            "Transmission holds exactly one torrent with the label, directory, ownership label and unlimited seed modes");
        Assert(accepted.GetProperty("openUrl").GetString() == "http://127.0.0.1:9/transmission/web/" &&
            accepted.GetProperty("seedRatio").GetDouble() == 1.0 && accepted.GetProperty("seedMinutes").GetInt32() == 2880 &&
            accepted.GetProperty("acceptedAt").ValueKind == JsonValueKind.String, "The accepted grab records seed requirements and the client link");
        Assert(await HistoryCountAsync(admin, movieId, "grabbed") == 1, "Acceptance writes exactly one grabbed event");
        movieDetail = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Entries/{movieId}"), 200, "Entry detail after grab");
        var viewerDetail = await ReadAsync(await ordinary.GetAsync($"/JellyfinMod/Entries/{movieId}"), 200, "Viewer entry detail after grab");
        Assert(movieDetail.GetProperty("acquisition").GetProperty("state").GetString() == "accepted" &&
            movieDetail.GetProperty("acquisition").GetProperty("operationId").AsGuid() == movieGrabId &&
            // Phase 5 projects an accepted grab onto the file-less card as "grabbed" (PHASE4 A6); the stored state is unchanged.
            movieDetail.GetProperty("entry").GetProperty("state").GetString() == "grabbed" &&
            viewerDetail.GetProperty("acquisition").GetProperty("state").GetString() == "accepted" &&
            viewerDetail.GetProperty("acquisition").GetProperty("operationId").ValueKind == JsonValueKind.Null &&
            viewerDetail.GetProperty("acquisition").GetProperty("releaseTitle").ValueKind == JsonValueKind.Null,
            "Entry detail summarizes the grab without changing file state; ordinary users see only its state");
        await using (var stored = new ModDbContext(dbPath))
            Assert((await stored.Entries.AsNoTracking().SingleAsync(entry => entry.Id == movieId)).State == FileState.None,
                "The grab projection never writes the entry's file state");
        var blocked = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Search after grab");
        Assert(!blocked.GetProperty("grab").GetProperty("available").GetBoolean() &&
            blocked.GetProperty("grab").GetProperty("reason").GetString() == "grab_active" &&
            blocked.GetProperty("grab").GetProperty("activeOperationId").AsGuid() == movieGrabId, "The picker learns the target is already grabbed");
        await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Entries/{movieId}"), 409, "grab_active", "An entry with an active grab cannot be removed");

        // Cross-library copy of the same title: the same torrent cannot be owned twice.
        var copySearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={copyId}"), 200, "Copy search");
        var copyConflict = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(copySearch.GetProperty("searchId").AsGuid(), Release(copySearch, "Example.Movie.2024.1080p.WEB-DL.DDP5.1.H.264-GRP"), "copy-0001"));
        var copyBody = await copyConflict.Content.ReadAsStringAsync();
        Assert(copyConflict.StatusCode == HttpStatusCode.Conflict && copyBody.Contains("duplicate_hash") && copyBody.Contains(movieGrabId.ToString("N")) &&
            transmission.AddCalls == 1, "A second library's copy cannot claim a torrent another grab owns");

        // An unrelated torrent already in the client is a conflict and is never claimed or changed.
        transmission.Torrents[webripTorrent.InfoHash] = new HeldTorrent { Hash = webripTorrent.InfoHash, DownloadDir = "/elsewhere", Labels = ["other"] };
        var unrelated = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(copySearch.GetProperty("searchId").AsGuid(), Release(copySearch, "Example.Movie.2024.720p.WEBRip.x264-GRP"), "unrelated-0001")), 202,
            "Grab of a release whose torrent already exists");
        var unrelatedResult = await WaitForStateAsync(admin, unrelated.GetProperty("id").AsGuid(), "failed");
        Assert(unrelatedResult.GetProperty("failureCode").GetString() == "client_torrent_exists" && transmission.AddCalls == 1 &&
            transmission.Torrents[webripTorrent.InfoHash].Labels.SequenceEqual(["other"]) && transmission.Torrents[webripTorrent.InfoHash].SeedRatioMode == 0,
            "An unrelated client torrent is left untouched");
        transmission.Torrents.TryRemove(webripTorrent.InfoHash, out _);

        // Stale and expired searches.
        copySearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={copyId}"), 200, "Copy search");
        var hdCurrent = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/QualityProfiles")).EnumerateArray()
            .Single(value => value.GetProperty("id").AsGuid() == hdId);
        await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{hdId}", new
        {
            name = "HD", qualities = Strings(hdCurrent.GetProperty("qualities")), minimumBytesPerHour = 500_000_000L,
            maximumBytesPerHour = 20_000_000_000L, revision = hdCurrent.GetProperty("revision").GetInt32()
        }), 200, "Profile revision advances");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(copySearch.GetProperty("searchId").AsGuid(), Release(copySearch, "Example.Movie.2024.720p.WEBRip.x264-GRP"), "stale-0001")),
            404, "search_not_found", "A settings change forgets cached searches");
        copySearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={copyId}"), 200, "Copy search");
        time.Offset += TimeSpan.FromMinutes(11);
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(copySearch.GetProperty("searchId").AsGuid(), Release(copySearch, "Example.Movie.2024.720p.WEBRip.x264-GRP"), "expired-0001")),
            410, "search_expired", "An expired search must be repeated");

        // A lost response after Transmission added the torrent leaves the grab unknown, never resubmitted.
        copySearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={copyId}"), 200, "Copy search");
        transmission.DropAfterAdd = true;
        var lost = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(copySearch.GetProperty("searchId").AsGuid(), Release(copySearch, "Example.Movie.2024.720p.WEBRip.x264-GRP"), "lost-0001")), 202,
            "Grab whose response will be lost");
        var lostId = lost.GetProperty("id").AsGuid();
        var lostResult = await WaitForStateAsync(admin, lostId, "unknown");
        transmission.DropAfterAdd = false;
        Assert(lostResult.GetProperty("failureCode").GetString() == "client_unconfirmed" && transmission.AddCalls == 2 &&
            transmission.Torrents.ContainsKey(webripTorrent.InfoHash) && await HistoryCountAsync(admin, copyId, "grabbed") == 0,
            "A lost add response is unknown; the torrent reached the client but no grabbed event is written yet");
        var retryBlocked = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={copyId}"), 200, "Copy search while unknown");
        Assert(retryBlocked.GetProperty("grab").GetProperty("reason").GetString() == "grab_active", "An unknown grab blocks blind resubmission");

        // Episode grab and a refused add.
        var episodeGrabSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[0]}"), 200,
            "Episode search for grabbing");
        var episodeGrab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(episodeGrabSearch.GetProperty("searchId").AsGuid(), Release(episodeGrabSearch, "Example.Show.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP"),
                "episode-0001")), 202, "Episode grab");
        await WaitForStateAsync(admin, episodeGrab.GetProperty("id").AsGuid(), "accepted");
        var seriesDetail = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Entries/{seriesId}"), 200, "Series detail");
        Assert(seriesDetail.GetProperty("episodes").EnumerateArray().Single(e => e.GetProperty("id").AsGuid() == episodeIds[0])
                .GetProperty("acquisition").GetProperty("state").GetString() == "accepted" &&
            seriesDetail.GetProperty("episodes").EnumerateArray().Single(e => e.GetProperty("id").AsGuid() == episodeIds[1])
                .GetProperty("acquisition").ValueKind == JsonValueKind.Null &&
            seriesDetail.GetProperty("history").EnumerateArray().Any(h => h.GetProperty("summary").GetString() == "Grabbed S01E01 webdl-1080p from Good"),
            "An episode grab is summarized on that episode only, with one history event naming it");

        transmission.RefuseAdd = true;
        var refusedSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[1]}"), 200, "E02 search");
        var refused = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(refusedSearch.GetProperty("searchId").AsGuid(), Release(refusedSearch, "Example.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP"),
                "refused-0001")), 202, "Grab Transmission will refuse");
        var refusedResult = await WaitForStateAsync(admin, refused.GetProperty("id").AsGuid(), "failed");
        transmission.RefuseAdd = false;
        Assert(refusedResult.GetProperty("failureCode").GetString() == "client_rejected" && !refusedResult.GetProperty("active").GetBoolean() &&
            await HistoryCountAsync(admin, seriesId, "grab_failed") == 1, "A refused add fails honestly and frees the episode");

        // Lost connection before Transmission recorded anything: unknown until identity lookup can decide.
        transmission.DropBeforeAdd = true;
        refusedSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[1]}"), 200, "E02 search");
        var dropped = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(refusedSearch.GetProperty("searchId").AsGuid(), Release(refusedSearch, "Example.Show.S01E02.1080p.WEB-DL.DDP5.1.H.264-GRP"),
                "dropped-0001")), 202, "Grab whose connection drops");
        var droppedId = dropped.GetProperty("id").AsGuid();
        await WaitForStateAsync(admin, droppedId, "unknown");
        transmission.DropBeforeAdd = false;
        var recheck = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Grabs/{droppedId}/Recheck", null), 200, "Recheck within grace");
        Assert(recheck.GetProperty("state").GetString() == "unknown", "An absent torrent inside the in-flight grace stays unknown");

        // Interrupted during the hold: the restart below must fail it without sending anything.
        var e3Search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodeIds[2]}"), 200, "E03 search");
        var interrupted = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab",
            Grab(e3Search.GetProperty("searchId").AsGuid(), Release(e3Search, "Example.Show.S01E03.1080p.WEB-DL.DDP5.1.H.264-GRP"), "held-0001")),
            202, "Grab interrupted by a restart");
        var interruptedId = interrupted.GetProperty("id").AsGuid();
        var addsBeforeRestart = transmission.AddCalls;
        await host.DisposeAsync();

        // ---- Restart: recovery resolves every unresolved operation by identity lookup only.
        host = await PluginHost.StartAsync(world, dbPath, time, logs, hold, configuration);
        await host.Service<GrabDispatcher>().Recovery;
        using var restarted = host.Client(world.Admin, true);
        Assert((await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Grabs/{lostId}"), 200, "Lost grab after restart")).GetProperty("state")
                .GetString() == "accepted" && transmission.Torrents[webripTorrent.InfoHash].SeedRatioMode == 2 &&
            await HistoryCountAsync(restarted, copyId, "grabbed") == 1, "A torrent added before the response was lost is recovered as accepted once");
        var droppedAfter = await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Grabs/{droppedId}"), 200, "Dropped grab after restart");
        Assert(droppedAfter.GetProperty("state").GetString() == "failed" && droppedAfter.GetProperty("failureCode").GetString() == "client_absent",
            "A torrent the client never recorded is resolved as failed after restart");
        var interruptedAfter = await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Grabs/{interruptedId}"), 200, "Held grab after restart");
        Assert(interruptedAfter.GetProperty("state").GetString() == "failed" &&
            interruptedAfter.GetProperty("failureCode").GetString() == "interrupted_before_submit" &&
            transmission.AddCalls == addsBeforeRestart && !transmission.Torrents.ContainsKey(episodeThreeTorrent.InfoHash),
            "A grab interrupted during its hold fails without anything being sent");
        Assert((await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Grabs/{movieGrabId}"), 200, "Movie grab after restart")).GetProperty("state")
                .GetString() == "accepted" && await HistoryCountAsync(restarted, movieId, "grabbed") == 1,
            "Accepted grabs and their single history event survive a restart");
        var afterRestartTest = await ReadAsync(await restarted.PostAsync($"/JellyfinMod/Settings/Indexers/{goodId}/Test", null), 200, "Indexer test after restart");
        Assert(afterRestartTest.GetProperty("ok").GetBoolean(), "Stored secrets survive a restart");

        // The administrator removed the torrent in Transmission: a recheck frees the target, history stays.
        transmission.Torrents.TryRemove(movieTorrent.InfoHash, out _);
        var released = await ReadAsync(await restarted.PostAsync($"/JellyfinMod/Grabs/{movieGrabId}/Recheck", null), 200, "Recheck accepted grab");
        Assert(released.GetProperty("state").GetString() == "accepted" && !released.GetProperty("active").GetBoolean(),
            "An accepted grab whose torrent was removed in the client releases its target");
        var freed = await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Search after release");
        Assert(freed.GetProperty("grab").GetProperty("available").GetBoolean(), "The released target can be grabbed again");

        // Entry profile assignment is a separate administrator action; episodes inherit it.
        using var viewer = host.Client(world.Ordinary, false);
        Assert((await viewer.PatchAsJsonAsync($"/JellyfinMod/Entries/{movieId}", new { qualityProfileId = sdId })).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot assign profiles");
        await ExpectAsync(restarted.PatchAsJsonAsync($"/JellyfinMod/Entries/{movieId}", new { qualityProfileId = Guid.NewGuid() }), 400,
            "invalid_quality_profile", "Unknown profiles are refused");
        var assigned = await ReadAsync(await restarted.PatchAsJsonAsync($"/JellyfinMod/Entries/{movieId}", new { qualityProfileId = sdId }), 200,
            "Assign a profile");
        Assert(assigned.GetProperty("qualityProfileId").AsGuid() == sdId && assigned.GetProperty("monitored").GetBoolean(),
            "Assigning a profile keeps monitoring");
        var assignedSearch = await ReadAsync(await restarted.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "Search with assigned profile");
        Assert(assignedSearch.GetProperty("profile").GetProperty("id").AsGuid() == sdId &&
            !assignedSearch.GetProperty("profile").GetProperty("inherited").GetBoolean(), "Searches use the entry's saved profile");
        await ExpectAsync(restarted.DeleteAsync($"/JellyfinMod/Settings/QualityProfiles/{sdId}"), 409, "profile_assigned",
            "An assigned profile cannot be deleted");
        Assert((await restarted.PatchAsJsonAsync($"/JellyfinMod/Entries/{seriesId}/Episodes/{episodeIds[0]}", new { qualityProfileId = sdId }))
            .StatusCode == HttpStatusCode.BadRequest, "Episodes cannot carry their own profile");
        var inherited = await ReadAsync(await restarted.PatchAsJsonAsync($"/JellyfinMod/Entries/{movieId}", new { qualityProfileId = (Guid?)null }), 200,
            "Restore inheritance");
        Assert(inherited.GetProperty("qualityProfileId").ValueKind == JsonValueKind.Null, "An explicit null restores inheritance");

        // Nothing durable carries a credential or passkey.
        var dbBytes = await File.ReadAllBytesAsync(dbPath);
        foreach (var secret in secrets)
            Assert(!Contains(dbBytes, Encoding.UTF8.GetBytes(secret)), "The SQLite database never stores a credential or passkey");
        Assert(!torznab.Downloads.IsEmpty && torznab.Downloads.All(download => download.Contains(passkey, StringComparison.Ordinal)),
            "Torrent downloads used the indexer's own authenticated links server-side");
        Console.WriteLine($"Evidence: adds={transmission.AddCalls} sets={transmission.SetCalls} torrents={transmission.Torrents.Count} " +
            $"handshakes={transmission.HandshakeRejections} torznabDownloads={torznab.Downloads.Count} canaryHits={canary.Hits}");
    }
    finally
    {
        await host.DisposeAsync();
    }
}

static async Task<JsonElement> WaitForStateAsync(HttpClient client, Guid id, string state)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    JsonElement current = default;
    while (DateTime.UtcNow < deadline)
    {
        current = Json.Parse(await client.GetStringAsync($"/JellyfinMod/Grabs/{id}"));
        if (current.GetProperty("state").GetString() == state) return current;
        await Task.Delay(200);
    }

    throw new InvalidOperationException($"Grab {id} did not reach {state}: {current}");
}

static async Task<int> HistoryCountAsync(HttpClient client, Guid entryId, string eventType) =>
    Json.Parse(await client.GetStringAsync($"/JellyfinMod/Entries/{entryId}")).GetProperty("history").EnumerateArray()
        .Count(item => item.GetProperty("eventType").GetString() == eventType);

static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
{
    var body = await response.Content.ReadAsStringAsync();
    Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
    return Json.Parse(body);
}

static async Task ExpectAsync(Task<HttpResponseMessage> request, int status, string type, string message)
{
    using var response = await request;
    var body = await response.Content.ReadAsStringAsync();
    Assert((int)response.StatusCode == status && body.Contains($"\"{type}\"", StringComparison.Ordinal),
        $"{message}: expected {status} {type}, got {(int)response.StatusCode} {body}");
}

static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

static string Metadata(string mediaType, int tmdbId, string title, int year, string? imdbId, int? tvdbId, int? runtime) =>
    JsonSerializer.Serialize(new TmdbMetadata(mediaType, tmdbId, title, new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null,
        imdbId, tvdbId, false, null, runtime, [], [], mediaType == "series" ? [new TmdbSeason(1, "Season 1", 3, null, null)] : []));

static string Base32(byte[] bytes)
{
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    var output = new StringBuilder();
    int buffer = 0, bits = 0;
    foreach (var value in bytes)
    {
        buffer = (buffer << 8) | value;
        bits += 8;
        while (bits >= 5)
        {
            output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
            bits -= 5;
        }
    }

    if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
    return output.ToString();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
