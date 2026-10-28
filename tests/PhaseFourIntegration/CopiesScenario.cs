using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Phase 12 M30 and M31: the quality profile for phones, a profile's Prefer, the TVs catalogue and the add-version search that
/// starts on the profile for phones while a title holds no file at 1080p or below. Real Kestrel host, HTTP, auth, serializer,
/// migrations (a fresh database and two existing ones) and SQLite, with a Torznab boundary over real HTTP.
/// </summary>
internal static class CopiesScenario
{
    private static readonly Guid FourK = Guid.Parse("8fc450fa-ec3d-4d42-962e-a7835ed239e7");
    private static readonly Guid FullHd = Guid.Parse("49375e78-8599-44fa-9d7e-59e12a8b87da");
    private static readonly Guid SevenTwenty = Guid.Parse("698e6ea9-0b64-4c1f-824a-2dad08e315e8");

    public static async Task RunAsync(string folder, CapturingLoggerProvider logs)
    {
        var media = Path.Combine(folder, "copies-media");
        foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
        var world = new World
        {
            Admin = new User("cadmin", "auth", "reset") { Id = Guid.NewGuid() },
            SecondAdmin = new User("cadmin2", "auth", "reset") { Id = Guid.NewGuid() },
            RestrictedAdmin = new User("ctv", "auth", "reset") { Id = Guid.NewGuid() },
            Ordinary = new User("cviewer", "auth", "reset") { Id = Guid.NewGuid() },
            Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
            Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
            Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
            Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
            Folder = folder
        };

        await FreshInstallAsync(world, Path.Combine(folder, "copies-fresh.db"), logs);
        await ExistingInstallAsync(world, Path.Combine(folder, "copies-existing.db"), logs, withFullHdPreset: true);
        await ExistingInstallAsync(world, Path.Combine(folder, "copies-existing-custom.db"), logs, withFullHdPreset: false);
        Console.WriteLine("PASS: phase 12 copies: profile for phones, Prefer ranking, TVs catalogue, add-version search for phones, migrations");
    }

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

