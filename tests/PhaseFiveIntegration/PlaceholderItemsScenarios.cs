using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Episode = JellyfinMod.Data.Episode;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The catalog side of the placeholders as Jellyfin items (P1.FU30), restored after the stub scenarios went (review 2026-10-22),
/// through the same real host, SQLite and real files as the rest of Phase 5:
/// <list type="bullet">
///   <item>the one-time clean-up of the stubs line's files (review P1-1): before it, a marked <c>.disc</c> movie is never taken
///   for the title's file nor due for retention; it removes exactly the marked stubs and the season and title folders they
///   left empty, never a <c>.disc</c> without the marker or a real file;</item>
///   <item>Jellyfin's Delete media of a season, with its episodes announced or the season alone: each binding the removal names
///   is cleared once, with its history once, the series stays on disk through its other season, and the early
///   <c>LibraryChanged</c> goes only to a user who sees the library.</item>
/// </list>
/// The placeholder items themselves are made by a real Jellyfin: the live runner placeholder-items.mjs proves them.
/// </summary>
internal static class PlaceholderItemsScenarios
{
    public static async Task RunAsync(PluginHost host, HttpClient admin, World world, string dbPath)
    {
        await StubCleanupAsync(host, admin, world, dbPath);
        await DeleteRepairAsync(world, dbPath);
    }

    private static async Task StubCleanupAsync(PluginHost host, HttpClient admin, World world, string dbPath)
    {
        Guid filmId;
        await using (var database = new ModDbContext(dbPath))
        {
            var film = new Entry
            {
                MediaType = "movie", TmdbId = 771, Title = "Old Film", Year = 2020, TargetLibraryId = world.Movies.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("movie", 771, "Old Film", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 871, false, null, 100, [], [], []))
            };
            database.Entries.Add(film);
            filmId = film.Id;
            await database.SaveChangesAsync();
        }

        var stubBody = PlaceholderStubCleanup.Marker + "\n<discstub><message>Not downloaded yet.</message></discstub>\n";
        var filmFolder = Path.Combine(world.Movies.Location, "Old Film (2020) [tmdbid-771]");
        var filmStub = Path.Combine(filmFolder, "Old Film (2020) [tmdbid-771].disc");
        var seriesFolder = Path.Combine(world.Tv.Location, "Old Stubs [tmdbid-770]");
        var seasonOne = Path.Combine(seriesFolder, "Season 01");
        var seasonTwo = Path.Combine(seriesFolder, "Season 02");
        Directory.CreateDirectory(filmFolder);
        Directory.CreateDirectory(seasonOne);
        Directory.CreateDirectory(seasonTwo);
        var real = Path.Combine(seasonOne, "Old Stubs S01E01.mkv");
        var mine = Path.Combine(seasonOne, "Old Stubs S01E03 - my disc.disc");
        var stubs = new[] { filmStub, Path.Combine(seasonOne, "Old Stubs S01E02.disc"), Path.Combine(seasonTwo, "Old Stubs S02E01.disc"),
            Path.Combine(seasonTwo, "Old Stubs S02E02.disc") };
        await File.WriteAllBytesAsync(real, new byte[4096]);
        await File.WriteAllTextAsync(mine, "<discstub><message>mine</message></discstub>");
        foreach (var stub in stubs) await File.WriteAllTextAsync(stub, stubBody);
        world.Native.Scan(filmFolder);
        world.Native.Scan(seriesFolder);

        // Before the clean-up: the marked stub movie is not the title's file, so it is not on disk and nothing of it is due.
        await host.Service<JellyfinItemReconciliationRunner>().ReconcileAsync(world.Native.Items.Single(item => item.Path == filmStub).Id, CancellationToken.None);
        await using (var database = new ModDbContext(dbPath))
        {
            var film = await database.Entries.AsNoTracking().SingleAsync(entry => entry.Id == filmId);
            Assert(film.State != FileState.OnDisk && film.JellyfinItemId is null &&
                !await database.EntryBindings.AnyAsync(binding => binding.EntryId == filmId),
                $"A marked stub the stubs line left is never taken for the movie's file [state {film.State}, item {film.JellyfinItemId}]");
        }

