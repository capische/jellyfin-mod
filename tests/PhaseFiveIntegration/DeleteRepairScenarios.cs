using System.Diagnostics;
using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Episode = JellyfinMod.Data.Episode;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The placeholders come back right after a delete in Jellyfin (P1.FU29, P4.FU12), through the same real host, SQLite and real files
/// as the rest of Phase 5, with Jellyfin's own announcements of the removal as the simulated boundary: a season announced with its
/// episodes first and itself last, a season announced alone (the root-only pattern one measurement showed), the same season deleted
/// again at once (no suppression window may swallow it), and a whole series announced alone, which comes back in the folder it had.
/// Each repair clears only the bindings the removal names (once, in the history), keeps what still has a file, writes the aired
/// episodes' stubs where they were and has the library hold them, within seconds and without a library-wide pass.
/// </summary>
internal static class DeleteRepairScenarios
{
    public static async Task RunAsync(HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids)
    {
        Guid entryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 660, Title = "Gone Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 660, "Gone Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 760, false, null, 100, [], [],
                    [new TmdbSeason(1, "Season 1", 2, null, null), new TmdbSeason(2, "Season 2", 3, null, null)]))
            };
            database.Entries.Add(entry);
            entryId = entry.Id;
            ids["gone"] = entry.Id;
            foreach (var (season, number) in new[] { (1, 1), (1, 2), (2, 3), (2, 4), (2, 5) })
                database.Episodes.Add(new Episode
                {
                    EntryId = entry.Id, TmdbId = 66000 + season * 10 + number, SeasonNumber = season, EpisodeNumber = number,
                    Title = $"Gone {season}x{number}", RuntimeMinutes = 30, AirDate = new DateTime(2022, season + 2, number, 0, 0, 0, DateTimeKind.Utc)
                });
            await database.SaveChangesAsync();
        }

        // The series folder is not the import's name, so a returning series can be told to keep its own.
        var seriesFolder = Path.Combine(world.Tv.Location, "Gone Show [tmdbid-660]");
        var seasonOne = Path.Combine(seriesFolder, "Season 01");
        var seasonTwo = Path.Combine(seriesFolder, "Season 02");
        Directory.CreateDirectory(seasonOne);
        Directory.CreateDirectory(seasonTwo);
        string Real(string folder, int season, int number) => Path.Combine(folder, $"Gone Show S{season:00}E{number:00}.mkv");
        // The plugin names a stub as the import names its file: the series folder without its TMDB tag.
        string Stub(string folder, int season, int number) => Path.Combine(folder, $"Gone Show S{season:00}E{number:00}.disc");
        foreach (var (folder, season, number) in new[] { (seasonOne, 1, 1), (seasonOne, 1, 2), (seasonTwo, 2, 3), (seasonTwo, 2, 4) })
            await File.WriteAllBytesAsync(Real(folder, season, number), new byte[4096]);
        await File.WriteAllBytesAsync(Stub(seasonTwo, 2, 5), PlaceholderStub.Body("Gone Show", 2, 5, "Gone 2x5"));
        world.Native.Scan(seriesFolder);

        async Task<Entry> EntryAsync()
        {
            await using var database = new ModDbContext(dbPath);
            return await database.Entries.AsNoTracking().SingleAsync(value => value.Id == entryId);
        }

        async Task<int> OnDiskAsync()
        {
            await using var database = new ModDbContext(dbPath);
            return await database.Episodes.CountAsync(episode => episode.EntryId == entryId && episode.State == FileState.OnDisk);
        }

        await WaitAsync(async () => await OnDiskAsync() == 4 && (await EntryAsync()).State == FileState.OnDisk ? true : null, "Gone Show's four files are bound");
        TestSeries SeriesItem() => world.Native.Items.OfType<TestSeries>().Single(item => item.Path == seriesFolder || item.Path!.StartsWith(world.Tv.Location + "/Gone Show", StringComparison.Ordinal));
        MediaBrowser.Controller.Entities.TV.Episode[] Episodes(int season) => world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Where(item => item.Path is not null && item.Path.StartsWith(SeriesItem().Path + "/", StringComparison.Ordinal) && item.ParentIndexNumber == season)
            .OrderBy(item => item.IndexNumber).ToArray();
        async Task<int> MissingHistoryAsync(string type)
        {
            await using var database = new ModDbContext(dbPath);
            return await database.History.CountAsync(history => history.EntryId == entryId && history.EventType == type);
        }

        // The Jellyfin delete of a season: the files, the items and the announcements, descendants first or the season alone.
        void DeleteSeason(int season, string folder, bool withEpisodes)
        {
            var seasonItem = new Season { Id = Guid.NewGuid(), Name = $"Season {season}", Path = folder, SeriesId = SeriesItem().Id };
            var episodes = Episodes(season);
            Directory.Delete(folder, true);
            foreach (var episode in episodes)
                if (withEpisodes) world.Native.Remove(episode);
                else world.Native.Forget(episode);
            world.Native.Announce(seasonItem);
            Console.WriteLine($"INFO: season {season} deleted: {episodes.Length} episode(s) {(withEpisodes ? "announced" : "forgotten")}, the season announced");
        }

        // Each episode with a file names the Jellyfin item that plays it (P4.FU12), and the placeholders of the others name none.
        var beforeDetail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{entryId}")).GetProperty("episodes").EnumerateArray().ToArray();
        var withFile = beforeDetail.Where(episode => episode.GetProperty("availability").GetString() == "onDisk").ToArray();
        Assert(withFile.Length == 4 && withFile.All(episode => episode.TryGetProperty("playableItemId", out var playableId) &&
                world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Any(item => item.Id == playableId.AsGuid() && !item.IsPlaceHolder)) &&
            beforeDetail.Where(episode => episode.GetProperty("availability").GetString() != "onDisk").All(episode => !episode.TryGetProperty("playableItemId", out _)),
            "Each episode with a file names its playable Jellyfin item, and a placeholder's episode names none");

        // Two users have a session: the administrator, who has the TV library, and a user who has only the movie library.
        world.Native.Connected[world.Admin.Id] = 0;
        world.Native.Connected[world.MoviesOnly.Id] = 0;
        var episodesOfOne = Episodes(1).Select(item => item.Id.ToString("N")).ToArray();

        // 1. A season with its episodes announced first and itself last.
        var timer = Stopwatch.StartNew();
        DeleteSeason(1, seasonOne, withEpisodes: true);
        await WaitAsync(() => Task.FromResult(Episodes(1) is [{ IsPlaceHolder: true }, { IsPlaceHolder: true }] &&
            File.Exists(Stub(seasonOne, 1, 1)) && File.Exists(Stub(seasonOne, 1, 2)) ? true : (bool?)null), "Season 1 comes back as two placeholders", 30);
        var first = timer.Elapsed;
        await using (var database = new ModDbContext(dbPath))
        {
            var seasonOneEpisodes = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId && episode.SeasonNumber == 1).ToListAsync();
            var seasonOneIds = seasonOneEpisodes.Select(episode => episode.Id).ToArray();
            Assert(seasonOneEpisodes.All(episode => episode.State == FileState.None && episode.JellyfinItemId is null) &&
                !await database.EpisodeBindings.AnyAsync(binding => seasonOneIds.Contains(binding.EpisodeId)) &&
                (await EntryAsync()).State == FileState.OnDisk && await MissingHistoryAsync("episode_media_missing") == 2,
                "Deleting Season 1 clears its two bindings once and leaves the series on disk through Season 2" +
                $" [states {string.Join(',', seasonOneEpisodes.Select(episode => episode.State + "/" + episode.JellyfinItemId))}; bindings left {await database.EpisodeBindings.CountAsync(binding => seasonOneIds.Contains(binding.EpisodeId))}; entry {(await EntryAsync()).State}; history {await MissingHistoryAsync("episode_media_missing")}]");
        }

        await WaitAsync(() => Task.FromResult(Episodes(1).Length == 2 && Episodes(1).All(item => world.Native.SessionMessages.Any(message =>
            message.Users.Contains(world.Admin.Id) && message.Data is LibraryUpdateInfo info && info.ItemsAdded.Contains(item.Id.ToString("N")))) ? true : (bool?)null),
            "The administrator is told the placeholders are there", 10);
        var told = world.Native.SessionMessages.Where(message => message.Data is LibraryUpdateInfo).ToArray();
        var gone = told.Where(message => message.Users.Contains(world.Admin.Id)).SelectMany(message => ((LibraryUpdateInfo)message.Data).ItemsRemoved).ToArray();
        var there = told.Where(message => message.Users.Contains(world.Admin.Id)).SelectMany(message => ((LibraryUpdateInfo)message.Data).ItemsAdded).ToArray();
        Assert(episodesOfOne.All(gone.Contains) && Episodes(1).All(item => there.Contains(item.Id.ToString("N"))) &&
            // Other libraries' changes (the movie placeholders written meanwhile) are theirs to hear; nothing of this TV season is.
            told.Where(message => message.Users.Contains(world.MoviesOnly.Id)).SelectMany(message => ((LibraryUpdateInfo)message.Data).ItemsAdded
                .Concat(((LibraryUpdateInfo)message.Data).ItemsRemoved).Concat(((LibraryUpdateInfo)message.Data).FoldersAddedTo)).All(id => !gone.Contains(id) && !there.Contains(id)),
            "The early LibraryChanged names the removed episodes and the placeholders now there to the administrator, and a user without the TV library hears nothing of it" +
            $" [removed all named {episodesOfOne.All(gone.Contains)}, there all named {Episodes(1).All(item => there.Contains(item.Id.ToString("N")))}, gone {gone.Length}, there {there.Length}]");

        Assert(first < TimeSpan.FromSeconds(10), $"Season 1's placeholders were back in {first.TotalSeconds:F1} s");
        Console.WriteLine($"INFO: delete repair, season with descendants: placeholders held after {first.TotalSeconds:F2} s");

        // 2. The same season deleted again at once: nothing a first repair left may swallow the second.
        timer.Restart();
        DeleteSeason(1, seasonOne, withEpisodes: true);
        await WaitAsync(() => Task.FromResult(Episodes(1) is [{ IsPlaceHolder: true }, { IsPlaceHolder: true }] &&
            File.Exists(Stub(seasonOne, 1, 1)) && File.Exists(Stub(seasonOne, 1, 2)) ? true : (bool?)null), "Season 1 comes back again", 30);
        Console.WriteLine($"INFO: delete repair, the same season again at once: {timer.Elapsed.TotalSeconds:F2} s");
        Assert(await MissingHistoryAsync("episode_media_missing") == 2,
            "A deletion of placeholders only clears no binding and records nothing more");

        // 3. A season announced alone (its episodes only forgotten): the folder path names every binding under it.
        timer.Restart();
        DeleteSeason(2, seasonTwo, withEpisodes: false);
        await WaitAsync(() => Task.FromResult(Episodes(2) is [{ IsPlaceHolder: true }, { IsPlaceHolder: true }, { IsPlaceHolder: true }] &&
            File.Exists(Stub(seasonTwo, 2, 3)) && File.Exists(Stub(seasonTwo, 2, 4)) && File.Exists(Stub(seasonTwo, 2, 5)) ? true : (bool?)null),
            "Season 2 announced alone comes back as three placeholders", 30);
        Console.WriteLine($"INFO: delete repair, season announced alone: {timer.Elapsed.TotalSeconds:F2} s");
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(await OnDiskAsync() == 0 && (await EntryAsync()).State == FileState.None && (await EntryAsync()).JellyfinItemId is null &&
                await MissingHistoryAsync("episode_media_missing") == 4 && await MissingHistoryAsync("media_missing") == 1 &&
                Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories).All(file => file.EndsWith(".disc", StringComparison.Ordinal)) &&
                Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories).Count() == 5,
                "Announced alone, the season's two files are cleared once each, the series ends not on disk, and five stubs stand, none beside a file");
        }

        // 4. A whole series announced alone: it comes back in the folder it had, with every aired episode a placeholder.
        timer.Restart();
        var series = SeriesItem();
        var everything = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Where(item => item.SeriesId == series.Id).ToArray();
        Directory.Delete(seriesFolder, true);
        foreach (var episode in everything) world.Native.Forget(episode);
        world.Native.Forget(series);
        world.Native.Announce(series);
        await WaitAsync(() => Task.FromResult(Directory.Exists(seriesFolder) && Directory.EnumerateFiles(seriesFolder, "*.disc", SearchOption.AllDirectories).Count() == 5 &&
            world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Count(item => item.Path!.StartsWith(seriesFolder + "/", StringComparison.Ordinal) && item.IsPlaceHolder) == 5
            ? true : (bool?)null), "The deleted series comes back in its own folder with five placeholders", 30);
        Console.WriteLine($"INFO: delete repair, whole series announced alone: {timer.Elapsed.TotalSeconds:F2} s");
        Assert(!Directory.Exists(Path.Combine(world.Tv.Location, "Gone Show (2022) [tmdbid-660]")),
            "The series came back under the name it had, not under the import's name for it");

        // The scenario's own fixtures go: its entry (with its placeholders), its history and its native items.
        var removed = await admin.DeleteAsync($"/JellyfinMod/Entries/{entryId}");
        Assert((int)removed.StatusCode is 200 or 204, "The scenario's entry is removed again: " + removed.StatusCode);
        await using (var database = new ModDbContext(dbPath))
        {
            database.History.RemoveRange(database.History.Where(history => history.EntryId == entryId));
            await database.SaveChangesAsync();
        }

        await WaitAsync(() => Task.FromResult(!Directory.Exists(seriesFolder) || !Directory.EnumerateFiles(seriesFolder, "*", SearchOption.AllDirectories).Any()
            ? true : (bool?)null), "The scenario leaves no placeholder behind");
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);
}
