using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Season and series packs after the Codex review of the pack plugin (2026-10-08), through the same real host, HTTP,
/// SQLite, Torznab and Transmission boundaries and real files as the rest of Phase 5: Add and Replace imported end to end
/// through the replacement (refused when the episode is kept, when its old file is bound by number only, and when the pack
/// held no file for it; never while the new file is missing), an All Seasons pack across two seasons with a folder-numbered
/// file and a bare double-episode file, a retried episode, a coordinated seed detach that a sibling's failed check stops,
/// a whole-pack Remove, and access to every claimed episode for queue actions and grabs. After the re-review: Keep saved while
/// a removal is under way, a version added while a replacement waits, a file lost while an add pack is fetched, and a bare
/// triple-episode file. After re-review 2: another version of one episode grabbed after the episode lost its file.
/// </summary>
internal static class PackReviewScenarios
{
    private const long Size = 300_000;

    public static async Task RunAsync(Func<Task> tick, HttpClient admin, HttpClient tvAdmin, World world, string dbPath,
        Dictionary<string, Guid> ids, TorznabBoundary torznab, TransmissionBoundary transmission)
    {
        // Jellyfin's metadata gives the shows' files their TMDB ids by number, as it does once it fetched them: a file bound
        // this way is verified as its episode. Replace Show's old S01E03 is left numbered only, so it is never verified.
        var numbered = new Regex(@"S(\d{2})E(\d{2})", RegexOptions.IgnoreCase);
        var folderTmdb = new Regex(@"\[tmdbid-(2[2-6]0)\]");
        world.Native.EpisodeTmdbId = file =>
            folderTmdb.Match(file) is { Success: true } show && numbered.Match(Path.GetFileName(file)) is { Success: true } number &&
            !file.EndsWith("Replace Show S01E03.mkv", StringComparison.Ordinal)
                ? int.Parse(show.Groups[1].Value, CultureInfo.InvariantCulture) * 100 +
                    int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) * 10 + int.Parse(number.Groups[2].Value, CultureInfo.InvariantCulture)
                : null;

