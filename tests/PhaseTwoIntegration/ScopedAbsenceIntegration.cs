using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// P4.FU12 steps 1 and 2: the scoped absence confirmation that follows a delete in Jellyfin. Real SQLite, real files, and a mount
/// table the test writes (the one simulated boundary, as in the post-scan scenarios): an emptied folder on a healthy mount is
/// proof of absence, an emptied folder on a mount that changed is not; a season or series reported for its root alone clears
/// every binding under it once; another copy and another episode stay untouched.
/// </summary>
internal static class ScopedAbsenceIntegration
{
    public static async Task RunAsync(string folder)
    {
        var root = Path.Combine(folder, "scoped");
        var tv = Path.Combine(root, "tv");
        Directory.CreateDirectory(tv);
        await File.WriteAllTextAsync(Path.Combine(tv, "mounted"), "present");
        var mountInfo = Path.Combine(root, "mountinfo");
        // The tv folder is its own mount (device 3:1) under the test root.
        async Task Mounts(string device) => await File.WriteAllLinesAsync(mountInfo,
        [
            $"10 1 1:1 / {root} rw - apfs /dev/root rw",
            $"12 10 {device} /library/tv {tv} rw - ext4 /dev/tv rw"
        ]);
        await Mounts("3:1");
        var storage = new MediaStorageIdentity(mountInfo);
        var libraryId = Guid.NewGuid();
        var locations = new[] { tv };

        await using var database = new ModDbContext(Path.Combine(root, "scoped.db"));
        await database.Database.MigrateAsync();
        var service = new ReconciliationService(database, new ReconciliationLibraryLock(), storage);

        string Write(string relative)
        {
            var path = Path.Combine(tv, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative);
            return path;
        }

        // A series of two seasons (three episodes, one with a second copy) and a series of one season, bound as a scan would.
        async Task<(Guid Entry, Guid Series, Guid[] Episodes)> Series(int tmdb, string name, params string[] files)
        {
            var seriesId = Guid.NewGuid();
            var ids = files.Select(_ => Guid.NewGuid()).ToArray();
            var result = await service.ReconcileAsync(new NativeTitleSnapshot("series", tmdb, libraryId, name, 2020, null, null, null, null,
                [new(seriesId, libraryId, false, seriesId, Path.Combine(tv, name), storage.Capture(Path.Combine(tv, name)))],
                files.Select((relative, index) => new NativeEpisodeSnapshot(ids[index], seriesId, tmdb * 10 + index,
                    int.Parse(Path.GetFileName(Path.GetDirectoryName(relative))!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture),
                    index + 1, true, "Episode " + index, MediaPath: Path.Combine(tv, relative),
                    StorageIdentity: storage.Capture(Path.Combine(tv, relative)))).ToArray()), default);
            return (result.EntryId!.Value, seriesId, ids);
        }

        foreach (var relative in new[] { "Series A/Season 01/e1.mkv", "Series A/Season 01/e2.mkv", "Series A/Season 02/e3.mkv",
                     "Series B/Season 01/e1.mkv" })
            Write(relative);
        var a = await Series(701, "Series A", "Series A/Season 01/e1.mkv", "Series A/Season 01/e2.mkv", "Series A/Season 02/e3.mkv");
        var b = await Series(702, "Series B", "Series B/Season 01/e1.mkv");
        database.ChangeTracker.Clear();
        Assert(await database.EpisodeBindings.CountAsync(binding => binding.TargetLibraryId == libraryId) == 4 &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).State == FileState.OnDisk,
            "Scoped absence: the fixture series are bound");

