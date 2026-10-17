using System.Net;
using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Placeholders;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The backfill of an existing catalogue and the watched restore (P1.FU27), through the same real host, HTTP, SQLite and real files
/// as the rest of Phase 5. Twenty-five series that existed before placeholders (no file, two aired episodes each) converge in three
/// Sync Now batches of at most ten series, with the progress counters moving each time; a batch gives way to a running library
/// scan and writes nothing; each batch asks Jellyfin to look at its folders once; a second Sync Now while one runs is refused; no
/// episode ever gets a second stub. A reclaimed series' placeholder gets its watched mark back from the plugin's retention
/// history only for the user Jellyfin holds nothing for, never over another user's data or from a revoked or unreclaimed record,
/// and a second restore changes nothing.
/// </summary>
internal static class BackfillScenarios
{
    private const int Series = 25;

    public static async Task RunAsync(HttpClient admin, HttpClient ordinary, World world, string dbPath)
    {
        Assert((await ordinary.GetAsync("/JellyfinMod/Placeholders/Progress")).StatusCode == HttpStatusCode.Forbidden &&
            (await ordinary.PostAsync("/JellyfinMod/Placeholders/SyncNow", null)).StatusCode == HttpStatusCode.Forbidden &&
            (await ordinary.GetAsync("/JellyfinMod/Placeholders/RestoreWatched")).StatusCode == HttpStatusCode.Forbidden &&
            (await ordinary.PostAsync("/JellyfinMod/Placeholders/RestoreWatched", null)).StatusCode == HttpStatusCode.Forbidden,
            "Progress, Sync Now and the watched restore are administrators only");

        // Whatever the earlier scenarios left pending is written first, so the counts below are this scenario's own.
        for (var round = 0; round < 6 && TvRow(await ProgressAsync(admin)).GetProperty("pending").GetInt32() > 0; round++)
            await ReadAsync(await admin.PostAsync("/JellyfinMod/Placeholders/SyncNow", null), 200, "Sync Now (settling)");
        var before = TvRow(await ProgressAsync(admin));
        Assert(before.GetProperty("pending").GetInt32() == 0, "The TV library starts with nothing pending: " + before.GetRawText());
        var titlesBefore = before.GetProperty("titles").GetInt32();
        // Reclaimed titles of the earlier scenarios count in the restore too: this scenario's marks are measured from here.
        var restoreBefore = await ReadAsync(await admin.GetAsync("/JellyfinMod/Placeholders/RestoreWatched"), 200, "Restore dry run before seeding");
        var waitingBefore = restoreBefore.GetProperty("waiting").GetInt32();
        var keptBefore = restoreBefore.GetProperty("kept").GetInt32();
        Assert(restoreBefore.GetProperty("items").GetArrayLength() == 0, "Nothing to restore before this scenario: " + restoreBefore.GetRawText());

        var entries = new List<Guid>();
        Guid reclaimedEntry;
        Guid reclaimedEpisode;
        Guid unreclaimedEpisode;
        await using (var database = new ModDbContext(dbPath))
        {
            for (var index = 0; index < Series; index++)
            {
                var entry = SeriesEntry(world, 7100 + index, $"Backfill Show {index:00}");
                database.Entries.Add(entry);
                entries.Add(entry.Id);
                for (var number = 1; number <= 2; number++)
                    database.Episodes.Add(new Episode
                    {
                        EntryId = entry.Id, TmdbId = 710000 + index * 10 + number, SeasonNumber = 1, EpisodeNumber = number,
                        Title = $"Episode {number}", RuntimeMinutes = 30, AirDate = new DateTime(2020, 1, number, 0, 0, 0, DateTimeKind.Utc)
                    });
            }

            // A series retention reclaimed before placeholders existed: S01E01 reclaimed, S01E02 never downloaded.
            var reclaimed = SeriesEntry(world, 7200, "Reclaimed Show");
            reclaimed.State = FileState.Reclaimed;
            database.Entries.Add(reclaimed);
            var first = new Episode
            {
                EntryId = reclaimed.Id, TmdbId = 720001, SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot", RuntimeMinutes = 30,
                AirDate = new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc), State = FileState.Reclaimed
            };
            var second = new Episode
            {
                EntryId = reclaimed.Id, TmdbId = 720002, SeasonNumber = 1, EpisodeNumber = 2, Title = "Second", RuntimeMinutes = 30,
                AirDate = new DateTime(2019, 3, 8, 0, 0, 0, DateTimeKind.Utc)
            };
            database.Episodes.AddRange(first, second);
            reclaimedEntry = reclaimed.Id;
            reclaimedEpisode = first.Id;
            unreclaimedEpisode = second.Id;
            var watched = new DateTime(2025, 5, 4, 20, 0, 0, DateTimeKind.Utc);
            CompletionObservation Record(Guid episode, Guid user, string reason = "PlaybackStopped") => new()
            {
                EntryId = reclaimed.Id, EpisodeId = episode, TargetId = episode, UserId = user, JellyfinItemId = Guid.NewGuid(),
                EvidenceAvailable = true, Played = true, CompletedAt = watched, LastPlayedAt = watched, ObservedAt = watched, SourceReason = reason
            };
            database.CompletionObservations.AddRange(
                Record(first.Id, world.Admin.Id),
                Record(first.Id, world.Ordinary.Id),
                Record(first.Id, world.RestrictedAdmin.Id, "UnwatchedSeenLive"),
                Record(second.Id, world.Admin.Id));
            await database.SaveChangesAsync();
        }