        await using (var database = new ModDbContext(dbPath))
        {
            (await database.AcquisitionSettings.SingleAsync()).EpisodeUpgradesEnabled = true;
            void Show(string key, int tmdb, string title, params (int Season, int Episode)[] numbers)
            {
                var entry = new Entry
                {
                    MediaType = "series", TmdbId = tmdb, Title = title, Year = 2022, TargetLibraryId = world.Tv.Id,
                    MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", tmdb, title, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        null, null, null, null, tmdb + 100, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", 5, null, null)]))
                };
                database.Entries.Add(entry);
                ids[key] = entry.Id;
                foreach (var (season, episode) in numbers)
                {
                    var row = new Episode
                    {
                        EntryId = entry.Id, TmdbId = tmdb * 100 + season * 10 + episode, SeasonNumber = season, EpisodeNumber = episode,
                        Title = $"{title} {season}x{episode}", RuntimeMinutes = 45,
                        AirDate = new DateTime(2022, 3, season * 10 + episode, 0, 0, 0, DateTimeKind.Utc)
                    };
                    database.Episodes.Add(row);
                    ids[$"{key}s{season}e{episode}"] = row.Id;
                }
            }

            Show("repl", 220, "Replace Show", (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 7));
            Show("addp", 230, "Add Show", (1, 1), (1, 2));
            Show("multi", 240, "Multi Show", (1, 1), (1, 2), (2, 1), (2, 2));
            Show("rmv", 250, "Remove Show", (1, 1), (1, 2), (1, 3));
            Show("race", 260, "Race Show", (1, 1), (1, 2));
            await database.SaveChangesAsync();
        }

        // The held files: Replace Show holds all seven episodes, Add Show and Race Show their first.
        var replFolder = Path.Combine(world.Tv.Location, "Replace Show (2022) [tmdbid-220]", "Season 01");
        var addFolder = Path.Combine(world.Tv.Location, "Add Show (2022) [tmdbid-230]", "Season 01");
        var raceFolder = Path.Combine(world.Tv.Location, "Race Show (2022) [tmdbid-260]", "Season 01");
        Directory.CreateDirectory(replFolder);
        Directory.CreateDirectory(addFolder);
        Directory.CreateDirectory(raceFolder);
        string Old(int number) => Path.Combine(replFolder, $"Replace Show S01E0{number}.mkv");
        for (var number = 1; number <= 7; number++) await File.WriteAllBytesAsync(Old(number), RandomNumberGenerator.GetBytes(4096));
        var addOld = Path.Combine(addFolder, "Add Show S01E01.mkv");
        await File.WriteAllBytesAsync(addOld, RandomNumberGenerator.GetBytes(4096));
        await File.WriteAllBytesAsync(Path.Combine(raceFolder, "Race Show S01E01.mkv"), RandomNumberGenerator.GetBytes(4096));
        world.Native.Scan(Path.GetDirectoryName(replFolder)!);
        world.Native.Scan(Path.GetDirectoryName(addFolder)!);
        world.Native.Scan(Path.GetDirectoryName(raceFolder)!);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            var bound = await database.EpisodeBindings.AsNoTracking().Where(binding =>
                    Enumerable.Range(1, 7).Select(number => ids[$"repls1e{number}"]).Contains(binding.EpisodeId) ||
                    binding.EpisodeId == ids["addps1e1"] || binding.EpisodeId == ids["races1e1"])
                .ToListAsync();
            return bound.Count == 9 && bound.Count(binding => binding.IdentityUnverified) == 1 &&
                bound.Single(binding => binding.IdentityUnverified).EpisodeId == ids["repls1e3"] ? true : null;
        }, "The held files are bound, Replace Show's S01E03 by its number only");
        await using (var database = new ModDbContext(dbPath))
        {
            // Keep protects Replace Show's S01E04.
            (await database.Episodes.SingleAsync(value => value.Id == ids["repls1e4"])).RetentionPolicy = RetentionPolicy.Never;
            await database.SaveChangesAsync();
        }

        TorrentFixture Pack(string key, string name, string tvdb, params string[] files)
        {
            var fixture = TorrentFixture.Multi(name, files.Select(file => (file, Size)).ToArray());
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(name, "guid-" + key, torznab.Download(key), Size * files.Length, 25, new() { ["tvdbid"] = tvdb }));
            return fixture;
        }

        var replPack = Pack("replpack", "Replace.Show.S01.1080p.WEB-DL-GRP", "320", "Replace.Show.S01E01.1080p.WEB-DL-GRP.mkv",
            "Replace.Show.S01E03.1080p.WEB-DL-GRP.mkv", "Replace.Show.S01E04.1080p.WEB-DL-GRP.mkv", "Replace.Show.S01E05.1080p.WEB-DL-GRP.mkv",
            "Replace.Show.S01E06.1080p.WEB-DL-GRP.mkv", "Replace.Show.S01E07.1080p.WEB-DL-GRP.mkv");
        var addPack = Pack("addpack", "Add.Show.S01.1080p.WEB-DL-GRP", "330", "Add.Show.S01E01.1080p.WEB-DL-GRP.mkv",
            "Add.Show.S01E02.1080p.WEB-DL-GRP.mkv");
        // "Season 01/E01E02E03.mkv" comes first, so read as S01E01 alone it would take that episode (re-review, finding 4).
        var multiPack = Pack("multipack", "Multi.Show.S01-S02.1080p.WEB-DL-GRP", "340", "Season 01/E01E02E03.mkv",
            "Season 01/Multi.Show.S01E01.1080p.WEB-DL-GRP.mkv",
            "Season 01/E02.mkv", "Season 02/Multi.Show.S02E01.1080p.WEB-DL-GRP.mkv", "Season 02/Multi.Show.S02E02.1080p.WEB-DL-GRP.mkv",
            "Season 02/E02E03.mkv");
        var rmvPack = Pack("rmvpack", "Remove.Show.S01.1080p.WEB-DL-GRP", "350", "Remove.Show.S01E01.1080p.WEB-DL-GRP.mkv",
            "Remove.Show.S01E02.1080p.WEB-DL-GRP.mkv");
        Pack("racepack", "Race.Show.S01.1080p.WEB-DL-GRP", "360", "Race.Show.S01E01.1080p.WEB-DL-GRP.mkv",
            "Race.Show.S01E02.1080p.WEB-DL-GRP.mkv");
        lock (torznab.MovieItems)
        {
            torznab.TvItems.Add(new("Add.Show.S01E02.1080p.WEB-DL-GRP", "guid-add-e2", torznab.Download("adde2"), Size, 25,
                new() { ["tvdbid"] = "330" }));
            torznab.TvItems.Add(new("Race.Show.S01E01.1080p.WEB-DL-GRP", "guid-race-e1", torznab.Download("racee1"), Size, 25,
                new() { ["tvdbid"] = "360" }));
        }

        async Task<(JsonElement Search, string ReleaseId)> PackSearchAsync(HttpClient client, string key, string query, string rawTitle)
        {
            var search = await ReadAsync(await client.GetAsync($"/JellyfinMod/Releases?entryId={ids[key]}&{query}"), 200, "Pack search " + rawTitle);
            var row = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == rawTitle);
            Assert(row.GetProperty("eligible").GetBoolean(), "The pack is eligible: " + row.GetRawText());
            return (search, row.GetProperty("releaseId").GetString()!);
        }

        async Task<Guid> GrabPackAsync(string key, string query, string rawTitle, string? mode, string expectedMode)
        {
            var (search, release) = await PackSearchAsync(admin, key, query, rawTitle);
            var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = release, idempotencyKey = "p5r-" + Guid.NewGuid().ToString("N"),
                mode
            }), 202, "Administrator grabs " + rawTitle);
            Assert(grab.GetProperty("mode").GetString() == expectedMode, $"{rawTitle} is grabbed to {expectedMode}: " + grab.GetRawText());
            var id = grab.GetProperty("id").AsGuid();
            await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{id}")).GetProperty("state").GetString() ==
                "accepted" ? true : (bool?)null, rawTitle + " is accepted");
            return id;
        }

        async Task<List<ImportOperation>> ChildrenAsync(Guid grabId)
        {
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId).OrderBy(value => value.CreatedAt)
                .ToListAsync();
        }

        async Task SettleAsync(Guid grabId, int children, string message) => await WaitAsync(async () =>
        {
            await tick();
            var all = await ChildrenAsync(grabId);
            return all.Count >= children && all.All(child => !ImportStates.Open.Contains(child.State)) ? true : (bool?)null;
        }, message, 60);

        // ---------------- Add: the held S01E01 gains the pack's file as another version, the missing S01E02 is filled. The
        // missing episode is claimed under its own key, so a single-episode grab of it is refused while the pack holds it
        // (finding 4).
        var addId = await GrabPackAsync("addp", "scope=season&seasonNumber=1", "Add.Show.S01.1080p.WEB-DL-GRP", null, "add");
        await using (var database = new ModDbContext(dbPath))
        {
            var claims = await database.GrabClaims.AsNoTracking().Where(claim => claim.GrabId == addId).ToDictionaryAsync(claim => claim.EpisodeId);
            Assert(claims.Count == 2 && claims[ids["addps1e1"]].ActiveKey == ids["addps1e1"].ToString("N") + "+add" &&
                claims[ids["addps1e2"]].ActiveKey == ids["addps1e2"].ToString("N"),
                "An add pack claims the held episode under its version key and the missing one under its own key");
        }

        var e2Search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["addp"]}&episodeId={ids["addps1e2"]}"), 200,
            "Add Show S01E02 search while the add pack holds it");
        Assert(e2Search.GetProperty("grab").GetProperty("reason").GetString() == "grab_active" &&
            e2Search.GetProperty("grab").GetProperty("activeOperationId").AsGuid() == addId, "The episode search learns the add pack holds it");
        var singleE2 = e2Search.GetProperty("candidates").EnumerateArray()
            .Single(item => item.GetProperty("rawTitle").GetString() == "Add.Show.S01E02.1080p.WEB-DL-GRP");
        var duplicate = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = e2Search.GetProperty("searchId").AsGuid(), releaseId = singleE2.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5r-e2-" + Guid.NewGuid().ToString("N")
        });
        var duplicateBody = await duplicate.Content.ReadAsStringAsync();
        Assert(duplicate.StatusCode == HttpStatusCode.Conflict && duplicateBody.Contains("grab_active", StringComparison.Ordinal) &&
            duplicateBody.Contains("S01E02", StringComparison.Ordinal),
            "A single-episode grab of a missing episode an add pack fills is refused (finding 4): " + duplicateBody);

        transmission.Progress(addPack.InfoHash, 1.0);
        await SettleAsync(addId, 2, "Every import of the add pack is final");
        await using (var database = new ModDbContext(dbPath))
        {
            var children = (await ChildrenAsync(addId)).ToDictionary(child => child.EpisodeId!.Value);
            var e1Bindings = await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == ids["addps1e1"]).ToListAsync();
            Assert(children[ids["addps1e1"]].State == ImportStates.Completed && children[ids["addps1e1"]].Intent == GrabIntents.AddVersion &&
                children[ids["addps1e1"]].ReplaceState is null && children[ids["addps1e2"]].State == ImportStates.Completed &&
                children[ids["addps1e2"]].Intent == GrabIntents.Acquire && File.Exists(addOld) && e1Bindings.Count == 2 &&
                e1Bindings.Any(binding => binding.MediaPath == addOld) &&
                e1Bindings.Any(binding => binding.MediaPath == children[ids["addps1e1"]].DestinationPath),
                "Add imports the pack's S01E01 beside the held file and fills S01E02: " +
                string.Join(" | ", children.Values.Select(child => $"{child.State} {child.Reason} {child.Intent} {child.DestinationPath}")));
            Assert(await database.GrabClaims.Where(claim => claim.GrabId == addId).AllAsync(claim => claim.ActiveKey == null) &&
                (await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == addId)).ActiveTarget is null,
                "The add pack holds nothing once both episodes are imported");
        }

        // ---------------- A coordinated detach stopped by a sibling (finding 3): the first removal request fails and both
        // releases stay `removing`; then S01E02's library file is moved away. The retry checks every release of the pack, so
        // S01E02's blocks and S01E01's waits with it: the torrent stays in the client. With the file back, both detach once.
        var addE2Library = (await ChildrenAsync(addId)).Single(child => child.EpisodeId == ids["addps1e2"]).DestinationPath!;
        var addE2Away = addE2Library + ".away";
        // Every removal is refused until the file is back, so no tick, the background monitor's included, can detach early.
        var removesBefore = transmission.RemoveCalls;
        transmission.FailRemoves = 1_000;
        transmission.Torrents[addPack.InfoHash].UploadRatio = 1.5;
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return await database.SeedReleaseOperations.Where(seed => seed.GrabId == addId).AllAsync(seed => seed.State == SeedReleaseStates.Removing) &&
                transmission.RemoveCalls > removesBefore ? true : (bool?)null;
        }, "The add pack's releases are removing after a refused removal");
        File.Move(addE2Library, addE2Away);
        try
        {
            var e1Seed = ids["addps1e1"];
            var e2Seed = ids["addps1e2"];
            await WaitAsync(async () =>
            {
                await tick();
                await using var probe = new ModDbContext(dbPath);
                return await probe.SeedReleaseOperations.AnyAsync(seed => seed.GrabId == addId && seed.EpisodeId == e2Seed &&
                        seed.State == SeedReleaseStates.Blocked) &&
                    await probe.SeedReleaseOperations.AnyAsync(seed => seed.GrabId == addId && seed.EpisodeId == e1Seed &&
                        seed.State == SeedReleaseStates.Waiting) ? true : (bool?)null;
            }, "The retry blocks S01E02's release and S01E01's waits");
            for (var index = 0; index < 3; index++) await tick();
            await using var database = new ModDbContext(dbPath);
            var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(seed => seed.GrabId == addId).ToDictionaryAsync(seed => seed.EpisodeId!.Value);
            Assert(transmission.Torrents.ContainsKey(addPack.InfoHash) && seeds.Values.All(seed => seed.State != SeedReleaseStates.Detached) &&
                seeds[ids["addps1e2"]].State == SeedReleaseStates.Blocked &&
                seeds[ids["addps1e2"]].Reason == SeedReleaseReasons.LibraryLinkUnexpected &&
                seeds[ids["addps1e1"]].State == SeedReleaseStates.Waiting && seeds[ids["addps1e1"]].Reason == SeedReleaseReasons.PackWaiting,
                "A retried pack detach checks every release: one whose library file went blocks, the other waits, the torrent stays (finding 3): " +
                string.Join(" | ", seeds.Values.Select(seed => $"{seed.State} {seed.Reason}")) + $" removals {transmission.RemoveCalls - removesBefore}");
        }
        finally
        {
            if (File.Exists(addE2Away)) File.Move(addE2Away, addE2Library);
            transmission.FailRemoves = 0;
        }

        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return await database.SeedReleaseOperations.Where(seed => seed.GrabId == addId).AllAsync(seed => seed.State == SeedReleaseStates.Detached)
                ? true : (bool?)null;
        }, "With the file back, the add pack's releases detach together");
        Assert(!transmission.Torrents.ContainsKey(addPack.InfoHash) && File.Exists(addOld) && File.Exists(addE2Library),
            "The torrent is detached once, and every library file stays");

        // ---------------- Replace: S01E01 is replaced once its new file is in the library; S01E02's file is missing from the
        // pack, so its old file stays; S01E03's old file is bound by number only and S01E04 is kept, so both stay; S01E05's
        // replacement waits while its old file plays and while its new file is gone, and goes ahead once it is back.
        var replId = await GrabPackAsync("repl", "scope=season&seasonNumber=1", "Replace.Show.S01.1080p.WEB-DL-GRP", "replace", "replace");
        Guid OldItem(int number) => world.Native.Items.Single(item => item.Path == Old(number)).Id;
        world.Native.Playing[OldItem(5)] = true;
        world.Native.Playing[OldItem(6)] = true;
        world.Native.Playing[OldItem(7)] = true;
        transmission.Progress(replPack.InfoHash, 1.0);
        await SettleAsync(replId, 7, "Every import of the replace pack is final");
        for (var index = 0; index < 2; index++) await tick();
        var replChildren = (await ChildrenAsync(replId)).ToDictionary(child => child.EpisodeId!.Value);
        ImportOperation Child(int number) => replChildren[ids[$"repls1e{number}"]];
        await using (var database = new ModDbContext(dbPath))
        {
            var bindings = await database.EpisodeBindings.AsNoTracking().ToListAsync();
            int Bound(int number) => bindings.Count(binding => binding.EpisodeId == ids[$"repls1e{number}"]);
            var summary = string.Join(" | ", replChildren.Values.Select(child =>
                $"{child.State} {child.Reason} {child.ReplaceState} {child.ReplaceDetail}"));
            Assert(Child(1).State == ImportStates.Completed && Child(1).ReplaceState == ReplaceStates.Done && !File.Exists(Old(1)) &&
                File.Exists(Child(1).DestinationPath!) && Bound(1) == 1,
                "Replace removes S01E01's old file once the pack's file is bound: " + summary);
            var oldE1 = Old(1);
            var newE1Binding = Child(1).BindingId;
            var operation = await database.RetentionOperations.AsNoTracking().SingleAsync(value => value.MediaPath == oldE1);
            Assert(operation.Provenance == RetentionProvenances.PackReplaced && operation.State == "completed" &&
                await database.History.AnyAsync(history => history.EventType == "version_replaced" && history.BindingId == operation.BindingId) &&
                await database.History.AnyAsync(history => history.EventType == "pack_replaced" && history.BindingId == newE1Binding),
                "The removal is a pack_replaced retention operation, named on the old file's and the new file's history");
            Assert(Child(2).State == ImportStates.Failed && Child(2).Reason == ImportReasons.PackEpisodeMissing && Child(2).ReplaceState is null &&
                File.Exists(Old(2)) && Bound(2) == 1, "S01E02's old file stays: the pack held no file for it: " + summary);
            Assert(Child(3).State == ImportStates.Completed && Child(3).ReplaceState == ReplaceStates.Refused &&
                Child(3).ReplaceDetail!.Contains("identity_unverified", StringComparison.Ordinal) && File.Exists(Old(3)) && Bound(3) == 2,
                "S01E03's old file, bound by its number only, is kept beside the new one (finding 2): " + summary);
            Assert(Child(4).State == ImportStates.Completed && Child(4).ReplaceState == ReplaceStates.Refused &&
                Child(4).ReplaceDetail!.Contains("Keep", StringComparison.Ordinal) && File.Exists(Old(4)) && Bound(4) == 2,
                "Keep on S01E04 keeps its old file: " + summary);
            Assert(Child(5).State == ImportStates.Completed && Child(5).ReplaceState == ReplaceStates.Pending &&
                Child(5).ReplaceDetail!.Contains("active_session", StringComparison.Ordinal) && File.Exists(Old(5)),
                "S01E05's replacement waits while its old file plays: " + summary);
        }

        // The new S01E05 file goes away while the old one still plays; then playback stops. The replacement must not remove
        // the old file, now the only copy (finding 1).
        var newE5 = Child(5).DestinationPath!;
        var e5Import = Child(5).Id;
        var oldE5 = Old(5);
        var newE5Away = newE5 + ".away";
        File.Move(newE5, newE5Away);
        try
        {
            world.Native.Playing.TryRemove(OldItem(5), out _);
            for (var index = 0; index < 3; index++) await tick();
            await using var database = new ModDbContext(dbPath);
            var e5 = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e5Import);
            Assert(e5.ReplaceState == ReplaceStates.Pending && e5.ReplaceDetail!.Contains("successor_unavailable", StringComparison.Ordinal) &&
                File.Exists(oldE5) && !await database.RetentionOperations.AnyAsync(value => value.MediaPath == oldE5),
                $"With the new file gone, the old S01E05 file is never removed (finding 1): {e5.ReplaceState} {e5.ReplaceDetail}");
        }
        finally
        {
            if (File.Exists(newE5Away)) File.Move(newE5Away, newE5);
        }

        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e5Import)).ReplaceState == ReplaceStates.Done
                ? true : (bool?)null;
        }, "With the new file back, S01E05's replacement goes ahead");
        Assert(!File.Exists(Old(5)) && File.Exists(newE5), "S01E05's old file is replaced and the new one stays");

        // ---------------- A version added while a replacement waits is never removed (re-review, finding 2): S01E07's
        // replacement waits while its old file plays; meanwhile another version of S01E07 is added. Once playback stops, only
        // the old file the replacement fixed when its import completed is removed; the added version stays.
        var e7Import = Child(7).Id;
        var newE7 = Child(7).DestinationPath!;
        var addedE7 = Path.Combine(replFolder, "Replace Show S01E07 - 720p.mkv");
        await using (var database = new ModDbContext(dbPath))
        {
            var e7 = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e7Import);
            Assert(e7.ReplaceState == ReplaceStates.Pending && e7.ReplaceTargets is { } targets &&
                targets.Contains(JsonSerializer.Serialize(Old(7)), StringComparison.Ordinal) &&
                !targets.Contains(JsonSerializer.Serialize(newE7), StringComparison.Ordinal),
                $"S01E07's replacement waits and names only the old file as its target: {e7.ReplaceState} {e7.ReplaceTargets}");
        }

        await File.WriteAllBytesAsync(addedE7, RandomNumberGenerator.GetBytes(4096));
        world.Native.Scan(Path.GetDirectoryName(replFolder)!);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return await database.EpisodeBindings.AnyAsync(binding => binding.EpisodeId == ids["repls1e7"] && binding.MediaPath == addedE7 &&
                !binding.IdentityUnverified && binding.FileFingerprint != null) ? true : (bool?)null;
        }, "The added S01E07 version is bound and verified as that episode");
        world.Native.Playing.TryRemove(OldItem(7), out _);
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return (await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e7Import)).ReplaceState != ReplaceStates.Pending
                ? true : (bool?)null;
        }, "S01E07's replacement ends once its old file stops playing");
        await using (var database = new ModDbContext(dbPath))
        {
            var e7 = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e7Import);
            Assert(e7.ReplaceState == ReplaceStates.Done && !File.Exists(Old(7)) && File.Exists(newE7) && File.Exists(addedE7) &&
                await database.EpisodeBindings.AnyAsync(binding => binding.EpisodeId == ids["repls1e7"] && binding.MediaPath == addedE7) &&
                !await database.RetentionOperations.AnyAsync(value => value.MediaPath == addedE7),
                $"Only S01E07's old file is replaced; the version added while it waited stays (finding 2): {e7.ReplaceState} {e7.ReplaceDetail}");
        }

        // ---------------- Keep that lands while a replacement is under way is honoured (re-review, finding 1): S01E06's
        // replacement passed its own Keep check; Keep on the episode is saved while the removal reads the sessions, after the
        // replacement looked and before its unlink. The removal reads Keep again last, under its locks, and keeps the old file.
        var e6Import = Child(6).Id;
        var e6Episode = ids["repls1e6"];
        var keptDuringRemoval = 0;
        world.Native.SessionsRead = () =>
        {
            if (Interlocked.Exchange(ref keptDuringRemoval, 1) != 0) return;
            using var keeping = new ModDbContext(dbPath);
            keeping.Episodes.Where(value => value.Id == e6Episode)
                .ExecuteUpdate(set => set.SetProperty(value => value.RetentionPolicy, RetentionPolicy.Never));
        };
        try
        {
            world.Native.Playing.TryRemove(OldItem(6), out _);
            await WaitAsync(async () =>
            {
                await tick();
                await using var database = new ModDbContext(dbPath);
                return (await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e6Import)).ReplaceState != ReplaceStates.Pending
                    ? true : (bool?)null;
            }, "S01E06's replacement ends once its old file stops playing");
        }
        finally
        {
            world.Native.SessionsRead = null;
        }

        var oldE6 = Old(6);
        await using (var database = new ModDbContext(dbPath))
        {
            var e6 = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == e6Import);
            Assert(keptDuringRemoval == 1 && e6.ReplaceState == ReplaceStates.Refused &&
                e6.ReplaceDetail!.Contains("(" + "kept" + ")", StringComparison.Ordinal) && File.Exists(oldE6) &&
                !await database.RetentionOperations.AnyAsync(value => value.MediaPath == oldE6),
                $"Keep saved during the removal keeps S01E06's old file (finding 1): {e6.ReplaceState} {e6.ReplaceDetail}");
        }

        // ---------------- All Seasons: a S01-S02 pack fills both seasons. "Season 01/E02.mkv" takes its season from its folder;
        // "Season 02/E02E03.mkv" is a double episode and "Season 01/E01E02E03.mkv" a triple; both are skipped, never imported as
        // S02E02 or S01E01 (finding 6; re-review, finding 4). The pack's own S02E02
        // file is not wanted in the client at first, so S02E02 fails; once it is wanted, Retry imports that episode alone with
        // the pack's ownership taken again (finding 5).
        var (seriesSearch, _) = await PackSearchAsync(admin, "multi", "scope=series", "Multi.Show.S01-S02.1080p.WEB-DL-GRP");
        Assert(seriesSearch.GetProperty("target").GetProperty("covered").GetArrayLength() == 4, "All Seasons covers both seasons' episodes");
        var multiId = await GrabPackAsync("multi", "scope=series", "Multi.Show.S01-S02.1080p.WEB-DL-GRP", null, "fill");
        var unwanted = transmission.Torrents[multiPack.InfoHash].Files.Single(file => file.Name.EndsWith("Multi.Show.S02E02.1080p.WEB-DL-GRP.mkv",
            StringComparison.Ordinal));
        unwanted.Wanted = false;
        transmission.Progress(multiPack.InfoHash, 1.0);
        await SettleAsync(multiId, 4, "Every import of the All Seasons pack is final");
        var multiChildren = (await ChildrenAsync(multiId)).ToDictionary(child => child.EpisodeId!.Value);
        var showFolder = Path.Combine(world.Tv.Location, "Multi Show (2022) [tmdbid-240]");
        Assert(multiChildren[ids["multis1e1"]].DestinationPath == Path.Combine(showFolder, "Season 01", "Multi Show (2022) S01E01.mkv") &&
            multiChildren[ids["multis1e2"]].DestinationPath == Path.Combine(showFolder, "Season 01", "Multi Show (2022) S01E02.mkv") &&
            multiChildren[ids["multis2e1"]].DestinationPath == Path.Combine(showFolder, "Season 02", "Multi Show (2022) S02E01.mkv") &&
            multiChildren.Values.Count(child => child.State == ImportStates.Completed) == 3 &&
            multiChildren[ids["multis2e2"]].State == ImportStates.Failed && multiChildren[ids["multis2e2"]].Reason == ImportReasons.PackEpisodeMissing,
            "The All Seasons pack imports into both seasons; S02E02 has no file of its own: " +
            string.Join(" | ", multiChildren.Values.Select(child => $"{child.State} {child.Reason} {child.DestinationPath}")));
        await using (var database = new ModDbContext(dbPath))
        {
            var skipped = await database.History.AsNoTracking().Where(history => history.EntryId == ids["multi"] &&
                history.EventType == "pack_file_skipped").Select(history => history.Summary).ToListAsync();
            Assert(skipped.Count == 2 && skipped.All(summary => summary.Contains("several episodes", StringComparison.Ordinal)) &&
                skipped.Any(summary => summary.StartsWith("Skipped E02E03.mkv", StringComparison.Ordinal)) &&
                skipped.Any(summary => summary.StartsWith("Skipped E01E02E03.mkv", StringComparison.Ordinal)),
                "The bare double- and triple-episode files are skipped as several episodes (re-review, finding 4): " + string.Join(" | ", skipped));
            Assert((await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == multiId)).ActiveTarget is null &&
                await database.GrabClaims.Where(claim => claim.GrabId == multiId).AllAsync(claim => claim.ActiveKey == null),
                "The pack holds nothing once every import is final");
        }

        unwanted.Wanted = true;
        transmission.Progress(multiPack.InfoHash, 1.0);
        var failedE2 = multiChildren[ids["multis2e2"]];
        var retried = await ReadAsync(await admin.PostAsync($"/JellyfinMod/Imports/{failedE2.Id}/Retry", null), 202, "Retry S02E02 of the pack");
        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == multiId);
            var claims = await database.GrabClaims.AsNoTracking().Where(claim => claim.GrabId == multiId).ToListAsync();
            Assert(retried.GetProperty("episodeId").AsGuid() == ids["multis2e2"] && retried.GetProperty("retryOfId").AsGuid() == failedE2.Id &&
                grab.ActiveTarget == GrabService.PackKey(ids["multi"], null) &&
                claims.Single(claim => claim.EpisodeId == ids["multis2e2"]).ActiveKey == ids["multis2e2"].ToString("N") &&
                claims.Where(claim => claim.EpisodeId != ids["multis2e2"]).All(claim => claim.ActiveKey == null),
                "Retry recreates S02E02 alone, as the failed one's retry, and takes back the pack's target and that episode's claim only: " +
                retried.GetRawText());
        }

        await SettleAsync(multiId, 5, "The retried S02E02 is final");
        await using (var database = new ModDbContext(dbPath))
        {
            var retry = await database.ImportOperations.AsNoTracking().SingleAsync(value => value.Id == retried.GetProperty("id").AsGuid());
            Assert(retry.State == ImportStates.Completed && retry.Intent == failedE2.Intent &&
                retry.DestinationPath == Path.Combine(showFolder, "Season 02", "Multi Show (2022) S02E02.mkv") &&
                (await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == multiId)).ActiveTarget is null &&
                await database.GrabClaims.Where(claim => claim.GrabId == multiId).AllAsync(claim => claim.ActiveKey == null),
                $"The retried episode imports its now wanted file and the pack lets go again: {retry.State} {retry.Reason} {retry.DestinationPath}");
        }

        await ExpectAsync(admin.PostAsync($"/JellyfinMod/Imports/{multiChildren[ids["multis1e1"]].Id}/Retry", null), 409, "import_not_retryable",
            "Retry on the pack's row once no episode of it failed is refused");

        // ---------------- Access to every claimed episode (finding 10), then a whole-pack Remove (finding 9). Remove Show's
        // pack lacks S01E03, so its row is failed while S01E01 and S01E02 seed.
        var rmvId = await GrabPackAsync("rmv", "scope=season&seasonNumber=1", "Remove.Show.S01.1080p.WEB-DL-GRP", null, "fill");
        transmission.Progress(rmvPack.InfoHash, 1.0);
        await SettleAsync(rmvId, 3, "Every import of the Remove Show pack is final");
        var rmvChildren = (await ChildrenAsync(rmvId)).ToDictionary(child => child.EpisodeId!.Value);
        Assert(rmvChildren[ids["rmvs1e3"]].State == ImportStates.Failed &&
            rmvChildren.Values.Count(child => child.State == ImportStates.Completed) == 2, "Remove Show's S01E03 fails, the others import");
        var queue = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Queue?entryId={ids["rmv"]}"));
        var rmvRows = queue.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("grabId").AsGuid() == rmvId).ToArray();
        Assert(rmvRows.Length == 1 && rmvRows[0].GetProperty("state").GetString() == "failed" &&
            rmvRows[0].GetProperty("id").AsGuid() == rmvChildren[ids["rmvs1e3"]].Id,
            "The pack's one row shows what the pack still needs: its failed episode, not a seeding one (finding 9): " + queue.GetRawText());

        // The restricted administrator may read Remove Show's S01E01 but not S01E02, whose file carries a tag they block.
        world.RestrictedAdmin.SetPreference(PreferenceKind.BlockedTags, ["jfmod-hidden"]);
        var (tvSearch, tvRelease) = await PackSearchAsync(tvAdmin, "rmv", "scope=season&seasonNumber=1", "Remove.Show.S01.1080p.WEB-DL-GRP");
        var hiddenItem = world.Native.Items.Single(item => item.Id == rmvChildren[ids["rmvs1e2"]].NativeItemId);
        hiddenItem.Tags = ["jfmod-hidden"];
        try
        {
            Assert((await tvAdmin.GetAsync($"/JellyfinMod/Releases?entryId={ids["rmv"]}&scope=season&seasonNumber=1")).StatusCode ==
                HttpStatusCode.Forbidden, "A season search is refused to someone who cannot read one of its episodes");
            var hiddenGrab = await tvAdmin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = tvSearch.GetProperty("searchId").AsGuid(), releaseId = tvRelease, idempotencyKey = "p5r-hidden-" + Guid.NewGuid().ToString("N")
            });
            Assert(hiddenGrab.StatusCode == HttpStatusCode.NotFound,
                $"A pack grab is refused once one of its episodes is hidden from the requester: {(int)hiddenGrab.StatusCode}");
            var tvQueue = Json.Parse(await tvAdmin.GetStringAsync($"/JellyfinMod/Queue?entryId={ids["rmv"]}"));
            Assert(!tvQueue.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("grabId").AsGuid() == rmvId),
                "The pack's row is hidden from them");
            var readable = rmvChildren[ids["rmvs1e1"]].Id;
            Assert((await tvAdmin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{readable}")
                    { Content = JsonContent.Create(new { blocklist = true }) })).StatusCode == HttpStatusCode.NotFound &&
                (await tvAdmin.PostAsync($"/JellyfinMod/Imports/{rmvChildren[ids["rmvs1e3"]].Id}/Retry", null)).StatusCode == HttpStatusCode.NotFound,
                "Remove and Retry through an episode they can read are refused: the pack claims one they cannot (finding 10)");
            await using var database = new ModDbContext(dbPath);
            Assert(await database.ImportOperations.CountAsync(value => value.GrabId == rmvId && value.State == ImportStates.Cancelled) == 0 &&
                !await database.ReleaseBlocklist.AnyAsync(value => value.InfoHash == rmvPack.InfoHash),
                "Nothing of the pack was removed or blocklisted by them");
        }
        finally
        {
            hiddenItem.Tags = [];
            world.RestrictedAdmin.SetPreference(PreferenceKind.BlockedTags, []);
        }

        var removed = await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{rmvChildren[ids["rmvs1e1"]].Id}")
            { Content = JsonContent.Create(new { }) }), 200, "Administrator removes the pack through a seeding episode");
        await using (var database = new ModDbContext(dbPath))
        {
            var children = await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == rmvId).ToListAsync();
            var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(value => value.GrabId == rmvId).ToListAsync();
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == rmvId);
            Assert(children.Single(child => child.EpisodeId == ids["rmvs1e3"]).State == ImportStates.Cancelled &&
                seeds.Count == 2 && seeds.All(seed => seed.State == SeedReleaseStates.Cancelled) && grab.ActiveHash is null &&
                grab.ActiveTarget is null && removed.GetProperty("id").AsGuid() == rmvChildren[ids["rmvs1e1"]].Id,
                "Removing the pack cancels its failed episode and every seed release of it (finding 9): " +
                string.Join(" | ", children.Select(child => $"{child.State} {child.Reason}")) + " / " +
                string.Join(" | ", seeds.Select(seed => seed.State)));
        }

        var after = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Queue?entryId={ids["rmv"]}"));
        Assert(!after.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("grabId").AsGuid() == rmvId),
            "No row of the removed pack is left in the queue: " + after.GetRawText());
        // ---------------- A file lost while an add pack fetches its torrent (re-review, finding 3): Race Show's S01E01 is held when
        // the pack is planned and loses its file while the torrent downloads. The claim is read again under the library lock, so
        // S01E01 is claimed under its own key, to acquire it, and a single-episode grab of it is refused while the pack holds it.
        var raceE1 = ids["races1e1"];
        var heldWhenFetched = false;
        torznab.OnDownload = async id =>
        {
            if (id != "racepack") return;
            await using var database = new ModDbContext(dbPath);
            heldWhenFetched = await database.Episodes.AnyAsync(value => value.Id == raceE1 && value.State == FileState.OnDisk);
            await database.Episodes.Where(value => value.Id == raceE1).ExecuteUpdateAsync(set => set.SetProperty(value => value.State, FileState.None));
        };
        Guid raceId;
        try
        {
            raceId = await GrabPackAsync("race", "scope=season&seasonNumber=1", "Race.Show.S01.1080p.WEB-DL-GRP", "add", "add");
        }
        finally
        {
            torznab.OnDownload = null;
        }

        await using (var database = new ModDbContext(dbPath))
        {
            var claims = await database.GrabClaims.AsNoTracking().Where(claim => claim.GrabId == raceId).ToDictionaryAsync(claim => claim.EpisodeId);
            var children = await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == raceId).ToListAsync();
            Assert(heldWhenFetched && claims.Count == 2 && claims[raceE1].ActiveKey == raceE1.ToString("N") && !claims[raceE1].Held &&
                children.Single(child => child.EpisodeId == raceE1).Intent == GrabIntents.Acquire,
                "An episode that lost its file while the pack was fetched is claimed under its own key, to acquire it (finding 3): " +
                string.Join(" | ", claims.Values.Select(claim => $"{claim.ActiveKey} {claim.Held}")));
        }

        var raceSearch = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["race"]}&episodeId={raceE1}"), 200,
            "Race Show S01E01 search while the add pack holds it");
        var raceSingle = raceSearch.GetProperty("candidates").EnumerateArray()
            .Single(item => item.GetProperty("rawTitle").GetString() == "Race.Show.S01E01.1080p.WEB-DL-GRP");
        var raceDuplicate = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = raceSearch.GetProperty("searchId").AsGuid(), releaseId = raceSingle.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5r-race-" + Guid.NewGuid().ToString("N")
        });
        var raceDuplicateBody = await raceDuplicate.Content.ReadAsStringAsync();
        Assert(raceDuplicate.StatusCode == HttpStatusCode.Conflict && raceDuplicateBody.Contains("grab_active", StringComparison.Ordinal),
            "A single-episode grab of the episode the add pack now acquires is refused (finding 3): " +
            $"{(int)raceDuplicate.StatusCode} {raceDuplicateBody}");

        var raceChild = (await ChildrenAsync(raceId)).First();
        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{raceChild.Id}")
            { Content = JsonContent.Create(new { }) }), 200, "Administrator removes the Race Show pack");
        await using (var database = new ModDbContext(dbPath))
        {
            await database.Episodes.Where(value => value.Id == raceE1).ExecuteUpdateAsync(set => set.SetProperty(value => value.State, FileState.OnDisk));
            Assert(await database.GrabClaims.Where(claim => claim.GrabId == raceId).AllAsync(claim => claim.ActiveKey == null) &&
                (await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == raceId)).ActiveTarget is null,
                "The removed pack holds nothing");
        }

        // ---------------- Another version from a search made while the episode held its file (re-review 2, P2 2). Race Show's
        // S01E01 is held when the Add search runs and loses its file before the grab: the grab is refused whatever its first read
        // saw, so no "+add" claim stands beside nothing, and a plain acquire of the episode is not blocked by one. The same holds
        // when the file goes while the torrent is fetched, the case the grab's locked read catches.
        var raceE1Torrent = TorrentFixture.Single("Race.Show.S01E01.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["racee1"] = raceE1Torrent.Bytes;
        transmission.Register(raceE1Torrent);
        async Task<(Guid SearchId, string ReleaseId)> RaceE1SearchAsync(string intent)
        {
            var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["race"]}&episodeId={raceE1}&intent={intent}"),
                200, $"Race Show S01E01 {intent} search");
            var row = search.GetProperty("candidates").EnumerateArray()
                .Single(item => item.GetProperty("rawTitle").GetString() == "Race.Show.S01E01.1080p.WEB-DL-GRP");
            Assert(row.GetProperty("eligible").GetBoolean() && search.GetProperty("grab").GetProperty("available").GetBoolean(),
                $"The {intent} search offers the single-episode release: " + search.GetRawText());
            return (search.GetProperty("searchId").AsGuid(), row.GetProperty("releaseId").GetString()!);
        }

        async Task SetRaceE1Async(FileState state)
        {
            await using var database = new ModDbContext(dbPath);
            await database.Episodes.Where(value => value.Id == raceE1).ExecuteUpdateAsync(set => set.SetProperty(value => value.State, state));
        }

        async Task ExpectStaleAsync((Guid SearchId, string ReleaseId) search, string message)
        {
            using var response = await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.SearchId, releaseId = search.ReleaseId, idempotencyKey = "p5r-stale-" + Guid.NewGuid().ToString("N")
            });
            var body = await response.Content.ReadAsStringAsync();
            await using var database = new ModDbContext(dbPath);
            var versionKey = GrabService.ClaimKey(raceE1, true);
            Assert(response.StatusCode == HttpStatusCode.Conflict && body.Contains("\"search_stale\"", StringComparison.Ordinal) &&
                !await database.GrabClaims.AnyAsync(claim => claim.ActiveKey == versionKey) &&
                !await database.GrabOperations.AnyAsync(value => value.ActiveTarget == versionKey),
                message + $": {(int)response.StatusCode} {body}");
        }

        var cachedAdd = await RaceE1SearchAsync(GrabIntents.AddVersion);
        await SetRaceE1Async(FileState.None);
        await ExpectStaleAsync(cachedAdd, "Another version grabbed from a search made before the episode lost its file is refused");

        await SetRaceE1Async(FileState.OnDisk);
        var fetchedAdd = await RaceE1SearchAsync(GrabIntents.AddVersion);
        var lostWhileFetched = false;
        torznab.OnDownload = async id =>
        {
            if (id != "racee1") return;
            await using var database = new ModDbContext(dbPath);
            lostWhileFetched = await database.Episodes.AnyAsync(value => value.Id == raceE1 && value.State == FileState.OnDisk);
            await database.Episodes.Where(value => value.Id == raceE1).ExecuteUpdateAsync(set => set.SetProperty(value => value.State, FileState.None));
        };
        try
        {
            await ExpectStaleAsync(fetchedAdd, "Another version whose episode lost its file while the torrent was fetched is refused");
        }
        finally
        {
            torznab.OnDownload = null;
        }

        Assert(lostWhileFetched, "The episode still held its file when the grab started fetching the torrent");
        var acquire = await RaceE1SearchAsync(GrabIntents.Acquire);
        var acquired = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = acquire.SearchId, releaseId = acquire.ReleaseId, idempotencyKey = "p5r-race-acquire-" + Guid.NewGuid().ToString("N")
        }), 202, "A plain acquire of the episode that lost its file is not blocked by a version claim");
        var acquiredId = acquired.GetProperty("id").AsGuid();
        await using (var database = new ModDbContext(dbPath))
        {
            var claim = await database.GrabClaims.AsNoTracking().SingleAsync(value => value.GrabId == acquiredId);
            Assert(claim.EpisodeId == raceE1 && claim.ActiveKey == raceE1.ToString("N") && !claim.Held,
                "The acquire claims the episode under its own key: " + $"{claim.ActiveKey} {claim.Held}");
        }

        await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{acquiredId}")).GetProperty("state").GetString() ==
            "accepted" && (await ChildrenAsync(acquiredId)).Count > 0 ? true : (bool?)null, "The Race Show S01E01 acquire is accepted");
        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{(await ChildrenAsync(acquiredId)).First().Id}")
            { Content = JsonContent.Create(new { }) }), 200, "Administrator removes the Race Show S01E01 acquire");
        await SetRaceE1Async(FileState.OnDisk);
        await using (var database = new ModDbContext(dbPath))
            Assert((await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == acquiredId)).ActiveTarget is null &&
                await database.GrabClaims.Where(claim => claim.GrabId == acquiredId).AllAsync(claim => claim.ActiveKey == null),
                "The removed acquire holds nothing");

        world.Native.EpisodeTmdbId = null;
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);

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
}
