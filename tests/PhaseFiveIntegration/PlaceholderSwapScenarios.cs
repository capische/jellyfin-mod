using System.Net.Http.Json;
using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Import;
using JellyfinMod.Services.Placeholders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The import swaps a placeholder for the real file (P1.FU25), through the same real host, HTTP, SQLite, Torznab and Transmission
/// boundaries and real files as the rest of Phase 5. A held series with the plugin's stubs: a season pack swaps several stubs at
/// once, one of them in the series folder rather than the season folder the import uses (where a reclaim leaves it); a sync while
/// an episode downloads writes no stub for it; an import that blocks before its link leaves the stub as it was; a stub the import
/// cannot remove (a read-only folder) leaves the import complete, is logged once and goes with the next sync. Every swapped episode
/// is on disk once, with no placeholder item left beside it and one import event.
/// </summary>
internal static class PlaceholderSwapScenarios
{
    private const long Size = 300_000;

    public static async Task RunAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission, PluginHost host, CapturingLoggerProvider logs)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 650, Title = "Stub Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 650, "Stub Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 750, false, null, 100, [], [],
                    [new TmdbSeason(1, "Season 1", 4, null, null), new TmdbSeason(2, "Season 2", 3, null, null)]))
            };
            database.Entries.Add(entry);
            ids["stub"] = entry.Id;
            foreach (var (season, count) in new[] { (1, 4), (2, 3) })
                for (var number = 1; number <= count; number++)
                {
                    var row = new Episode
                    {
                        EntryId = entry.Id, TmdbId = 65000 + season * 10 + number, SeasonNumber = season, EpisodeNumber = number,
                        Title = $"Stub {season}x{number}", RuntimeMinutes = 30, AirDate = new DateTime(2022, season + 2, number, 0, 0, 0, DateTimeKind.Utc)
                    };
                    database.Episodes.Add(row);
                    ids[$"stub{season}x{number}"] = row.Id;
                }

            await database.SaveChangesAsync();
        }

        // Stub Show holds S01E01; the plugin's stubs stand for S01E03, S01E04 (in the series folder, where a reclaim leaves one),
        // S02E01 and S02E02 (in Season 02) and S02E03 (in the series folder). S01E02 has none.
        var seriesFolder = Path.Combine(world.Tv.Location, "Stub Show (2022) [tmdbid-650]");
        var seasonOne = Path.Combine(seriesFolder, "Season 01");
        var seasonTwo = Path.Combine(seriesFolder, "Season 02");
        Directory.CreateDirectory(seasonOne);
        Directory.CreateDirectory(seasonTwo);
        await File.WriteAllBytesAsync(Path.Combine(seasonOne, "Stub Show S01E01.mkv"), new byte[4096]);
        string Stub(string folder, int season, int number) => Path.Combine(folder, $"Stub Show (2022) S{season:00}E{number:00}.disc");
        var stubs = new Dictionary<string, string>
        {
            ["1x3"] = Stub(seasonOne, 1, 3), ["1x4"] = Stub(seriesFolder, 1, 4),
            ["2x1"] = Stub(seasonTwo, 2, 1), ["2x2"] = Stub(seasonTwo, 2, 2), ["2x3"] = Stub(seriesFolder, 2, 3)
        };
        foreach (var (key, path) in stubs)
        {
            var numbers = key.Split('x').Select(int.Parse).ToArray();
            await File.WriteAllBytesAsync(path, PlaceholderStub.Body("Stub Show", numbers[0], numbers[1], $"Stub {key}"));
        }

        world.Native.Scan(seriesFolder);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return (await database.Episodes.SingleAsync(episode => episode.Id == ids["stub1x1"])).State == FileState.OnDisk &&
                (await database.Entries.SingleAsync(entry => entry.Id == ids["stub"])).JellyfinItemId is not null ? true : null;
        }, "Stub Show's held episode is bound");
        MediaBrowser.Controller.Entities.TV.Episode[] NativeEpisodes(int season, int number) => world.Native.Items
            .OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Where(item => item.Path is not null && item.Path.StartsWith(seriesFolder + "/", StringComparison.Ordinal) &&
                item.ParentIndexNumber == season && item.IndexNumber == number).ToArray();
        Assert(stubs.Keys.All(key => NativeEpisodes(int.Parse(key[..1]), int.Parse(key[2..])) is [{ IsPlaceHolder: true }]),
            "Each stub is a placeholder episode in the library (Jellyfin's IsPlaceHolder)");
        await using (var database = new ModDbContext(dbPath))
            Assert(await database.Episodes.CountAsync(episode => episode.EntryId == ids["stub"] && episode.State == FileState.OnDisk) == 1,
                "Placeholders are never read as files: only S01E01 is on disk");

        TorrentFixture Release(string key, string name, params (string File, long Length)[] files)
        {
            var fixture = files.Length == 0 ? TorrentFixture.Single(name + ".mkv", Size) : TorrentFixture.Multi(name, files);
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.TvItems.Add(new(name, "guid-" + key, torznab.Download(key), Size * Math.Max(1, files.Length), 25, new() { ["tvdbid"] = "750" }));
            return fixture;
        }

        async Task<Guid> GrabAsync(string query, string rawTitle)
        {
            var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["stub"]}&{query}"), 200, "Search " + rawTitle);
            var row = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == rawTitle);
            Assert(row.GetProperty("eligible").GetBoolean(), "The release is eligible: " + row.GetRawText());
            var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
            {
                searchId = search.GetProperty("searchId").AsGuid(), releaseId = row.GetProperty("releaseId").GetString(),
                idempotencyKey = "p5s-" + Guid.NewGuid().ToString("N")
            }), 202, "Administrator grabs " + rawTitle);
            var id = grab.GetProperty("id").AsGuid();
            await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{id}")).GetProperty("state").GetString() ==
                "accepted" ? true : (bool?)null, rawTitle + " is accepted");
            return id;
        }

        async Task<Dictionary<Guid, ImportOperation>> ImportsAsync(Guid grabId)
        {
            await using var database = new ModDbContext(dbPath);
            return await database.ImportOperations.AsNoTracking().Where(value => value.GrabId == grabId && value.EpisodeId != null)
                .ToDictionaryAsync(value => value.EpisodeId!.Value);
        }

        async Task<Dictionary<Guid, JsonElement>> DetailAsync()
        {
            var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["stub"]}"));
            return detail.GetProperty("episodes").EnumerateArray().ToDictionary(item => item.GetProperty("id").AsGuid());
        }

        // An episode the swap is done for: its file at the import's path, no stub of it anywhere in the series, one library item for
        // it and no placeholder, on disk in the entry detail (what the web reads) with no placeholder item, imported once.
        async Task SwappedAsync(string key, string file, string message)
        {
            var (season, number) = (int.Parse(key[..1]), int.Parse(key[2..]));
            var episodeId = ids["stub" + key];
            await WaitAsync(async () =>
            {
                await tick();
                return NativeEpisodes(season, number) is [{ IsPlaceHolder: false }] ? true : null;
            }, message + ": the placeholder item is gone and the file's item is there");
            var row = (await DetailAsync())[episodeId];
            await using var database = new ModDbContext(dbPath);
            var episode = await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == episodeId);
            var imported = await database.History.AsNoTracking().CountAsync(history => history.EntryId == ids["stub"] &&
                history.EventType == "imported" && history.Data!.Contains(episodeId.ToString()));
            var leftovers = Directory.EnumerateFiles(seriesFolder, "*.disc", SearchOption.AllDirectories)
                .Where(path => PlaceholderStub.IsStubFile(path) && path.Contains($"S{season:00}E{number:00}", StringComparison.Ordinal)).ToArray();
            Assert(File.Exists(file) && leftovers.Length == 0 && episode.State == FileState.OnDisk && episode.JellyfinItemId is not null &&
                row.GetProperty("availability").GetString() == "onDisk" && !row.TryGetProperty("placeholderItemId", out _) && imported == 1,
                $"{message}: file {File.Exists(file)}, stubs left {string.Join(", ", leftovers)}, state {episode.State}, " +
                $"detail {row.GetRawText()}, imported events {imported}");
        }

        // ---------------- 1. A season pack swaps several stubs at once, one of them outside the season folder the import uses.
        var pack = Release("stubpack", "Stub.Show.S02.1080p.WEB-DL-GRP",
            ("Stub.Show.S02E01.1080p.WEB-DL-GRP.mkv", Size), ("Stub.Show.S02E02.1080p.WEB-DL-GRP.mkv", Size),
            ("Stub.Show.S02E03.1080p.WEB-DL-GRP.mkv", Size));
        var scansBefore = world.Native.ScanRequests.Count;
        var packGrab = await GrabAsync("scope=season&seasonNumber=2", "Stub.Show.S02.1080p.WEB-DL-GRP");
        transmission.Progress(pack.InfoHash, 0.5);
        await tick();
        Assert(stubs.Values.All(File.Exists), "While the pack downloads, every stub stays");
        transmission.Progress(pack.InfoHash, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            var imports = await ImportsAsync(packGrab);
            return imports.Count == 3 && imports.Values.All(value => value.State == ImportStates.Completed) ? true : null;
        }, "Every episode of the pack is imported", 60);
        for (var number = 1; number <= 3; number++)
            await SwappedAsync($"2x{number}", Path.Combine(seasonTwo, $"Stub Show (2022) S02E0{number}.mkv"),
                $"The pack swaps S02E0{number}'s placeholder for its file");
        Assert(world.Native.ScanRequests.Skip(scansBefore).Contains(seriesFolder),
            "The series folder that held S02E03's stub is reported to Jellyfin as well as the season folder");
        Assert(File.Exists(stubs["1x3"]) && File.Exists(stubs["1x4"]) && NativeEpisodes(1, 4) is [{ IsPlaceHolder: true }],
            "Stubs of other episodes, in the same folders, stay");

        // ---------------- 2. A sync while an episode downloads writes no stub for it (an import is on its way).
        var single = Release("stub1x2", "Stub.Show.S01E02.1080p.WEB-DL-GRP");
        var singleGrab = await GrabAsync($"episodeId={ids["stub1x2"]}", "Stub.Show.S01E02.1080p.WEB-DL-GRP");
        transmission.Progress(single.InfoHash, 0.3);
        await tick();
        using (var scope = host.App.Services.CreateScope())
        {
            var placeholders = scope.ServiceProvider.GetRequiredService<PlaceholderService>();
            var plan = await placeholders.PlanAsync(world.Tv.Id, default);
            Assert(!plan.Writes.Any(write => write.EntryId == ids["stub"]) &&
                plan.Skipped.Any(skip => skip.EntryId == ids["stub"] && skip.SeasonNumber == 1 && skip.Reason == "importing") &&
                plan.Removals.All(removal => !removal.Path.StartsWith(seriesFolder + "/", StringComparison.Ordinal)),
                "The dry run writes nothing for S01E02 while it downloads and says why; it removes none of Stub Show's stubs: " +
                JsonSerializer.Serialize(plan));
            var synced = await placeholders.SyncAsync(world.Tv.Id, default);
            Assert(!synced.Writes.Any(write => write.EntryId == ids["stub"]) && !Directory.EnumerateFiles(seriesFolder, "*S01E02*.disc",
                SearchOption.AllDirectories).Any(),
                "A sync while S01E02 downloads writes no stub for it: " + JsonSerializer.Serialize(synced.Writes));
        }

        transmission.Progress(single.InfoHash, 1.0);
        await WaitAsync(async () =>
        {
            await tick();
            return (await ImportsAsync(singleGrab)).Values.Single().State == ImportStates.Completed ? true : null;
        }, "S01E02 is imported");
        await SwappedAsync("1x2", Path.Combine(seasonOne, "Stub Show (2022) S01E02.mkv"), "S01E02 arrives with no stub ever beside it");

        // ---------------- 3. An import that stops before its link changes nothing: the stub stays as it was.
        var stubBytes = await File.ReadAllBytesAsync(stubs["1x3"]);
        var mismatch = Release("stub1x3", "Stub.Show.S01E03.1080p.WEB-DL-GRP", ("Stub.Show.S01E09.1080p.WEB-DL-GRP.mkv", Size));
        var mismatchGrab = await GrabAsync($"episodeId={ids["stub1x3"]}", "Stub.Show.S01E03.1080p.WEB-DL-GRP");
        transmission.Progress(mismatch.InfoHash, 1.0);
        var blocked = await WaitAsync(async () =>
        {
            await tick();
            var operation = (await ImportsAsync(mismatchGrab)).Values.Single();
            return operation.State == ImportStates.Blocked ? operation : null;
        }, "The mismatched download blocks");
        Assert(blocked.Reason == ImportReasons.EpisodeMismatch && (await File.ReadAllBytesAsync(stubs["1x3"])).SequenceEqual(stubBytes) &&
            NativeEpisodes(1, 3) is [{ IsPlaceHolder: true }] && (await DetailAsync())[ids["stub1x3"]].GetProperty("availability").GetString() == "missing",
            $"An import blocked before its link leaves the stub byte for byte and the episode missing ({blocked.Reason})");

        // ---------------- 4. A stub the import cannot remove: the import still completes, the failure is logged once, the sync removes it.
        var single4 = Release("stub1x4", "Stub.Show.S01E04.1080p.WEB-DL-GRP");
        var grab4 = await GrabAsync($"episodeId={ids["stub1x4"]}", "Stub.Show.S01E04.1080p.WEB-DL-GRP");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Phase 5 runs on Linux");
        File.SetUnixFileMode(seriesFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        try
        {
            transmission.Progress(single4.InfoHash, 1.0);
            await WaitAsync(async () =>
            {
                await tick();
                return (await ImportsAsync(grab4)).Values.Single().State == ImportStates.Completed ? true : null;
            }, "S01E04 is imported although its stub cannot be removed");
            var warnings = logs.Lines.Count(line => line.StartsWith("Warning", StringComparison.Ordinal) &&
                line.Contains("could not remove the placeholder", StringComparison.Ordinal) && line.Contains("S01E04", StringComparison.Ordinal));
            Assert(File.Exists(Path.Combine(seasonOne, "Stub Show (2022) S01E04.mkv")) && File.Exists(stubs["1x4"]) && warnings == 1,
                $"The import completes, the stub in the read-only folder stays, and the failure is logged once ({warnings})");
        }
        finally
        {
            File.SetUnixFileMode(seriesFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        using (var scope = host.App.Services.CreateScope())
        {
            var synced = await scope.ServiceProvider.GetRequiredService<PlaceholderService>().SyncAsync(world.Tv.Id, default);
            Assert(synced.Removals.Any(removal => removal.Path == stubs["1x4"] && removal.Reason == "file_present") && !File.Exists(stubs["1x4"]) &&
                File.Exists(stubs["1x3"]) && !synced.Writes.Any(write => write.EntryId == ids["stub"]),
                "Once the folder can be written, the next sync removes the leftover stub, and only it: " + JsonSerializer.Serialize(synced));
        }

        await SwappedAsync("1x4", Path.Combine(seasonOne, "Stub Show (2022) S01E04.mkv"), "S01E04 ends on disk with no stub");
        await using (var database = new ModDbContext(dbPath))
            Assert(!await database.History.AnyAsync(history => history.EntryId == ids["stub"] &&
                    (history.EventType == "reclaimed" || history.EventType == "media_missing" || history.EventType == "episode_media_missing")),
                "No swap reads as media lost or reclaimed in the history");
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);

    private static Task<T> WaitAsync<T>(Func<Task<T?>> probe, string message, int seconds = 30) where T : class => Phase5.WaitAsync(probe, message, seconds);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return Json.Parse(body);
    }
}
