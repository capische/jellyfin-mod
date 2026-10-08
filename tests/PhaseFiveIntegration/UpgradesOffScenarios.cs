using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Naming.Common;
using Emby.Naming.Video;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The user's three answers after the first live pack run on 48096 (2026-10-09), through the same real host, HTTP, SQLite,
/// Torznab and Transmission boundaries and real files as the rest of Phase 5: with episode upgrades off a pack search offers
/// fill and add but never replace, and an Add pack imports beside the held files as further versions; a new version names its
/// resolution in its file name only when it is higher than every version held, so Jellyfin 12's own version grouping (its
/// <c>VideoListResolver</c>, run here over the season folder) keeps the higher resolution as the episode's main version.
/// </summary>
internal static class UpgradesOffScenarios
{
    private const long Size = 300_000;

    public static async Task RunAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 270, Title = "Off Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 270, "Off Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 370, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", 3, null, null)]))
            };
            database.Entries.Add(entry);
            ids["off"] = entry.Id;
            for (var number = 1; number <= 3; number++)
            {
                var row = new Episode
                {
                    EntryId = entry.Id, TmdbId = 27_000 + number, SeasonNumber = 1, EpisodeNumber = number, Title = "Off Show 1x" + number,
                    RuntimeMinutes = 45, AirDate = new DateTime(2022, 4, number, 0, 0, 0, DateTimeKind.Utc)
                };
                database.Episodes.Add(row);
                ids["offs1e" + number] = row.Id;
            }

            await database.SaveChangesAsync();
        }

        // S01E01 is held at 2160p and S01E02 at 720p, by the size Jellyfin probed: their names, as a person named them, say
        // nothing of their resolution, and no import of the plugin brought them. S01E01 is a scope-ratio 3840x1600 picture,
        // 2160p by its width although its height alone is a 1080p one.
        var folder = Path.Combine(world.Tv.Location, "Off Show (2022) [tmdbid-270]", "Season 01");
        Directory.CreateDirectory(folder);
        var heldE1 = Path.Combine(folder, "Off Show S01E01.mkv");
        var heldE2 = Path.Combine(folder, "Off Show S01E02.mkv");
        await File.WriteAllBytesAsync(heldE1, RandomNumberGenerator.GetBytes(4096));
        await File.WriteAllBytesAsync(heldE2, RandomNumberGenerator.GetBytes(4096));
        // A macOS "._" sidecar beside the held file: Jellyfin's scan leaves it out, so the grouping check must too (Codex
        // re-review 5 of the user fixes, finding 1).
        await File.WriteAllBytesAsync(Path.Combine(folder, "._Off Show S01E01.mkv"), RandomNumberGenerator.GetBytes(512));
        world.Native.Scan(Path.GetDirectoryName(folder)!);
        var offE1Item = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Single(item => item.Path == heldE1);
        offE1Item.Width = 3840;
        offE1Item.Height = 1600;
        var offE2Item = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Single(item => item.Path == heldE2);
        offE2Item.Width = 1280;
        offE2Item.Height = 720;
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == ids["offs1e1"] || binding.EpisodeId == ids["offs1e2"]) == 2
                ? true : null;
        }, "Off Show's held S01E01 and S01E02 are bound");

        var pack = TorrentFixture.Multi("Off.Show.S01.1080p.WEB-DL-GRP", ("Off.Show.S01E01.1080p.WEB-DL-GRP.mkv", Size),
            ("Off.Show.S01E02.1080p.WEB-DL-GRP.mkv", Size), ("Off.Show.S01E03.1080p.WEB-DL-GRP.mkv", Size));
        torznab.Torrents["offpack"] = pack.Bytes;
        transmission.Register(pack);
        lock (torznab.MovieItems)
            torznab.TvItems.Add(new("Off.Show.S01.1080p.WEB-DL-GRP", "guid-offpack", torznab.Download("offpack"), Size * 3, 25,
                new() { ["tvdbid"] = "370" }));

        await SetEpisodeUpgradesAsync(admin, false);
        try
        {
            var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["off"]}&scope=season&seasonNumber=1"), 200,
                "Off Show season search with episode upgrades off");
            var modes = search.GetProperty("grab").GetProperty("modes").EnumerateArray().Select(value => value.GetString()).ToArray();
            var row = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == "Off.Show.S01.1080p.WEB-DL-GRP");
            Assert(modes.SequenceEqual(["fill", "add"]) && row.GetProperty("eligible").GetBoolean() &&
                row.GetProperty("coverage").GetProperty("held").GetInt32() == 2,
                "With episode upgrades off the pack search offers fill and add, never replace: " + search.GetProperty("grab").GetRawText());
            var replace = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = row.GetProperty("releaseId").GetString(),
                idempotencyKey = "p5-off-replace-" + Guid.NewGuid().ToString("N"), mode = "replace"
            });
            var replaceBody = await replace.Content.ReadAsStringAsync();
            Assert((int)replace.StatusCode == 409 && replaceBody.Contains("\"episode_replace_disabled\"", StringComparison.Ordinal),
                "Replace is refused while episode upgrades are off: " + replaceBody);
            // Enter on a row holding files adds: no mode is sent and the server takes add.
            var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = row.GetProperty("releaseId").GetString(),
                idempotencyKey = "p5-off-add-" + Guid.NewGuid().ToString("N")
            }), 202, "Administrator adds the Off Show pack while episode upgrades are off");
            var grabId = grab.GetProperty("id").AsGuid();
            Assert(grab.GetProperty("mode").GetString() == "add", "The pack is added: " + grab.GetRawText());
            await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{grabId}")).GetProperty("state").GetString() ==
                "accepted" ? true : null, "The Off Show pack is accepted");
            transmission.Progress(pack.InfoHash, 1.0);
            await WaitAsync(async () =>
            {
                await tick();
                await using var database = new ModDbContext(dbPath);
                var children = await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId).ToListAsync();
                return children.Count == 3 && children.All(child => !ImportStates.Open.Contains(child.State)) ? true : null;
            }, "Every import of the Off Show pack is final", 60);

            await using (var database = new ModDbContext(dbPath))
            {
                var children = (await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId).ToListAsync())
                    .ToDictionary(child => child.EpisodeId!.Value);
                var summary = string.Join(" | ", children.Values.Select(child => $"{child.State} {child.Reason} {child.Intent} {child.DestinationPath}"));
                var e1 = children[ids["offs1e1"]];
                var e2 = children[ids["offs1e2"]];
                var e3 = children[ids["offs1e3"]];
                Assert(children.Values.All(child => child.State == ImportStates.Completed) && File.Exists(heldE1) && File.Exists(heldE2) &&
                    await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == ids["offs1e1"]) == 2 &&
                    await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == ids["offs1e2"]) == 2,
                    "With episode upgrades off, Add imports the pack's files beside the held ones as further versions and fills S01E03: " + summary);
                Assert(e1.DestinationPath == Path.Combine(folder, "Off Show S01E01 - WEB-DL.mkv") && e1.VersionLabel == "1080p WEB-DL",
                    "A 1080p version beside a 2160p file names no resolution and is named after the main file it goes beside: " + summary);
                Assert(e2.DestinationPath == Path.Combine(folder, "Off Show (2022) S01E02 - 1080p WEB-DL.mkv") && e2.VersionLabel == "1080p WEB-DL",
                    "A 1080p version beside a 720p file names its resolution: " + summary);
                Assert(e3.DestinationPath == Path.Combine(folder, "Off Show (2022) S01E03.mkv") && e3.Intent == "acquire",
                    "The missing S01E03 is filled as before: " + summary);
            }

            // Jellyfin 12's own grouping of the season folder: the higher resolution stays each episode's main version.
            var options = new NamingOptions();
            var videos = Directory.EnumerateFiles(folder, "*.mkv").Where(path => !Path.GetFileName(path).StartsWith('.'))
                .Select(path => VideoResolver.Resolve(path, false, options, true, world.Tv.Location)).OfType<VideoFileInfo>().ToList();
            var groups = new VideoListResolver(options).Resolve(videos, true, true, world.Tv.Location, Jellyfin.Data.Enums.CollectionType.tvshows);
            string Main(string anyPath) => groups.Single(group => group.Files[0].Path == anyPath ||
                group.AlternateVersions.Any(version => version.Files[0].Path == anyPath)).Files[0].Path;
            var layout = string.Join(" | ", groups.Select(group => Path.GetFileName(group.Files[0].Path) + " <- " +
                string.Join(", ", group.AlternateVersions.Select(version => Path.GetFileName(version.Files[0].Path)))));
            Assert(Main(heldE1) == heldE1 && Main(heldE2) == Path.Combine(folder, "Off Show (2022) S01E02 - 1080p WEB-DL.mkv"),
                "Jellyfin keeps the 2160p S01E01 as its main version and makes the new 1080p S01E02 the main one over the 720p: " + layout);

            // The plugin's row still reads the new S01E01 version's resolution from its label.
            var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["off"]}"));
            var e1Versions = detail.GetProperty("episodes").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == ids["offs1e1"])
                .GetProperty("versions").EnumerateArray().ToArray();
            var added = e1Versions.SingleOrDefault(version => version.GetProperty("label").GetString() == "1080p WEB-DL");
            Assert(added.ValueKind == JsonValueKind.Object && added.GetProperty("resolution").GetString() == "1080p" &&
                !added.GetProperty("isDefault").GetBoolean(),
                "The added version's row reads 1080p from its label and is not the default: " + string.Join(" | ", e1Versions.Select(v => v.GetRawText())));

            await RunNamingAsync(tick, admin, world, dbPath, ids, torznab, transmission);
        }
        finally
        {
            await SetEpisodeUpgradesAsync(admin, true);
        }
    }

    /// <summary>
    /// The Codex review of the user fixes (findings 1–4), with episode upgrades still off, through single-episode Get Another
    /// Quality grabs: a main file named with an alias (<c>UHD</c>) is never copied into the lower version's name; a main file
    /// whose name leaves no room for a label is not cut short; 576p outranks a 480p file; a 4:3 1440x1080 file is 1080p, so a
    /// 1080p version does not outrank it; and a label speaks for a file only while that file is the one its import linked.
    /// </summary>
    private static async Task RunNamingAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 280, Title = "Alias Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 280, "Alias Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 380, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", 4, null, null)]))
            };
            database.Entries.Add(entry);
            ids["alias"] = entry.Id;
            for (var number = 1; number <= 4; number++)
            {
                var row = new Episode
                {
                    EntryId = entry.Id, TmdbId = 28_000 + number, SeasonNumber = 1, EpisodeNumber = number, Title = "Alias Show 1x" + number,
                    RuntimeMinutes = 45, AirDate = new DateTime(2022, 5, number, 0, 0, 0, DateTimeKind.Utc)
                };
                database.Episodes.Add(row);
                ids["aliass1e" + number] = row.Id;
            }

            await database.SaveChangesAsync();
        }

        // S01E01's name says UHD (2160p to the plugin, nothing to Jellyfin's ordering); S01E02's name leaves no room for a label
        // (a 255-byte name, sorting first by its year); S01E03 is a 480p file by the size Jellyfin probed; S01E04 is a 4:3
        // 1440x1080 file, 1080p by its height although its width alone is a 720p one.
        var folder = Path.Combine(world.Tv.Location, "Alias Show (2022) [tmdbid-280]", "Season 01");
        Directory.CreateDirectory(folder);
        var heldE1 = Path.Combine(folder, "Alias Show S01E01 UHD Remux.mkv");
        var longStem = "Alias Show (2021) S01E02 ";
        var heldE2 = Path.Combine(folder, longStem + new string('A', 255 - 4 - longStem.Length) + ".mkv");
        var heldE3 = Path.Combine(folder, "Alias Show S01E03.mkv");
        var heldE4 = Path.Combine(folder, "Alias Show S01E04.mkv");
        foreach (var held in new[] { heldE1, heldE2, heldE3, heldE4 }) await File.WriteAllBytesAsync(held, RandomNumberGenerator.GetBytes(4096));
        world.Native.Scan(Path.GetDirectoryName(folder)!);
        var e3Item = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Single(item => item.Path == heldE3);
        e3Item.Width = 720;
        e3Item.Height = 480;
        var e4Item = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Single(item => item.Path == heldE4);
        e4Item.Width = 1440;
        e4Item.Height = 1080;
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == ids["aliass1e1"] || binding.EpisodeId == ids["aliass1e2"] ||
                binding.EpisodeId == ids["aliass1e3"] || binding.EpisodeId == ids["aliass1e4"]) == 4 ? true : null;
        }, "Alias Show's four held files are bound");

        // The SD release needs a profile that lists it; it is chosen for that one search, as the picker's profile choice does.
        var sdProfile = (await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Settings/QualityProfiles", new
        {
            name = "SD versions", qualities = new[] { "webdl-576p", "webdl-480p" }
        }), 201, "Administrator creates an SD profile")).GetProperty("id").AsGuid();

        async Task<ImportOperation> AddVersionAsync(int number, string release, Guid? profileId = null)
        {
            var fixture = TorrentFixture.Single(release + ".mkv", Size);
            var key = "alias" + number;
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(release, "guid-" + key, torznab.Download(key), Size, 25, new() { ["tvdbid"] = "380" }));
            var search = await ReadAsync(await admin.GetAsync(
                $"/JellyfinMod/Releases?entryId={ids["alias"]}&episodeId={ids["aliass1e" + number]}&intent=addVersion" +
                (profileId is { } chosen ? "&profileId=" + chosen : string.Empty)), 200,
                $"Get Another Quality for Alias Show S01E0{number} with episode upgrades off");
            var row = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == release);
            Assert(row.GetProperty("eligible").GetBoolean(), "The release is eligible: " + row.GetRawText());
            var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = row.GetProperty("releaseId").GetString(),
                idempotencyKey = "p5-alias-" + Guid.NewGuid().ToString("N")
            }), 202, "Administrator adds another version of Alias Show S01E0" + number);
            var grabId = grab.GetProperty("id").AsGuid();
            await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{grabId}")).GetProperty("state").GetString() ==
                "accepted" ? true : null, "The Alias Show grab is accepted");
            transmission.Progress(fixture.InfoHash, 1.0);
            ImportOperation? done = null;
            await WaitAsync(async () =>
            {
                await tick();
                await using var database = new ModDbContext(dbPath);
                done = await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.GrabId == grabId);
                return done is not null && !ImportStates.Open.Contains(done.State) ? true : null;
            }, "The Alias Show import is final", 60);
            Assert(done!.State == ImportStates.Completed && done.Intent == "addVersion",
                $"Another version of Alias Show S01E0{number} is imported with episode upgrades off: {done.State} {done.Reason} {done.Error}");
            return done;
        }

        string Main(string anyPath)
        {
            var options = new NamingOptions();
            var videos = Directory.EnumerateFiles(folder, "*.mkv").Where(path => !Path.GetFileName(path).StartsWith('.'))
                .Select(path => VideoResolver.Resolve(path, false, options, true, world.Tv.Location)).OfType<VideoFileInfo>().ToList();
            var groups = new VideoListResolver(options).Resolve(videos, true, true, world.Tv.Location, Jellyfin.Data.Enums.CollectionType.tvshows);
            return groups.Single(group => group.Files[0].Path == anyPath ||
                group.AlternateVersions.Any(version => version.Files[0].Path == anyPath)).Files[0].Path;
        }

        JsonElement[] Versions(int number) => Json.Parse(admin.GetStringAsync($"/JellyfinMod/Entries/{ids["alias"]}").GetAwaiter().GetResult())
            .GetProperty("episodes").EnumerateArray().Single(item => item.GetProperty("id").AsGuid() == ids["aliass1e" + number])
            .GetProperty("versions").EnumerateArray().ToArray();

        // Finding 1: neither "UHD" nor the stripped name that would sort first; the alias is kept but ended by a letter.
        var e1 = await AddVersionAsync(1, "Alias.Show.S01E01.1080p.WEB-DL-GRP");
        var e1Name = Path.GetFileName(e1.DestinationPath!);
        var e1Row = Versions(1).SingleOrDefault(version => version.GetProperty("label").GetString() == "1080p WEB-DL");
        Assert(e1Name == "Alias Show S01E01 UHDx Remux - WEB-DL.mkv" && JellyfinMod.Services.Automation.VersionQuality.Parse(e1.DestinationPath).Resolution is null &&
            Main(heldE1) == heldE1 && e1Row.ValueKind == JsonValueKind.Object && e1Row.GetProperty("resolution").GetString() == "1080p",
            $"A 1080p version beside a UHD file carries no resolution alias in its name and sorts after it ({e1Name}, main " +
            $"{Path.GetFileName(Main(heldE1))}, row {e1Row})");

        // Finding 2: the main file's name is not cut to make room; the series form, which keeps the episode number, is used.
        var e2 = await AddVersionAsync(2, "Alias.Show.S01E02.1080p.WEB-DL-GRP");
        await using (var database = new ModDbContext(dbPath))
            Assert(e2.DestinationPath == Path.Combine(folder, "Alias Show (2022) S01E02 - WEB-DL.mkv") && Main(heldE2) == heldE2 &&
                await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == ids["aliass1e2"]) == 2,
                $"Beside a 255-byte name the new version takes the series form, binds to S01E02 and sorts after the main file: " +
                $"{Path.GetFileName(e2.DestinationPath)}, main {Path.GetFileName(Main(heldE2)).Length} chars");

        // Finding 4: 576p is higher than a known 480p file, so it names its resolution and becomes the default.
        var e3 = await AddVersionAsync(3, "Alias.Show.S01E03.576p.WEB-DL-GRP", sdProfile);
        Assert(e3.DestinationPath == Path.Combine(folder, "Alias Show (2022) S01E03 - 576p WEB-DL.mkv") && Main(heldE3) == e3.DestinationPath,
            $"A 576p version beside a 480p file names its resolution and becomes Jellyfin's main version: {Path.GetFileName(e3.DestinationPath)}, " +
            $"main {Path.GetFileName(Main(heldE3))}");

        // A 4:3 1440x1080 file is 1080p by the larger of its width and height tiers, as Jellyfin's own row reads it: a 1080p
        // version is not higher, so it names no resolution and sorts after the held file, which stays the main version.
        var e4 = await AddVersionAsync(4, "Alias.Show.S01E04.1080p.WEB-DL-GRP");
        Assert(e4.DestinationPath == Path.Combine(folder, "Alias Show S01E04 - WEB-DL.mkv") && e4.VersionLabel == "1080p WEB-DL" &&
            Main(heldE4) == heldE4,
            $"A 1080p version beside a 1440x1080 file names no resolution and the held file stays Jellyfin's main version: " +
            $"{Path.GetFileName(e4.DestinationPath)}, main {Path.GetFileName(Main(heldE4))}");

        // Finding 3 (re-review 5): other media written over the S01E01 version in place, the same inode and birth time, is no
        // longer read as the import's 1080p; its row follows the media (no streams here, so no resolution).
        var inspector = new UnixFileInspector();
        Assert(inspector.TryInspect(e1.DestinationPath!, out var before), "The added S01E01 version can be inspected");
        await using (var stream = new FileStream(e1.DestinationPath!, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(0);
            await stream.WriteAsync(RandomNumberGenerator.GetBytes(6144));
        }

        Assert(inspector.TryInspect(e1.DestinationPath!, out var after) && after.PhysicalIdentity == before.PhysicalIdentity &&
            after.FileFingerprint != before.FileFingerprint, "The rewrite kept the file's identity and changed its content");
        var rewrittenRow = Versions(1).SingleOrDefault(version => version.GetProperty("label").GetString() == "1080p WEB-DL");
        Assert(rewrittenRow.ValueKind == JsonValueKind.Object && rewrittenRow.GetProperty("resolution").ValueKind == JsonValueKind.Null,
            "A file rewritten in place at the version's path is no longer read as the import's 1080p: " + rewrittenRow);
    }

    private static async Task SetEpisodeUpgradesAsync(HttpClient admin, bool on)
    {
        var body = JsonNode.Parse(await admin.GetStringAsync("/JellyfinMod/Settings/Automation"))!.AsObject();
        body["episodeUpgradesEnabled"] = on;
        var saved = await ReadAsync(await admin.PatchAsJsonAsync("/JellyfinMod/Settings/Automation", body), 200,
            "Episode upgrades turned " + (on ? "on" : "off"));
        Assert(saved.GetProperty("episodeUpgradesEnabled").GetBoolean() == on, "The episode upgrades switch reads back " + on);
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return Json.Parse(body);
    }
}