        var plan = TvRow(await ProgressAsync(admin));
        Assert(plan.GetProperty("titles").GetInt32() == titlesBefore + Series + 1 && plan.GetProperty("pending").GetInt32() == Series * 2 + 2 &&
            plan.GetProperty("titlesDone").GetInt32() == titlesBefore,
            "Progress counts every series of the existing catalogue as waiting for its placeholders: " + plan.GetRawText());

        // A running library scan: the batch writes nothing and says why.
        world.Native.ScanRunning = true;
        var deferred = await ReadAsync(await admin.PostAsync("/JellyfinMod/Placeholders/SyncNow", null), 200, "Sync Now during a library scan");
        world.Native.ScanRunning = false;
        Assert(deferred.GetProperty("deferred").GetString() == "library_scan" && TvRow(deferred).GetProperty("pending").GetInt32() == Series * 2 + 2 &&
            Stubs(world).Length == 0, "While Jellyfin scans a library the backfill writes nothing and waits: " + deferred.GetRawText());

        // Three batches of at most ten series: the counters move each time, and each batch reports its folders once.
        var pendingTitles = Series + 1;
        for (var batch = 1; batch <= 3; batch++)
        {
            var scansBefore = world.Native.ScanRequests.Count;
            var running = admin.PostAsync("/JellyfinMod/Placeholders/SyncNow", null);
            if (batch == 1)
            {
                await Task.Delay(1500);
                var second = await admin.PostAsync("/JellyfinMod/Placeholders/SyncNow", null);
                var body = await second.Content.ReadAsStringAsync();
                Assert(second.StatusCode == HttpStatusCode.Conflict && body.Contains("sync_running", StringComparison.Ordinal),
                    $"A second Sync Now while a batch runs is refused ({(int)second.StatusCode} {body})");
            }

            var after = TvRow(await ReadAsync(await running, 200, $"Sync Now, batch {batch}"));
            pendingTitles = Math.Max(0, pendingTitles - PlaceholderService.SeriesPerRun);
            Assert(after.GetProperty("titlesDone").GetInt32() == titlesBefore + Series + 1 - pendingTitles &&
                after.GetProperty("pending").GetInt32() == pendingTitles * 2,
                $"Batch {batch} writes ten series and the counters follow: " + after.GetRawText());
            var reported = world.Native.ScanRequests.Skip(scansBefore).ToArray();
            Assert(reported.Length == 1 && reported[0] == world.Tv.Location,
                $"Batch {batch} asks Jellyfin to look once, at the library folder its new series folders are in: " + string.Join(", ", reported));
        }

