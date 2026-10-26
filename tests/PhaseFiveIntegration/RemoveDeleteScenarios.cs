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
