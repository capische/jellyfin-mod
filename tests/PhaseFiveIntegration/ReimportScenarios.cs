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
        var request = new
        {
            searchId = search.GetProperty("searchId").AsGuid(), releaseId = candidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-again-" + Guid.NewGuid().ToString("N")
        };
        // The episode scenario reuses this key and this grab for the checks across the two key stores.
        ids["movieAgainGrab"] = grabId;
        ids["movieAgainKey"] = Guid.Parse(request.idempotencyKey["p5-again-".Length..]);
        var again = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", request), 200,
            "Adding the seeding release again answers with its earlier grab");
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

        // The same request replayed, as after a lost answer: the same grab, and no third import (Codex review of P4.FU29, finding 3).
        var replayed = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", request), 200, "The re-import request replayed");
        for (var index = 0; index < 3; index++) await tick();
        Assert(replayed.GetProperty("id").AsGuid() == grabId && await CountImports(dbPath, grabId) == 2 && transmission.AddCalls == addsBefore,
            $"A replay answers with the same grab and starts nothing: {replayed.GetProperty("id").GetString()}, imports {await CountImports(dbPath, grabId)}");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new { request.searchId, releaseId = "another-release", request.idempotencyKey }),
            409, "idempotency_conflict", "The re-import's key used for another release is refused");

        // The run's last check finds no media_missing: this scenario's own goes with it.
        await using (var database = new ModDbContext(dbPath))
            await database.History.Where(history => history.EntryId == entryId && history.EventType == "media_missing").ExecuteDeleteAsync();
    }

    /// <summary>
    /// An episode imported again from its seeding copy is claimed while the import runs: a season pack covering it is refused
    /// until it is in (Codex review of P4.FU29, finding 2), and a replay of the request while it runs starts nothing.
    /// </summary>
    private static async Task ReimportEpisodeAsync(Func<Task> tick, HttpClient admin, World world, string dbPath, Dictionary<string, Guid> ids,
        TorznabBoundary torznab, TransmissionBoundary transmission, UnixFileInspector inspector)
    {
        var folder = new System.Text.RegularExpressions.Regex(@"\[tmdbid-912\].*S(\d{2})E(\d{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var earlier = world.Native.EpisodeTmdbId;
        world.Native.EpisodeTmdbId = file => folder.Match(file) is { Success: true } match
            ? 91200 + int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 10 +
                int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
            : earlier?.Invoke(file);
        Guid entryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = new Entry
            {
                MediaType = "series", TmdbId = 912, Title = "Again Show", Year = 2022, TargetLibraryId = world.Tv.Id,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new TmdbMetadata("series", 912, "Again Show",
                    new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, 1012, false, null, 100, [], [],
                    [new TmdbSeason(1, "Season 1", 2, null, null)]))
            };
            database.Entries.Add(entry);
            entryId = ids["showAgain"] = entry.Id;
            foreach (var number in new[] { 1, 2 })
            {
                var row = new Episode
                {
                    EntryId = entry.Id, TmdbId = 91210 + number, SeasonNumber = 1, EpisodeNumber = number, Title = $"Again Show 1x{number}",
                    RuntimeMinutes = 45, AirDate = new DateTime(2022, 3, 10 + number, 0, 0, 0, DateTimeKind.Utc)
                };
                database.Episodes.Add(row);
                ids[$"showAgains1e{number}"] = row.Id;
            }

            await database.SaveChangesAsync();
        }

        var episodeId = ids["showAgains1e1"];
        const string title = "Again.Show.S01E01.1080p.WEB-DL-GRP";
        var fixture = TorrentFixture.Single(title + ".mkv", Size);
        torznab.Torrents["showAgainE1"] = fixture.Bytes;
        transmission.Register(fixture);
        var pack = TorrentFixture.Multi("Again.Show.S01.1080p.WEB-DL-GRP", ("Again.Show.S01E01.1080p.WEB-DL-GRP.mkv", Size),
            ("Again.Show.S01E02.1080p.WEB-DL-GRP.mkv", Size));
        torznab.Torrents["showAgainPack"] = pack.Bytes;
        transmission.Register(pack);
        lock (torznab.MovieItems)
        {
            torznab.TvItems.Add(new(title, "guid-showAgainE1", torznab.Download("showAgainE1"), Size, 25, new() { ["tvdbid"] = "1012" }));
            torznab.TvItems.Add(new("Again.Show.S01.1080p.WEB-DL-GRP", "guid-showAgainPack", torznab.Download("showAgainPack"), Size * 2, 25,
                new() { ["tvdbid"] = "1012" }));
        }

        uint Links(string path) => inspector.TryInspect(path, out var file) ? file.HardlinkCount : 0;
        // An ordinary grab whose key is kept, for the check across the two key stores below.
        var ordinaryKey = "p5-again-ordinary-" + Guid.NewGuid().ToString("N");
        var episodeSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}&episodeId={episodeId}"));
        var grabbed = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = episodeSearch.GetProperty("searchId").AsGuid(),
            releaseId = episodeSearch.GetProperty("candidates").EnumerateArray().Single(value => value.GetProperty("rawTitle").GetString() == title)
                .GetProperty("releaseId").GetString(),
            idempotencyKey = ordinaryKey
        }), 202, "Administrator grabs " + title);
        var grabId = grabbed.GetProperty("id").AsGuid();
        var hash = fixture.InfoHash;
        await WaitAsync(async () => Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Grabs/{grabId}")).GetProperty("state").GetString() ==
            "accepted" ? true : (bool?)null, "The episode's grab is accepted");
        transmission.Progress(hash, 1.0);
        var first = await CompleteAsync(dbPath, grabId, tick);

        // The key one store holds is refused in the other (Codex re-review of P4.FU29): the ordinary grab's key on a re-import
        // request for another title, and the movie re-import's key on an ordinary grab of this one.
        var movieGrab = ids["movieAgainGrab"];
        var movieImports = await CountImports(dbPath, movieGrab);
        var movieSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={ids["movieAgain"]}"));
        var movieRelease = movieSearch.GetProperty("candidates").EnumerateArray()
            .Single(value => value.GetProperty("rawTitle").GetString() == "Again.Movie.2021.1080p.WEB-DL-GRP").GetProperty("releaseId").GetString();
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = movieSearch.GetProperty("searchId").AsGuid(), releaseId = movieRelease, idempotencyKey = ordinaryKey
        }), 409, "idempotency_conflict", "An ordinary grab's key reused by a re-import request for another title is refused");
        Assert(await CountImports(dbPath, movieGrab) == movieImports, "The refused request imported nothing");
        var item = world.Native.Items.Single(value => value.Path == first.DestinationPath);
        File.Delete(first.DestinationPath!);
        world.Native.Remove(item);
        await WaitAsync(async () =>
        {
            await using var database = new ModDbContext(dbPath);
            return !await database.EpisodeBindings.AnyAsync(binding => binding.EpisodeId == episodeId) &&
                (await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == episodeId)).State != FileState.OnDisk ? true : null;
        }, "Deleting the episode clears its binding");

        // The client is out while the copy is imported again, so the import waits and the claim can be tried.
        transmission.Offline = true;
        await tick();
        var search = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}&episodeId={episodeId}"));
        var candidate = search.GetProperty("candidates").EnumerateArray().Single(value => value.GetProperty("rawTitle").GetString() == title);
        var request = new
        {
            searchId = search.GetProperty("searchId").AsGuid(), releaseId = candidate.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-again-episode-" + Guid.NewGuid().ToString("N")
        };
        var addsBefore = transmission.AddCalls;
        var again = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", request), 200, "Adding the seeding episode again");
        Assert(again.GetProperty("id").AsGuid() == grabId, "The earlier grab answers: " + again.GetRawText());
        await tick();
        Assert((await ImportFor(dbPath, grabId)) is { State: ImportStates.Waiting } waiting && waiting.Id != first.Id,
            "The episode's new import waits for the client");

        var packSearch = Json.Parse(await admin.GetStringAsync($"/JellyfinMod/Releases?entryId={entryId}&scope=season&seasonNumber=1"));
        var packRow = packSearch.GetProperty("candidates").EnumerateArray().Single(value => value.GetProperty("rawTitle").GetString() ==
            "Again.Show.S01.1080p.WEB-DL-GRP");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = packSearch.GetProperty("searchId").AsGuid(), releaseId = packRow.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-again-" + ids["movieAgainKey"].ToString("N")
        }), 409, "idempotency_conflict", "A re-import's key reused by an ordinary grab of another title is refused");
        await ExpectAsync(admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", new
        {
            searchId = packSearch.GetProperty("searchId").AsGuid(), releaseId = packRow.GetProperty("releaseId").GetString(),
            idempotencyKey = "p5-again-pack-" + Guid.NewGuid().ToString("N")
        }), 409, "grab_active", "A season pack covering the episode being imported again is refused");
        var replayed = await ReadAsync(await admin.PostAsJsonAsync("/JellyfinMod/Releases/Grab", request), 200, "The request replayed while it imports");
        Assert(replayed.GetProperty("id").AsGuid() == grabId && await CountImports(dbPath, grabId) == 2 && transmission.AddCalls == addsBefore,
            $"The replay starts nothing: imports {await CountImports(dbPath, grabId)}, adds {transmission.AddCalls - addsBefore}");

        transmission.Offline = false;
        var second = await WaitAsync(async () =>
        {
            await tick();
            var latest = await ImportFor(dbPath, grabId);
            Assert(latest.State is not (ImportStates.Blocked or ImportStates.Failed), $"The import stopped: {latest.State} {latest.Reason} {latest.Error}");
            return latest.Id != first.Id && latest.State == ImportStates.Completed ? latest : null;
        }, "The seeding episode is imported again");
        Assert(Links(second.SourceLocalPath!) == 2 && await CountImports(dbPath, grabId) == 2,
            $"The episode is hardlinked again from its seeding file: links {Links(second.SourceLocalPath!)}");
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(!await database.GrabClaims.AnyAsync(claim => claim.GrabId == grabId && claim.ActiveKey != null),
                "Once imported, the episode's claim is released again");
            await database.History.Where(history => history.EntryId == entryId &&
                (history.EventType == "media_missing" || history.EventType == "episode_media_missing")).ExecuteDeleteAsync();
        }
    }
}
