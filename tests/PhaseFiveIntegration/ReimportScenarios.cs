using System.Net.Http.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A release still seeding in the client, whose library file was deleted, is imported again from the seeding copy when it is
/// added again, not downloaded twice (user, 2026-10-28), through the same real host, HTTP, SQLite, Torznab and Transmission
/// boundaries and real files as the rest of Phase 5: the grab of the same torrent answers with the earlier grab, nothing is sent
/// to the client, a new import hardlinks the seeding file into the library again, and the queue keeps one row for it whose seed
/// release is the new import's.
/// </summary>
internal static partial class Phase5
{
    private static async Task ReimportAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission, UnixFileInspector inspector)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieAgain", "movie", 135, "Again Movie", 2021, "tt0000135", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        const string title = "Again.Movie.2021.1080p.WEB-DL-GRP";
        var fixture = TorrentFixture.Single(title + ".mkv", Size);
        torznab.Torrents["movieAgain"] = fixture.Bytes;
        transmission.Register(fixture);
        lock (torznab.MovieItems)
            torznab.MovieItems.Add(new(title, "guid-movieAgain", torznab.Download("movieAgain"), Size, 25, new() { ["imdbid"] = "0000135" }));
        uint Links(string path) => inspector.TryInspect(path, out var file) ? file.HardlinkCount : 0;
        var entryId = ids["movieAgain"];

        // ---------------- Grabbed, downloaded and imported as usual.
        var (grabId, hash) = await GrabAsync(admin, entryId, null, fixture);
        transmission.Progress(hash, 1.0);
        var first = await CompleteAsync(dbPath, grabId, tick);
        var seeding = first.SourceLocalPath!;
        var library = first.DestinationPath!;
        Assert(Links(library) == 2, $"The first import is a hardlink: {Links(library)}");

        // ---------------- Delete Media: the library file and its Jellyfin item go; the torrent keeps seeding.
        var item = world.Native.Items.Single(value => value.Path == library);
        File.Delete(library);
        world.Native.Remove(item);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return !await database.EntryBindings.AnyAsync(binding => binding.EntryId == entryId) &&
                await database.History.AnyAsync(history => history.EntryId == entryId && history.EventType == "media_missing") ? true : null;
        }, "Deleting the movie clears its binding and records it leaving the disk");
        Assert(transmission.Torrents.ContainsKey(hash) && File.Exists(seeding) && Links(seeding) == 1,
            $"The torrent still seeds its file, now with one name: {Links(seeding)}");

        // ---------------- Added again: the earlier grab answers, the client is asked nothing, and the seeding copy is imported.
        var search = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}"));
        Assert(search.GetProperty("grab").GetProperty("available").GetBoolean(), "The search offers Add for the seeding release: " +
            search.GetProperty("grab").GetRawText());
        var candidate = search.GetProperty("candidates").EnumerateArray().Single(value => value.GetProperty("rawTitle").GetString() == title);
        var addsBefore = transmission.AddCalls;
        var torrentsBefore = transmission.Torrents.Count;
        var again = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = search.GetProperty("searchId").AsGuid(), releaseId = candidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-again-" + Guid.NewGuid().ToString("N")
        }), 200, "Adding the seeding release again answers with its earlier grab");
        Assert(again.GetProperty("id").AsGuid() == grabId && again.GetProperty("state").GetString() == "accepted",
            "The answer is the earlier, accepted grab: " + again.GetRawText());
        // While it imports the earlier grab holds the title again (or has already let go: the import may be done by now).
        var held = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}")).GetProperty("grab");
        Assert(held.GetProperty("available").GetBoolean() ? (await ImportFor(dbPath, grabId)) is { State: ImportStates.Completed } latestImport &&
                latestImport.Id != first.Id : held.GetProperty("activeOperationId").AsGuid() == grabId,
            "The earlier grab holds the title while the seeding copy imports: " + held.GetRawText());

        var second = await WaitAsync(async () =>
        {
            await tick();
            var latest = await ImportFor(dbPath, grabId);
            Assert(latest.State is not (ImportStates.Blocked or ImportStates.Failed), $"The import stopped: {latest.State} {latest.Reason} {latest.Error}");
            return latest.Id != first.Id && latest.State == ImportStates.Completed ? latest : null;
        }, "The seeding copy is imported again");
        Assert(transmission.AddCalls == addsBefore && transmission.Torrents.Count == torrentsBefore && transmission.Torrents.ContainsKey(hash),
            $"Nothing was sent to the client: adds {transmission.AddCalls - addsBefore}, torrents {transmission.Torrents.Count - torrentsBefore}");
        Assert(second.SourceLocalPath == seeding && File.Exists(second.DestinationPath!) && Links(seeding) == 2,
            $"The new import hardlinks the seeding file into the library: {second.SourceLocalPath} -> {second.DestinationPath}, links {Links(seeding)}");

        await using (var database = new ModDbContext(dbPath))
        {
            var grab = await database.GrabOperations.AsNoTracking().SingleAsync(value => value.Id == grabId);
            var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(value => value.GrabId == grabId).ToListAsync();
            Assert(grab.ActiveTarget is null && grab.ActiveHash is not null && await CountImports(dbPath, grabId) == 2,
                $"The grab let go of the title again and still owns the torrent: target {grab.ActiveTarget}, hash {grab.ActiveHash is not null}");
            Assert(seeds.Count == 2 && seeds.Single(value => value.ImportOperationId == first.Id) is
                    { State: SeedReleaseStates.Cancelled, Reason: SeedReleaseReasons.Reimported } &&
                seeds.Single(value => value.ImportOperationId == second.Id).State == SeedReleaseStates.Waiting,
                "The first import's seed release is closed and the new one's waits: " +
                string.Join(", ", seeds.Select(value => $"{value.ImportOperationId == first.Id}:{value.State}:{value.Reason}")));
            Assert(await database.History.CountAsync(history => history.EntryId == entryId && history.EventType == "imported") == 2,
                "Each import wrote its own imported event");
        }

        var rows = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Queue?entryId={entryId}")).GetProperty("items").EnumerateArray().ToArray();
        Assert(rows.Length == 1 && rows[0].GetProperty("id").AsGuid() == second.Id && rows[0].GetProperty("grabId").AsGuid() == grabId &&
            rows[0].GetProperty("state").GetString() == "seeding",
            "The queue keeps one seeding row, the new import's: " + string.Join(" | ", rows.Select(row => row.GetRawText()[..Math.Min(200, row.GetRawText().Length)])));

        // The run's last check finds no media_missing: this scenario's own goes with it.
        await using (var database = new ModDbContext(dbPath))
            await database.History.Where(history => history.EntryId == entryId && history.EventType == "media_missing").ExecuteDeleteAsync();
    }
}
