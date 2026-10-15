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

        await MovieAndFilelessSeriesAsync(tick, admin, world, dbPath, ids, torznab, transmission, host);
    }

    /// <summary>
    /// P1.FU26: a movie with no file gets its placeholder where its import puts the file (<c>Title (Year) [tmdbid-N]</c> in the
    /// library folder), is never bound or called on disk for it, is found by it, and its import swaps it for the file in one step
    /// into the same folder; a series with nothing on disk gets its own folder with a placeholder for each aired episode, and
    /// removing that never-imported title from the catalog removes its placeholders and the folders made for them, and nothing else.
    /// </summary>
    private static async Task MovieAndFilelessSeriesAsync(Func<Task> tick, HttpClient admin, World world, string dbPath,
        Dictionary<string, Guid> ids, TorznabBoundary torznab, TransmissionBoundary transmission, PluginHost host)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            var movie = new Entry
            {
                MediaType = "movie", TmdbId = 651, Title = "Stub Movie", Year = 2021, ImdbId = "tt0000651", TargetLibraryId = world.Movies.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 651, "Stub Movie", new DateTime(2021, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, "tt0000651", null, false, null, 100, [], [], []))
            };
            var unreleased = new Entry
            {
                MediaType = "movie", TmdbId = 653, Title = "Future Movie", Year = 2099, TargetLibraryId = world.Movies.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 653, "Future Movie", new DateTime(2099, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, null, false, null, 100, [], [], []))
            };
            var series = new Entry
            {
                MediaType = "series", TmdbId = 652, Title = "Fileless Show", Year = 2020, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 652, "Fileless Show", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 752, false, null, 30, [], [], [new TmdbSeason(1, "Season 1", 3, null, null)]))
            };
            database.Entries.AddRange(movie, unreleased, series);
            ids["stubMovie"] = movie.Id;
            ids["futureMovie"] = unreleased.Id;
            ids["fileless"] = series.Id;
            for (var number = 1; number <= 3; number++)
                database.Episodes.Add(new Episode
                {
                    EntryId = series.Id, TmdbId = 65200 + number, SeasonNumber = 1, EpisodeNumber = number, Title = $"Fileless {number}",
                    RuntimeMinutes = 30, AirDate = number < 3 ? new DateTime(2020, 1, number, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
            await database.SaveChangesAsync();
        }

        var movieFolder = Path.Combine(world.Movies.Location, "Stub Movie (2021) [tmdbid-651]");
        var movieStub = Path.Combine(movieFolder, "Stub Movie (2021) [tmdbid-651].disc");
        var showFolder = Path.Combine(world.Tv.Location, "Fileless Show (2020) [tmdbid-652]");
        string ShowStub(int number) => Path.Combine(showFolder, "Season 01", $"Fileless Show (2020) S01E0{number}.disc");
        using (var scope = host.App.Services.CreateScope())
        {
            var placeholders = scope.ServiceProvider.GetRequiredService<PlaceholderService>();
            var moviePlan = await placeholders.PlanAsync(world.Movies.Id, default);
            Assert(moviePlan.Writes.Any(write => write.Path == movieStub && write.Kind == PlaceholderKinds.Movie && write.NewFolder) &&
                !moviePlan.Writes.Any(write => write.EntryId == ids["futureMovie"]) && !Directory.Exists(movieFolder),
                "The movie library's dry run lists the released movie's placeholder in its import folder, none for the unreleased one, " +
                "and writes nothing: " + JsonSerializer.Serialize(moviePlan.Writes));
            var synced = await placeholders.SyncAsync(world.Movies.Id, default);
            Assert(PlaceholderStub.IsStubFile(movieStub) && synced.Writes.Any(write => write.Path == movieStub) &&
                Directory.EnumerateFileSystemEntries(movieFolder).Count() == 1 &&
                !Directory.EnumerateDirectories(world.Movies.Location, "*tmdbid-653*").Any(),
                "A sync writes the movie's marked placeholder, alone in its new folder, and nothing for the unreleased movie");
            var showSync = await placeholders.SyncAsync(world.Tv.Id, default);
            Assert(PlaceholderStub.IsStubFile(ShowStub(1)) && PlaceholderStub.IsStubFile(ShowStub(2)) && !File.Exists(ShowStub(3)) &&
                showSync.Writes.Count(write => write.EntryId == ids["fileless"]) == 2,
                "A series with nothing on disk gets its own folder in the library with a placeholder for each aired episode only: " +
                JsonSerializer.Serialize(showSync.Writes.Where(write => write.EntryId == ids["fileless"])));
        }

        world.Native.Scan(movieFolder);
        world.Native.Scan(showFolder);
        var stubItem = world.Native.Items.OfType<MediaBrowser.Controller.Entities.Movies.Movie>().Single(item => item.Path == movieStub);
        Assert(stubItem.IsPlaceHolder, "Jellyfin holds the movie's placeholder as a placeholder movie");
        await tick();
        await Task.Delay(1500);
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["stubMovie"]);
            Assert(entry.State == FileState.None && entry.JellyfinItemId is null &&
                !await database.EntryBindings.AnyAsync(binding => binding.EntryId == entry.Id) &&
                !await database.Entries.AnyAsync(value => value.MediaType == "movie" && value.TmdbId == 651 && value.Id != entry.Id),
                $"The placeholder movie is never bound or called on disk, and backfills nothing ({entry.State}, {entry.JellyfinItemId})");
        }

        var lookup = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries?jellyfinItemId={stubItem.Id}&limit=1"));
        var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{ids["stubMovie"]}"));
        Assert(lookup.GetProperty("items").EnumerateArray().Single().GetProperty("id").AsGuid() == ids["stubMovie"] &&
            detail.GetProperty("entry").GetProperty("placeholderItemId").AsGuid() == stubItem.Id,
            "The placeholder finds its entry, and the entry names it as its placeholder item");

        // The import swaps the placeholder for the file, in the same folder.
        var fixture = TorrentFixture.Single("Stub.Movie.2021.1080p.WEB-DL-GRP.mkv", Size);
        torznab.Torrents["stubMovie"] = fixture.Bytes;
        transmission.Register(fixture);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new("Stub.Movie.2021.1080p.WEB-DL-GRP", "guid-stubMovie", torznab.Download("stubMovie"), Size, 25,
                new() { ["imdbid"] = "0000651" }));
        var search = await ReadAsync(await admin.GetAsync($"/JellyfinMod/Releases?entryId={ids["stubMovie"]}"), 200, "Search the movie");
        var candidate = search.GetProperty("candidates").EnumerateArray().Single(item => item.GetProperty("rawTitle").GetString() == "Stub.Movie.2021.1080p.WEB-DL-GRP");
        var grab = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = search.GetProperty("searchId").AsGuid(), releaseId = candidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5m-" + Guid.NewGuid().ToString("N")
        }), 202, "Administrator grabs the movie");
        var grabId = grab.GetProperty("id").AsGuid();
        await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{grabId}")).GetProperty("state").GetString() ==
            "accepted" ? true : (bool?)null, "The movie's grab is accepted");
        transmission.Progress(fixture.InfoHash, 1.0);
        var imported = await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            var operation = await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.GrabId == grabId);
            Assert(operation?.State is not (ImportStates.Blocked or ImportStates.Failed), $"The movie's import stopped: {operation?.State} {operation?.Reason}");
            return operation?.State == ImportStates.Completed ? operation : null;
        }, "The movie's import completes", 60);
        var files = Directory.EnumerateFiles(movieFolder).ToArray();
        Assert(Path.GetDirectoryName(imported.DestinationPath) == movieFolder && !File.Exists(movieStub) && files.Length == 1 &&
            files[0] == imported.DestinationPath,
            "The movie's file goes into its placeholder's folder and the placeholder goes in the same step: " + string.Join(", ", files));
        await WaitAsync(async () =>
        {
            await tick();
            await using var database = new ModDbContext(dbPath);
            var entry = await database.Entries.AsNoTracking().SingleAsync(value => value.Id == ids["stubMovie"]);
            return entry.State == FileState.OnDisk && entry.JellyfinItemId is { } bound && bound != stubItem.Id &&
                world.Native.Items.All(item => item.Path != movieStub) ? true : null;
        }, "The movie is on disk, bound to its file's item, and the placeholder item is gone");

        // Removing the series that was never imported removes its placeholders and the folders made for them.
        var removed = await admin.DeleteAsync($"/JellyfinMod/Entries/{ids["fileless"]}");
        Assert(removed.StatusCode == System.Net.HttpStatusCode.NoContent && !Directory.Exists(showFolder),
            $"Removing a series that has only placeholders removes it, its placeholders and its folders ({(int)removed.StatusCode})");
        var removedMovie = await admin.DeleteAsync($"/JellyfinMod/Entries/{ids["futureMovie"]}");
        Assert(removedMovie.StatusCode == System.Net.HttpStatusCode.NoContent && Directory.Exists(movieFolder) && File.Exists(imported.DestinationPath!),
            "Removing an unreleased movie works and touches no other title's folder");
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
