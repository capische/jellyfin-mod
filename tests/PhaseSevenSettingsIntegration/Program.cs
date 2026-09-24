using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Phase 7 S7 settings contract: a real Kestrel plugin host with authentication, authorization, MVC serialization,
// EF migrations and SQLite, talking real HTTP to a TMDB boundary, a Transmission RPC boundary and a Torznab
// boundary. Nothing in the plugin is mocked; the boundaries are the external systems.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-seven-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var logs = new CapturingLoggerProvider();
var secrets = new List<string>();
var bodies = new List<string>();
try
{
    await RunAsync(folder, logs, secrets, bodies);
    foreach (var secret in secrets)
    {
        Assert(!logs.Lines.Any(line => line.Contains(secret, StringComparison.Ordinal)), "Logs never contain a secret");
        Assert(!bodies.Any(body => body.Contains(secret, StringComparison.Ordinal)), "No response ever returns a secret");
    }

    Console.WriteLine($"PASS: Phase 7 settings contract, XML import, typed endpoints, tests, overview and setup ({stopwatch.Elapsed.TotalSeconds:F1}s, {bodies.Count} responses leak-checked)");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static async Task RunAsync(string folder, CapturingLoggerProvider logs, List<string> secrets, List<string> bodies)
{
    var media = Path.Combine(folder, "media");
    foreach (var directory in new[] { "movies", "movies2", "tv", "far", "downloads" })
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
        Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
        Folder = folder
    };

    // ---- Migration: a Phase 6 database with an existing settings row upgrades with safe defaults.
    var dbPath = Path.Combine(folder, "jellyfinmod.db");
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20260920112836_PhaseSixAlternateMediaSources");
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AcquisitionSettings (Id, Enabled, Revision) VALUES ({AcquisitionSettings.SingletonId}, 0, 4)");
        await database.Database.MigrateAsync();
        var row = await database.AcquisitionSettings.AsNoTracking().SingleAsync();
        Assert(row.Revision == 4 && row.DiscoveryRevision == 1 && row.SeedProtectionRevision == 1 && row.RetentionRevision == 1 &&
            row.SeedProtectionSource == SeedProtectionSources.AcquisitionClient && row.TmdbReadAccessTokenRef is null &&
            row.SetupCompletedAt is null && row.SetupDismissedAt is null,
            "The Phase 7 migration keeps the row and starts every new area at revision 1, reading seed state through the acquisition client");
        var integrity = await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync();
        var foreignKeys = await database.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check").ToListAsync();
        Assert(integrity.SequenceEqual(["ok"]) && foreignKeys.Count == 0, "The migrated database passes integrity and foreign-key checks");
    }

    await using var tmdb = new TmdbBoundary();
    await using var transmission = new TransmissionBoundary();
    await using var torznab = new TorznabBoundary();
    await tmdb.StartAsync();
    await transmission.StartAsync();
    await torznab.StartAsync();
    tmdb.Token = "tmdb-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    secrets.Add(tmdb.Token);
    secrets.Add(transmission.Password);

    // ---- The pre-Phase-7 XML holds the token reference and a seed-protection endpoint of its own.
    var store = new AcquisitionSecretStore(folder);
    var configuration = new PluginConfiguration
    {
        TmdbReadAccessTokenRef = await store.AddAsync(tmdb.Token, default),
        TransmissionRpcUrl = transmission.Endpoint.ToString(),
        TransmissionUsername = transmission.Username,
        TransmissionPasswordRef = await store.AddAsync(transmission.Password, default)
    };
    var importedTokenRef = configuration.TmdbReadAccessTokenRef;
    var importedPasswordRef = configuration.TransmissionPasswordRef;

    void Configure(IServiceCollection services)
    {
        services.AddSingleton(provider => new SettingsXmlImportSource(provider.GetRequiredService<RetentionConfigurationSource>()));
        services.AddSingleton(new TmdbEndpoint(tmdb.Address));
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(), () => configuration,
            provider.GetRequiredService<ILogger<TmdbClient>>(), provider.GetRequiredService<AcquisitionSecretStore>(),
            () => new ModDbContext(dbPath), provider.GetRequiredService<TmdbEndpoint>()));
    }

    var time = new ShiftedTimeProvider();
    var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(2), configuration, Configure);
    try
    {
        // ---- One-time import at startup (default 11): references move, values never pass through.
        await using (var database = new ModDbContext(dbPath))
        {
            var row = await database.AcquisitionSettings.AsNoTracking().SingleAsync();
            Assert(configuration.TmdbReadAccessTokenRef is null && configuration.TransmissionRpcUrl.Length == 0 &&
                configuration.TransmissionUsername.Length == 0 && configuration.TransmissionPasswordRef is null,
                "The XML is left empty after the import");
            Assert(row.TmdbReadAccessTokenRef == importedTokenRef && row.DiscoveryRevision == 2 && row.DiscoveryVerifiedRevision is null,
                "The TMDB token reference moved into the settings row");
            Assert(row.SeedProtectionSource == SeedProtectionSources.Separate && row.SeedProtectionRpcUrl == transmission.Endpoint.ToString() &&
                row.SeedProtectionUsername == transmission.Username && row.SeedProtectionPasswordRef == importedPasswordRef &&
                row.SeedProtectionRevision == 2,
                "A seed endpoint that is not the acquisition client is imported as a separate source, unchanged");
            Assert(await store.GetAsync(importedTokenRef, default) == tmdb.Token && await store.GetAsync(importedPasswordRef, default) == transmission.Password,
                "Imported secrets stay in the 0600 store under the same references");
        }

        using var anonymous = host.Client(null, false);
        using var ordinary = host.Client(world.Ordinary, false);
        using var admin = host.Client(world.Admin, true);

        async Task<JsonElement> Read(Task<HttpResponseMessage> request, int status, string message)
        {
            using var response = await request;
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            Assert((int)response.StatusCode == status, $"{message} (expected {status}, got {(int)response.StatusCode}: {body})");
            return body.Length == 0 ? default : Json.Parse(body);
        }

        async Task Expect(Task<HttpResponseMessage> request, int status, string type, string message)
        {
            var body = await Read(request, status, message);
            Assert(body.GetProperty("type").GetString() == type, $"{message} (type {body.GetProperty("type")})");
        }

        // ---- Authorization: every new route refuses anonymous callers and ordinary users.
        var reads = new[] { "Settings/Discovery", "Settings/SeedProtection", "Settings/Retention", "Settings/Overview", "Setup/State" };
        var posts = new[] { "Settings/Discovery/Test", "Settings/SeedProtection/Test", "Setup/Dismiss" };
        var patches = new[] { "Settings/Discovery", "Settings/SeedProtection", "Settings/Retention" };
        foreach (var path in reads)
        {
            Assert((await anonymous.GetAsync("/JellyfinMod/" + path)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous is refused: GET " + path);
            Assert((await ordinary.GetAsync("/JellyfinMod/" + path)).StatusCode == HttpStatusCode.Forbidden, "Ordinary user is refused: GET " + path);
        }

        foreach (var path in posts)
        {
            Assert((await anonymous.PostAsync("/JellyfinMod/" + path, null)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous is refused: POST " + path);
            Assert((await ordinary.PostAsync("/JellyfinMod/" + path, null)).StatusCode == HttpStatusCode.Forbidden, "Ordinary user is refused: POST " + path);
        }

        foreach (var path in patches)
        {
            Assert((await anonymous.PatchAsJsonAsync("/JellyfinMod/" + path, new { revision = 1 })).StatusCode == HttpStatusCode.Unauthorized,
                "Anonymous is refused: PATCH " + path);
            Assert((await ordinary.PatchAsJsonAsync("/JellyfinMod/" + path, new { revision = 1 })).StatusCode == HttpStatusCode.Forbidden,
                "Ordinary user is refused: PATCH " + path);
        }

        var health = await Read(admin.GetAsync("/JellyfinMod/Health"), 200, "Health answers");
        var capabilities = health.GetProperty("Capabilities").EnumerateArray().Select(value => value.GetString()).ToHashSet();
        Assert(new[] { "settings.overview", "settings.discovery", "settings.seedProtection", "settings.retention", "setup" }.All(capabilities.Contains),
            "Health advertises the settings contract and setup");

        // ---- Discovery: token write-only, test gated by revision, every failure a code and a sentence.
        var discovery = await Read(admin.GetAsync("/JellyfinMod/Settings/Discovery"), 200, "Discovery reads");
        Assert(discovery.GetProperty("tokenConfigured").GetBoolean() && !discovery.GetProperty("verified").GetBoolean() &&
            discovery.GetProperty("revision").GetInt32() == 2 && !discovery.GetRawText().Contains("sec_", StringComparison.Ordinal),
            "Discovery reports the imported token as configured, never its value or reference, and unverified");
        var test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Discovery test answers");
        Assert(test.GetProperty("ok").GetBoolean() && test.GetProperty("code").GetString() == "ok" && tmdb.LastAuthorization == "Bearer " + tmdb.Token,
            "Discovery still works after the import without re-entering the token: TMDB receives it as a bearer credential");
        Assert((await Read(admin.GetAsync("/JellyfinMod/Settings/Discovery"), 200, "Discovery re-reads")).GetProperty("verified").GetBoolean(),
            "A passing test verifies the current discovery revision");

        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery", new { token = new { action = "unchanged" } }), 400,
            "revision_required", "A discovery change without a revision is refused");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery", new { token = new { action = "unchanged" }, revision = 1 }), 409,
            "revision_conflict", "A stale discovery revision is refused");
        Assert((await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery", new { token = new { action = "unchanged" }, revision = 2, extra = 1 }))
            .StatusCode == HttpStatusCode.BadRequest, "Unknown discovery fields are refused");
        var newToken = "tmdb-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        secrets.Add(newToken);
        discovery = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery",
            new { token = new { action = "replace", value = newToken }, revision = 2 }), 200, "Administrator replaces the token");
        Assert(discovery.GetProperty("revision").GetInt32() == 3 && discovery.GetProperty("tokenConfigured").GetBoolean() &&
            !discovery.GetProperty("verified").GetBoolean(), "Replacing the token advances the revision and needs a new test");
        Assert(await store.GetAsync(importedTokenRef, default) is null, "The replaced token is removed from the secret store");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Wrong-token test answers");
        Assert(!test.GetProperty("ok").GetBoolean() && test.GetProperty("code").GetString() == "unauthorized" &&
            tmdb.LastAuthorization == "Bearer " + newToken, "TMDB receives the new token and its refusal is reported as unauthorized");
        tmdb.Token = newToken;
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "New-token test answers");
        Assert(test.GetProperty("ok").GetBoolean(), "The replaced token passes");
        tmdb.Abort = true;
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Unreachable test answers");
        Assert(test.GetProperty("code").GetString() == "unreachable", "A dropped TMDB connection is reported as unreachable");
        tmdb.Abort = false;
        tmdb.Delay = TimeSpan.FromSeconds(20);
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Slow test answers");
        Assert(test.GetProperty("code").GetString() == "timeout", "A TMDB that does not answer in time is reported as a timeout");
        tmdb.Delay = TimeSpan.Zero;
        Assert(!(await Read(admin.GetAsync("/JellyfinMod/Settings/Discovery"), 200, "Discovery re-reads")).GetProperty("verified").GetBoolean(),
            "A failed test leaves discovery unverified");
        discovery = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery", new { token = new { action = "clear" }, revision = 3 }), 200,
            "Administrator clears the token");
        Assert(!discovery.GetProperty("tokenConfigured").GetBoolean(), "Clear flips the indicator");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Unconfigured test answers");
        Assert(test.GetProperty("code").GetString() == "not_configured", "A test without a token says so");
        discovery = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Discovery",
            new { token = new { action = "replace", value = newToken }, revision = 4 }), 200, "Administrator saves the token again");
        Assert((await Read(admin.PostAsync("/JellyfinMod/Settings/Discovery/Test", null), 200, "Discovery test")).GetProperty("ok").GetBoolean(),
            "The saved token passes again");

        // ---- Seed protection: separate endpoint, then the acquisition client (default 12).
        var seed = await Read(admin.GetAsync("/JellyfinMod/Settings/SeedProtection"), 200, "Seed protection reads");
        Assert(seed.GetProperty("source").GetString() == "separate" && seed.GetProperty("rpcUrl").GetString() == transmission.Endpoint.ToString() &&
            seed.GetProperty("passwordConfigured").GetBoolean() && !seed.GetProperty("legacyXmlEndpoint").GetBoolean() &&
            seed.GetProperty("revision").GetInt32() == 2, "Seed protection reports the imported separate endpoint without its password");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/SeedProtection/Test", null), 200, "Seed test answers");
        Assert(test.GetProperty("ok").GetBoolean() && test.GetProperty("version").GetString()!.Contains("boundary", StringComparison.Ordinal),
            "The imported separate endpoint reaches Transmission with its stored password");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/SeedProtection", new
        {
            source = "separate", rpcUrl = "http://user:pass@127.0.0.1:9/transmission/rpc", username = "", password = new { action = "unchanged" }, revision = 2
        }), 400, "invalid_rpc_url", "Credentials inside the RPC address are refused");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/SeedProtection", new
        {
            source = "elsewhere", password = new { action = "unchanged" }, revision = 2
        }), 400, "invalid_seed_protection_source", "An unknown source is refused");
        var wrongPassword = "wrong-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        secrets.Add(wrongPassword);
        seed = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/SeedProtection", new
        {
            source = "separate", rpcUrl = transmission.Endpoint.ToString(), username = transmission.Username,
            password = new { action = "replace", value = wrongPassword }, revision = 2
        }), 200, "Administrator replaces the separate password");
        Assert(seed.GetProperty("revision").GetInt32() == 3 && await store.GetAsync(importedPasswordRef, default) is null,
            "The replaced separate password is removed from the store");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/SeedProtection/Test", null), 200, "Wrong-password seed test answers");
        Assert(test.GetProperty("code").GetString() == "unauthorized", "A refused Transmission password is reported as unauthorized");
        seed = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/SeedProtection", new
        {
            source = "separate", rpcUrl = "http://127.0.0.1:9/transmission/rpc", username = transmission.Username,
            password = new { action = "unchanged" }, revision = 3
        }), 200, "Administrator points seed protection at a closed port");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/SeedProtection/Test", null), 200, "Unreachable seed test answers");
        Assert(test.GetProperty("code").GetString() == "unreachable", "A closed Transmission port is reported as unreachable");
        seed = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/SeedProtection", new
        {
            source = "acquisitionClient", password = new { action = "unchanged" }, revision = 4
        }), 200, "Administrator selects the acquisition client");
        Assert(seed.GetProperty("source").GetString() == "acquisitionClient" && !seed.GetProperty("passwordConfigured").GetBoolean() &&
            !File.ReadAllText(Path.Combine(folder, "acquisition-secrets.json")).Contains(wrongPassword, StringComparison.Ordinal),
            "Using the acquisition client drops the separate password from the store");
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/SeedProtection/Test", null), 200, "Seed test without a client answers");
        Assert(test.GetProperty("code").GetString() == "not_configured", "With no acquisition client there is nothing to read, and the test says so");

        var client = await Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/DownloadClients", new
        {
            name = "JellyfinMod Transmission", kind = "transmission", baseUrl = transmission.Endpoint.ToString(), username = transmission.Username,
            password = new { action = "replace", value = transmission.Password }, enabled = true, label = "jellyfinmod-test",
            downloadDirectory = "/downloads/jellyfinmod", localDirectory = Path.Combine(media, "downloads")
        }), 201, "Administrator adds the acquisition client");
        var clientId = client.GetProperty("id").AsGuid();
        Assert((await Read(admin.PostAsync($"/JellyfinMod/Settings/DownloadClients/{clientId}/Test", null), 200, "Client test"))
            .GetProperty("ok").GetBoolean(), "The acquisition client verifies");
        var acquisition = await Read(admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition reads");
        acquisition = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
        {
            enabled = false, downloadClientId = clientId, revision = acquisition.GetProperty("revision").GetInt32()
        }), 200, "Administrator selects the client");
        Assert(acquisition.GetProperty("seedProtectionMatchesClient").GetBoolean(),
            "With no XML endpoint, Settings/Acquisition reports seed protection reading the acquisition client");
        seed = await Read(admin.GetAsync("/JellyfinMod/Settings/SeedProtection"), 200, "Seed protection re-reads");
        Assert(seed.GetProperty("effectiveRpcUrl").GetString() == transmission.Endpoint.ToString() && seed.GetProperty("matchesAcquisitionClient").GetBoolean(),
            "The effective seed endpoint is the acquisition client's");
        var before = transmission.UnauthenticatedCalls;
        test = await Read(admin.PostAsync("/JellyfinMod/Settings/SeedProtection/Test", null), 200, "Seed test through the client answers");
        Assert(test.GetProperty("ok").GetBoolean() && transmission.UnauthenticatedCalls == before,
            "Seed protection reaches Transmission with the acquisition client's own credentials");

        // ---- Retention: XML-held, typed, revisioned; a selected user must exist (T13).
        var retention = await Read(admin.GetAsync("/JellyfinMod/Settings/Retention"), 200, "Retention reads");
        Assert(!retention.GetProperty("enabled").GetBoolean() && retention.GetProperty("reclaimAfterDays").GetInt32() == 14 &&
            retention.GetProperty("watchedUserMode").GetString() == "allUsers" && retention.GetProperty("revision").GetInt32() == 1,
            "Retention reports the XML values");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Retention", new
        {
            enabled = true, reclaimAfterDays = 7, watchedUserMode = "selectedUser", selectedUserId = Guid.NewGuid(), exemptFavourites = true, revision = 1
        }), 400, "invalid_selected_user", "A selected user who does not exist is refused");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Retention", new
        {
            enabled = true, reclaimAfterDays = 0, watchedUserMode = "allUsers", exemptFavourites = true, revision = 1
        }), 400, "invalid_reclaim_days", "Zero days is refused");
        retention = await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Retention", new
        {
            enabled = false, reclaimAfterDays = 7, watchedUserMode = "selectedUser", selectedUserId = world.Ordinary.Id, exemptFavourites = false, revision = 1
        }), 200, "Administrator changes retention");
        Assert(retention.GetProperty("revision").GetInt32() == 2 && retention.GetProperty("selectedUserName").GetString() == "viewer" &&
            configuration.ReclaimAfterDays == 7 && configuration.RetentionWatchedUserMode == WatchedUserMode.SelectedUser &&
            configuration.RetentionSelectedUserId == world.Ordinary.Id && !configuration.ExemptFavourites && !configuration.RetentionEnabled,
            "A retention change is written to the XML configuration and echoes the new revision");
        await Expect(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Retention", new
        {
            enabled = false, reclaimAfterDays = 9, watchedUserMode = "allUsers", exemptFavourites = true, revision = 1
        }), 409, "revision_conflict", "A stale retention revision is refused");

        // ---- Setup state and Overview, before indexers and profiles exist.
        var setup = await Read(admin.GetAsync("/JellyfinMod/Setup/State"), 200, "Setup state reads");
        string Status(JsonElement state, string id) => state.GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("id").GetString() == id).GetProperty("status").GetString()!;
        Assert(!setup.GetProperty("complete").GetBoolean() && Status(setup, "discovery") == "done" && Status(setup, "downloadClient") == "done" &&
            Status(setup, "indexers") == "pending" && Status(setup, "qualityProfile") == "blocked" && Status(setup, "enable") == "blocked",
            "Setup is derived from readiness: discovery and the client done, indexers the first incomplete step");
        var overview = await Read(admin.GetAsync("/JellyfinMod/Settings/Overview"), 200, "Overview reads");
        acquisition = await Read(admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition reads");
        var acquisitionArea = overview.GetProperty("areas").EnumerateArray().Single(area => area.GetProperty("id").GetString() == "acquisition");
        Assert(acquisitionArea.GetProperty("blockers").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(acquisition.GetProperty("blockers").EnumerateArray().Select(value => value.GetString())) &&
            !acquisitionArea.GetProperty("ready").GetBoolean(), "The Overview carries exactly the blockers Settings/Acquisition returns");
        Assert(overview.GetProperty("areas").EnumerateArray().Single(area => area.GetProperty("id").GetString() == "seedProtection")
            .GetProperty("ready").GetBoolean(), "The Overview sees seed protection configured through the client");
        var dismissed = await Read(admin.PostAsync("/JellyfinMod/Setup/Dismiss", null), 200, "Administrator dismisses setup");
        Assert(dismissed.TryGetProperty("dismissedAt", out var dismissedAt) && dismissedAt.ValueKind == JsonValueKind.String,
            "Dismiss records when the banner was hidden");

        // ---- Finish setup through the existing resources: a verified indexer, a default profile, enable.
        var indexerKey = "idx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        secrets.Add(indexerKey);
        torznab.Indexers["good"] = new IndexerScript { ApiKey = indexerKey, Caps = TorznabBoundary.Caps("q,imdbid", "q,tvdbid,season,ep") };
        var indexer = await Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "JellyfinMod Indexer", baseUrl = new Uri(torznab.Address, "/good/api").ToString(), enabled = true, categories = new[] { 2000, 5000 },
            apiKey = new { action = "replace", value = indexerKey }
        }), 201, "Administrator adds an indexer");
        Assert((await Read(admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200, "Indexer test"))
            .GetProperty("ok").GetBoolean(), "The indexer verifies");
        setup = await Read(admin.GetAsync("/JellyfinMod/Setup/State"), 200, "Setup state reads");
        Assert(Status(setup, "indexers") == "done" && Status(setup, "qualityProfile") == "pending", "A verified indexer completes its step");
        var profile = await Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new { name = "JellyfinMod HD", qualities = new[] { "webdl-1080p" } }),
            201, "Administrator adds a profile");
        acquisition = await Read(admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition reads");
        await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
        {
            enabled = false, downloadClientId = clientId, defaultQualityProfileId = profile.GetProperty("id").AsGuid(),
            revision = acquisition.GetProperty("revision").GetInt32()
        }), 200, "Administrator selects the default profile");
        setup = await Read(admin.GetAsync("/JellyfinMod/Setup/State"), 200, "Setup state reads");
        Assert(Status(setup, "qualityProfile") == "done" && Status(setup, "enable") == "pending", "A default profile completes its step");
        acquisition = await Read(admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition reads");
        await Read(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
        {
            enabled = true, downloadClientId = clientId, defaultQualityProfileId = profile.GetProperty("id").AsGuid(),
            revision = acquisition.GetProperty("revision").GetInt32()
        }), 200, "Administrator enables acquisition");
        setup = await Read(admin.GetAsync("/JellyfinMod/Setup/State"), 200, "Setup state reads");
        Assert(setup.GetProperty("complete").GetBoolean() && setup.GetProperty("completedAt").ValueKind == JsonValueKind.String &&
            setup.GetProperty("dismissedAt").ValueKind == JsonValueKind.String, "Setup completes once acquisition is enabled");

        // ---- History: settings changes are on the record with area and revision, never a value.
        await using (var database = new ModDbContext(dbPath))
        {
            var history = await database.History.AsNoTracking().Where(row => row.EventType == "settings_changed").ToListAsync();
            Assert(history.Count >= 8 && history.All(row => row.EntryId == Guid.Empty) &&
                history.Select(row => JsonDocument.Parse(row.Data!).RootElement.GetProperty("area").GetString()).ToHashSet()
                    .IsSupersetOf(["discovery", "seedProtection", "retention", "setup"]) &&
                !history.Any(row => secrets.Any(secret => row.Data!.Contains(secret, StringComparison.Ordinal))),
                "settings_changed history rows name the area and revision and hold no value");
        }
    }
    finally
    {
        await host.DisposeAsync();
    }

    // ---- Restart: every value re-reads from SQLite and the store; nothing re-imports.
    host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(2), configuration, Configure);
    try
    {
        using var admin = host.Client(world.Admin, true);
        var discovery = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Discovery"));
        var seed = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/SeedProtection"));
        var retention = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Retention"));
        var setup = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Setup/State"));
        bodies.AddRange([discovery.GetRawText(), seed.GetRawText(), retention.GetRawText(), setup.GetRawText()]);
        Assert(discovery.GetProperty("tokenConfigured").GetBoolean() && discovery.GetProperty("verified").GetBoolean() &&
            discovery.GetProperty("revision").GetInt32() == 5, "Discovery survives a restart, still verified");
        Assert(seed.GetProperty("source").GetString() == "acquisitionClient" && seed.GetProperty("revision").GetInt32() == 5,
            "Seed protection survives a restart");
        Assert(retention.GetProperty("revision").GetInt32() == 2 && retention.GetProperty("reclaimAfterDays").GetInt32() == 7,
            "Retention survives a restart");
        Assert(setup.GetProperty("complete").GetBoolean(), "Setup stays complete after a restart");
    }
    finally
    {
        await host.DisposeAsync();
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("ok - " + message);
}

/// <summary>
/// A real HTTP TMDB boundary: <c>/3/configuration</c> answers 200 for the expected bearer token and 401 otherwise,
/// with injectable delay and dropped connections.
/// </summary>
internal sealed class TmdbBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    public string Token { get; set; } = string.Empty;
    public string? LastAuthorization { get; private set; }
    public TimeSpan Delay { get; set; }
    public bool Abort { get; set; }
    public Uri Address { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/3/configuration", async context =>
        {
            LastAuthorization = context.Request.Headers.Authorization;
            if (Abort) { context.Abort(); return; }
            if (Delay > TimeSpan.Zero)
            {
                try { await Task.Delay(Delay, context.RequestAborted); } catch (OperationCanceledException) { return; }
            }

            if (LastAuthorization != "Bearer " + Token)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { status_code = 7, status_message = "Invalid API key" });
                return;
            }

            await context.Response.WriteAsJsonAsync(new { images = new { secure_base_url = "https://image.invalid/" } });
        });
        await _app.StartAsync();
        Address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