        var stubs = Stubs(world);
        Assert(stubs.Length == Series * 2 + 2 && stubs.GroupBy(path => Path.GetFileName(path)).All(group => group.Count() == 1),
            $"Every aired episode of the catalogue has exactly one placeholder ({stubs.Length})");
        var idle = await ReadAsync(await admin.PostAsync("/JellyfinMod/Placeholders/SyncNow", null), 200, "Sync Now with nothing pending");
        Assert(Stubs(world).Length == stubs.Length && idle.GetProperty("pending").GetInt32() == 0 && idle.GetProperty("nextRunMinutes").GetInt32() >= 5,
            "Another Sync Now writes nothing more, and the scheduled pace goes back to idle: " + idle.GetRawText());

        // The watched restore. The reclaimed episode's placeholder item, before Jellyfin's first refresh of it: the restore waits.
        var item = await WaitAsync(() => Task.FromResult(world.Native.Items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
            .FirstOrDefault(value => value.IsPlaceHolder && value.Path is { } path && path.Contains("Reclaimed Show", StringComparison.Ordinal) &&
                value.IndexNumber == 1)), "The reclaimed episode's placeholder is a Jellyfin item");
        item.DateLastRefreshed = DateTime.MinValue;
        var early = await ReadAsync(await admin.GetAsync("/JellyfinMod/Placeholders/RestoreWatched"), 200, "Restore dry run before the refresh");
        for (var attempt = 0; attempt < 30 && !(early.GetProperty("waiting").GetInt32() == waitingBefore + 2 && early.GetProperty("items").GetArrayLength() == 0); attempt++)
        {
            await Task.Delay(1000);
            early = await ReadAsync(await admin.GetAsync("/JellyfinMod/Placeholders/RestoreWatched"), 200, "Restore dry run before the refresh");
        }

        Assert(early.GetProperty("waiting").GetInt32() == waitingBefore + 2 && early.GetProperty("items").GetArrayLength() == 0,
            "Before Jellyfin's first refresh of the placeholder the restore waits for it: " + early.GetRawText() + " item " + item.Id +
            " refreshed " + item.DateLastRefreshed.ToString("O") + " series " + item.SeriesId);

