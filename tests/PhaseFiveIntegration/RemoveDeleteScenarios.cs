using System.Diagnostics;
using System.Net.Http.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Remove from Queue can delete the downloaded files (user, 2026-10-26), through the same real host, HTTP, SQLite, Torznab and
/// Transmission boundaries and real files as the rest of Phase 5: <c>deleteFiles</c> without the client removal is refused; a
/// download with a file linked elsewhere is refused; a download not yet imported is deleted by the client's delete-with-data
/// and nothing else moves; an imported download is refused with every file and link count as it was. The pack case (one
/// episode imported early) is in <see cref="NextUpScenarios"/>.
/// </summary>
internal static partial class Phase5
{
    private static async Task RemoveDeleteAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission, UnixFileInspector inspector)
    {
        await using (var database = new ModDbContext(dbPath))
        {
            AddEntry(database, ids, "movieDel", "movie", 131, "Delete Movie", 2021, "tt0000131", null, world.Movies.Id);
            AddEntry(database, ids, "movieKeep", "movie", 132, "Keep Movie", 2021, "tt0000132", null, world.Movies.Id);
            AddEntry(database, ids, "movieFail", "movie", 133, "Failed Movie", 2021, "tt0000133", null, world.Movies.Id);
            AddEntry(database, ids, "movieGone", "movie", 134, "Gone Movie", 2021, "tt0000134", null, world.Movies.Id);
            await database.SaveChangesAsync();
        }

        TorrentFixture Fixture(string key, string title, string imdb)
        {
            var fixture = TorrentFixture.Single(title + ".mkv", Size);
            torznab.Torrents[key] = fixture.Bytes;
            transmission.Register(fixture);
            lock (torznab.MovieItems)
                torznab.MovieItems.Add(new(title, "guid-" + key, torznab.Download(key), Size, 25, new() { ["imdbid"] = imdb }));
            return fixture;
        }

        HttpRequestMessage Remove(Guid id, object body) =>
            new(HttpMethod.Delete, $"/JellyfinMod/Queue/{id}") { Content = JsonContent.Create(body) };

        string DataPath(string hash, TorrentFixture fixture) => transmission.Local(transmission.Torrents[hash].DownloadDir + "/" + fixture.Name);

        uint Links(string path) => inspector.TryInspect(path, out var file) ? file.HardlinkCount : 0;

        // ---------------- A download not yet imported: refused without the client removal and while a file has a second
        // name, then deleted by the client with nothing else touched.
        var fixtureDel = Fixture("movieDel", "Delete.Movie.2021.1080p.WEB-DL-GRP", "0000131");
        var (grabDel, hashDel) = await GrabAsync(admin, ids["movieDel"], null, fixtureDel);
        transmission.Progress(hashDel, 0.4);
        var waitingDel = await WaitAsync(async () =>
        {
            await tick();
            var operation = await ImportFor(dbPath, grabDel);
            return operation.State == ImportStates.Waiting && operation.Progress is > 0 and < 1 ? operation : null;
        }, "Delete Movie is downloading");
        var partial = DataPath(hashDel, fixtureDel);
        var libraryBefore = LibraryFiles(world);
        Assert(File.Exists(partial) && new FileInfo(partial).Length > 0, "The partial download is on disk: " + partial);

        await ExpectAsync(admin.SendAsync(Remove(waitingDel.Id, new { removeFromClient = false, deleteFiles = true })), 409, "delete_not_allowed",
            "Deleting the files without removing the torrent from the client is refused");
        Assert(transmission.Torrents.ContainsKey(hashDel) && File.Exists(partial) && (await ImportFor(dbPath, grabDel)).State == ImportStates.Waiting,
            "The refused removal changed nothing");

        // A second name for the partial file, as a person's own hardlink outside every library: its data is never deleted.
        var second = Path.Combine(Path.GetDirectoryName(partial)!, "..", "delete-movie-second-name.mkv");
        using (var link = Process.Start("ln", [partial, second])!)
        {
            await link.WaitForExitAsync();
            Assert(link.ExitCode == 0 && Links(partial) == 2, "The partial file has a second name");
        }

        await ExpectAsync(admin.SendAsync(Remove(waitingDel.Id, new { removeFromClient = true, deleteFiles = true })), 409, "delete_not_allowed",
            "A download with a file linked elsewhere is never deleted");
        Assert(transmission.Torrents.ContainsKey(hashDel) && File.Exists(partial) && File.Exists(second),
            "The refused removal kept the torrent and both names");
        File.Delete(second);

        var deleteRequestsBefore = transmission.RemoveDeletedData.Count;
        var removedDel = await ReadAsync(await admin.SendAsync(Remove(waitingDel.Id, new { removeFromClient = true, blocklist = false, deleteFiles = true })),
            200, "Remove a download not yet imported and delete its files");
        Assert(removedDel.GetProperty("state").GetString() == "cancelled" && !transmission.Torrents.ContainsKey(hashDel) && !File.Exists(partial) &&
            transmission.RemoveDeletedData.Skip(deleteRequestsBefore).SequenceEqual([true]) && LibraryFiles(world).SequenceEqual(libraryBefore),
            $"The client forgot the torrent and deleted its partial file, and the library is as it was: torrent {transmission.Torrents.ContainsKey(hashDel)}, " +
            $"file {File.Exists(partial)}, delete-data [{string.Join(",", transmission.RemoveDeletedData.Skip(deleteRequestsBefore))}]");
        await using (var database = new ModDbContext(dbPath))
        {
            var history = await database.History.AsNoTracking().SingleAsync(value => value.EntryId == ids["movieDel"] && value.EventType == "queue_removed");
            Assert(history.Summary!.StartsWith("Removed from the queue and from the download client, and its downloaded files were deleted: ",
                    StringComparison.Ordinal) && history.Data!.Contains("\"deleteFiles\":true", StringComparison.Ordinal),
                "The history says the downloaded files were deleted: " + history.Summary);
        }

        // ---------------- A failed row whose torrent is still in the client: its own hash is read although no open import
        // tracks it, so its files are deleted (Codex review 2026-10-26, P2 6). The failure is written as FailAsync writes it.
        var fixtureFail = Fixture("movieFail", "Failed.Movie.2021.1080p.WEB-DL-GRP", "0000133");
        var (grabFail, hashFail) = await GrabAsync(admin, ids["movieFail"], null, fixtureFail);
        transmission.Progress(hashFail, 0.4);
        var waitingFail = await WaitAsync(async () =>
        {
            await tick();
            var operation = await ImportFor(dbPath, grabFail);
            return operation.State == ImportStates.Waiting && operation.Progress is > 0 and < 1 ? operation : null;
        }, "Failed Movie is downloading");
        var partialFail = DataPath(hashFail, fixtureFail);
        Assert(File.Exists(partialFail), "The failed row's partial download is on disk: " + partialFail);
        await using (var database = new ModDbContext(dbPath))
            await database.ImportOperations.Where(value => value.Id == waitingFail.Id).ExecuteUpdateAsync(set => set
                .SetProperty(value => value.State, ImportStates.Failed).SetProperty(value => value.Reason, ImportReasons.TargetMissing)
                .SetProperty(value => value.OpenGrabKey, (string?)null).SetProperty(value => value.CompletedAt, DateTime.UtcNow));
        var failDeletesBefore = transmission.RemoveDeletedData.Count;
        var removedFail = await ReadAsync(await admin.SendAsync(Remove(waitingFail.Id, new { removeFromClient = true, deleteFiles = true })),
            200, "Remove a failed row not yet imported and delete its files");
        Assert(removedFail.GetProperty("state").GetString() == "cancelled" && !transmission.Torrents.ContainsKey(hashFail) && !File.Exists(partialFail) &&
            transmission.RemoveDeletedData.Skip(failDeletesBefore).SequenceEqual([true]),
            $"The client forgot the failed row's torrent and deleted its partial file: torrent {transmission.Torrents.ContainsKey(hashFail)}, " +
            $"file {File.Exists(partialFail)}, delete-data [{string.Join(",", transmission.RemoveDeletedData.Skip(failDeletesBefore))}]");

        // ---------------- An imported, seeding download whose torrent was removed outside the plugin: deleting is asked for, and
        // the row goes with nothing deleted and nothing refused (Codex review 2026-10-26, P2 7).
        var fixtureGone = Fixture("movieGone", "Gone.Movie.2021.1080p.WEB-DL-GRP", "0000134");
        var (grabGone, hashGone) = await GrabAsync(admin, ids["movieGone"], null, fixtureGone);
        transmission.Progress(hashGone, 1.0);
        var completedGone = await CompleteAsync(dbPath, grabGone, tick);
        var seedingGone = completedGone.SourceLocalPath!;
        var libraryGone = completedGone.DestinationPath!;
        var goneLinks = Links(libraryGone);
        Assert(goneLinks == 2, $"The gone movie's import is a hardlink: {goneLinks}");
        Assert(Row(Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue")), completedGone.Id).GetProperty("state").GetString() == "seeding",
            "The gone movie is seeding in the queue");
        // As a seed release leaves it when its torrent-remove went through and then timed out: removing, the torrent gone.
        Assert(transmission.Torrents.TryRemove(hashGone, out _), "The torrent is removed in the client");
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedGone.Id);
            seed.State = SeedReleaseStates.Removing;
            seed.Reason = null;
            seed.RemovingAt = DateTime.UtcNow;
            await database.SaveChangesAsync();
        }

        await ExpectAsync(admin.SendAsync(Remove(completedGone.Id, new { removeFromClient = true, deleteFiles = false })), 409,
            "seed_release_in_progress", "A removal that deletes nothing still waits for the seeding copy's release");
        var goneRemovalsBefore = transmission.RemoveCalls;
        var removedGone = await admin.SendAsync(Remove(completedGone.Id, new { removeFromClient = true, deleteFiles = true }));
        var removedGoneBody = await removedGone.Content.ReadAsStringAsync();
        Assert((int)removedGone.StatusCode == 200 && transmission.RemoveCalls == goneRemovalsBefore && File.Exists(seedingGone) &&
            File.Exists(libraryGone) && Links(libraryGone) == goneLinks,
            $"A torrent the client no longer has is not refused and nothing is deleted: {(int)removedGone.StatusCode} {removedGoneBody}, " +
            $"remove calls {transmission.RemoveCalls - goneRemovalsBefore}, links {Links(libraryGone)}");
        Assert(!Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue")).GetProperty("items").EnumerateArray()
                .Any(row => row.GetProperty("id").AsGuid() == completedGone.Id),
            "The gone movie's row left the queue");
        await using (var database = new ModDbContext(dbPath))
        {
            var history = await database.History.AsNoTracking().SingleAsync(value => value.EntryId == ids["movieGone"] && value.EventType == "queue_removed");
            Assert(history.Summary!.StartsWith("Removed from the queue; the download client no longer had the torrent, so nothing was deleted: ",
                    StringComparison.Ordinal) && history.Data!.Contains("\"deleteFiles\":false", StringComparison.Ordinal),
                "The history says nothing was deleted: " + history.Summary);
        }

        // ---------------- An imported download: refused, with the library file, the seeding file and their link count unchanged.
        var fixtureKeep = Fixture("movieKeep", "Keep.Movie.2021.1080p.WEB-DL-GRP", "0000132");
        var (grabKeep, hashKeep) = await GrabAsync(admin, ids["movieKeep"], null, fixtureKeep);
        transmission.Progress(hashKeep, 1.0);
        var completedKeep = await CompleteAsync(dbPath, grabKeep, tick);
        var seedingKeep = completedKeep.SourceLocalPath!;
        var libraryKeep = completedKeep.DestinationPath!;
        var linksBefore = Links(seedingKeep);
        Assert(linksBefore == 2 && Links(libraryKeep) == 2, $"The import is a hardlink: {linksBefore}");
        var removalsBefore = transmission.RemoveCalls;
        await ExpectAsync(admin.SendAsync(Remove(completedKeep.Id, new { removeFromClient = true, deleteFiles = true })), 409, "delete_not_allowed",
            "An imported download's files are never deleted");

        // The same while its seeding copy is being released and the client still has the torrent: delete_not_allowed, nothing
        // changed; the release is then put back as it was.
        string keepState;
        string? keepReason;
        DateTime? keepRemovingAt;
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedKeep.Id);
            (keepState, keepReason, keepRemovingAt) = (seed.State, seed.Reason, seed.RemovingAt);
            seed.State = SeedReleaseStates.Removing;
            seed.Reason = null;
            seed.RemovingAt = DateTime.UtcNow;
            await database.SaveChangesAsync();
        }

        await ExpectAsync(admin.SendAsync(Remove(completedKeep.Id, new { removeFromClient = true, deleteFiles = true })), 409, "delete_not_allowed",
            "A download whose seeding copy is being released, its torrent still in the client, is never deleted");
        await using (var database = new ModDbContext(dbPath))
        {
            var seed = await database.SeedReleaseOperations.SingleAsync(value => value.ImportOperationId == completedKeep.Id);
            Assert(seed.State == SeedReleaseStates.Removing, "The refused removal left the release as it was: " + seed.State);
            (seed.State, seed.Reason, seed.RemovingAt) = (keepState, keepReason, keepRemovingAt);
            await database.SaveChangesAsync();
        }

        Assert(transmission.Torrents.ContainsKey(hashKeep) && transmission.RemoveCalls == removalsBefore && File.Exists(seedingKeep) &&
            File.Exists(libraryKeep) && Links(seedingKeep) == linksBefore && Links(libraryKeep) == linksBefore,
            $"The refused removal kept the torrent, the seeding file and the library file, still {Links(seedingKeep)} links");
        var queue = Json.Parse(await admin.GetStringAsync("/JellyfinMod/Queue"));
        Assert(Row(queue, completedKeep.Id).GetProperty("state").GetString() == "seeding", "The seeding row stays in the queue");

        // The same removal without deleting: the client forgets the torrent and both files stay.
        await ReadAsync(await admin.SendAsync(Remove(completedKeep.Id, new { removeFromClient = true, deleteFiles = false })), 200,
            "Remove the imported download from the client, keeping its files");
        Assert(!transmission.Torrents.ContainsKey(hashKeep) && transmission.RemoveDeletedData.Last() == false && File.Exists(seedingKeep) &&
            File.Exists(libraryKeep) && Links(libraryKeep) == linksBefore, "Without deleteFiles nothing is deleted");
    }
}
