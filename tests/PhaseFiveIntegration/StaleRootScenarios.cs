using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Placeholders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Episode = JellyfinMod.Data.Episode;

/// <summary>
/// A binding left in a folder that is no longer a library root (2026-10-24, Peep Show on beta): the comparison seed removed the
/// old TV root as a library path only, so its files stayed and the plugin kept their bindings beside the live root's. When the
/// live copy is deleted in Jellyfin, the episodes must not fall back to the old root's bindings: they end missing, the entry ends
/// not on disk, the series page lists them as missing with their placeholders, and the old root's files are never touched.
/// Same real host, SQLite, files and delete repair as the other Phase 5 scenarios; Jellyfin's removal is the simulated boundary.
/// </summary>
internal static class StaleRootScenarios
{
    public static async Task RunAsync(HttpClient admin, World world, string dbPath, PluginHost host)
    {
        Guid entryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 661, Title = "Stale Show", Year = 2003, TargetLibraryId = world.Tv.Id,
                MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", 661, "Stale Show", new DateTime(2003, 9, 19, 0, 0, 0, DateTimeKind.Utc),
                    null, null, null, null, 761, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", 2, null, null)]))
            };
            database.Entries.Add(entry);
            entryId = entry.Id;
            foreach (var number in new[] { 1, 2 })
                database.Episodes.Add(new Episode
                {
                    EntryId = entry.Id, TmdbId = 66100 + number, SeasonNumber = 1, EpisodeNumber = number, Title = $"Stale 1x{number}",
                    RuntimeMinutes = 30, AirDate = new DateTime(2003, 9, 18 + number, 0, 0, 0, DateTimeKind.Utc)
                });
            await database.SaveChangesAsync();
        }

        // The old root sits beside the library's root on the same mount, as /data5/TV Series beside /test-media/tv.
        var oldRoot = Path.Combine(Path.GetDirectoryName(world.Tv.Location)!, "tv-retired");
        var oldFolder = Path.Combine(oldRoot, "Stale Show (2003)");
        var liveFolder = Path.Combine(world.Tv.Location, "Stale Show (2003) [tmdbid-661]");
        string File(string folder, int number) => Path.Combine(folder, "Season 01", $"Stale Show (2003) S01E{number:00}.mkv");
        foreach (var folder in new[] { oldFolder, liveFolder })
        {
            Directory.CreateDirectory(Path.Combine(folder, "Season 01"));
            foreach (var number in new[] { 1, 2 }) await System.IO.File.WriteAllBytesAsync(File(folder, number), new byte[4096]);
        }

        // While the old folder was a library root, a scan bound its copy: that observation, reconciled as the scan reconciled it.
        // Its items are gone from Jellyfin since the root was removed; the files stay.
        var storage = host.Service<MediaStorageIdentity>();
        var oldSeries = Guid.NewGuid();
        var oldEpisodes = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using (var scope = host.App.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().ReconcileAsync(new NativeTitleSnapshot("series", 661,
                world.Tv.Id, "Stale Show", 2003, null, null, null, null,
                [new(oldSeries, world.Tv.Id, false, oldSeries, oldFolder, storage.Capture(oldFolder))],
                oldEpisodes.Select((id, index) => new NativeEpisodeSnapshot(id, oldSeries, null, 1, index + 1, true, $"Stale 1x{index + 1}",
                    MediaPath: File(oldFolder, index + 1), StorageIdentity: storage.Capture(File(oldFolder, index + 1)))).ToArray()), default);
            Assert(result.EntryId == entryId, "Stale root: the old root's copy was bound to the entry while it was a library root");
        }

        // The live root's copy is scanned now and bound beside it, as on alpha and beta.
        world.Native.Scan(liveFolder);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            var episodes = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId).ToListAsync();
            var ids = episodes.Select(episode => episode.Id).ToArray();
            return episodes.All(episode => episode.State == FileState.OnDisk && episode.JellyfinItemId is { } item && !oldEpisodes.Contains(item)) &&
                await database.EpisodeBindings.CountAsync(binding => ids.Contains(binding.EpisodeId)) == 4 ? true : null;
        }, "Stale root: each episode is bound in both roots and points at the live root's copy");
        var liveSeries = world.Native.Items.OfType<TestSeries>().Single(item => item.Path == liveFolder);
        var before = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{entryId}"));
        Assert(before.GetProperty("episodes").EnumerateArray().Count(episode => episode.GetProperty("availability").GetString() == "onDisk") == 2,
            "Stale root: the series page lists both episodes on disk before the delete");

        // Jellyfin's Delete media on the live series: its files, its items, and the series announced alone.
        var liveEpisodes = world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Where(item => item.SeriesId == liveSeries.Id).ToArray();
        Directory.Delete(liveFolder, true);
        foreach (var episode in liveEpisodes) world.Native.Forget(episode);
        world.Native.Forget(liveSeries);
        world.Native.Announce(liveSeries);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return !await database.EpisodeBindings.AnyAsync(binding => binding.SeriesItemId == liveSeries.Id) ? true : null;
        }, "Stale root: the delete repair clears the live root's bindings");

        await using (var database = new ModDbContext(dbPath))
        {
            var entry = await database.Entries.AsNoTracking().SingleAsync(value => value.Id == entryId);
            var episodes = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == entryId).ToListAsync();
            var ids = episodes.Select(episode => episode.Id).ToArray();
            var left = await database.EpisodeBindings.AsNoTracking().Where(binding => ids.Contains(binding.EpisodeId)).ToListAsync();
            var leftSeries = await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entryId).ToListAsync();
            Assert(episodes.All(episode => episode.State == FileState.None && episode.JellyfinItemId is null) && left.Count == 0 && leftSeries.Count == 0 &&
                entry.State == FileState.None && entry.JellyfinItemId is null,
                "Stale root: after the live copy is deleted the episodes do not fall back to the old root's bindings, which are dropped, " +
                "and the entry ends not on disk" +
                $" [episodes {string.Join(',', episodes.Select(episode => episode.State + "/" + episode.JellyfinItemId))}; episode bindings left " +
                $"{string.Join(',', left.Select(binding => binding.MediaPath))}; series bindings left {leftSeries.Count}; entry {entry.State}/{entry.JellyfinItemId}]");
        }

        Assert(new[] { 1, 2 }.All(number => System.IO.File.Exists(File(oldFolder, number))),
            "Stale root: the old root's files are not touched; only their bindings went");

        // Placeholders come back where the live copy was, and the series page lists both episodes as missing with them.
        await WaitAsync(() => Task.FromResult(new[] { 1, 2 }.All(number =>
            Directory.Exists(liveFolder) && Directory.EnumerateFiles(liveFolder, $"*S01E{number:00}.disc", SearchOption.AllDirectories).Any()) ? true : (bool?)null),
            "Stale root: the two episodes come back as placeholders in the live root");
        await WaitAsync(async () =>
        {
            var detail = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Entries/{entryId}"));
            var listed = detail.GetProperty("episodes").EnumerateArray().ToArray();
            return detail.GetProperty("entry").GetProperty("state").GetString() != "onDisk" && listed.Length == 2 &&
                listed.All(episode => episode.GetProperty("availability").GetString() != "onDisk" && episode.TryGetProperty("placeholderItemId", out _))
                ? true : null;
        }, "Stale root: the series page lists both episodes as missing, each with its placeholder, and the title is not on disk");

        // The scenario's own fixtures go: its entry (with its placeholders), its history and the old root's files.
        var removed = await admin.DeleteAsync($"/JellyfinMod/Entries/{entryId}");
        Assert((int)removed.StatusCode is 200 or 204, "Stale root: the scenario's entry is removed again: " + removed.StatusCode);
        await using (var database = new ModDbContext(dbPath))
        {
            database.History.RemoveRange(database.History.Where(history => history.EntryId == entryId));
            await database.SaveChangesAsync();
        }

        Directory.Delete(oldRoot, true);
        await WaitAsync(() => Task.FromResult(!Directory.Exists(liveFolder) || !Directory.EnumerateFiles(liveFolder, "*", SearchOption.AllDirectories).Any()
            ? true : (bool?)null), "Stale root: the scenario leaves no placeholder behind");
    }

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);
}
