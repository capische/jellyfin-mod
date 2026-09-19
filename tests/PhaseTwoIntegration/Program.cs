using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.EntityFrameworkCore;

var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-two-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var dbPath = Path.Combine(folder, "reconciliation.db");
    await using (var database = new ModDbContext(dbPath))
    {
        await database.Database.MigrateAsync();
        var libraryLock = new ReconciliationLibraryLock();
        var service = new ReconciliationService(database, libraryLock);
        var movieLibrary = Guid.NewGuid();
        var tvLibrary = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        var movieCopyA = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var movieCopyB = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var movie = Snapshot("movie", 123, movieLibrary, "Shared numeric id", movieCopyB, movieCopyA);

        var created = await service.ReconcileAsync(movie, default);
        var movieEntry = await database.Entries.SingleAsync(entry => entry.Id == created.EntryId);
        Assert(created.Outcome == ReconciliationOutcome.Created && !movieEntry.Monitored &&
            movieEntry.JellyfinItemId == movieCopyA && movieEntry.State == FileState.OnDisk,
            "Backfill creates an unmonitored entry and deterministically chooses the lowest playable representation");
        Assert(await database.EntryBindings.CountAsync(binding => binding.EntryId == movieEntry.Id &&
                binding.TargetLibraryId == movieLibrary && binding.VersionGroupId == binding.JellyfinItemId) == 2,
            "Backfill durably records every observed native representation with library and version provenance");
        Assert(await database.History.CountAsync(history => history.EntryId == movieEntry.Id && history.EventType == "backfilled") == 1 &&
            !await database.History.AnyAsync(history => history.EntryId == movieEntry.Id && history.EventType == "added"),
            "Backfill records one distinct history event and never repeats an explicit-add event");

        movieEntry.Monitored = true;
        movieEntry.ReclaimAfterDays = 42;
        movieEntry.WatchedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await database.SaveChangesAsync();
        var repeated = await service.ReconcileAsync(movie, default);
        database.ChangeTracker.Clear();
        movieEntry = await database.Entries.SingleAsync(entry => entry.Id == created.EntryId);
        Assert(repeated.Outcome == ReconciliationOutcome.Unchanged && movieEntry.Monitored && movieEntry.ReclaimAfterDays == 42 &&
            movieEntry.WatchedAt == new DateTime(2026, 9, 1) && await database.History.CountAsync(history => history.EntryId == movieEntry.Id) == 1,
            "Repeated observation preserves identity, history and user preferences");

        var nonCanonicalConflict = await service.ReconcileAsync(
            Snapshot("movie", 456, movieLibrary, "Wrong identity", movieCopyB), default);
        Assert(nonCanonicalConflict.Outcome == ReconciliationOutcome.Conflict &&
            !await database.Entries.AnyAsync(entry => entry.MediaType == "movie" && entry.TmdbId == 456),
            "A non-canonical native copy cannot later bind to a conflicting catalog identity");

        var survivingCopy = Snapshot("movie", 123, movieLibrary, "Shared numeric id", movieCopyB);
        var rebound = await service.ReconcileAsync(survivingCopy, default);
        Assert(rebound.Outcome == ReconciliationOutcome.Updated &&
            (await database.Entries.SingleAsync(entry => entry.Id == movieEntry.Id)).JellyfinItemId == movieCopyB &&
            await database.History.CountAsync(history => history.EntryId == movieEntry.Id && history.EventType == "media_available") == 1,
            "A vanished binding moves to the deterministic surviving copy exactly once");
        Assert((await service.ReconcileAsync(survivingCopy, default)).Outcome == ReconciliationOutcome.Unchanged &&
            await database.History.CountAsync(history => history.EntryId == movieEntry.Id) == 2,
            "Repeating a replacement observation creates no duplicate transition event");

        var secondLibraryResult = await service.ReconcileAsync(Snapshot("movie", 123, secondLibrary, "Second library", Guid.NewGuid()), default);
        var seriesId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var seriesCopy = Guid.Parse("21000000-0000-0000-0000-000000000001");
        var episodeId = Guid.Parse("31000000-0000-0000-0000-000000000001");
        var episodeCopy = Guid.Parse("41000000-0000-0000-0000-000000000001");
        var specialId = Guid.Parse("32000000-0000-0000-0000-000000000001");
        var seriesResult = await service.ReconcileAsync(new NativeTitleSnapshot("series", 123, tvLibrary, "Series sharing 123", 2020,
            null, null, null, null, [new(seriesCopy, tvLibrary, false, seriesId), new(seriesId, tvLibrary, false, seriesId)],
            [new(episodeCopy, seriesCopy, 9001, 1, 1, true, "Pilot"),
                new(episodeId, seriesId, 9001, 1, 1, true, "Pilot"),
                new(specialId, seriesId, 9002, 0, 1, false, "Special")]), default);
        Assert(secondLibraryResult.Outcome == ReconciliationOutcome.Created && seriesResult.Outcome == ReconciliationOutcome.Created &&
            await database.Entries.CountAsync(entry => entry.TmdbId == 123) == 3,
            "Numeric TMDB identities remain isolated by media type and target library");

        var series = await database.Entries.SingleAsync(entry => entry.Id == seriesResult.EntryId);
        var trackedEpisode = await database.Episodes.SingleAsync(episode => episode.EntryId == series.Id && episode.TmdbId == 9001);
        var trackedEpisodeId = trackedEpisode.Id;
        var special = await database.Episodes.SingleAsync(episode => episode.EntryId == series.Id && episode.TmdbId == 9002);
        Assert(series.JellyfinItemId == seriesId && series.State == FileState.OnDisk &&
            trackedEpisode.JellyfinItemId == episodeId && !trackedEpisode.Monitored && trackedEpisode.State == FileState.OnDisk &&
            special.SeasonNumber == 0 && special.JellyfinItemId is null && special.State == FileState.None &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == series.Id) == 2 &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == series.Id &&
                binding.VersionGroupId == seriesId) == 2 &&
            await database.EpisodeBindings.CountAsync(binding => binding.TargetLibraryId == tvLibrary &&
                (binding.SeriesItemId == seriesId || binding.SeriesItemId == seriesCopy)) == 3,
            "First series observation records native version grouping, creates episodes with parent/library provenance and derives availability from playable children");

        trackedEpisode.Monitored = true;
        await database.SaveChangesAsync();
        var seriesCopyC = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var episodeCopyC = Guid.Parse("51000000-0000-0000-0000-000000000001");
        var seriesUpdate = await service.ReconcileAsync(new NativeTitleSnapshot("series", 123, tvLibrary, "Series sharing 123", 2020,
            null, null, null, null, [new(seriesCopyC, tvLibrary, false), new(seriesCopy, tvLibrary, false, seriesId),
                new(seriesId, tvLibrary, false, seriesId)],
            [new(episodeCopyC, seriesCopyC, 9001, 1, 2, true, "Pilot renamed"),
                new(episodeCopy, seriesCopy, 9001, 1, 2, true, "Pilot renamed"),
                new(episodeId, seriesId, 9001, 1, 2, true, "Pilot renamed"),
                new(specialId, seriesId, 9002, 0, 1, false, "Special")]), default);
        Assert(seriesUpdate.Outcome == ReconciliationOutcome.Updated && seriesUpdate.ChangedEpisodes == 1 &&
            trackedEpisode.Id == trackedEpisodeId && trackedEpisode.Monitored &&
            trackedEpisode.JellyfinItemId == episodeId && trackedEpisode.State == FileState.OnDisk &&
            trackedEpisode.SeasonNumber == 1 && trackedEpisode.EpisodeNumber == 2 && trackedEpisode.Title == "Pilot renamed",
            "Provider identity preserves the local episode and monitoring through metadata renumbering");
        Assert((await service.ReconcileAsync(new NativeTitleSnapshot("series", 123, tvLibrary, "Series sharing 123", 2020,
            null, null, null, null, [new(seriesCopyC, tvLibrary, false), new(seriesCopy, tvLibrary, false, seriesId),
                new(seriesId, tvLibrary, false, seriesId)],
            [new(episodeCopyC, seriesCopyC, 9001, 1, 2, true, "Pilot renamed"),
                new(episodeCopy, seriesCopy, 9001, 1, 2, true, "Pilot renamed"),
                new(episodeId, seriesId, 9001, 1, 2, true, "Pilot renamed"),
                new(specialId, seriesId, 9002, 0, 1, false, "Special")]), default)).Outcome == ReconciliationOutcome.Unchanged,
            "Repeated episode observations are idempotent");

        var conflictHistory = await database.History.CountAsync(history => history.EntryId == series.Id);
        var laterEpisode = Guid.Parse("52000000-0000-0000-0000-000000000001");
        NativeTitleSnapshot ConflictSnapshot() => new("series", 123, tvLibrary, "Series sharing 123", 2020,
            null, null, null, null, [new(seriesCopyC, tvLibrary, false), new(seriesCopy, tvLibrary, false, seriesId),
                new(seriesId, tvLibrary, false, seriesId)],
            [new(episodeCopyC, seriesCopyC, 9999, 1, 1, true),
                new(episodeCopy, seriesCopy, 9001, 1, 2, true, "Pilot renamed"),
                new(episodeId, seriesId, 9001, 1, 2, true, "Pilot renamed"),
                new(specialId, seriesId, 9002, 0, 1, false, "Special"),
                new(laterEpisode, seriesId, 9003, 1, 3, true, "Later")]);
        var episodeConflict = await service.ReconcileAsync(ConflictSnapshot(), default);
        database.ChangeTracker.Clear();
        var conflictRow = await database.EpisodeConflicts.SingleOrDefaultAsync(conflict => conflict.JellyfinItemId == episodeCopyC);
        Assert(episodeConflict.Outcome == ReconciliationOutcome.Updated && episodeConflict.EpisodeDiagnostics.Count == 1 &&
            (await database.Episodes.SingleAsync(episode => episode.Id == trackedEpisodeId)).JellyfinItemId == episodeId &&
            await database.EpisodeBindings.AnyAsync(binding => binding.JellyfinItemId == episodeCopyC &&
                binding.EpisodeId == trackedEpisodeId) &&
            conflictRow is { State: EpisodeConflictStates.Open, ObservedTmdbId: 9999 } &&
            conflictRow.EpisodeId == trackedEpisodeId,
            "A non-canonical episode copy cannot silently change provider identity; only it is skipped and recorded");
        Assert(await database.Episodes.AnyAsync(episode => episode.EntryId == series.Id && episode.TmdbId == 9003 &&
                episode.JellyfinItemId == laterEpisode && episode.State == FileState.OnDisk),
            "Other episodes of a series with one conflicting episode still bind (P2.R9)");
        conflictRow!.State = EpisodeConflictStates.Kept;
        database.EpisodeConflicts.Update(conflictRow);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var keptRun = await service.ReconcileAsync(ConflictSnapshot(), default);
        Assert(keptRun.Outcome == ReconciliationOutcome.Unchanged && keptRun.EpisodeDiagnostics.Count == 0 &&
            await database.EpisodeConflicts.AnyAsync(conflict => conflict.JellyfinItemId == episodeCopyC &&
                conflict.State == EpisodeConflictStates.Kept),
            "A kept identity is no longer reported while Jellyfin keeps reporting the same identity");
        trackedEpisode = await database.Episodes.SingleAsync(episode => episode.Id == trackedEpisodeId);

        // P2.R9: an S01E01-E02 file makes the tracked E02 available.
        var multiSeries = Guid.NewGuid();
        var firstEpisode = Guid.NewGuid();
        var multiResult = await service.ReconcileAsync(new NativeTitleSnapshot("series", 5550, tvLibrary, "Multi", 2020,
            null, null, null, null, [new(multiSeries, tvLibrary, false)],
            [new(firstEpisode, multiSeries, 5551, 1, 1, true, "One")]), default);
        database.Episodes.Add(new JellyfinMod.Data.Episode
        {
            EntryId = multiResult.EntryId!.Value, TmdbId = 5552, SeasonNumber = 1, EpisodeNumber = 2, Title = "Two"
        });
        await database.SaveChangesAsync();
        var doubleEpisode = Guid.NewGuid();
        await service.ReconcileAsync(new NativeTitleSnapshot("series", 5550, tvLibrary, "Multi", 2020,
            null, null, null, null, [new(multiSeries, tvLibrary, false)],
            [new(firstEpisode, multiSeries, 5551, 1, 1, true, "One"),
                new(doubleEpisode, multiSeries, null, 1, 3, true, "Three-Four", EpisodeNumberEnd: 4)]), default);
        database.ChangeTracker.Clear();
        database.Episodes.Add(new JellyfinMod.Data.Episode
        {
            EntryId = multiResult.EntryId!.Value, TmdbId = 5553, SeasonNumber = 1, EpisodeNumber = 3, Title = "Three"
        });
        database.Episodes.Add(new JellyfinMod.Data.Episode
        {
            EntryId = multiResult.EntryId!.Value, TmdbId = 5554, SeasonNumber = 1, EpisodeNumber = 4, Title = "Four"
        });
        await database.SaveChangesAsync();
        var combined = Guid.NewGuid();
        await service.ReconcileAsync(new NativeTitleSnapshot("series", 5550, tvLibrary, "Multi", 2020,
            null, null, null, null, [new(multiSeries, tvLibrary, false)],
            [new(combined, multiSeries, 5551, 1, 1, true, "One", EpisodeNumberEnd: 2),
                new(doubleEpisode, multiSeries, null, 1, 3, true, "Three-Four", EpisodeNumberEnd: 4)]), default);
        database.ChangeTracker.Clear();
        Assert(await database.Episodes.AnyAsync(episode => episode.TmdbId == 5552 && episode.JellyfinItemId == combined &&
                episode.State == FileState.OnDisk) &&
            await database.Episodes.AnyAsync(episode => episode.TmdbId == 5553 && episode.JellyfinItemId == doubleEpisode &&
                episode.State == FileState.OnDisk) &&
            await database.Episodes.AnyAsync(episode => episode.TmdbId == 5554 && episode.JellyfinItemId == doubleEpisode &&
                episode.State == FileState.OnDisk),
            "A multi-episode file reports every episode in its IndexNumberEnd range on disk");

        await AssertThrowsAsync<ArgumentException>(() => service.ReconcileAsync(new NativeTitleSnapshot(
            "series", 123, tvLibrary, "Series sharing 123", 2020, null, null, null, null,
            [new(seriesId, tvLibrary, false)], [new(Guid.NewGuid(), Guid.NewGuid(), 9010, 1, 2, true)]), default),
            "Episodes outside the observed series representations are rejected");
        await AssertThrowsAsync<ArgumentException>(() => service.ReconcileAsync(new NativeTitleSnapshot(
            "movie", 789, movieLibrary, "Wrong library provenance", 2020, null, null, null, null,
            [new(Guid.NewGuid(), secondLibrary, true)], []), default),
            "Every native representation must prove membership in the reconciled library");

        // P3.T15: reconciliation records the native rating and tags for when the file is gone.
        var rated = await service.ReconcileAsync(Snapshot("movie", 7700, secondLibrary, "Rated", Guid.NewGuid())
            with { NativeRating = "TV-MA", NativeTags = ["Zeta", "Alpha"] }, default);
        var ratedEntry = await database.Entries.AsNoTracking().SingleAsync(entry => entry.Id == rated.EntryId);
        Assert(ratedEntry.NativeRating == "TV-MA" && ratedEntry.NativeTagsJson == """["Alpha","Zeta"]""",
            "Reconciliation records the effective native rating and sorted tags");
        var unmatched = await service.ReconcileAsync(Snapshot("movie", null, movieLibrary, "No provider id", Guid.NewGuid()), default);
        // The P2.R9 multi-episode series and the P3.T15 rated movie add the fourth and fifth entries.
        Assert(unmatched.Outcome == ReconciliationOutcome.Unmatched && await database.Entries.CountAsync() == 5,
            "Native items without TMDB identity are reported unmatched without title guessing");

        var concurrentLibrary = Guid.NewGuid();
        var concurrentEntry = new Entry
        {
            MediaType = "movie", TmdbId = 321, TargetLibraryId = concurrentLibrary, Title = "Concurrent",
            Monitored = true, State = FileState.None
        };
        database.Entries.Add(concurrentEntry);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var concurrentSnapshot = Snapshot("movie", 321, concurrentLibrary, "Concurrent", Guid.NewGuid());
        async Task<ReconciliationResult> ReconcileFromSeparateContext()
        {
            await using var competingDatabase = new ModDbContext(dbPath);
            return await new ReconciliationService(competingDatabase, libraryLock)
                .ReconcileAsync(concurrentSnapshot, default);
        }

        var concurrentResults = await Task.WhenAll(ReconcileFromSeparateContext(), ReconcileFromSeparateContext());
        Assert(concurrentResults.Count(result => result.Outcome == ReconciliationOutcome.Updated) == 1 &&
            concurrentResults.Count(result => result.Outcome == ReconciliationOutcome.Unchanged) == 1 &&
            await database.History.CountAsync(history => history.EntryId == concurrentEntry.Id) == 1 &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == concurrentEntry.Id) == 1,
            "Overlapping reconciliation services serialize by library and write one transition event");
    }

    await using (var restarted = new ModDbContext(dbPath))
    {
        await restarted.Database.MigrateAsync();
        var persisted = (await restarted.Entries.CountAsync(), await restarted.Episodes.CountAsync(),
            await restarted.EntryBindings.CountAsync(), await restarted.EpisodeBindings.CountAsync(),
            await restarted.History.CountAsync());
        Assert(persisted == (6, 7, 9, 8, 13),
            $"Reconciled identities, every observed copy and exact transition history persist after a real SQLite restart {persisted}");
    }

    await BackfillIntegration.RunAsync(folder);
    await AbsenceIntegration.RunAsync(folder);
    Console.WriteLine("PASS: deterministic Phase 2 reconciliation persists across real SQLite transactions and restart");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static NativeTitleSnapshot Snapshot(string mediaType, int? tmdbId, Guid libraryId, string title, params Guid[] representations) =>
    new(mediaType, tmdbId, libraryId, title, 2026, null, null, null, null,
        representations.Select(id => new NativeRepresentation(id, libraryId, true)).ToArray(), []);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
