using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Phase 7 S9 Prowlarr: a real Kestrel plugin host (authentication, authorization, MVC serialization, EF migrations,
// SQLite) talking real HTTP to a Prowlarr boundary server, whose per-indexer feeds forward to the Torznab boundary.
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-prowlarr-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var logs = new CapturingLoggerProvider();
var secrets = new List<string>();
var bodies = new List<string>();
try
{
    await RunAsync(folder, logs, secrets, bodies);
    foreach (var secret in secrets)
    {
        Assert(!logs.Lines.Any(line => line.Contains(secret, StringComparison.Ordinal)), "Logs never print a Prowlarr key");
        Assert(!bodies.Any(body => body.Contains(secret, StringComparison.Ordinal)), "No response returns a Prowlarr key");
    }

    Console.WriteLine($"PASS: Phase 7 Prowlarr sync, fail-closed rules, overrides, breaker, rotation and removal ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static async Task RunAsync(string folder, CapturingLoggerProvider logs, List<string> secrets, List<string> bodies)
{
    var media = Path.Combine(folder, "media");
    foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
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
    var dbPath = Path.Combine(folder, "jellyfinmod.db");
    await using var torznab = new TorznabBoundary();
    await torznab.StartAsync();
    await using var prowlarr = new ProwlarrBoundary(torznab);
    await prowlarr.StartAsync();
    prowlarr.ApiKey = "prowlarr-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    secrets.Add(prowlarr.ApiKey);
    torznab.Indexers["alpha"] = new IndexerScript { ApiKey = prowlarr.ApiKey, Caps = TorznabBoundary.Caps("q,imdbid", "q,tvdbid,season,ep") };
    torznab.Indexers["beta"] = new IndexerScript { ApiKey = prowlarr.ApiKey, Caps = TorznabBoundary.Caps("q", "q") };
    prowlarr.Indexers[1] = new() { ["id"] = 1, ["name"] = "Alpha", ["protocol"] = "torrent", ["enable"] = true, ["priority"] = 10,
        ["supportsRedirect"] = true, ["indexerUrls"] = new JsonArray("https://alpha.example/"),
        ["capabilities"] = new JsonObject { ["categories"] = new JsonArray(
            new JsonObject { ["id"] = 2000, ["subCategories"] = new JsonArray(new JsonObject { ["id"] = 2040 }) },
            new JsonObject { ["id"] = 5000, ["subCategories"] = new JsonArray(new JsonObject { ["id"] = 5040 }) },
            new JsonObject { ["id"] = 8000 }) } };
    prowlarr.Feeds[1] = "alpha";
    prowlarr.Indexers[2] = new() { ["id"] = 2, ["name"] = "Beta", ["protocol"] = "torrent", ["enable"] = false, ["priority"] = 30 };
    prowlarr.Feeds[2] = "beta";
    prowlarr.Indexers[3] = new() { ["id"] = 3, ["name"] = "Gamma", ["protocol"] = "usenet", ["enable"] = true, ["priority"] = 5 };

    var time = new ShiftedTimeProvider();
    var configuration = new PluginConfiguration();
    var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(2), configuration);
    try
    {
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

        async Task<JsonElement[]> Indexers() => (await Read(admin.GetAsync("/JellyfinMod/Settings/Indexers"), 200, "Indexers read")).EnumerateArray().ToArray();
        JsonElement Synced(JsonElement[] rows, int prowlarrId) => rows.Single(row => row.GetProperty("prowlarrIndexerId").ValueKind == JsonValueKind.Number &&
            row.GetProperty("prowlarrIndexerId").GetInt32() == prowlarrId);

        // A manual indexer already named Alpha: the synced one must take the source-suffixed name.
        var manualKey = "manual-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        torznab.Indexers["manual"] = new IndexerScript { ApiKey = manualKey, Caps = TorznabBoundary.Caps("q", "q") };
        await Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
        {
            name = "Alpha", baseUrl = new Uri(torznab.Address, "/manual/api").ToString(), enabled = true, categories = new[] { 2000 },
            apiKey = new { action = "replace", value = manualKey }
        }), 201, "A manual indexer named Alpha exists");

        // ---- Authorization on every Prowlarr route.
        var any = Guid.NewGuid();
        foreach (var (method, path) in new[] { ("GET", ""), ("POST", ""), ("PATCH", $"/{any}"), ("DELETE", $"/{any}"), ("POST", $"/{any}/Test"), ("POST", $"/{any}/Sync") })
        {
            HttpRequestMessage Message() => new(new HttpMethod(method), "/JellyfinMod/Settings/Prowlarr" + path)
            {
                Content = method is "POST" or "PATCH" ? JsonContent.Create(new { name = "x", baseUrl = "http://127.0.0.1:1" }) : null
            };
            Assert((await anonymous.SendAsync(Message())).StatusCode == HttpStatusCode.Unauthorized, $"Anonymous is refused: {method} Prowlarr{path}");
            Assert((await ordinary.SendAsync(Message())).StatusCode == HttpStatusCode.Forbidden, $"Ordinary user is refused: {method} Prowlarr{path}");
        }

        await Expect(Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/Prowlarr", new
        {
            name = "Prowlarr", baseUrl = "http://user:pw@127.0.0.1:9696", apiKey = new { action = "replace", value = prowlarr.ApiKey }
        }), 400, "A URL with credentials is refused"), "invalid_prowlarr_url");
        var wrong = "wrong-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        secrets.Add(wrong);
        var source = await Read(admin.PostAsJsonAsync("/JellyfinMod/Settings/Prowlarr", new
        {
            name = "Prowlarr", baseUrl = prowlarr.Address.ToString(), apiKey = new { action = "replace", value = wrong }
        }), 201, "Administrator adds the source");
        var sourceId = source.GetProperty("id").AsGuid();
        Assert(source.GetProperty("apiKeyConfigured").GetBoolean(), "The source reports its key as configured, never the value");
        var test = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Test", null), 200, "Wrong-key test");
        Assert(test.GetProperty("code").GetString() == "unauthorized", "A wrong Prowlarr key is reported as unauthorized");
        var sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Wrong-key sync");
        Assert(sync.GetProperty("code").GetString() == "unauthorized" && (await Indexers()).Length == 1, "A refused sync changes nothing");
        source = await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}", new
        {
            name = "Prowlarr", baseUrl = prowlarr.Address.ToString(), apiKey = new { action = "replace", value = prowlarr.ApiKey }, revision = 1
        }), 200, "Administrator saves the right key");
        test = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Test", null), 200, "Test");
        Assert(test.GetProperty("ok").GetBoolean() && test.GetProperty("version").GetString() == "1.99.0-boundary",
            "Test reads system status, health and one enabled torrent indexer");

        // ---- First sync: one row per torrent indexer, verified through Prowlarr with the source's key.
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(sync.GetProperty("code").GetString() == "ok" && sync.GetProperty("seen").GetInt32() == 2 && sync.GetProperty("created").GetInt32() == 2 &&
            sync.GetProperty("verified").GetInt32() == 1, "Sync creates both torrent indexers and verifies the enabled one: " + sync.GetRawText());
        var rows = await Indexers();
        var alpha = Synced(rows, 1);
        var beta = Synced(rows, 2);
        Assert(alpha.GetProperty("name").GetString() == "Alpha (Prowlarr)" && alpha.GetProperty("managedBy").GetString() == "prowlarr" &&
            alpha.GetProperty("baseUrl").GetString() == new Uri(prowlarr.Address, "/1/api").ToString() &&
            alpha.GetProperty("categories").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([2000, 2040, 5000, 5040]) &&
            alpha.GetProperty("priority").GetInt32() == 10 &&
            alpha.GetProperty("downloadHosts").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["127.0.0.1", "alpha.example"]) &&
            alpha.GetProperty("verified").GetBoolean() && alpha.GetProperty("enabled").GetBoolean() && !alpha.GetProperty("apiKeyConfigured").GetBoolean(),
            "The synced indexer carries the documented name, feed URL, movie/TV categories, priority and download hosts, and no key of its own");
        Assert(!beta.GetProperty("enabled").GetBoolean(), "An indexer disabled in Prowlarr arrives disabled");
        Assert(!rows.Any(row => row.GetProperty("name").GetString() == "Gamma"), "Usenet indexers are not imported");
        Assert(torznab.Indexers["alpha"].Queries.Any(query => query.Contains("t=caps", StringComparison.Ordinal) &&
            query.Contains("apikey=" + prowlarr.ApiKey, StringComparison.Ordinal)), "t=caps went through Prowlarr's feed with the source key");
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(await database.AcquisitionIndexers.Where(row => row.ManagedBy == "prowlarr").AllAsync(row => row.ApiKeySecretRef == null),
                "Synced rows hold no key reference");
            Assert(CountOf(File.ReadAllText(Path.Combine(folder, "acquisition-secrets.json")), prowlarr.ApiKey) == 1 &&
                !File.ReadAllText(Path.Combine(folder, "acquisition-secrets.json")).Contains(wrong, StringComparison.Ordinal),
                "The Prowlarr key exists exactly once in the secret store and the replaced one is gone");
            Assert(await database.History.AnyAsync(row => row.EventType == "prowlarr_synced"), "A sync is on the record");
        }

        // ---- An administrator's budget and off-switch survive syncs; identity edits are ignored; removal is refused.
        var alphaId = alpha.GetProperty("id").AsGuid();
        var patched = await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{alphaId}", new
        {
            name = "Renamed", baseUrl = "http://127.0.0.1:1/api", enabled = true, categories = new[] { 1000 }, dailyQueryBudget = 7,
            revision = alpha.GetProperty("revision").GetInt32()
        }), 200, "Administrator changes the synced indexer's budget");
        Assert(patched.GetProperty("dailyQueryBudget").GetInt32() == 7 && patched.GetProperty("name").GetString() == "Alpha (Prowlarr)" &&
            patched.GetProperty("baseUrl").GetString() == alpha.GetProperty("baseUrl").GetString(), "Only the override fields change on a synced indexer");
        Assert(patched.GetProperty("verified").GetBoolean(),
            "S9-R2: a budget change on a verified synced indexer keeps it verified (its endpoint did not change)");
        var readiness = await Read(admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition reads");
        Assert(!readiness.GetProperty("blockers").EnumerateArray().Any(value => value.GetString() == "no_verified_indexer"),
            "S9-R2: with the synced indexer the only verified one, acquisition readiness still has a verified indexer after the edit");
        await Expect(Read(admin.DeleteAsync($"/JellyfinMod/Settings/Indexers/{alphaId}"), 409, "Deleting a synced indexer is refused"), "prowlarr_managed");
        for (var run = 0; run < 3; run++) await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(Synced(await Indexers(), 1).GetProperty("dailyQueryBudget").GetInt32() == 7, "The budget override survives three syncs");

        // ---- Rotation: one key change reaches every feed.
        var rotated = "prowlarr-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        secrets.Add(rotated);
        prowlarr.ApiKey = rotated;
        torznab.Indexers["alpha"].ApiKey = rotated;
        source = (await Read(admin.GetAsync("/JellyfinMod/Settings/Prowlarr"), 200, "Sources")).EnumerateArray().Single();
        await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}", new
        {
            name = "Prowlarr", baseUrl = prowlarr.Address.ToString(), apiKey = new { action = "replace", value = rotated },
            revision = source.GetProperty("revision").GetInt32()
        }), 200, "Administrator rotates the key");
        test = await Read(admin.PostAsync($"/JellyfinMod/Settings/Indexers/{alphaId}/Test", null), 200, "Synced indexer test");
        Assert(test.GetProperty("ok").GetBoolean() && torznab.Indexers["alpha"].Queries.Last().Contains("apikey=" + rotated, StringComparison.Ordinal),
            "The next request through a synced indexer uses the rotated key");

        // ---- S9-R3: Prowlarr moves host. The synced feeds follow in the same save and are re-verified at the new
        //      address; the old boundary receives nothing more, not even with the new key.
        await using (var moved = new ProwlarrBoundary(torznab, "127.0.0.2"))
        {
            await moved.StartAsync();
            moved.ApiKey = prowlarr.ApiKey;
            foreach (var pair in prowlarr.Indexers) moved.Indexers[pair.Key] = (JsonObject)pair.Value.DeepClone();
            foreach (var pair in prowlarr.Feeds) moved.Feeds[pair.Key] = pair.Value;
            var oldRequests = prowlarr.Requests;
            source = (await Read(admin.GetAsync("/JellyfinMod/Settings/Prowlarr"), 200, "Sources")).EnumerateArray().Single();
            source = await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}", new
            {
                name = "Prowlarr", baseUrl = moved.Address.ToString(), apiKey = new { action = "unchanged" }, revision = source.GetProperty("revision").GetInt32()
            }), 200, "Administrator moves the source to a new host");
            alpha = Synced(await Indexers(), 1);
            Assert(alpha.GetProperty("baseUrl").GetString() == new Uri(moved.Address, "/1/api").ToString() &&
                alpha.GetProperty("downloadHosts").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["127.0.0.2", "alpha.example"]) &&
                alpha.GetProperty("verified").GetBoolean() && Synced(await Indexers(), 2).GetProperty("baseUrl").GetString()!.StartsWith(moved.Address.ToString(), StringComparison.Ordinal),
                "S9-R3: the synced feeds and the download allow-list name the new host at once, re-verified there");
            test = await Read(admin.PostAsync($"/JellyfinMod/Settings/Indexers/{alphaId}/Test", null), 200, "Synced indexer test after the move");
            Assert(test.GetProperty("ok").GetBoolean() && moved.FeedRequests > 0, "S9-R3: t=caps reaches the new host");
            Assert(prowlarr.Requests == oldRequests, "S9-R3: the old host receives nothing after the move");
            source = await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}", new
            {
                name = "Prowlarr", baseUrl = prowlarr.Address.ToString(), apiKey = new { action = "unchanged" }, revision = source.GetProperty("revision").GetInt32()
            }), 200, "Administrator moves the source back");
            Assert(Synced(await Indexers(), 1).GetProperty("baseUrl").GetString() == new Uri(prowlarr.Address, "/1/api").ToString() &&
                Synced(await Indexers(), 1).GetProperty("verified").GetBoolean(), "Moving back re-points and re-verifies the feeds");
        }

        // ---- S9-R1: the scheduled task honours the source's own interval.
        var task = new JellyfinMod.Services.Acquisition.ProwlarrSyncTask(host.Service<IServiceScopeFactory>());
        Assert(task.GetDefaultTriggers().Single().IntervalTicks == TimeSpan.FromMinutes(15).Ticks, "The sync task wakes every 15 minutes");
        async Task<DateTime?> LastSync()
        {
            await using var database = new ModDbContext(dbPath);
            return (await database.ProwlarrSources.AsNoTracking().SingleAsync()).LastSyncAt;
        }

        async Task SetInterval(int minutes)
        {
            var current = (await Read(admin.GetAsync("/JellyfinMod/Settings/Prowlarr"), 200, "Sources")).EnumerateArray().Single();
            await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}", new
            {
                name = "Prowlarr", baseUrl = prowlarr.Address.ToString(), apiKey = new { action = "unchanged" }, syncIntervalMinutes = minutes,
                enabled = true, revision = current.GetProperty("revision").GetInt32()
            }), 200, $"Administrator sets the interval to {minutes} minutes");
        }

        await SetInterval(60);
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        var synced = await LastSync();
        time.Offset += TimeSpan.FromMinutes(30);
        await task.ExecuteAsync(new Progress<double>(), default);
        Assert(await LastSync() == synced, "S9-R1: 30 minutes into a 60-minute interval the task does not sync");
        time.Offset += TimeSpan.FromMinutes(31);
        await task.ExecuteAsync(new Progress<double>(), default);
        var resynced = await LastSync();
        Assert(resynced > synced, "S9-R1: once 60 minutes have passed the task syncs");
        await SetInterval(15);
        time.Offset += TimeSpan.FromMinutes(16);
        await task.ExecuteAsync(new Progress<double>(), default);
        Assert(await LastSync() > resynced, "S9-R1: with a 15-minute interval it syncs after 16 minutes, where 60 would have waited");
        await SetInterval(360);
        time.Offset += TimeSpan.FromMinutes(16);
        var before360 = await LastSync();
        await task.ExecuteAsync(new Progress<double>(), default);
        Assert(await LastSync() == before360, "S9-R1: back at the 360-minute default it waits again");

        // ---- Prowlarr's own back-off opens the breaker.
        prowlarr.DisabledTill[1] = DateTime.UtcNow.AddHours(3);
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(Synced(await Indexers(), 1).GetProperty("breakerOpenUntil").ValueKind == JsonValueKind.String, "disabledTill opens the breaker");

        // ---- Disabled in Prowlarr disables locally; enabled again re-enables; an administrator's off-switch wins.
        prowlarr.Indexers[1]["enable"] = false;
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(!Synced(await Indexers(), 1).GetProperty("enabled").GetBoolean(), "Disabled in Prowlarr disables it locally");
        prowlarr.Indexers[1]["enable"] = true;
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        alpha = Synced(await Indexers(), 1);
        Assert(alpha.GetProperty("enabled").GetBoolean(), "Enabled again in Prowlarr re-enables it");
        await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{alphaId}", new
        {
            name = "Alpha", baseUrl = "http://x/api", enabled = false, categories = new[] { 2000 }, dailyQueryBudget = 7, revision = alpha.GetProperty("revision").GetInt32()
        }), 200, "Administrator turns the synced indexer off");
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(!Synced(await Indexers(), 1).GetProperty("enabled").GetBoolean(), "A sync never enables what an administrator disabled");
        alpha = Synced(await Indexers(), 1);
        await Read(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/Indexers/{alphaId}", new
        {
            name = "Alpha", baseUrl = "http://x/api", enabled = true, categories = new[] { 2000 }, dailyQueryBudget = 7, revision = alpha.GetProperty("revision").GetInt32()
        }), 200, "Administrator turns it back on");

        // ---- Fail-closed: 401, 429 and a malformed body change nothing.
        var before = JsonSerializer.Serialize((await Indexers()).Select(row => new { id = row.GetProperty("id").GetString(), e = row.GetProperty("enabled").GetBoolean() }));
        foreach (var (fault, code) in new[] { ("401", "unauthorized"), ("429", "rate_limited"), ("schema", "prowlarr_schema"), ("500", "unreachable") })
        {
            prowlarr.Fault = fault;
            sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Faulted sync");
            var after = JsonSerializer.Serialize((await Indexers()).Select(row => new { id = row.GetProperty("id").GetString(), e = row.GetProperty("enabled").GetBoolean() }));
            Assert(sync.GetProperty("code").GetString() == code && after == before, $"A {fault} answer aborts with {code} and changes nothing");
        }

        prowlarr.Fault = null;

        // ---- Empty list: one changes nothing; two, an hour apart, disable the synced indexers.
        var saved = new Dictionary<int, JsonObject>(prowlarr.Indexers);
        prowlarr.Indexers.Clear();
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Empty sync");
        Assert(sync.GetProperty("code").GetString() == "empty_waiting" && Synced(await Indexers(), 1).GetProperty("enabled").GetBoolean(),
            "One empty answer changes nothing");
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Empty sync");
        Assert(sync.GetProperty("code").GetString() == "empty_waiting", "A second empty answer within the hour still changes nothing");
        time.Offset += TimeSpan.FromHours(2);
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Empty sync");
        alpha = Synced(await Indexers(), 1);
        Assert(sync.GetProperty("code").GetString() == "ok" && !alpha.GetProperty("enabled").GetBoolean() &&
            alpha.GetProperty("lastError").GetString() == "prowlarr_removed" && alpha.GetProperty("prowlarrRemovedAt").ValueKind == JsonValueKind.String,
            "Two empty answers an hour apart (clock shifted by the test) disable the synced indexers as removed");

        // ---- Back again: re-enabled. Then removed for good: kept 30 days, then deleted.
        foreach (var pair in saved) prowlarr.Indexers[pair.Key] = pair.Value;
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        alpha = Synced(await Indexers(), 1);
        Assert(alpha.GetProperty("enabled").GetBoolean() && alpha.GetProperty("prowlarrRemovedAt").ValueKind == JsonValueKind.Null,
            "An indexer that reappears is enabled again");
        prowlarr.Indexers.Remove(1);
        await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        alpha = Synced(await Indexers(), 1);
        Assert(!alpha.GetProperty("enabled").GetBoolean() && alpha.GetProperty("lastError").GetString() == "prowlarr_removed",
            "An indexer removed from Prowlarr is disabled with prowlarr_removed");
        time.Offset += TimeSpan.FromDays(31);
        sync = await Read(admin.PostAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}/Sync", null), 200, "Sync");
        Assert(sync.GetProperty("removed").GetInt32() == 1 && !(await Indexers()).Any(row => row.GetProperty("prowlarrIndexerId").ValueKind == JsonValueKind.Number &&
            row.GetProperty("prowlarrIndexerId").GetInt32() == 1), "After 30 days a removed indexer is deleted");

        // ---- Removing the source removes its synced indexers and its key; the manual indexer stays.
        Assert((await admin.DeleteAsync($"/JellyfinMod/Settings/Prowlarr/{sourceId}")).StatusCode == HttpStatusCode.NoContent, "Administrator removes the source");
        rows = await Indexers();
        Assert(rows.Length == 1 && rows[0].GetProperty("managedBy").GetString() == "manual" &&
            !File.ReadAllText(Path.Combine(folder, "acquisition-secrets.json")).Contains(rotated, StringComparison.Ordinal),
            "Its synced indexers and its key go with it; the manual indexer stays");
        var health = await Read(admin.GetAsync("/JellyfinMod/Health"), 200, "Health");
        Assert(health.GetProperty("Capabilities").EnumerateArray().Any(value => value.GetString() == "acquisition.prowlarr"), "Health advertises acquisition.prowlarr");
    }
    finally
    {
        await host.DisposeAsync();
    }
}