        // After the refresh: the ordinary user has data of their own on it (re-attached, or set since), the administrator none.
        item.DateLastRefreshed = DateTime.UtcNow;
        item.UserData = [new Jellyfin.Database.Implementations.Entities.UserData
        {
            ItemId = item.Id, UserId = world.Ordinary.Id, CustomDataKey = item.Id.ToString("N"), Played = false, Item = null!, User = null!
        }];
        var dry = await ReadAsync(await admin.GetAsync("/JellyfinMod/Placeholders/RestoreWatched"), 200, "Restore dry run");
        var rows = dry.GetProperty("items").EnumerateArray().ToArray();
        Assert(dry.GetProperty("dryRun").GetBoolean() && rows.Length == 1 && rows[0].GetProperty("userId").AsGuid() == world.Admin.Id &&
            rows[0].GetProperty("itemId").AsGuid() == item.Id && rows[0].GetProperty("seasonNumber").GetInt32() == 1 &&
            rows[0].GetProperty("playedAt").GetDateTime() == new DateTime(2025, 5, 4, 20, 0, 0, DateTimeKind.Utc) &&
            dry.GetProperty("kept").GetInt32() == keptBefore + 1 && world.Native.SavedUserData.IsEmpty,
            "The dry run lists one mark (the administrator's, with the recorded date), keeps the ordinary user's own data, ignores a " +
            "revoked record and an episode never reclaimed, and saves nothing: " + dry.GetRawText());
        var applied = await ReadAsync(await admin.PostAsync("/JellyfinMod/Placeholders/RestoreWatched", null), 200, "Restore");
        var saved = world.Native.SavedUserData.ToArray();
        Assert(!applied.GetProperty("dryRun").GetBoolean() && applied.GetProperty("items").GetArrayLength() == 1 && saved.Length == 1 &&
            saved[0].UserId == world.Admin.Id && saved[0].ItemId == item.Id && saved[0].Data.Played &&
            saved[0].Data.LastPlayedDate == new DateTime(2025, 5, 4, 20, 0, 0, DateTimeKind.Utc),
            "The restore marks the placeholder watched for the administrator alone, with the recorded date: " + applied.GetRawText());
        var again = await ReadAsync(await admin.PostAsync("/JellyfinMod/Placeholders/RestoreWatched", null), 200, "Restore again");
        Assert(again.GetProperty("items").GetArrayLength() == 0 && again.GetProperty("kept").GetInt32() == keptBefore + 2 && world.Native.SavedUserData.Count == 1,
            "A second restore changes nothing: " + again.GetRawText());
        await using (var database = new ModDbContext(dbPath))
        {
            Assert(await database.History.CountAsync(history => history.EntryId == reclaimedEntry && history.EventType == "watched_restored") == 1,
                "The restore leaves one history line on the title");
            Assert(await database.Episodes.CountAsync(episode => (episode.Id == reclaimedEpisode && episode.State == FileState.Reclaimed) ||
                (episode.Id == unreclaimedEpisode && episode.State == FileState.None)) == 2, "The restore changes no catalog state");
        }

        // The fixtures go: removing each title removes its placeholders and the folders made for them.
        foreach (var id in entries.Append(reclaimedEntry))
            Assert((await admin.DeleteAsync($"/JellyfinMod/Entries/{id}")).StatusCode == HttpStatusCode.NoContent, "A backfill fixture is removed");
        Assert(Stubs(world).Length == 0, "No backfill placeholder is left");
    }

    private static Entry SeriesEntry(World world, int tmdbId, string title) => new()
    {
        MediaType = "series", TmdbId = tmdbId, Title = title, Year = 2020, TargetLibraryId = world.Tv.Id,
        MetadataJson = JsonSerializer.Serialize(new TmdbMetadata("series", tmdbId, title, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, false, null, 100, [], [], [new TmdbSeason(1, "Season 1", 2, null, null)]))
    };

    private static string[] Stubs(World world) => Directory.EnumerateFiles(world.Tv.Location, "*.disc", SearchOption.AllDirectories)
        .Where(path => path.Contains("Backfill Show", StringComparison.Ordinal) || path.Contains("Reclaimed Show", StringComparison.Ordinal)).ToArray();

    private static async Task<JsonElement> ProgressAsync(HttpClient admin) =>
        await ReadAsync(await admin.GetAsync("/JellyfinMod/Placeholders/Progress"), 200, "Progress");

    private static JsonElement TvRow(JsonElement progress) =>
        progress.GetProperty("libraries").EnumerateArray().Single(row => row.GetProperty("mediaType").GetString() == "series" &&
            row.GetProperty("writable").GetBoolean() && row.GetProperty("name").GetString() == "TV");

    private static void Assert(bool condition, string message) => Phase5.Assert(condition, message);

    private static Task<T> WaitAsync<T>(Func<Task<T?>> probe, string message, int seconds = 30) where T : class => Phase5.WaitAsync(probe, message, seconds);

    private static Task<bool> WaitAsync(Func<Task<bool?>> probe, string message, int seconds = 30) => Phase5.WaitAsync(probe, message, seconds);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int status, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert((int)response.StatusCode == status, $"{message}: expected {status}, got {(int)response.StatusCode} {body}");
        return Json.Parse(body);
    }
}