        // One episode removed (its file gone, its id named): only that binding goes, once, and the series stays on disk.
        File.Delete(Path.Combine(tv, "Series A/Season 01/e2.mkv"));
        var episode = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([a.Episodes[1]], [Path.Combine(tv, "Series A/Season 01/e2.mkv")]), default);
        database.ChangeTracker.Clear();
        Assert(episode.RemovedBindings == 1 && episode.MissingItems == 1 && episode.EntryIds.Contains(a.Entry) &&
            await database.EpisodeBindings.CountAsync(binding => binding.SeriesItemId == a.Series) == 2 &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).State == FileState.OnDisk &&
            await database.History.CountAsync(history => history.EntryId == a.Entry && history.EventType == "episode_media_missing") == 1,
            "Scoped absence: a removed episode clears its own binding, records it once and leaves the series on disk");
        var again = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([a.Episodes[1]], [Path.Combine(tv, "Series A/Season 01/e2.mkv")]), default);
        Assert(again.RemovedBindings == 0 && again.EntryIds.Count == 0, "Scoped absence: repeating a removal changes nothing");

        // A season reported for its root alone (no per-episode event): the folder path clears the binding under it.
        Directory.Delete(Path.Combine(tv, "Series A/Season 01"), true);
        var season = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([], [Path.Combine(tv, "Series A/Season 01")]), default);
        database.ChangeTracker.Clear();
        Assert(season.RemovedBindings == 1 &&
            await database.EpisodeBindings.CountAsync(binding => binding.SeriesItemId == a.Series) == 1 &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).State == FileState.OnDisk,
            "Scoped absence: a season reported for its root clears the episodes under it and the other season keeps the series on disk");

        // The series that kept an empty folder on a healthy mount (its only season deleted): proven absent.
        Directory.Delete(Path.Combine(tv, "Series B/Season 01"), true);
        var healthy = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([], [Path.Combine(tv, "Series B/Season 01")]), default);
        database.ChangeTracker.Clear();
        Assert(healthy.RemovedBindings == 1 && Directory.Exists(Path.Combine(tv, "Series B")) &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(tv, "Series B")).Any() &&
            (await database.Episodes.SingleAsync(episodeRow => episodeRow.EntryId == b.Entry)).State == FileState.None,
            "An empty series folder on the mount the file was seen on is proof of absence (step 1)");

        // The same, but the mount the file was seen on is no longer the one the path is on: nothing is cleared.
        Directory.Delete(Path.Combine(tv, "Series A"), true);
        await Mounts("3:9");
        var changed = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([a.Series], [Path.Combine(tv, "Series A")]), default);
        database.ChangeTracker.Clear();
        Assert(changed.RemovedBindings == 0 && changed.Unverified.Count == 1 &&
            await database.EpisodeBindings.AnyAsync(binding => binding.SeriesItemId == a.Series) &&
            await database.EntryBindings.AnyAsync(binding => binding.EntryId == a.Entry) &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).State == FileState.OnDisk,
            "A path on a mount that changed is not proven absent: the title keeps its bindings and state (step 1)");

        // The mount is back: the whole series reported for its root clears its series binding and last episode once.
        await Mounts("3:1");
        var whole = await service.ConfirmAbsenceInScopeUnderLeaseAsync(libraryId, locations,
            new RemovedScope([a.Series], [Path.Combine(tv, "Series A")]), default);
        database.ChangeTracker.Clear();
        Assert(whole.RemovedBindings == 2 &&
            !await database.EpisodeBindings.AnyAsync(binding => binding.SeriesItemId == a.Series) &&
            !await database.EntryBindings.AnyAsync(binding => binding.EntryId == a.Entry) &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).State == FileState.None &&
            (await database.Entries.SingleAsync(entry => entry.Id == a.Entry)).JellyfinItemId is null &&
            await database.History.CountAsync(history => history.EntryId == a.Entry && history.EventType == "media_missing") == 1,
            "Scoped absence: a series reported for its root clears every binding under it exactly once and ends on disk no more");

        // The files of another title are never touched by a scope that does not name them.
        Assert((await database.Episodes.CountAsync(episodeRow => episodeRow.EntryId == b.Entry)) == 1 && await database.EntryBindings
                .CountAsync(binding => binding.EntryId == b.Entry) == 1,
            "Scoped absence: a scope leaves other titles' rows alone");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