            // ---- The presets: 4K prefers the TVs, 1080p plays everywhere and is the quality profile for phones.
            var profiles = await ListAsync(admin);
            Assert(Prefer(profiles[FourK]) == "tvs" && Prefer(profiles[FullHd]) == "everywhere" &&
                profiles.Where(pair => pair.Key != FourK && pair.Key != FullHd).All(pair => Prefer(pair.Value) is null),
                "A fresh install's 4K profile prefers Best on My TVs, its 1080p Plays Everywhere, every other No Preference: " +
                string.Join(", ", profiles.Values.Select(profile => Name(profile) + "=" + Prefer(profile))));
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings");
            Assert(acquisition.GetProperty("phoneProfileId").AsGuid() == FullHd && acquisition.GetProperty("defaultQualityProfileId").AsGuid() == FourK &&
                profiles.Values.Count(profile => profile.GetProperty("isPhoneProfile").GetBoolean()) == 1 &&
                profiles[FullHd].GetProperty("isPhoneProfile").GetBoolean(),
                "A fresh install's quality profile for phones is the 1080p preset, and its row says so: " + acquisition.GetRawText());
            var presets = (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles/Presets"), 200, "Presets")).EnumerateArray().ToArray();
            var fullHdPreset = presets.Single(preset => preset.GetProperty("id").AsGuid() == FullHd);
            Assert(presets.Count(preset => preset.GetProperty("isPhonesPreset").GetBoolean()) == 1 && fullHdPreset.GetProperty("isPhonesPreset").GetBoolean() &&
                fullHdPreset.GetProperty("prefer").GetString() == "everywhere" &&
                fullHdPreset.GetProperty("description").GetString() == "Full HD, the sensible middle. Plays on phones too. Falls back to 720p." &&
                !presets.Any(preset => Name(preset).Contains("Mobile", StringComparison.OrdinalIgnoreCase)),
                "The 1080p preset is the one for phones, says so, and there is no Mobile preset: " + fullHdPreset.GetRawText());
            var health = await ReadAsync(await admin.GetAsync("/JellyfinMod/Health"), 200, "Health");
            Assert(health.GetProperty("Capabilities").EnumerateArray().Any(value => value.GetString() == "copies"), "The plugin advertises copies");

            // ---- The TVs catalogue: C1 and G2, none ticked; admin only; unknown ids and stale revisions refused.
            var tvs = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/TvModels"), 200, "TVs");
            var models = tvs.GetProperty("models").EnumerateArray().ToArray();
            Assert(models.Select(model => model.GetProperty("id").GetString()).SequenceEqual(["lg-c1", "lg-g2"]) &&
                models.All(model => model.GetProperty("dolbyVision").GetBoolean() && !model.GetProperty("hdr10Plus").GetBoolean() &&
                    model.GetProperty("ddPlusAtmos").GetBoolean() && !model.GetProperty("dts").GetBoolean() && !model.GetProperty("selected").GetBoolean()) &&
                models[0].GetProperty("name").GetString() == "LG C1" && models[0].GetProperty("year").GetInt32() == 2021 &&
                models[0].GetProperty("webOS").GetString() == "6" && models[1].GetProperty("webOS").GetString() == "22" &&
                tvs.GetProperty("selected").GetArrayLength() == 0 && tvs.GetProperty("revision").GetInt32() == 1,
                "The catalogue lists the C1 and the G2 with their capabilities, none ticked: " + tvs.GetRawText());
            Assert((await anonymous.GetAsync("/JellyfinMod/Settings/TvModels")).StatusCode == HttpStatusCode.Unauthorized &&
                (await ordinary.GetAsync("/JellyfinMod/Settings/TvModels")).StatusCode == HttpStatusCode.Forbidden &&
                (await ordinary.PatchAsJsonAsync("/JellyfinMod/Settings/TvModels", new { selected = new[] { "lg-c1" }, revision = 1 })).StatusCode ==
                    HttpStatusCode.Forbidden, "Only administrators read or tick TVs");
            await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/TvModels", new { selected = new[] { "lg-c9" }, revision = 1 }), 400,
                "invalid_tv_model", "An unknown TV model is refused");
            await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/TvModels", new { selected = new[] { "lg-c1" }, revision = 7 }), 409,
                "revision_conflict", "A stale TVs revision is refused");

            // ---- Ranking. A two-hour film on one indexer; every release is seeded alike so only quality and Prefer decide.
            await using (var database = new ModDbContext(dbPath))
            {
                var metadata = JsonSerializer.Serialize(new TmdbMetadata("movie", 1200, "Copy Movie", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, "tt0001200", null, false, null, 120, [], [], []));
                var added = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Entries (Id, MediaType, TmdbId, ImdbId, Title, Year, MetadataJson, State, Monitored, AddedAt, TargetLibraryId, RetentionPolicy)
                    VALUES ({movieId}, 'movie', 1200, 'tt0001200', 'Copy Movie', 2025, {metadata}, 0, 1, {added}, {world.Movies.Id}, 0)
                    """);
            }

            var feed = new IndexerScript { ApiKey = "copies-key", Caps = TorznabBoundary.Caps("q,imdbid", "q") };
            torznab.Indexers["copies"] = feed;
            var indexer = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/Indexers", new
            {
                name = "Copies", baseUrl = new Uri(torznab.Address, "/copies/api").ToString(), enabled = true, categories = new[] { 2000 },
                priority = 1, minIntervalSeconds = 0, apiKey = new { action = "replace", value = feed.ApiKey }
            }), 201, "The copies indexer is created");
            Assert((await ReadAsync(await admin.PostAsync($"/JellyfinMod/Settings/Indexers/{indexer.GetProperty("id").AsGuid()}/Test", null), 200,
                "The copies indexer is verified")).GetProperty("ok").GetBoolean(), "The copies indexer answers");

            FeedItem Row(string title, long size = 20_000_000_000) =>
                new(title, title, new Uri(torznab.Address, "/dl/none").ToString(), size, 20, new() { ["imdbid"] = "0001200" });
            var remux = Row("Copy.Movie.2025.2160p.BluRay.REMUX.HEVC.TrueHD.7.1-DISC", 60_000_000_000);
            var dvDdp = Row("Copy.Movie.2025.2160p.WEB-DL.DV.DDP5.1.Atmos.H.265-AAA");
            var dvDts = Row("Copy.Movie.2025.2160p.WEB-DL.DV.DTS-HD.MA.5.1.H.265-BBB");
            var hdrPlus = Row("Copy.Movie.2025.2160p.WEB-DL.HDR10+.DDP5.1.H.265-CCC");
            var hdr10 = Row("Copy.Movie.2025.2160p.WEB-DL.HDR10.DDP5.1.H.265-DDD");
            var sdr = Row("Copy.Movie.2025.2160p.WEB-DL.DDP5.1.H.265-EEE");
            var avc = Row("Copy.Movie.2025.1080p.WEB-DL.DD5.1.H.264-FFF", 5_000_000_000);
            var hevcHdr = Row("Copy.Movie.2025.1080p.WEB-DL.HDR10.DTS.5.1.H.265-GGG", 5_000_000_000);
            var hevc = Row("Copy.Movie.2025.1080p.WEB-DL.AAC2.0.H.265-HHH", 5_000_000_000);

            async Task<JsonElement[]> SearchAsync(string step, Guid profile, params FeedItem[] rows)
            {
                feed.MovieItems.Clear();
                feed.MovieItems.AddRange(rows);
                var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&profileId={profile}"), 200, step);
                return search.GetProperty("candidates").EnumerateArray().Where(row => row.GetProperty("eligible").GetBoolean()).ToArray();
            }

            static string[] Titles(JsonElement[] rows) => rows.Select(row => row.GetProperty("rawTitle").GetString()!).ToArray();
            static int Points(JsonElement row, string code) => row.GetProperty("contributions").EnumerateArray()
                .Where(item => item.GetProperty("code").GetString() == code).Select(item => item.GetProperty("points").GetInt32()).SingleOrDefault();

            // No Preference: today's order and contributions, nothing from Prefer.
            var editable = (await ListAsync(admin))[FourK];
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}", ProfileBody(editable, null)), 200,
                "The 4K profile set to No Preference");
            var plain = await SearchAsync("No Preference", FourK, sdr, dvDts, hdr10, dvDdp, hdrPlus, remux);
            Assert(Titles(plain)[0] == remux.Title && plain.All(row => Points(row, "prefer_picture") == 0 && Points(row, "prefer_sound") == 0) &&
                plain.Skip(1).Select(row => row.GetProperty("score").GetInt32()).Distinct().Count() == 1,
                "No Preference ranks as before: the remux first, the 4K WEB-DLs tied and no Prefer points: " + string.Join(", ", Titles(plain)));

            // Best on My TVs with no TV ticked: Dolby Vision, HDR10+, HDR10, SDR; within a picture, lossless and Atmos first.
            editable = (await ListAsync(admin))[FourK];
            var tvsProfile = await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}", ProfileBody(editable, "tvs")), 200,
                "The 4K profile set to Best on My TVs");
            Assert(Prefer(tvsProfile) == "tvs", "The profile answers its Prefer");
            var general = Titles(await SearchAsync("Best on My TVs, none ticked", FourK, sdr, hdr10, hdrPlus, dvDdp, dvDts, remux));
            Assert(general.SequenceEqual([remux.Title, dvDts.Title, dvDdp.Title, hdrPlus.Title, hdr10.Title, sdr.Title]),
                "With no TV ticked, the remux stays first, then Dolby Vision (DTS-HD MA above DD+ Atmos), HDR10+, HDR10, SDR: " + string.Join(", ", general));

            // Tick the C1 and the G2: HDR10+ plays as HDR10 and DTS cannot be decoded, so DD+ wins at an equal picture.
            var ticked = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/TvModels", new { selected = new[] { "lg-g2", "lg-c1" }, revision = 1 }),
                200, "Tick the C1 and the G2");
            Assert(Strings(ticked.GetProperty("selected")).SequenceEqual(["lg-c1", "lg-g2"]) && ticked.GetProperty("revision").GetInt32() == 2,
                "The ticks are stored in catalogue order with a new revision: " + ticked.GetRawText());
            var mine = await SearchAsync("Best on My TVs, C1 and G2", FourK, sdr, hdr10, hdrPlus, dvDdp, dvDts, remux);
            var mineTitles = Titles(mine);
            Assert(mineTitles[0] == remux.Title && mineTitles[1] == dvDdp.Title && mineTitles[2] == dvDts.Title && mineTitles.Last() == sdr.Title &&
                Points(mine[3], "prefer_picture") == Points(mine[4], "prefer_picture") &&
                mineTitles.ToList().IndexOf(hdrPlus.Title) > mineTitles.ToList().IndexOf(dvDts.Title),
                "For the C1 and the G2: the remux first (a better quality step), Dolby Vision with DD+ above Dolby Vision with DTS, HDR10+ no better than " +
                "HDR10, SDR last: " + string.Join(", ", mineTitles));
            Assert(mine.All(row => Points(row, "prefer_picture") + Points(row, "prefer_sound") < 40) &&
                mine.Skip(1).All(row => row.GetProperty("score").GetInt32() < mine[0].GetProperty("score").GetInt32()),
                "Prefer stays under one quality step: " + string.Join(", ", mine.Select(row => row.GetProperty("score").GetInt32())));

            // Plays Everywhere on the 1080p preset: H.264 with Dolby Digital above HEVC with HDR10 and DTS.
            var everywhere = Titles(await SearchAsync("Plays Everywhere", FullHd, hevcHdr, hevc, avc));
            Assert(everywhere.SequenceEqual([avc.Title, hevc.Title, hevcHdr.Title]),
                "Plays Everywhere: H.264 and DD first, HEVC next, HDR10 with DTS last: " + string.Join(", ", everywhere));

            // ---- Prefer is validated, survives a save that leaves it out (the Dashboard page), and is admin-only.
            editable = (await ListAsync(admin))[FourK];
            await ExpectAsync(admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}", ProfileBody(editable, "sparkly")), 400,
                "invalid_prefer", "An unknown Prefer is refused");
            Assert((await ordinary.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}", ProfileBody(editable, null))).StatusCode ==
                HttpStatusCode.Forbidden, "Ordinary users cannot change a profile's Prefer");
            await ReadAsync(await admin.PatchAsJsonAsync($"/JellyfinMod/Settings/QualityProfiles/{FourK}", new
            {
                name = Name(editable), qualities = Strings(editable.GetProperty("qualities")), upgradeMode = "replace",
                cutoff = editable.GetProperty("cutoff").GetString(), minimumSeeders = 2, revision = editable.GetProperty("revision").GetInt32()
            }), 200, "A save without Prefer, as the Dashboard page sends it");
            Assert(Prefer((await ListAsync(admin))[FourK]) == "tvs", "A save that leaves Prefer out keeps it");

            // ---- The quality profile for phones: a save that leaves it out keeps it; an unknown one is refused; it cannot be deleted.
            var settings = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings");
            var kept = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = FourK, revision = settings.GetProperty("revision").GetInt32()
            }), 200, "A Grabbing save without the profile for phones");
            Assert(kept.GetProperty("phoneProfileId").AsGuid() == FullHd, "A Grabbing save that leaves the profile for phones out keeps it");
            await ExpectAsync(admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = FourK, phoneProfileId = Guid.NewGuid(),
                revision = kept.GetProperty("revision").GetInt32()
            }), 400, "invalid_phone_profile", "An unknown profile for phones is refused");
            var moved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = FourK, phoneProfileId = SevenTwenty,
                revision = kept.GetProperty("revision").GetInt32()
            }), 200, "The 720p profile chosen for phones");
            Assert(moved.GetProperty("phoneProfileId").AsGuid() == SevenTwenty, "The profile for phones can be changed");
            await ExpectAsync(admin.DeleteAsync($"/JellyfinMod/Settings/QualityProfiles/{SevenTwenty}"), 409, "profile_is_phone_profile",
                "The profile for phones cannot be deleted while it is chosen");
            moved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Acquisition", new
            {
                enabled = false, downloadClientId = (Guid?)null, defaultQualityProfileId = FourK, phoneProfileId = FullHd,
                revision = moved.GetProperty("revision").GetInt32()
            }), 200, "The 1080p profile chosen for phones again");

            // ---- Get Another Quality on a movie that holds only a 4K file starts on the profile for phones and offers Add only.
            var uhdPath = Path.Combine(world.Movies.Location!, "Copy Movie (2025)", "Copy Movie (2025) - 2160p WEB-DL.mkv");
            await using (var database = new ModDbContext(dbPath))
            {
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE Entries SET State = 4 WHERE Id = {movieId}");
                var item = Guid.NewGuid();
                database.EntryBindings.Add(new EntryBinding
                {
                    EntryId = movieId, JellyfinItemId = item, VersionGroupId = item, TargetLibraryId = world.Movies.Id, MediaPath = uhdPath
                });
                await database.SaveChangesAsync();
            }

            feed.MovieItems.Clear();
            feed.MovieItems.AddRange([avc, hevcHdr, sdr]);
            var forPhones = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&intent=addVersion"), 200,
                "Get Another Quality on a 4K-only movie");
            Assert(forPhones.GetProperty("profile").GetProperty("id").AsGuid() == FullHd && !forPhones.GetProperty("profile").GetProperty("inherited").GetBoolean() &&
                forPhones.GetProperty("grab").GetProperty("forPhones").GetBoolean() &&
                Strings(forPhones.GetProperty("grab").GetProperty("modes")).SequenceEqual(["add"]) &&
                forPhones.GetProperty("candidates")[0].GetProperty("rawTitle").GetString() == avc.Title,
                "A movie with only a 4K file opens Get Another Quality on the profile for phones, Add only, Plays Everywhere first: " +
                forPhones.GetProperty("profile").GetRawText() + forPhones.GetProperty("grab").GetRawText());
            var chosen = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&intent=addVersion&profileId={FourK}"), 200,
                "Get Another Quality on the 4K profile by choice");
            Assert(chosen.GetProperty("profile").GetProperty("id").AsGuid() == FourK && !chosen.GetProperty("grab").GetProperty("forPhones").GetBoolean() &&
                Strings(chosen.GetProperty("grab").GetProperty("modes")).Contains("add"),
                "Any other profile is one choice away and searches as before: " + chosen.GetProperty("grab").GetRawText());
            var acquire = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}"), 200, "A plain search of the same movie");
            Assert(acquire.GetProperty("profile").GetProperty("id").AsGuid() == FourK && !acquire.GetProperty("grab").GetProperty("forPhones").GetBoolean(),
                "A search that is not for another quality keeps the default profile");

            // Once the movie also holds a 1080p file, the phone copy is no longer missing.
            await using (var database = new ModDbContext(dbPath))
            {
                var item = Guid.NewGuid();
                database.EntryBindings.Add(new EntryBinding
                {
                    EntryId = movieId, JellyfinItemId = item, VersionGroupId = item, TargetLibraryId = world.Movies.Id,
                    MediaPath = Path.Combine(Path.GetDirectoryName(uhdPath)!, "Copy Movie (2025) - 1080p WEB-DL.mkv")
                });
                await database.SaveChangesAsync();
            }

            var held = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={movieId}&intent=addVersion"), 200,
                "Get Another Quality on a movie with a 4K and a 1080p");
            Assert(held.GetProperty("profile").GetProperty("id").AsGuid() == FourK && held.GetProperty("profile").GetProperty("inherited").GetBoolean() &&
                !held.GetProperty("grab").GetProperty("forPhones").GetBoolean(),
                "With a 1080p file held, Get Another Quality starts on the title's profile again: " + held.GetProperty("profile").GetRawText());
        }

        // ---- Series: an episode whose files are all 4K, and season packs. A season whose every episode holds only 4K opens on the
        // profile for phones (Add only); a season with an episode that has no file keeps the title's profile (D85).
        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            var seriesId = Guid.NewGuid();
            var episodes = new Dictionary<string, Guid>();
            await using (var database = new ModDbContext(dbPath))
            {
                database.Entries.Add(new Entry
                {
                    Id = seriesId, MediaType = "series", TmdbId = 1250, Title = "Copy Show", Year = 2022, TargetLibraryId = world.Tv.Id, Monitored = true,
                    State = FileState.OnDisk,
                    MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 1250, "Copy Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        null, null, null, null, 7250, false, null, 45, [], [], [new TmdbSeason(1, "Season 1", 2, null, null), new TmdbSeason(2, "Season 2", 2, null, null)]))
                });
                var seriesItem = Guid.NewGuid();
                foreach (var (key, season, number, held) in new[] { ("s1e1", 1, 1, true), ("s1e2", 1, 2, false), ("s2e1", 2, 1, true), ("s2e2", 2, 2, true) })
                {
                    var episode = new Episode
                    {
                        EntryId = seriesId, TmdbId = 12500 + season * 10 + number, SeasonNumber = season, EpisodeNumber = number, Title = key,
                        AirDate = DateTime.UtcNow.AddDays(-90 + season * 10 + number), RuntimeMinutes = 45, Monitored = true,
                        State = held ? FileState.OnDisk : FileState.None
                    };
                    database.Episodes.Add(episode);
                    episodes[key] = episode.Id;
                    if (!held) continue;
                    var item = Guid.NewGuid();
                    database.EpisodeBindings.Add(new EpisodeBinding
                    {
                        EpisodeId = episode.Id, JellyfinItemId = item, SeriesItemId = seriesItem, TargetLibraryId = world.Tv.Id,
                        MediaPath = Path.Combine(world.Tv.Location!, "Copy Show (2022)", $"Season 0{season}", $"Copy Show (2022) S0{season}E0{number} - 2160p WEB-DL.mkv")
                    });
                }

                await database.SaveChangesAsync();
            }

            var episodeSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&episodeId={episodes["s1e1"]}&intent=addVersion"),
                200, "Get Another Quality on a 4K-only episode");
            Assert(episodeSearch.GetProperty("profile").GetProperty("id").AsGuid() == FullHd && episodeSearch.GetProperty("grab").GetProperty("forPhones").GetBoolean() &&
                Strings(episodeSearch.GetProperty("grab").GetProperty("modes")).SequenceEqual(["add"]),
                "An episode with only a 4K file opens on the profile for phones, Add only: " + episodeSearch.GetProperty("grab").GetRawText());
            // Episode detail's phoneCopyMissing needs Jellyfin's items for the files (this host has none); the charlie browser run reads it.
            var allHeld = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&scope=season&seasonNumber=2"), 200,
                "A season pack search over 4K-only episodes");
            Assert(allHeld.GetProperty("profile").GetProperty("id").AsGuid() == FullHd && allHeld.GetProperty("grab").GetProperty("forPhones").GetBoolean() &&
                Strings(allHeld.GetProperty("grab").GetProperty("modes")).SequenceEqual(["add"]),
                "A season whose every episode holds only 4K opens on the profile for phones, Add only: " + allHeld.GetProperty("grab").GetRawText());
            var mixed = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&scope=season&seasonNumber=1"), 200,
                "A season pack search with an episode that has no file");
            Assert(mixed.GetProperty("profile").GetProperty("id").AsGuid() == FourK && mixed.GetProperty("profile").GetProperty("inherited").GetBoolean() &&
                !mixed.GetProperty("grab").GetProperty("forPhones").GetBoolean() && Strings(mixed.GetProperty("grab").GetProperty("modes")).Contains("fill"),
                "A season with an episode that has no file keeps the title's profile and the pack's modes: " + mixed.GetProperty("grab").GetRawText());
            var series = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={seriesId}&scope=series"), 200, "An All Seasons search");
            Assert(series.GetProperty("profile").GetProperty("id").AsGuid() == FourK, "All Seasons, which covers the episode without a file, keeps the title's profile");
        }

        // ---- A restart keeps the Prefer values, the ticks and the profile for phones.
        await using (var restarted = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = restarted.Client(world.Admin, true);
            var profiles = await ListAsync(admin);
            var tvs = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/TvModels"), 200, "TVs after a restart");
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition after a restart");
            Assert(Prefer(profiles[FourK]) == "tvs" && Prefer(profiles[FullHd]) == "everywhere" &&
                Strings(tvs.GetProperty("selected")).SequenceEqual(["lg-c1", "lg-g2"]) && acquisition.GetProperty("phoneProfileId").AsGuid() == FullHd,
                "A restart keeps Prefer, the ticked TVs and the profile for phones");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A database from before Phase 12, migrated: every stored profile keeps No Preference, a profile named Mobile is left as it
    /// is, and the profile for phones is the stored copy of the 1080p preset when there is one, else none.
    /// </summary>
    private static async Task ExistingInstallAsync(World world, string dbPath, CapturingLoggerProvider logs, bool withFullHdPreset)
    {
        var mobile = Guid.NewGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            await database.GetService<IMigrator>().MigrateAsync("20260919090423_PhaseFiveImport");
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AcquisitionSettings (Id, Enabled, Revision, ImportEnabled, SeedReleaseEnabled, SeedFloorRatio, SeedFloorHours,
                    ImportPollSeconds, VideoExtensions, StalledAfterHours, ScanTimeoutMinutes, QueueVisibleToUsers, ImportRevision)
                VALUES ({AcquisitionSettings.SingletonId}, 0, 4, 1, 0, 1.0, 168, 15, 'mkv', 24, 10, 0, 1)
                """);
            await database.GetService<IMigrator>().MigrateAsync("20261028083409_GrabReplaceTargets");
            if (withFullHdPreset)
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AcquisitionQualityProfiles (Id, Name, QualitiesJson, MaximumBytesPerHour, Revision, UpgradeAllowed, UpgradeMode)
                    VALUES ({FullHd}, '1080p', '["webdl-1080p","bluray-1080p"]', 6000000000, 3, 0, 'replace')
                    """);
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AcquisitionQualityProfiles (Id, Name, QualitiesJson, MaximumBytesPerHour, Revision, UpgradeAllowed, UpgradeMode)
                VALUES ({mobile}, 'Mobile', '["webdl-1080p","webdl-720p"]', 3000000000, 2, 0, 'replace')
                """);
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AcquisitionSettings SET DefaultQualityProfileId = {mobile}");
            await database.Database.MigrateAsync();
            var migrated = await database.AcquisitionSettings.AsNoTracking().SingleAsync();
            Assert(migrated.PhoneProfileId == (withFullHdPreset ? FullHd : null) && migrated.TvModels == string.Empty && migrated.TvModelsRevision == 1 &&
                !migrated.FetchPhoneCopies && migrated.Revision == 4,
                $"The migration picks the stored 1080p preset as the profile for phones ({withFullHdPreset}: {migrated.PhoneProfileId}), ticks no TV and " +
                "leaves fetching copies for phones off");
            Assert(await database.AcquisitionQualityProfiles.AsNoTracking().AllAsync(profile => profile.Prefer == null),
                "Every stored profile keeps No Preference");
        }

        await using (var host = await PluginHost.StartAsync(world, dbPath, new ShiftedTimeProvider(), logs, TimeSpan.FromSeconds(2), new PluginConfiguration()))
        {
            using var admin = host.Client(world.Admin, true);
            var profiles = await ListAsync(admin);
            Assert(profiles.Count == (withFullHdPreset ? 2 : 1) && Name(profiles[mobile]) == "Mobile" && profiles[mobile].GetProperty("revision").GetInt32() == 2 &&
                profiles[mobile].GetProperty("isDefault").GetBoolean() && profiles.Values.All(profile => Prefer(profile) is null) &&
                (!withFullHdPreset || profiles[FullHd].GetProperty("revision").GetInt32() == 3),
                "An existing install's profiles, a Mobile one included, are not changed and get no presets at start");
            var acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings");
            Assert(withFullHdPreset ? acquisition.GetProperty("phoneProfileId").AsGuid() == FullHd
                    : acquisition.GetProperty("phoneProfileId").ValueKind == JsonValueKind.Null,
                "The API answers the migrated profile for phones: " + acquisition.GetRawText());
            if (!withFullHdPreset)
            {
                // Add Preset Profiles brings the 1080p preset, and with no profile for phones chosen it becomes that profile.
                await ReadAsync(await admin.PostAsync("/JellyfinMod/Settings/QualityProfiles/Presets", null), 200, "Add Preset Profiles");
                acquisition = await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/Acquisition"), 200, "Acquisition settings after presets");
                profiles = await ListAsync(admin);
                Assert(acquisition.GetProperty("phoneProfileId").AsGuid() == FullHd && Prefer(profiles[FullHd]) == "everywhere" &&
                    acquisition.GetProperty("defaultQualityProfileId").AsGuid() == mobile,
                    "Add Preset Profiles makes the added 1080p preset the profile for phones and leaves the default alone: " + acquisition.GetRawText());
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static object ProfileBody(JsonElement profile, string? prefer) => new
    {
        name = Name(profile), qualities = Strings(profile.GetProperty("qualities")), upgradeMode = "replace",
        cutoff = profile.GetProperty("cutoff").GetString(), minimumSeeders = 2, prefer, revision = profile.GetProperty("revision").GetInt32()
    };

    private static async Task<Dictionary<Guid, JsonElement>> ListAsync(HttpClient admin) =>
        (await ReadAsync(await admin.GetAsync("/JellyfinMod/Settings/QualityProfiles"), 200, "Profiles read")).EnumerateArray()
        .ToDictionary(profile => profile.GetProperty("id").AsGuid());

    private static string Name(JsonElement value) => value.GetProperty("name").GetString()!;

    private static string? Prefer(JsonElement value) => value.GetProperty("prefer").GetString();

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