static async Task Expect(Task<JsonElement> response, string type) =>
    Assert((await response).GetProperty("type").GetString() == type, "Refusal type is " + type);

static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("ok - " + message);
}

/// <summary>
/// A real HTTP Prowlarr boundary: system status, health, the indexer list and status with <c>X-Api-Key</c>, and each
/// indexer's Torznab feed at <c>/{id}/api</c> forwarded to the Torznab boundary. Faults are injectable.
/// </summary>
internal sealed class ProwlarrBoundary(TorznabBoundary torznab, string host = "127.0.0.1") : IAsyncDisposable
{
    private int _requests;
    private int _feedRequests;
    public int Requests => _requests;
    public int FeedRequests => _feedRequests;
    private WebApplication _app = null!;
    private readonly HttpClient _forward = new();
    public string ApiKey { get; set; } = string.Empty;
    public Dictionary<int, JsonObject> Indexers { get; } = [];
    public Dictionary<int, string> Feeds { get; } = [];
    public ConcurrentDictionary<int, DateTime> DisabledTill { get; } = new();
    public string? Fault { get; set; }
    public Uri Address { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls($"http://{host}:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _requests);
            await next();
        });
        _app.MapGet("/api/v1/system/status", context => Api(context, () => new JsonObject { ["version"] = "1.99.0-boundary" }));
        _app.MapGet("/api/v1/health", context => Api(context, () => new JsonArray()));
        _app.MapGet("/api/v1/indexer", context => Api(context, () =>
        {
            if (Fault == "schema") return new JsonArray(new JsonObject { ["id"] = 9, ["name"] = "Broken" });
            return new JsonArray(Indexers.Values.Select(value => (JsonNode)value.DeepClone()).ToArray());
        }));
        _app.MapGet("/api/v1/indexerstatus", context => Api(context, () => new JsonArray(DisabledTill.Select(pair =>
            (JsonNode)new JsonObject { ["indexerId"] = pair.Key, ["disabledTill"] = pair.Value.ToString("O") }).ToArray())));
        _app.MapGet("/{id:int}/api", async context =>
        {
            Interlocked.Increment(ref _feedRequests);
            var id = int.Parse((string)context.Request.RouteValues["id"]!, System.Globalization.CultureInfo.InvariantCulture);
            if (!Feeds.TryGetValue(id, out var feed)) { context.Response.StatusCode = 404; return; }
            var response = await _forward.GetAsync(new Uri(torznab.Address, $"/{feed}/api{context.Request.QueryString}"));
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body);
        });
        await _app.StartAsync();
        Address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private async Task Api(HttpContext context, Func<JsonNode> body)
    {
        if (context.Request.Headers["X-Api-Key"] != ApiKey || Fault == "401") { context.Response.StatusCode = 401; return; }
        if (Fault == "429") { context.Response.StatusCode = 429; return; }
        if (Fault == "500") { context.Response.StatusCode = 500; return; }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body().ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        _forward.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
