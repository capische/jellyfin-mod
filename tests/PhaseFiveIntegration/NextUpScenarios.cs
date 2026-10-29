using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Next-up priority (design next-up-priority-download.md, P5.FU4), through the same real host, HTTP, SQLite, Torznab and
/// Transmission boundaries and real files as the rest of Phase 5: the first missing episode and each viewer's next one are set
/// to download first through the client's own priority calls (exact torrent-set bodies, never an empty file list, only a
/// difference, only torrents the plugin added), rolling forward as episodes finish and following each viewer's played state
/// in the simulated library; a pack episode is imported as soon as its own file is complete, once, with the rest of the pack
/// imported and detached at completion; a file the client keeps in its incomplete folder waits for the torrent; a recheck
/// before linking sends the episode back to waiting; a magnet is ordered once its files are known; a pack removed mid-download
/// leaves the early episode's library file in place.
/// </summary>
internal static class NextUpScenarios
{
    private const long Size = 300_000;

    public static async Task RunAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission, PluginHost host, ShiftedTimeProvider time)
    {
        // Jellyfin's metadata gives these shows' files their TMDB ids by number, so their bindings are verified.
        var numbered = new Regex(@"S(\d{2})E(\d{2})", RegexOptions.IgnoreCase);
        var folderTmdb = new Regex(@"\[tmdbid-(6[1-4]0)\]");
        world.Native.EpisodeTmdbId = file =>
            folderTmdb.Match(file) is { Success: true } show && numbered.Match(Path.GetFileName(file)) is { Success: true } number
                ? int.Parse(show.Groups[1].Value, CultureInfo.InvariantCulture) * 100 +
                    int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) * 10 + int.Parse(number.Groups[2].Value, CultureInfo.InvariantCulture)
                : null;

        await using (var database = new ModDbContext(dbPath))
        {
            void Show(string key, int tmdb, string title, int episodes)
            {
                var entry = new Entry
                {
                    MediaType = "series", TmdbId = tmdb, Title = title, Year = 2022, TargetLibraryId = world.Tv.Id,
                    MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", tmdb, title, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        null, null, null, null, tmdb + 100, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", episodes, null, null)]))
                };
                database.Entries.Add(entry);
                ids[key] = entry.Id;
                for (var episode = 1; episode <= episodes; episode++)
                {
                    var row = new Episode
                    {
                        EntryId = entry.Id, TmdbId = tmdb * 100 + 10 + episode, SeasonNumber = 1, EpisodeNumber = episode,
                        Title = $"{title} 1x{episode}", RuntimeMinutes = 45, AirDate = new DateTime(2022, 3, episode, 0, 0, 0, DateTimeKind.Utc)
                    };
                    database.Episodes.Add(row);
                    ids[$"{key}e{episode}"] = row.Id;
                }
            }

            Show("nxfirst", 610, "First Show", 5);
            Show("nxview", 620, "Viewer Show", 7);
            Show("nxfold", 630, "Folder Show", 2);
            Show("nxsingle", 640, "Single Show", 3);
            await database.SaveChangesAsync();
        }

        // Viewer Show holds S01E01, S01E03 and S01E05.
        var viewerFolder = Path.Combine(world.Tv.Location, "Viewer Show (2022) [tmdbid-620]", "Season 01");
        Directory.CreateDirectory(viewerFolder);
        foreach (var number in new[] { 1, 3, 5 })
            await File.WriteAllBytesAsync(Path.Combine(viewerFolder, $"Viewer Show S01E0{number}.mkv"), RandomNumberGenerator.GetBytes(4096));
        world.Native.Scan(Path.GetDirectoryName(viewerFolder)!);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            var held = new[] { ids["nxviewe1"], ids["nxviewe3"], ids["nxviewe5"] };
            return await database.Episodes.CountAsync(episode => held.Contains(episode.Id) && episode.State == FileState.OnDisk) == 3 &&
                (await database.Entries.SingleAsync(entry => entry.Id == ids["nxview"])).JellyfinItemId is not null ? true : null;
        }, "Viewer Show's held episodes are bound");

        TorrentFixture Pack(string key, string name, string tvdb, params string[] files)
        {
            var fixture = TorrentFixture.Multi(name, files.Select(file => (file, Size)).ToArray());
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(name, "guid-" + key, torznab.Download(key), Size * files.Length, 25, new() { ["tvdbid"] = tvdb }));
            return fixture;
        }

        string[] Files(string show, int count) => Enumerable.Range(1, count).Select(number => $"{show}.S01E0{number}.1080p.WEB-DL-GRP.mkv").ToArray();
        var firstPack = Pack("nxfirstpack", "First.Show.S01.1080p.WEB-DL-GRP", "710", Files("First.Show", 5));
        var viewerPack = Pack("nxviewpack", "Viewer.Show.S01.1080p.WEB-DL-GRP", "720", Files("Viewer.Show", 7));
        var folderPack = Pack("nxfoldpack", "Folder.Show.S01.1080p.WEB-DL-GRP", "730", Files("Folder.Show", 2));
        var singles = Enumerable.Range(1, 3).Select(number =>
        {
            var fixture = TorrentFixture.Single($"Single.Show.S01E0{number}.1080p.WEB-DL-GRP.mkv", Size);
            torznab.Torrents["nxsingle" + number] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(fixture.Name[..^4], "guid-nxsingle" + number, torznab.Download("nxsingle" + number), Size, 25,
                    new() { ["tvdbid"] = "740" }));
            return fixture;
        }).ToArray();

        async Task<Guid> GrabAsync(string key, string query, string rawTitle, string? mode = null)
        {
            var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids[key]}&{query}"), 200, "Search " + rawTitle);
            var row = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == rawTitle);
            Assert(row.GetProperty("eligible").GetBoolean(), "The release is eligible: " + row.GetRawText());
            var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = row.GetProperty("releaseId").GetString(),
                idempotencyKey = "p5n-" + Guid.NewGuid().ToString("N"), mode
            }), 202, "Administrator grabs " + rawTitle);
            var id = grab.GetProperty("id").AsGuid();
            await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{id}")).GetProperty("state").GetString() ==
                "accepted" ? true : (bool?)null, rawTitle + " is accepted");
            return id;
        }

        async Task<Dictionary<Guid, ImportOperation>> ChildrenAsync(Guid grabId)
        {
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId && value.EpisodeId != null)
                .ToDictionaryAsync(value => value.EpisodeId!.Value);
        }

        // The next order write for a torrent after the ones already seen; the monitor ticks by itself every second as well.
        async Task<JsonObject> NextWriteAsync(string hash, int seen, string message)
        {
            await WaitAsync(async () =>
            {
                await tick();
                return transmission.OrderWrites(hash).Count > seen ? true : null;
            }, message);
            return transmission.OrderWrites(hash)[seen].Arguments;
        }

        int[] Priorities(string hash) => transmission.Torrents[hash].Files.Select(file => file.Priority).ToArray();
        // The exact arguments the driver sends: the torrent, then only the lists and the priority it changes.
        string Body(string hash, int[]? high = null, int[]? normal = null, int? bandwidth = null)
        {
            var body = new JsonObject { ["ids"] = new JsonArray((JsonNode)hash) };
            if (high is not null) body["priority-high"] = new JsonArray(high.Select(index => (JsonNode)index).ToArray());
            if (normal is not null) body["priority-normal"] = new JsonArray(normal.Select(index => (JsonNode)index).ToArray());
            if (bandwidth is not null) body["bandwidthPriority"] = bandwidth;
            return body.ToJsonString();
        }

        // ---------------- 1. A season pack with nothing held and no history: only S01E01 goes first, set once.
        var firstHash = firstPack.InfoHash;
        var firstId = await GrabAsync("nxfirst", "scope=season&seasonNumber=1", "First.Show.S01.1080p.WEB-DL-GRP");
        var firstWrite = await NextWriteAsync(firstHash, 0, "The first pack's order is set");
        Assert(firstWrite.ToJsonString() == Body(firstHash, high: [0]) && Priorities(firstHash).SequenceEqual([1, 0, 0, 0, 0]),
            "One torrent-set makes only S01E01 high: " + firstWrite.ToJsonString());
        for (var index = 0; index < 3; index++) await tick();
        Assert(transmission.OrderWrites(firstHash).Count == 1, "Nothing is sent again while nothing changes");

        // ---------------- 7. A recheck before linking: S01E01 was seen complete and is identifying, but the client now reports
        // its file incomplete. It goes back to waiting, and nothing is linked.
        transmission.ProgressFile(firstHash, 0, 0.9);
        transmission.ProgressFile(firstHash, 1, 0.4);
        var firstE1 = (await ChildrenAsync(firstId))[ids["nxfirste1"]].Id;
        await using (await host.Service<JellyfinMod.Services.Import.ImportTickGate>().AcquireAsync(CancellationToken.None))
        await using (var database = new ModDbContext(dbPath))
        {
            var child = await database.ImportOperations.SingleAsync(value => value.Id == firstE1);
            child.State = ImportStates.Identifying;
            child.CompletedDownloadAt = time.GetUtcNow().UtcDateTime;
            await database.SaveChangesAsync();
        }

        await tick();
        var rechecked = (await ChildrenAsync(firstId))[ids["nxfirste1"]];
        Assert(rechecked.State == ImportStates.Waiting && rechecked.CompletedDownloadAt is null && rechecked.DestinationPath is null &&
            rechecked.Reason is null, $"A recheck before linking sends the episode back to waiting: {rechecked.State} {rechecked.Reason}");

        // ---------------- 1 (continued). S01E01's file completes while the torrent downloads: it is imported now, as a hardlink of
        // the torrent's file, once; S01E02 becomes the one to download first.
        transmission.ProgressFile(firstHash, 0, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            return (await ChildrenAsync(firstId))[ids["nxfirste1"]].State == ImportStates.Completed ? true : null;
        }, "S01E01 is imported while the pack downloads");
        var early = (await ChildrenAsync(firstId))[ids["nxfirste1"]];
        var inspector = new UnixFileInspector();
        Assert(transmission.Torrents[firstHash].Left > 0 && inspector.TryInspect(early.DestinationPath!, out var linked) &&
            inspector.TryInspect(transmission.Local("/data/torrents/jfmod/" + firstPack.Files[0].Path), out var seeding) &&
            linked.PhysicalIdentity == seeding.PhysicalIdentity && linked.HardlinkCount == 2,
            "The early episode is a hardlink of the torrent's file, imported before the torrent completed");
        await using (var database = new ModDbContext(dbPath))
        {
            var episode = await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == ids["nxfirste1"]);
            Assert(episode.State == FileState.OnDisk &&
                await database.History.CountAsync(history => history.EventType == "imported" && history.Id == early.Id) == 1,
                "The early episode is in the library with one imported event");
        }

        var secondWrite = await NextWriteAsync(firstHash, 1, "The order rolls forward to S01E02");
        Assert(secondWrite.ToJsonString() == Body(firstHash, high: [1], normal: [0]) && Priorities(firstHash).SequenceEqual([0, 1, 0, 0, 0]),
            "S01E02 goes first once S01E01 is in: " + secondWrite.ToJsonString());

        // The queue keeps one pack row for the whole torrent and shows each episode's own progress.
        var queue = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Queue?entryId={ids["nxfirst"]}"));
        var packRow = queue.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("grabId").AsGuid() == firstId);
        var episodes = packRow.GetProperty("pack").GetProperty("episodes").EnumerateArray()
            .ToDictionary(item => item.GetProperty("episodeNumber").GetInt32());
        Assert(packRow.GetProperty("state").GetString() == "downloading" && episodes[1].GetProperty("importState").GetString() == "completed" &&
            episodes[1].GetProperty("progress").GetDouble() == 1 && episodes[2].GetProperty("progress").GetDouble() == 0.4 &&
            episodes[3].GetProperty("progress").GetDouble() == 0 &&
            episodes[2].GetProperty("sizeBytes").GetInt64() == transmission.Torrents[firstHash].Files[1].Length,
            "The pack row stays downloading; each episode shows its own progress and file size: " +
            packRow.GetRawText());
        Assert(packRow.GetProperty("parsed").GetProperty("seasonPack").GetBoolean() &&
            !string.IsNullOrEmpty(packRow.GetProperty("indexerName").GetString()),
            "The pack row carries the pack's parsed release and its indexer name (P1.FU20): " + packRow.GetRawText());

        // Retention does not count the early episode's file as seeded while its torrent is incomplete: past its deadline, and with
        // the torrent's ratio above every goal, it is blocked, not due. Its seed release cannot meet a goal before the torrent is
        // complete, so retention names the unmet goal.
        transmission.Torrents[firstHash].UploadRatio = 5;
        await tick();
        JsonElement PreviewOfEarly(JsonElement preview) =>
            preview.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("bindingId").AsGuid() == early.BindingId);
        var deadline = PreviewOfEarly(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview"))).GetProperty("deadline").GetDateTime();
        var shift = DateTime.SpecifyKind(deadline, DateTimeKind.Utc) - time.GetUtcNow().UtcDateTime + TimeSpan.FromHours(1);
        time.Offset += shift;
        try
        {
            var item = PreviewOfEarly(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Retention/Preview")));
            await using var database = new ModDbContext(dbPath);
            var seed = await database.SeedReleaseOperations.AsNoTracking().SingleAsync(value => value.ImportOperationId == early.Id);
            Assert(item.GetProperty("state").GetString() == "blocked" && item.GetProperty("reason").GetString() == "seed_goal_unmet" &&
                seed.State == SeedReleaseStates.Waiting && seed.Reason == SeedReleaseReasons.Incomplete && seed.GoalMetAt is null,
                $"Retention blocks the watched early episode while its pack downloads ({seed.State} {seed.Reason}): " + item.GetRawText());
        }
        finally
        {
            time.Offset -= shift;
            transmission.Torrents[firstHash].UploadRatio = 0;
        }

        // ---------------- 5. The torrent completes later: the other episodes import, S01E01 is not imported again, and the pack's
        // seed releases detach the torrent once.
        transmission.Progress(firstHash, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            return (await ChildrenAsync(firstId)).Values.All(child => child.State == ImportStates.Completed) ? true : null;
        }, "Every episode of the first pack is imported", 60);
        await using (var database = new ModDbContext(dbPath))
        {
            var children = await ChildrenAsync(firstId);
            var imported = await database.History.AsNoTracking().Where(history => history.EventType == "imported" && history.EntryId == ids["nxfirst"])
                .Select(history => history.Id).ToListAsync();
            Assert(children.Count == 5 && imported.Count == 5 && children.Values.All(child => imported.Contains(child.Id)) &&
                children[ids["nxfirste1"]].Id == early.Id && children[ids["nxfirste1"]].CompletedAt == early.CompletedAt,
                "One imported event per episode; the early one is not imported again");
        }

        var removals = transmission.RemoveCalls;
        transmission.Torrents[firstHash].UploadRatio = 1.5;
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return await database.SeedReleaseOperations.Where(seed => seed.GrabId == firstId).CountAsync(seed => seed.State == SeedReleaseStates.Detached) == 5
                ? true : null;
        }, "The first pack's seed releases detach together");
        Assert(transmission.RemoveCalls == removals + 1 && !transmission.Torrents.ContainsKey(firstHash) &&
            (await ChildrenAsync(firstId)).Values.All(child => File.Exists(child.DestinationPath)),
            "The pack is detached once and every library file stays");

        // ---------------- 3 and 2. A magnet without metadata is not ordered; once its files are known, the first missing episode
        // and each viewer's next one go first: everyone who may see the series and has watched part of it.
        var viewerHash = viewerPack.InfoHash;
        Guid NativeOf(int number) => world.Native.Items.Single(item => item.Path == Path.Combine(viewerFolder, $"Viewer Show S01E0{number}.mkv")).Id;
        world.Native.Played[(world.Admin.Id, NativeOf(3))] = true;
        // A user without access to TV has a play record of S01E05: it never counts.
        world.Native.Played[(world.MoviesOnly.Id, NativeOf(5))] = true;
        transmission.MetadataPending[viewerHash] = true;
        var viewerId = await GrabAsync("nxview", "scope=season&seasonNumber=1", "Viewer.Show.S01.1080p.WEB-DL-GRP", "fill");
        for (var index = 0; index < 2; index++) await tick();
        var waitingChildren = await ChildrenAsync(viewerId);
        Assert(transmission.OrderWrites(viewerHash).Count == 0 && waitingChildren.Count == 4 &&
            waitingChildren.Values.All(child => child.State == ImportStates.Waiting && child.SourceClientPath is null),
            "A magnet without its files is neither ordered nor blocked");
        transmission.MetadataPending.TryRemove(viewerHash, out _);
        var viewerWrite = await NextWriteAsync(viewerHash, 0, "The viewer pack's order is set once its files are known");
        Assert(viewerWrite.ToJsonString() == Body(viewerHash, high: [1, 3]) && Priorities(viewerHash).SequenceEqual([0, 1, 0, 1, 0, 0, 0]),
            "S01E02 (first missing) and S01E04 (after the administrator's S01E03) go first; no access, no say: " + viewerWrite.ToJsonString());

        world.Native.Played[(world.Ordinary.Id, NativeOf(5))] = true;
        var ordinaryWrite = await NextWriteAsync(viewerHash, 1, "A second viewer's next episode is added");
        Assert(ordinaryWrite.ToJsonString() == Body(viewerHash, high: [5]) && Priorities(viewerHash).SequenceEqual([0, 1, 0, 1, 0, 1, 0]),
            "A second viewer who watched S01E05 adds S01E06: " + ordinaryWrite.ToJsonString());

        world.Native.Played.TryRemove((world.Admin.Id, NativeOf(3)), out _);
        var unplayedWrite = await NextWriteAsync(viewerHash, 2, "An unwatched episode drops its viewer's next");
        Assert(unplayedWrite.ToJsonString() == Body(viewerHash, normal: [3]) && Priorities(viewerHash).SequenceEqual([0, 1, 0, 0, 0, 1, 0]),
            "S01E03 marked unwatched puts S01E04 back to normal: " + unplayedWrite.ToJsonString());

        transmission.ProgressFile(viewerHash, 1, 1.0);
        var rolledWrite = await NextWriteAsync(viewerHash, 3, "The first missing episode rolls forward");
        Assert(rolledWrite.ToJsonString() == Body(viewerHash, high: [3], normal: [1]) &&
            Priorities(viewerHash).SequenceEqual([0, 0, 0, 1, 0, 1, 0]),
            "With S01E02 done, S01E04 is the first missing episode: " + rolledWrite.ToJsonString());
        await WaitAsync(async () =>
        {
            await tick();
            return (await ChildrenAsync(viewerId))[ids["nxviewe2"]].State == ImportStates.Completed ? true : null;
        }, "Viewer Show's S01E02 is imported early");

        // ---------------- 8. The pack is removed mid-download and from the client: the early episode's library file stays.
        var viewerE2 = (await ChildrenAsync(viewerId))[ids["nxviewe2"]];
        // Its files cannot be deleted from any of its rows while one episode is in the library (user, 2026-10-26).
        var viewerE4 = (await ChildrenAsync(viewerId))[ids["nxviewe4"]];
        var viewerRow = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue")).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("grabId").AsGuid() == viewerId);
        Assert(Phase5.CanDelete(viewerRow) == false, "A pack with an imported episode offers no delete (canDeleteFiles): " + viewerRow);
        using (var refused = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{viewerE4.Id}")
               { Content = JsonContent.Create(new { removeFromClient = true, deleteFiles = true }) }))
        {
            var body = await refused.Content.ReadAsStringAsync();
            Assert((int)refused.StatusCode == 409 && body.Contains("\"delete_not_allowed\"", StringComparison.Ordinal) &&
                viewerE4.State == ImportStates.Waiting && transmission.Torrents.ContainsKey(viewerHash) && File.Exists(viewerE2.DestinationPath) &&
                File.Exists(viewerE2.SourceLocalPath) && (await ChildrenAsync(viewerId))[ids["nxviewe4"]].State == ImportStates.Waiting,
                $"A pack with an imported episode is never deleted, from a waiting episode's row either: {(int)refused.StatusCode} {body}");
        }

        await ReadAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/JellyfinMod/Queue/{viewerE2.Id}")
            { Content = JsonContent.Create(new { removeFromClient = true }) }), 200, "Administrator removes the viewer pack from the client");
        await using (var database = new ModDbContext(dbPath))
        {
            var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(seed => seed.GrabId == viewerId).ToListAsync();
            Assert(!transmission.Torrents.ContainsKey(viewerHash) && File.Exists(viewerE2.DestinationPath) &&
                new[] { 1, 3, 5 }.All(number => File.Exists(Path.Combine(viewerFolder, $"Viewer Show S01E0{number}.mkv"))) &&
                (await ChildrenAsync(viewerId)).Values.Where(child => child.Id != viewerE2.Id).All(child => child.State == ImportStates.Cancelled) &&
                seeds.Count == 1 && seeds[0].State == SeedReleaseStates.Cancelled,
                "Removing a pack mid-download keeps the early episode's library file and every held file");
        }

        world.Native.Played.Clear();

        // ---------------- 6. A file complete in the client but kept in its incomplete folder: no early import, no block; it is
        // imported at completion.
        var folderHash = folderPack.InfoHash;
        var folderId = await GrabAsync("nxfold", "scope=season&seasonNumber=1", "Folder.Show.S01.1080p.WEB-DL-GRP");
        transmission.Torrents[folderHash].IncompleteDir = Path.Combine(world.Folder, "media", "downloads", "incomplete");
        transmission.ProgressFile(folderHash, 0, 1.0);
        for (var index = 0; index < 4; index++) await tick();
        var held = (await ChildrenAsync(folderId))[ids["nxfolde1"]];
        Assert(held.State == ImportStates.Waiting && held.Reason is null && held.CompletedDownloadAt is null &&
            held.SourceClientPath is not null, $"A finished file still in the incomplete folder waits for its torrent: {held.State} {held.Reason}");
        transmission.ProgressFile(folderHash, 1, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            return (await ChildrenAsync(folderId)).Values.All(child => child.State == ImportStates.Completed) ? true : null;
        }, "Folder Show imports at completion");

        // ---------------- 4. Single-episode torrents of one series: the first missing one gets the torrent priority and the top
        // of the queue; another set high by hand goes back to normal; one the plugin did not add is never touched; a lone one
        // gets no call.
        var singleIds = new List<Guid>();
        for (var number = 1; number <= 3; number++)
        {
            singleIds.Add(await GrabAsync("nxsingle", $"episodeId={ids["nxsinglee" + number]}", $"Single.Show.S01E0{number}.1080p.WEB-DL-GRP"));
            if (number == 1) transmission.Torrents[singles[0].InfoHash].Queued = true;
        }

        var (e1, e2, e3) = (singles[0].InfoHash, singles[1].InfoHash, singles[2].InfoHash);
        // S01E03 loses the plugin's owner label (an administrator relabels it) and is set high by hand; S01E02 is set high by hand.
        transmission.Torrents[e3].Labels.Remove(GrabService.OwnerLabel(singleIds[2]));
        transmission.Torrents[e3].BandwidthPriority = 1;
        var e3Writes = transmission.OrderWrites(e3).Count;
        transmission.Torrents[e2].BandwidthPriority = 1;
        await WaitAsync(async () =>
        {
            await tick();
            return transmission.Torrents[e1].BandwidthPriority == 1 && transmission.Torrents[e2].BandwidthPriority == 0 &&
                transmission.OrderWrites(e1).Any(write => write.Method == "queue-move-top") ? true : null;
        }, "The single-episode torrents are ordered");
        Assert(transmission.OrderWrites(e1).Any(write => write.Method == "torrent-set" && write.Arguments.ToJsonString() == Body(e1, bandwidth: 1)) &&
            transmission.OrderWrites(e1).Where(write => write.Method == "queue-move-top")
                .All(write => write.Arguments.ToJsonString() == new JsonObject { ["ids"] = new JsonArray((JsonNode)e1) }.ToJsonString()) &&
            transmission.OrderWrites(e2).All(write => write.Arguments.ToJsonString() == Body(e2, bandwidth: 0)) &&
            transmission.OrderWrites(e3).Count == e3Writes && transmission.Torrents[e3].BandwidthPriority == 1,
            "S01E01 goes first and to the top of the queue, S01E02 back to normal, the relabelled S01E03 is left alone");
        // At the top of the queue it stays there: queue-move-top is not sent again while it waits in the queue.
        for (var index = 0; index < 3; index++) await tick();
        Assert(transmission.Torrents[e1].QueuePosition == 0 &&
            transmission.OrderWrites(e1).Count(write => write.Method == "queue-move-top") == 1,
            "A queued torrent already first is moved to the top once: " +
            transmission.OrderWrites(e1).Count(write => write.Method == "queue-move-top"));

        transmission.Torrents[e1].Queued = false;
        transmission.Progress(e1, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AnyAsync(value => value.GrabId == singleIds[0] && value.State == ImportStates.Completed)
                ? true : null;
        }, "Single Show S01E01 is imported");
        var e2Writes = transmission.OrderWrites(e2).Count;
        for (var index = 0; index < 3; index++) await tick();
        Assert(transmission.OrderWrites(e2).Count == e2Writes && transmission.Torrents[e2].BandwidthPriority == 0,
            "A lone single-episode download gets no call");

        transmission.Torrents[e3].Labels.Add(GrabService.OwnerLabel(singleIds[2]));
        transmission.Progress(e2, 1.0);
        transmission.Progress(e3, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.CountAsync(value => singleIds.Contains(value.GrabId) && value.State == ImportStates.Completed) == 3
                ? true : null;
        }, "Single Show imports every episode");

        // ---------------- 8 (continued). No priority list the client was sent was ever empty (an empty list means every file).
        Assert(transmission.Writes.All(write => write.Arguments.Where(pair => pair.Key.StartsWith("priority-", StringComparison.Ordinal))
                .All(pair => pair.Value is JsonArray { Count: > 0 })),
            "The client never receives an empty priority list");
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
}