        var preview = await admin.GetStringAsync("/JellyfinMod/Retention/Preview");
        Assert(!preview.Contains("Old Film (2020) [tmdbid-771].disc", StringComparison.Ordinal), "Retention never lists a marked stub");

        var (removed, failed) = host.Service<PlaceholderStubCleanup>().Run();
        Assert(removed == 4 && failed == 0 && stubs.All(stub => !File.Exists(stub)),
            $"The clean-up removes the four marked stubs [removed {removed}, failed {failed}]");
        Assert(File.Exists(real) && File.Exists(mine) && Directory.Exists(seasonOne),
            "It leaves the real file, a .disc without the marker and the season folder they are in");
        Assert(!Directory.Exists(seasonTwo) && !Directory.Exists(filmFolder) && Directory.Exists(seriesFolder),
            "It removes the season folder and the movie's title folder the stubs left empty, and keeps the series folder");
        Assert(!world.Native.Items.Any(item => item.Path is { } path && stubs.Contains(path)),
            "Jellyfin's items of the removed stubs go with them");
        Assert(host.Service<PlaceholderStubCleanup>().Run() == (0, 0), "A second run finds nothing more to remove");

        // The scenario's own fixtures go.
        Directory.Delete(seriesFolder, true);
        foreach (var item in world.Native.Items.Where(item => item.Path is { } path && path.StartsWith(seriesFolder, StringComparison.Ordinal)).ToArray())
            world.Native.Remove(item);
        await using (var database = new ModDbContext(dbPath))
        {
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == filmId));
            await database.SaveChangesAsync();
        }
    }

    private static async Task DeleteRepairAsync(World world, string dbPath)
    {
        Guid entryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 660, Title = "Gone Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 660, "Gone Show", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 760, false, null, 100, [], [],
                    [new TmdbSeason(1, "Season 1", 2, null, null), new TmdbSeason(2, "Season 2", 2, null, null)]))
            };
            database.Entries.Add(entry);
            entryId = entry.Id;
            foreach (var (season, number) in new[] { (1, 1), (1, 2), (2, 3), (2, 4) })
                database.Episodes.Add(new Episode
                {
                    EntryId = entry.Id, TmdbId = 66000 + season * 10 + number, SeasonNumber = season, EpisodeNumber = number,
                    Title = $"Gone {season}x{number}", RuntimeMinutes = 30, AirDate = new DateTime(2022, season + 2, number, 0, 0, 0, DateTimeKind.Utc)
                });
            await database.SaveChangesAsync();
        }

        var seriesFolder = Path.Combine(world.Tv.Location, "Gone Show [tmdbid-660]");
        var seasonOne = Path.Combine(seriesFolder, "Season 01");
        var seasonTwo = Path.Combine(seriesFolder, "Season 02");
        Directory.CreateDirectory(seasonOne);
        Directory.CreateDirectory(seasonTwo);
        foreach (var (folder, season, number) in new[] { (seasonOne, 1, 1), (seasonOne, 1, 2), (seasonTwo, 2, 3), (seasonTwo, 2, 4) })
            await File.WriteAllBytesAsync(Path.Combine(folder, $"Gone Show S{season:00}E{number:00}.mkv"), new byte[4096]);
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

        async Task<int> HistoryAsync(string type)
        {
            await using var database = new ModDbContext(dbPath);
            return await database.History.CountAsync(history => history.EntryId == entryId && history.EventType == type);
        }

        await WaitAsync(async () => await OnDiskAsync() == 4 && (await EntryAsync()).State == FileState.OnDisk ? true : null, "Gone Show's four files are bound");
        TestSeries SeriesItem() => world.Native.Items.OfType<TestSeries>().Single(item => item.Path == seriesFolder);
        MediaBrowser.Controller.Entities.TV.Episode[] Real(int season) => world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .Where(item => item.Path is not null && item.Path.StartsWith(seriesFolder + "/", StringComparison.Ordinal) && item.ParentIndexNumber == season)
            .OrderBy(item => item.IndexNumber).ToArray();
        void DeleteSeason(int season, string folder, bool withEpisodes)
        {
            var seasonItem = new Season { Id = Guid.NewGuid(), Name = $"Season {season}", Path = folder, SeriesId = SeriesItem().Id };
            var episodes = Real(season);
            Directory.Delete(folder, true);
            foreach (var episode in episodes)
                if (withEpisodes) world.Native.Remove(episode);
                else world.Native.Forget(episode);
            world.Native.Announce(seasonItem);
        }

        world.Native.Connected[world.Admin.Id] = 0;
        world.Native.Connected[world.MoviesOnly.Id] = 0;
        var removedIds = Real(1).Select(item => item.Id.ToString("N")).ToArray();

        // 1. A season deleted with its episodes announced first and itself last.
        DeleteSeason(1, seasonOne, withEpisodes: true);
        await WaitAsync(async () => await HistoryAsync("episode_media_missing") == 2 ? true : null, "Season 1's two removals are recorded");
        await using (var database = new ModDbContext(dbPath))
        {
            var rows = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId && episode.SeasonNumber == 1).ToListAsync();
            var rowIds = rows.Select(episode => episode.Id).ToArray();
            Assert(rows.All(episode => episode.State == FileState.None && episode.JellyfinItemId is null) &&
                !await database.EpisodeBindings.AnyAsync(binding => rowIds.Contains(binding.EpisodeId)) &&
                (await EntryAsync()).State == FileState.OnDisk,
                "Deleting Season 1 clears its two bindings once each and leaves the series on disk through Season 2");
        }

        await WaitAsync(() => Task.FromResult(world.Native.SessionMessages.Any(message => message.Users.Contains(world.Admin.Id) &&
            message.Data is LibraryUpdateInfo info && removedIds.All(info.ItemsRemoved.Contains)) ? true : (bool?)null),
            "The administrator is told at once that Season 1's episodes went", 10);
        var heardByMoviesOnly = world.Native.SessionMessages.Where(message => message.Users.Contains(world.MoviesOnly.Id) && message.Data is LibraryUpdateInfo)
            .SelectMany(message => ((LibraryUpdateInfo)message.Data).ItemsRemoved).ToArray();
        Assert(!removedIds.Any(heardByMoviesOnly.Contains), "A user without the TV library hears nothing of the removal");

        // 2. The other season announced alone (its episodes only forgotten): its folder path names every binding under it.
        DeleteSeason(2, seasonTwo, withEpisodes: false);
        await WaitAsync(async () => await OnDiskAsync() == 0 && (await EntryAsync()).State == FileState.None ? true : null,
            "Season 2 announced alone clears the bindings under its folder");
        Assert(await HistoryAsync("episode_media_missing") == 4 && await HistoryAsync("media_missing") == 1 && (await EntryAsync()).JellyfinItemId is null,
            "Each removal is recorded once, and the series once as it leaves the disk " +
            $"[episodes {await HistoryAsync("episode_media_missing")}, series {await HistoryAsync("media_missing")}]");

        // The scenario's own fixtures go: its native items, its entry (the series item, still in its emptied folder, keeps the
        // entry's binding, so it goes from SQLite) and its history.
        if (Directory.Exists(seriesFolder)) Directory.Delete(seriesFolder, true);
        world.Native.Forget(SeriesItem());
        await using (var database = new ModDbContext(dbPath))
        {
            database.History.RemoveRange(database.History.Where(history => history.EntryId == entryId));
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == entryId));
            await database.SaveChangesAsync();
        }
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);
}
