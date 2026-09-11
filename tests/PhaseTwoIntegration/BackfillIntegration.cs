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
using Microsoft.Extensions.DependencyInjection;
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
        EventHandler<ItemChangeEventArgs>? itemAdded = null;
        EventHandler<ItemChangeEventArgs>? itemUpdated = null;
        EventHandler<ItemChangeEventArgs>? itemRemoved = null;
        object? LibraryCall(MethodInfo method, object?[]? arguments)
        {
            switch (method.Name)
            {
                case "GetVirtualFolders": return virtualFolders;
                case "GetItemById" when (Guid)arguments![0]! == movieLibraryId: return movieLibrary;
                case "GetItemById" when (Guid)arguments![0]! == tvLibraryId: return tvLibrary;
                case "add_ItemAdded": itemAdded += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                case "remove_ItemAdded": itemAdded -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                case "add_ItemUpdated": itemUpdated += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                case "remove_ItemUpdated": itemUpdated -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                case "add_ItemRemoved": itemRemoved += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                case "remove_ItemRemoved": itemRemoved -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
                default: throw new NotSupportedException(method.ToString());
            }
        }

        var library = Stub<ILibraryManager>.Create(LibraryCall);

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

        void Raise(string eventName, ItemChangeEventArgs eventArgs)
        {
            var handler = eventName switch
            {
                "added" => itemAdded,
                "updated" => itemUpdated,
                "removed" => itemRemoved,
                _ => throw new ArgumentOutOfRangeException(nameof(eventName))
            };
            handler!(null, eventArgs);
        }

        await RunEventIntegrationAsync(folder, library, movieLibrary, movies, Raise);
    }

    private static async Task RunEventIntegrationAsync(
        string folder,
        ILibraryManager library,
        TestLibrary movieLibrary,
        List<BaseItem> movies,
        Action<string, ItemChangeEventArgs> raise)
    {
        var dbPath = Path.Combine(folder, "events.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(library);
        services.AddTransient(_ => new ModDbContext(dbPath));
        services.AddSingleton<ReconciliationLibraryLock>();
        services.AddSingleton<ReconciliationRunGate>();
        services.AddTransient<ReconciliationService>();
        services.AddTransient<JellyfinNativeTitleSource>();
        services.AddTransient<JellyfinItemReconciliationRunner>();
        services.AddTransient<CatalogBackfillRunner>();
        services.AddTransient<CatalogPostScanTask>();
        services.AddSingleton<LibraryEventListener>();
        await using var provider = services.BuildServiceProvider();
        await using (var setup = new ModDbContext(dbPath)) await setup.Database.MigrateAsync();
        var listener = provider.GetRequiredService<LibraryEventListener>();
        await listener.StartAsync(default);
        try
        {
            var added = Movie(8200, movieLibrary.Id);
            movies.Add(added);
            movieLibrary.Items = movies;
            raise("added", new ItemChangeEventArgs { Item = added, Parent = movieLibrary });
            var entryId = await WaitForEntryAsync(dbPath, 8200);

            var replacement = Movie(8200, movieLibrary.Id);
            movies.Remove(added);
            movies.Add(replacement);
            movieLibrary.Items = movies;
            raise("removed", new ItemChangeEventArgs { Item = added, Parent = movieLibrary });
            raise("added", new ItemChangeEventArgs { Item = replacement, Parent = movieLibrary });
            await WaitForAsync(async database => (await database.Entries.SingleAsync(entry => entry.Id == entryId)).JellyfinItemId == replacement.Id,
                dbPath, "Replacement event did not converge");
            await using (var replaced = new ModDbContext(dbPath))
            {
                Assert(await replaced.EntryBindings.CountAsync(binding => binding.EntryId == entryId) == 2 &&
                    await replaced.History.CountAsync(history => history.EntryId == entryId) == 2,
                    "Replacement native IDs preserve entry identity and one meaningful transition history event");
            }

            for (var repeat = 0; repeat < 20; repeat++)
                raise("updated", new ItemChangeEventArgs { Item = replacement, Parent = movieLibrary });

            var corrected = new Movie { Id = Guid.NewGuid(), Name = "Provider correction", Path = "/media/corrected.mkv" };
            movies.Add(corrected);
            movieLibrary.Items = movies;
            raise("added", new ItemChangeEventArgs { Item = corrected, Parent = movieLibrary });
            corrected.ProviderIds["Tmdb"] = "8201";
            raise("updated", new ItemChangeEventArgs { Item = corrected, Parent = movieLibrary });
            await WaitForEntryAsync(dbPath, 8201);

            var missed = Movie(8202, movieLibrary.Id);
            movies.Add(missed);
            movieLibrary.Items = movies;
            await provider.GetRequiredService<CatalogPostScanTask>().Run(new InlineProgress(_ => { }), default);
            await using (var repaired = new ModDbContext(dbPath))
                Assert(await repaired.Entries.AnyAsync(entry => entry.TmdbId == 8202),
                    "Successful post-scan repair picks up a title whose event was missed");

            movies.Remove(replacement);
            var sentinel = Movie(8203, movieLibrary.Id);
            movies.Add(sentinel);
            movieLibrary.Items = movies;
            raise("removed", new ItemChangeEventArgs { Item = replacement, Parent = movieLibrary });
            raise("added", new ItemChangeEventArgs { Item = sentinel, Parent = movieLibrary });
            await WaitForEntryAsync(dbPath, 8203);
            await using var afterRemoval = new ModDbContext(dbPath);
            var preserved = await afterRemoval.Entries.SingleAsync(entry => entry.Id == entryId);
            Assert(preserved.State == FileState.OnDisk && preserved.JellyfinItemId == replacement.Id,
                "A removal notification alone does not clear a binding before successful absence evidence");
            Assert(await afterRemoval.History.CountAsync(history => history.EntryId == entryId) == 2,
                "Coalesced repeated update notifications do not duplicate transition history");
        }
        finally
        {
            await listener.StopAsync(default);
        }
    }

    private static async Task<Guid> WaitForEntryAsync(string dbPath, int tmdbId)
    {
        Guid result = Guid.Empty;
        await WaitForAsync(async database =>
        {
            result = await database.Entries.Where(entry => entry.TmdbId == tmdbId).Select(entry => entry.Id)
                .FirstOrDefaultAsync();
            return result != Guid.Empty;
        }, dbPath, $"Timed out waiting for TMDB {tmdbId}");
        return result;
    }

    private static async Task WaitForAsync(Func<ModDbContext, Task<bool>> condition, string dbPath, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            await using var database = new ModDbContext(dbPath);
            if (await condition(database)) return;
            await Task.Delay(20, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new InvalidOperationException(message);
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
