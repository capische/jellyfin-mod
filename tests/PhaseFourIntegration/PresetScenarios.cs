using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Quality profile presets (P4.FU9, user 2026-10-15): a fresh install gets the presets with the 4K one as its default, an
/// install that has profiles keeps them untouched, Add Preset Profiles adds only what is missing, and the default 4K profile
/// takes the best allowed quality on offer, falling back down its list. Real Kestrel host, HTTP, serializer, migrations,
/// SQLite and a Torznab boundary over real HTTP.
/// </summary>
internal static class PresetScenarios
{
    private static readonly Guid FourK = Guid.Parse("8fc450fa-ec3d-4d42-962e-a7835ed239e7");
    private static readonly Guid SevenTwenty = Guid.Parse("698e6ea9-0b64-4c1f-824a-2dad08e315e8");
    private static readonly Guid AnyQuality = Guid.Parse("8afa2b3f-ebf0-40fd-a42b-bba6cebe1bde");

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "presets-media");
        foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var world = new World
        {
            Admin = new User("padmin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new User("padmin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new User("ptv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("pviewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
            Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
            Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
            Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
            Folder = folder
        };

        await FreshInstallAsync(world, Path.Combine(folder, "presets-fresh.db"), logs);
        await ExistingInstallAsync(world, Path.Combine(folder, "presets-existing.db"), logs);
        Console.WriteLine("PASS: quality profile presets: fresh install, existing install, Add Preset Profiles and 4K fallback ordering");
    }

    /// <summary>A database the plugin creates itself: every preset, the 4K one the default; then the fallback order.</summary>
    private static async Task FreshInstallAsync(World world, string dbPath, CapturingLoggerProvider logs)
    {
        await using var torznab = new TorznabBoundary();
        await torznab.StartAsync();
        var movieId = Guid.NewGuid();
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var anonymous = host.Client(null, false);
            using var ordinary = host.Client(world.Ordinary, false);
            using var admin = host.Client(world.Admin, true);

            var profiles = await ListAsync(admin);
            Assert(profiles.Count == QualityPresets.All.Count && QualityPresets.All.All(preset => profiles.ContainsKey(preset.Id)),
                "A fresh install has every preset as a profile: " + string.Join(", ", profiles.Values.Select(Name)));
            Assert(profiles.Values.Count(profile => profile.GetProperty("isDefault").GetBoolean()) == 1 &&
                profiles[FourK].GetProperty("isDefault").GetBoolean() && Name(profiles[FourK]) == "4K" &&
                profiles.Values.All(profile => profile.GetProperty("presetId").AsGuid() == profile.GetProperty("id").AsGuid()),
                "The 4K preset with fallback is the default profile, and every preset profile names its preset");
            // The one order every list of profiles shows (user, 2026-10-19): best first by the resolution of the best quality, then name.
            var listed = (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles"), 200, "Profiles read")).EnumerateArray().Select(Name).ToArray();
            Assert(listed.SequenceEqual(["4K", "Any Quality", "2K", "1080p", "720p", "SD"]), "Profiles list best first: " + string.Join(", ", listed));
            var fourK = profiles[FourK];
            Assert(Strings(fourK.GetProperty("qualities")).SequenceEqual(["remux-2160p", "bluray-2160p", "webdl-2160p", "webrip-2160p", "hdtv-2160p",
                    "bluray-1440p", "webdl-1440p", "webrip-1440p", "remux-1080p", "bluray-1080p", "webdl-1080p", "webrip-1080p", "hdtv-1080p",
                    "bluray-720p", "webdl-720p", "webrip-720p", "hdtv-720p"]) &&
                fourK.GetProperty("maximumBytesPerHour").GetInt64() == 20_000_000_000 && fourK.GetProperty("minimumBytesPerHour").ValueKind == JsonValueKind.Null &&
                fourK.GetProperty("cutoff").GetString() == "remux-2160p" && !fourK.GetProperty("upgradeAllowed").GetBoolean() &&
                fourK.GetProperty("upgradeMode").GetString() == "replace" && fourK.GetProperty("minimumSeeders").GetInt32() == 2,
                "The default preset lists 4K, 2K, 1080p then 720p by source, caps size at 20 GB per hour, sets no minimum and no upgrades: " + fourK.GetRawText());
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
            Assert(acquisition.GetProperty("defaultQualityProfileId").AsGuid() == FourK && !acquisition.GetProperty("enabled").GetBoolean() &&
                acquisition.GetProperty("qualities").EnumerateArray().Any(quality => quality.GetProperty("id").GetString() == "webdl-1440p" &&
                    quality.GetProperty("resolution").GetString() == "1440p") &&
                acquisition.GetProperty("qualities").EnumerateArray().Select(quality => quality.GetProperty("id").GetString())
                    .Where(id => id!.EndsWith("-1440p", StringComparison.Ordinal)).Order().SequenceEqual(["bluray-1440p", "webdl-1440p", "webrip-1440p"]),
                "The settings name the 4K preset as default, grabs stay off, and the vocabulary has 1440p for Blu-ray, WEB-DL and WEBRip");
            await using (var database = new ModDbContext(dbPath))
                Assert(!(await database.AcquisitionSettings.SingleAsync()).AutomationEnabled, "Automation stays off on a fresh install");

            // Admin-only.
            Assert((await anonymous.GetAsync("/JellyfinMod/Settings/QualityProfiles/Presets")).StatusCode == HttpStatusCode.Unauthorized &&
                (await ordinary.GetAsync("/JellyfinMod/Settings/QualityProfiles/Presets")).StatusCode == HttpStatusCode.Forbidden &&
                (await ordinary.PostAsync("/JellyfinMod/Settings/QualityProfiles/Presets", null)).StatusCode == HttpStatusCode.Forbidden,
                "Only administrators read or add presets");
            var presets = (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles/Presets"), 200, "Presets read")).EnumerateArray().ToArray();
            Assert(presets.Length == QualityPresets.All.Count && presets.All(preset => preset.GetProperty("added").GetBoolean() &&
                    preset.GetProperty("description").GetString()!.Length > 20) &&
                presets.Single(preset => preset.GetProperty("isDefaultPreset").GetBoolean()).GetProperty("id").AsGuid() == FourK &&
                presets.Select(Name).SequenceEqual(["4K", "2K", "1080p", "720p", "Any Quality", "SD"]),
                "Presets carry a description, the default mark and whether each is already a profile");

            // Adding again changes nothing.
            var revisions = profiles.ToDictionary(pair => pair.Key, pair => pair.Value.GetProperty("revision").GetInt32());
            var again = await ReadAsync(await admin.PostAsync("/JellyfinMod/Settings/QualityProfiles/Presets", null), 200, "Add presets again");
            Assert(again.GetProperty("added").GetArrayLength() == 0 && again.GetProperty("skipped").GetArrayLength() == QualityPresets.All.Count &&
                again.GetProperty("skipped").EnumerateArray().All(item => item.GetProperty("reason").GetString() == "already_added") &&
                again.GetProperty("defaultQualityProfileId").ValueKind == JsonValueKind.Null,
                "Adding the presets again adds nothing: " + again.GetRawText());
            var unchanged = await ListAsync(admin);
            Assert(unchanged.Count == profiles.Count && unchanged.All(pair => pair.Value.GetProperty("revision").GetInt32() == revisions[pair.Key]),
                "Adding the presets again changes no profile");

            // Presets are ordinary profiles: edit one, delete another, the default cannot be deleted.
            var edited = profiles[SevenTwenty];
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{SevenTwenty}", new
            {
                name = "My 720p", qualities = new[] { "webdl-720p" }, maximumBytesPerHour = 3_000_000_000L, upgradeMode = "replace",
                revision = edited.GetProperty("revision").GetInt32()
            }), 200, "A preset profile is edited like any other");
            Assert((await admin.DeleteAsync($"/JellyfinMod/Settings/QualityProfiles/{AnyQuality}")).StatusCode == HttpStatusCode.NoContent,
                "A preset profile that is not the default can be deleted");
            await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}"), 409, "profile_is_default",
                "The default preset cannot be deleted until another profile is the default");
            var readd = await ReadAsync(await admin.PostAsync("/JellyfinMod/Settings/QualityProfiles/Presets", null), 200, "Add presets after a delete");
            Assert(readd.GetProperty("added").EnumerateArray().Select(item => item.GetProperty("id").AsGuid()).SequenceEqual([AnyQuality]) &&
                readd.GetProperty("message").GetString()!.StartsWith("Added 1 preset profile.", StringComparison.Ordinal),
                "Add Preset Profiles brings back only the deleted preset: " + readd.GetRawText());
            var afterReadd = await ListAsync(admin);
            Assert(Name(afterReadd[SevenTwenty]) == "My 720p" && Strings(afterReadd[SevenTwenty].GetProperty("qualities")).SequenceEqual(["webdl-720p"]) &&
                afterReadd[FourK].GetProperty("isDefault").GetBoolean(), "An edited preset profile is never reset or renamed");

            // ---- Fallback: the default 4K profile takes the best allowed quality on offer and falls down its list.
            await using (var database = new ModDbContext(dbPath))
            {
                var metadata = JsonSerializer.Serialize(new TmdbMetadata("movie", 700, "Preset Movie", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, "tt0000700", null, false, null, 120, [], [], []));
                var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                    VALUES ({movieId}, 'movie', 700, 'tt0000700', 'Preset Movie', 2025, {metadata}, 0, 1, {added}, {world.Movies.Id}, 0)
                    """);
            }

            var feed = new IndexerScript { ApiKey = "preset-key", Caps = TorznabBoundary.Caps("q,imdbid", "q") };
            torznab.Indexers["presets"] = feed;
            var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Presets", baseUrl = new Uri(torznab.Address, "/presets/api").ToString(), enabled = true, categories = new[] { 2000 },
                priority = 1, minIntervalSeconds = 0, apiKey = new { action = "replace", value = feed.ApiKey }
            }), 201, "The preset scenario's indexer is created");
            var tested = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200,
                "The preset scenario's indexer is verified");
            Assert(tested.GetProperty("ok").GetBoolean(), "The preset scenario's indexer answers its capabilities");

            Dictionary<string, string> Id() => new() { ["imdbid"] = "0000700" };
            // A two-hour film: the 4K preset allows up to 20 GB per hour, 40 GB in all.
            FeedItem Row(string title, long size, int seeders) => new(title, title, new Uri(torznab.Address, "/dl/none").ToString(), size, seeders, Id());
            var fourKWeb = Row("Preset.Movie.2025.2160p.WEB-DL.DDP5.1.HDR.H.265-GRP", 20_000_000_000, 15);
            var fourKOversized = Row("Preset.Movie.2025.2160p.BluRay.x265.TrueHD.Atmos-HUGE", 70_000_000_000, 90);
            var fourKRemux = Row("Preset.Movie.2025.2160p.BluRay.REMUX.HEVC.TrueHD-DISC", 80_000_000_000, 60);
            var twoK = Row("Preset.Movie.2025.1440p.WEB-DL.DDP5.1.H.264-GRP", 9_000_000_000, 8);
            var fullHd = Row("Preset.Movie.2025.1080p.BluRay.x264-GRP", 12_000_000_000, 200);
            var hd = Row("Preset.Movie.2025.720p.WEB-DL.DDP5.1.H.264-GRP", 4_000_000_000, 500);
            var cam = Row("Preset.Movie.2025.HDCAM.x264-BAD", 1_000_000_000, 900);

            async Task<(string Top, JsonElement[] Rows)> SearchAsync(string step, params FeedItem[] rows)
            {
                feed.MovieItems.Clear();
                feed.MovieItems.AddRange(rows);
                var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, step);
                Assert(search.GetProperty("profile").GetProperty("id").AsGuid() == FourK && search.GetProperty("profile").GetProperty("inherited").GetBoolean(),
                    step + ": the search uses the inherited default 4K profile");
                var candidates = search.GetProperty("candidates").EnumerateArray().ToArray();
                Assert(candidates.Length == rows.Length && candidates[0].GetProperty("eligible").GetBoolean(), step + ": the first row is eligible");
                return (candidates[0].GetProperty("rawTitle").GetString()!, candidates);
            }

            static void Rejected(JsonElement[] rows, FeedItem item, string code) => Assert(rows.Any(row =>
                    row.GetProperty("rawTitle").GetString() == item.Title && !row.GetProperty("eligible").GetBoolean() &&
                    row.GetProperty("rejections").EnumerateArray().Any(rejection => rejection.GetProperty("code").GetString() == code)),
                $"{item.Title} is rejected as {code}");

            var (top, all) = await SearchAsync("Every tier on offer", hd, fullHd, twoK, fourKOversized, fourKRemux, cam, fourKWeb);
            Assert(top == fourKWeb.Title, "With a 4K copy on offer, the 4K copy ranks first even against better-seeded lower tiers: " + top);
            Rejected(all, fourKOversized, "size_above_maximum");
            Rejected(all, fourKRemux, "size_above_maximum");
            Rejected(all, cam, "quality_forbidden");
            var parsedTwoK = all.Single(row => row.GetProperty("rawTitle").GetString() == twoK.Title).GetProperty("parsed");
            Assert(parsedTwoK.GetProperty("resolution").GetString() == "1440p" && parsedTwoK.GetProperty("quality").GetString() == "webdl-1440p",
                "A 1440p release title parses to the 1440p tier: " + parsedTwoK.GetRawText());
            var eligible = all.Where(row => row.GetProperty("eligible").GetBoolean()).Select(row => row.GetProperty("rawTitle").GetString()).ToArray();
            Assert(eligible.SequenceEqual([fourKWeb.Title, twoK.Title, fullHd.Title, hd.Title]),
                "Eligible rows run best resolution first: 2160p, 1440p, 1080p, 720p: " + string.Join(", ", eligible));

            (top, _) = await SearchAsync("No acceptable 4K copy", hd, fullHd, fourKOversized, fourKRemux);
            Assert(top == fullHd.Title, "With no acceptable 4K copy (one too big, one a remux), 1080p is chosen: " + top);
            (top, _) = await SearchAsync("2K and lower", hd, fullHd, twoK);
            Assert(top == twoK.Title, "With no 4K copy, the 2K copy is chosen before 1080p: " + top);
            (top, _) = await SearchAsync("Only 720p", hd, fourKOversized);
            Assert(top == hd.Title, "With only 720p acceptable, 720p is chosen: " + top);
        }

        // A restart neither adds nor resets anything.
        await using (var restarted = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = restarted.Client(world.Admin, true);
            var profiles = await ListAsync(admin);
            Assert(profiles.Count == QualityPresets.All.Count && Name(profiles[SevenTwenty]) == "My 720p" && profiles[FourK].GetProperty("isDefault").GetBoolean(),
                "A restart keeps the profiles exactly as they were");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>An install that already has profiles: nothing is added at start; the explicit action adds only what is missing.</summary>
    private static async Task ExistingInstallAsync(World world, string dbPath, CapturingLoggerProvider logs)
    {
        var mine = Guid.NewGuid();
        var named720 = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Database.MigrateAsync();
            database.AcquisitionQualityProfiles.Add(new AcquisitionQualityProfile
            {
                Id = mine, Name = "Mine", QualitiesJson = "[\"webdl-1080p\",\"webrip-1080p\"]", MaximumBytesPerHour = 9_000_000_000, Revision = 4
            });
            database.AcquisitionQualityProfiles.Add(new AcquisitionQualityProfile { Id = named720, Name = "720p", QualitiesJson = "[\"webdl-720p\"]", Revision = 2 });
            database.AcquisitionSettings.Add(new AcquisitionSettings { DefaultQualityProfileId = mine, Revision = 5 });
            await database.SaveChangesAsync();
        }

        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            var profiles = await ListAsync(admin);
            Assert(profiles.Count == 2 && Name(profiles[mine]) == "Mine" && profiles[mine].GetProperty("revision").GetInt32() == 4 &&
                profiles[mine].GetProperty("isDefault").GetBoolean() && Name(profiles[named720]) == "720p",
                "An install with profiles gets no presets at start, and its profiles and default are untouched");
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings read");
            Assert(acquisition.GetProperty("revision").GetInt32() == 5, "The acquisition settings are not rewritten at start");
            var presets = (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles/Presets"), 200, "Presets read")).EnumerateArray();
            Assert(presets.All(preset => !preset.GetProperty("added").GetBoolean()), "No preset is a profile yet");

            var added = await ReadAsync(await admin.PostAsync("/JellyfinMod/Settings/QualityProfiles/Presets", null), 200, "Add Preset Profiles");
            Assert(added.GetProperty("added").GetArrayLength() == QualityPresets.All.Count - 1 &&
                added.GetProperty("skipped").EnumerateArray().Single().GetProperty("reason").GetString() == "name_in_use" &&
                added.GetProperty("skipped").EnumerateArray().Single().GetProperty("id").AsGuid() == SevenTwenty &&
                added.GetProperty("defaultQualityProfileId").ValueKind == JsonValueKind.Null,
                "Every missing preset is added except the one whose name a profile already has: " + added.GetRawText());
            profiles = await ListAsync(admin);
            Assert(profiles.Count == 2 + QualityPresets.All.Count - 1 && profiles[mine].GetProperty("isDefault").GetBoolean() &&
                profiles[mine].GetProperty("revision").GetInt32() == 4 && profiles[named720].GetProperty("revision").GetInt32() == 2 &&
                Strings(profiles[mine].GetProperty("qualities")).SequenceEqual(["webdl-1080p", "webrip-1080p"]) &&
                profiles[mine].GetProperty("maximumBytesPerHour").GetInt64() == 9_000_000_000,
                "The existing profiles and the default are unchanged after Add Preset Profiles");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task<Dictionary<Guid, JsonElement>> ListAsync(HttpClient admin) =>
        (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles"), 200, "Profiles read")).EnumerateArray()
        .ToDictionary(profile => profile.GetProperty("id").AsGuid());

    private static string Name(JsonElement value) => value.GetProperty("name").GetString()!;

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> request, int status, string type, string message)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status && body.Contains($"\"{type}\"", StringComparison.Ordinal),
            $"{message}: expected {status} {type}, got {(int)response.StatusCode} {body}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
