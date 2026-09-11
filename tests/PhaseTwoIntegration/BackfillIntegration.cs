using System.Reflection;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

internal static class BackfillIntegration
{
    public static async Task RunAsync(string folder)
    {
        var dbPath = Path.Combine(folder, "backfill.db");
        var movieLibraryId = Guid.NewGuid();
        var tvLibraryId = Guid.NewGuid();
        var movieLibrary = new TestLibrary { Id = movieLibraryId };
        var tvLibrary = new TestLibrary { Id = tvLibraryId };
        var movies = Enumerable.Range(1, 51).Select(number => Movie(number, movieLibraryId)).Cast<BaseItem>().ToList();
        movies.Add(new Movie { Id = Guid.NewGuid(), Name = "Unmatched", Path = "/media/unmatched.mkv" });
        var primary = Movie(8000, movieLibraryId);
        var alternate = Movie(8000, movieLibraryId);
        alternate.PrimaryVersionId = primary.Id.ToString("N");
        movies.Add(primary);
        movies.Add(alternate);
        var conflictingPrimary = Movie(8100, movieLibraryId);
        var conflictingAlternate = Movie(8101, movieLibraryId);
        conflictingAlternate.PrimaryVersionId = conflictingPrimary.Id.ToString("N");
        movies.Add(conflictingPrimary);
        movies.Add(conflictingAlternate);
        movieLibrary.Items = movies;

        var goodSeries = new TestSeries
        {
            Id = Guid.NewGuid(),
            Name = "Backfilled series",
            Path = "/media/series",
            Episodes =
            [
                Episode(9001, 1, 1, "/media/series/s01e01.mkv"),
                Episode(9002, 0, 1, "/media/series/s00e01.mkv")
            ]
        };
        goodSeries.ProviderIds["Tmdb"] = "7000";
        var badSeries = new TestSeries
        {
            Id = Guid.NewGuid(), Name = "Bad episode metadata", Path = "/media/bad",
            Episodes = [new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Name = "Unknown" }]
        };
        badSeries.ProviderIds["Tmdb"] = "7001";
        tvLibrary.Items = [goodSeries, badSeries];

        var virtualFolders = new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo
            {
                Name = "Movies", ItemId = movieLibraryId.ToString(), CollectionType = CollectionTypeOptions.movies
            },
            new VirtualFolderInfo
            {
                Name = "TV", ItemId = tvLibraryId.ToString(), CollectionType = CollectionTypeOptions.tvshows
            }
        };
        var library = Stub<ILibraryManager>.Create((method, arguments) => method.Name switch
        {
            "GetVirtualFolders" => virtualFolders,
            "GetItemById" when (Guid)arguments![0]! == movieLibraryId => movieLibrary,
            "GetItemById" when (Guid)arguments![0]! == tvLibraryId => tvLibrary,
            _ => throw new NotSupportedException(method.ToString())
        });

        await using var database = new ModDbContext(dbPath);
        await database.Database.MigrateAsync();
        var libraryLock = new ReconciliationLibraryLock();
        var gate = new ReconciliationRunGate();
        CatalogBackfillRunner Runner(ModDbContext context) => new(context,
            new ReconciliationService(context, libraryLock), new JellyfinNativeTitleSource(library), gate,
            NullLogger<CatalogBackfillRunner>.Instance);

        var first = await Runner(database).RunAsync(new InlineProgress(_ => { }), default);
        Assert(first.Status == "completed" && first.TotalItems == 56 && first.ScannedItems == 56 &&
            first.CreatedEntries == 53 && first.UnmatchedItems == 1 && first.FailedItems == 1 &&
            first.ConflictedItems == 1 && first.UpdatedBindings == 0 && first.UnchangedItems == 0,
            "Backfill reports exact bounded outcome counts while one bad title does not stop later work");
        var seriesEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 7000);
        Assert(await database.Episodes.CountAsync(episode => episode.EntryId == seriesEntry.Id) == 2 &&
            await database.EpisodeBindings.CountAsync(binding => binding.TargetLibraryId == tvLibraryId) == 2,
            "Fresh series backfill creates individual episodes through the production native snapshot source");
        Assert(await database.EntryBindings.CountAsync(binding => binding.EntryId ==
            database.Entries.Single(entry => entry.TmdbId == 8000).Id) == 2,
            "Native primary and alternate movie versions converge to one entry with two durable bindings");

        database.ChangeTracker.Clear();
        var rerun = await Runner(database).RunAsync(new InlineProgress(_ => { }), default);
        Assert(rerun.CreatedEntries == 0 && rerun.UpdatedBindings == 0 && rerun.UnchangedItems == 53 &&
            rerun.UnmatchedItems == 1 && rerun.FailedItems == 1 && rerun.ConflictedItems == 1 &&
            await database.History.CountAsync() == 53,
            "A complete rerun is idempotent and creates no duplicate entries or transition history");

        await using (var held = await gate.TryAcquireAsync(default))
            await AssertThrowsAsync<InvalidOperationException>(() => Runner(database).RunAsync(new InlineProgress(_ => { }), default),
                "Overlapping full runs must be rejected");

        database.ChangeTracker.Clear();
        using var cancellation = new CancellationTokenSource();
        await AssertThrowsAsync<OperationCanceledException>(() => Runner(database).RunAsync(
            new InlineProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token),
            "Cancellation must stop between items");
        database.ChangeTracker.Clear();
        var cancelled = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(cancelled.Status == "cancelled" && cancelled.CompletedAt.HasValue && cancelled.ScannedItems < cancelled.TotalItems,
            "Cancellation persists an interrupted summary that is safe to resume by rerunning");

        await using var restarted = new ModDbContext(dbPath);
        Assert(await restarted.ReconciliationRuns.CountAsync() == 3 &&
            (await restarted.ReconciliationRuns.OrderBy(run => run.StartedAt).LastAsync()).Status == "cancelled",
            "Backfill summaries persist across a real SQLite restart");
    }

    private static Movie Movie(int tmdbId, Guid libraryId)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(), Name = "Movie " + tmdbId, Path = $"/media/{libraryId}/{tmdbId}.mkv",
            ProductionYear = 2026
        };
        movie.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return movie;
    }

    private static MediaBrowser.Controller.Entities.TV.Episode Episode(
        int tmdbId,
        int seasonNumber,
        int episodeNumber,
        string path)
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Name = "Episode " + tmdbId, ParentIndexNumber = seasonNumber,
            IndexNumber = episodeNumber, Path = path
        };
        episode.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return episode;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
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

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class TestLibrary : CollectionFolder
    {
        public IReadOnlyList<BaseItem> Items { get; set; } = [];
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
            new() { Items = Items, TotalRecordCount = Items.Count };
    }

    private sealed class TestSeries : Series
    {
        public IReadOnlyList<BaseItem> Episodes { get; init; } = [];
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
            new() { Items = Episodes, TotalRecordCount = Episodes.Count };
    }

    private class Stub<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[]?, object?> callback = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> callback)
        {
            var instance = Create<T, Stub<T>>();
            ((Stub<T>)(object)instance).callback = callback;
            return instance;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments) =>
            callback(targetMethod!, arguments);
    }
}
