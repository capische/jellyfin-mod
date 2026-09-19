using System.Reflection;
using JellyfinMod;
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
        var movieLibrary = new CollectionFolder { Id = movieLibraryId, CollectionType = Jellyfin.Data.Enums.CollectionType.movies };
        var tvLibrary = new CollectionFolder { Id = tvLibraryId, CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        var movies = Enumerable.Range(1, 51).Select(number => Movie(number, movieLibraryId)).Cast<BaseItem>().ToList();
        movies.Add(new Movie { Id = Guid.NewGuid(), Name = "Unmatched", Path = "/media/unmatched.mkv" });
        var primary = Movie(8000, movieLibraryId);
        var alternate = Movie(8000, movieLibraryId);
        alternate.PrimaryVersionId = primary.Id.ToString("N");
        movies.Add(primary);
        movies.Add(alternate);
        var jellyfin12Primary = Movie12(8050, movieLibraryId);
        var jellyfin12Alternate = Movie12(8050, movieLibraryId);
        jellyfin12Primary.Id = Guid.Parse("30000000-0000-0000-0000-000000000050");
        jellyfin12Alternate.Id = Guid.Parse("40000000-0000-0000-0000-000000000050");
        jellyfin12Alternate.PrimaryVersionId = jellyfin12Primary.Id;
        movies.Add(jellyfin12Primary);
        movies.Add(jellyfin12Alternate);
        var conflictingPrimary = Movie12(8100, movieLibraryId);
        var conflictingAlternate = Movie12(8101, movieLibraryId);
        conflictingAlternate.PrimaryVersionId = conflictingPrimary.Id;
        movies.Add(conflictingPrimary);
        movies.Add(conflictingAlternate);
        var movieVersionGroups = new Dictionary<Guid, Guid>
        {
            [alternate.Id] = primary.Id,
            [jellyfin12Alternate.Id] = jellyfin12Primary.Id,
            [conflictingAlternate.Id] = conflictingPrimary.Id
        };

        var goodSeries = new Series { Id = Guid.NewGuid(), Name = "Backfilled series", Path = "/media/series" };
        goodSeries.ProviderIds["Tmdb"] = "7000";
        var badSeries = new Series { Id = Guid.NewGuid(), Name = "Bad episode metadata", Path = "/media/bad" };
        badSeries.ProviderIds["Tmdb"] = "7001";
        var groupedCopy = new Series { Id = Guid.NewGuid(), Name = "Backfilled series copy", Path = "/media/series-copy" };
        groupedCopy.ProviderIds["Tmdb"] = "7000";
        goodSeries.PresentationUniqueKey = groupedCopy.PresentationUniqueKey = "grouped-series";
        var unidentifiedSeries = new Series { Id = Guid.NewGuid(), Name = "Unidentified series", Path = "/media/unidentified" };
        BaseItem[] tvItems = [goodSeries, groupedCopy, badSeries, unidentifiedSeries];
        var goodPilot = Episode(9001, 1, 1, "/media/series/s01e01.mkv");
        var goodSpecial = Episode(9002, 0, 1, "/media/series/s00e01.mkv");
        goodPilot.SeriesId = goodSpecial.SeriesId = goodSeries.Id;
        var alternatePilot = Episode(9001, 1, 1, "/media/series-copy/s01e01.mkv");
        alternatePilot.SeriesId = groupedCopy.Id;
        BaseItem[] nativeEpisodes = [goodPilot, goodSpecial, alternatePilot,
            new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = badSeries.Id, Name = "Unknown" },
            new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), SeriesId = unidentifiedSeries.Id, Name = "No numbering" }];

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
        IEnumerable<BaseItem> AllItems() => movies.Concat(tvItems).Concat(nativeEpisodes);
        int? failedProvider = null;
        var pagedReads = new List<(Guid ParentId, int StartIndex, int? Limit)>();
        var episodeEnumerations = 0;
        IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
        {
            if (query.AncestorIds.Length > 0) Interlocked.Increment(ref episodeEnumerations);
            if (query.HasAnyProviderId?.GetValueOrDefault("Tmdb") == failedProvider?.ToString() && failedProvider.HasValue)
                throw new InvalidOperationException("Isolated native metadata failure");
            if (query.Limit.HasValue) pagedReads.Add((query.ParentId, query.StartIndex ?? 0, query.Limit));
            IEnumerable<BaseItem> items = query.ParentId == movieLibraryId ? movies :
                query.ParentId == tvLibraryId ? tvItems : AllItems();
            if (query.AncestorIds.Length > 0)
                items = items.OfType<MediaBrowser.Controller.Entities.TV.Episode>()
                    .Where(episode => query.AncestorIds.Contains(episode.SeriesId));
            if (query.ItemIds.Length > 0) items = items.Where(item => query.ItemIds.Contains(item.Id));
            if (query.HasAnyProviderId is { } providers)
                items = items.Where(item => providers.Any(provider => item.ProviderIds.GetValueOrDefault(provider.Key) == provider.Value));
            if (query.PresentationUniqueKey is { } group)
                items = items.OfType<Movie>().Where(movie =>
                    movieVersionGroups.GetValueOrDefault(movie.Id, movie.Id).ToString("N") == group);
            if (query.IncludeItemTypes.Length > 0)
                items = items.Where(item => query.IncludeItemTypes.Contains(item is Movie
                    ? Jellyfin.Data.Enums.BaseItemKind.Movie
                    : item.GetBaseItemKind()));
            return items.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
                .Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
        }

        var virtualFolderReads = 0;
        object? LibraryCall(MethodInfo method, object?[]? arguments)
        {
            switch (method.Name)
            {
                case "GetVirtualFolders": virtualFolderReads++; return virtualFolders;
                case "GetItemList": return Query((InternalItemsQuery)arguments![0]!);
                case "GetCount": return Query((InternalItemsQuery)arguments![0]!).Count;
                case "GetCollectionFolders": return new List<Folder>
                {
                    movies.Any(item => item.Id == ((BaseItem)arguments![0]!).Id) ? movieLibrary : tvLibrary
                };
                case "GetItemById" when (Guid)arguments![0]! == movieLibraryId: return movieLibrary;
                case "GetItemById" when (Guid)arguments![0]! == tvLibraryId: return tvLibrary;
                case "GetItemById": return AllItems().FirstOrDefault(item => item.Id == (Guid)arguments![0]!);
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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(library);
        new PluginServiceRegistrator().RegisterServices(services, null!);
        services.AddTransient(_ => new ModDbContext(dbPath));
        await using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<ReconciliationRunGate>();
        CatalogBackfillRunner Runner(ModDbContext context) => new(context,
            provider.GetRequiredService<IServiceScopeFactory>(), new JellyfinNativeTitleSource(library), gate,
            provider.GetRequiredService<ReconciliationLibraryLock>(),
            NullLogger<CatalogBackfillRunner>.Instance);

        var first = await Runner(database).RunAsync(new InlineProgress(_ => { }), default);
        Assert(first.Status == "completed" && first.TotalItems == 58 && first.ScannedItems == 58 &&
            first.CreatedEntries == 55 && first.UnmatchedItems == 2 && first.FailedItems == 0 &&
            first.ConflictedItems == 1 && first.UpdatedBindings == 0 && first.UnchangedItems == 0,
            "Backfill reports exact bounded outcome counts while one bad title does not stop later work");
        // An unnumbered episode is diagnosed on its own instead of failing its series (P2.R6).
        Assert(await database.Entries.AnyAsync(entry => entry.TmdbId == 7001 && entry.State == FileState.None) &&
            first.DiagnosticsJson?.Contains("no season and episode number", StringComparison.Ordinal) == true,
            "A series whose only episode is unnumbered is backfilled with a per-episode diagnostic");
        var seriesEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 7000);
        Assert(await database.Episodes.CountAsync(episode => episode.EntryId == seriesEntry.Id) == 2 &&
            await database.EpisodeBindings.CountAsync(binding => binding.TargetLibraryId == tvLibraryId) == 3,
            "Fresh series backfill creates individual episodes through the production native snapshot source");
        Assert(await database.EntryBindings.CountAsync(binding => binding.EntryId ==
            database.Entries.Single(entry => entry.TmdbId == 8000).Id) == 2,
            "Native primary and alternate movie versions converge to one entry with two durable bindings");
        var jellyfin12Entry = await database.Entries.SingleAsync(entry => entry.TmdbId == 8050);
        Assert(jellyfin12Entry.JellyfinItemId == new[] { jellyfin12Primary.Id, jellyfin12Alternate.Id }.Min() &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == jellyfin12Entry.Id &&
                binding.VersionGroupId == jellyfin12Primary.Id) == 2,
            "Jellyfin 12 nullable Guid version identities preserve deterministic grouping through reconciliation");

        database.ChangeTracker.Clear();
        var rerun = await Runner(database).RunAsync(new InlineProgress(_ => { }), default);
        Assert(rerun.CreatedEntries == 0 && rerun.UpdatedBindings == 0 && rerun.UnchangedItems == 55 &&
            rerun.UnmatchedItems == 2 && rerun.FailedItems == 0 && rerun.ConflictedItems == 1 &&
            await database.History.CountAsync() == 55,
            "A complete rerun is idempotent and creates no duplicate entries or transition history");

        failedProvider = 5;
        var failedItemRun = await Runner(database).RunAsync(new InlineProgress(_ => { }), default);
        Assert(failedItemRun.ScannedItems == 58 && failedItemRun.TotalItems == 58 &&
            failedItemRun.UnchangedItems == 54 && failedItemRun.FailedItems == 1 &&
            failedItemRun.UnmatchedItems == 2 && failedItemRun.ConflictedItems == 1,
            "Production transient registrations retain every preceding outcome after a mid-run item failure");
        failedProvider = null;
        Assert(pagedReads.All(read => read.Limit <= 50) && pagedReads.Any(read => read.StartIndex == 50),
            "Full runs page native queries instead of loading the complete library");

        await using (var held = await gate.TryAcquireAsync(default))
            await AssertThrowsAsync<InvalidOperationException>(() => Runner(database).RunAsync(new InlineProgress(_ => { }), default),
                "Overlapping full runs must be rejected");

        database.ChangeTracker.Clear();
        pagedReads.Clear();
        using var cancellation = new CancellationTokenSource();
        await AssertThrowsAsync<OperationCanceledException>(() => Runner(database).RunAsync(
            new InlineProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token),
            "Cancellation must stop between items");
        database.ChangeTracker.Clear();
        var cancelled = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(!pagedReads.Any(read => read.StartIndex == 50), "Cancellation is observed before the next native page is loaded");
        Assert(cancelled.Status == "cancelled" && cancelled.CompletedAt.HasValue && cancelled.ScannedItems == 1 &&
            cancelled.UnchangedItems == 1 && cancelled.ScannedItems < cancelled.TotalItems,
            "Cancellation persists an interrupted summary that is safe to resume by rerunning");

        await using var restarted = new ModDbContext(dbPath);
        Assert(await restarted.ReconciliationRuns.CountAsync() == 4 &&
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

        await RunEventIntegrationAsync(folder, library, movieLibrary, movies, Raise, () => virtualFolderReads,
            [goodPilot, goodSpecial], () => Volatile.Read(ref episodeEnumerations));
    }

    private static async Task RunEventIntegrationAsync(
        string folder,
        ILibraryManager library,
        CollectionFolder movieLibrary,
        List<BaseItem> movies,
        Action<string, ItemChangeEventArgs> raise,
        Func<int> getEnumerationCount,
        IReadOnlyList<BaseItem> seriesEpisodes,
        Func<int> getEpisodeEnumerations)
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
        // A short debounce keeps the 5-second event waits meaningful; the burst below is raised in a tight loop.
        services.AddSingleton(provider => new LibraryEventListener(library,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LibraryEventListener>>(),
            TimeSpan.FromMilliseconds(300)));
        await using var provider = services.BuildServiceProvider();
        await using (var setup = new ModDbContext(dbPath)) await setup.Database.MigrateAsync();
        var listener = provider.GetRequiredService<LibraryEventListener>();
        await listener.StartAsync(default);
        try
        {
            var initialEnumerations = getEnumerationCount();
            var added = Movie(8200, movieLibrary.Id);
            movies.Add(added);
            raise("added", new ItemChangeEventArgs { Item = added, Parent = movieLibrary });
            var entryId = await WaitForEntryAsync(dbPath, 8200);

            var replacement = Movie(8200, movieLibrary.Id);
            movies.Remove(added);
            movies.Add(replacement);
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

            Assert(getEnumerationCount() == initialEnumerations,
                "Item events query their own library/title without enumerating every server library");
            // Enumerate the work identity before a newer event commits its replacement.
            var staleWork = provider.GetRequiredService<JellyfinNativeTitleSource>().GetWorkItems(default)
                .Single(work => work.TmdbId == 8200);
            var newest = Movie(8200, movieLibrary.Id);
            movies.Remove(replacement);
            movies.Add(newest);
            raise("added", new ItemChangeEventArgs { Item = newest, Parent = movieLibrary });
            await WaitForAsync(async database => (await database.Entries.SingleAsync(entry => entry.Id == entryId)).JellyfinItemId == newest.Id,
                dbPath, "Newer event did not converge before queued repair work");
            using (var scope = provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<JellyfinItemReconciliationRunner>().ReconcileAsync(staleWork, default);
            await using (var afterQueuedWork = new ModDbContext(dbPath))
                Assert((await afterQueuedWork.Entries.SingleAsync(entry => entry.Id == entryId)).JellyfinItemId == newest.Id &&
                    await afterQueuedWork.History.CountAsync(history => history.EntryId == entryId) == 3,
                    "Queued repair work re-reads native state and cannot overwrite a newer event with the old item ID");
            replacement = newest;

            for (var repeat = 0; repeat < 20; repeat++)
                raise("updated", new ItemChangeEventArgs { Item = replacement, Parent = movieLibrary });

            var corrected = new Movie { Id = Guid.NewGuid(), Name = "Provider correction", Path = "/media/corrected.mkv" };
            movies.Add(corrected);
            raise("added", new ItemChangeEventArgs { Item = corrected, Parent = movieLibrary });
            corrected.ProviderIds["Tmdb"] = "8201";
            raise("updated", new ItemChangeEventArgs { Item = corrected, Parent = movieLibrary });
            await WaitForEntryAsync(dbPath, 8201);

            var missed = Movie(8202, movieLibrary.Id);
            movies.Add(missed);
            await provider.GetRequiredService<CatalogPostScanTask>().Run(new InlineProgress(_ => { }), default);
            await using (var repaired = new ModDbContext(dbPath))
                Assert(await repaired.Entries.AnyAsync(entry => entry.TmdbId == 8202),
                    "Successful post-scan repair picks up a title whose event was missed");

            movies.Remove(replacement);
            var sentinel = Movie(8203, movieLibrary.Id);
            movies.Add(sentinel);
            raise("removed", new ItemChangeEventArgs { Item = replacement, Parent = movieLibrary });
            raise("added", new ItemChangeEventArgs { Item = sentinel, Parent = movieLibrary });
            await WaitForEntryAsync(dbPath, 8203);
            await using var afterRemoval = new ModDbContext(dbPath);
            var preserved = await afterRemoval.Entries.SingleAsync(entry => entry.Id == entryId);
            Assert(preserved.State == FileState.OnDisk && preserved.JellyfinItemId == replacement.Id,
                "A removal notification alone does not clear a binding before successful absence evidence");
            Assert(await afterRemoval.History.CountAsync(history => history.EntryId == entryId) == 3,
                "Coalesced repeated update notifications do not duplicate transition history");

            // P2.R8: a burst of episode events becomes one series observation, and unchanged updates are dropped.
            var beforeBurst = getEpisodeEnumerations();
            for (var repeat = 0; repeat < 20; repeat++)
                foreach (var episode in seriesEpisodes)
                    raise("added", new ItemChangeEventArgs { Item = episode });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (getEpisodeEnumerations() == beforeBurst && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Task.Delay(2500);
            var afterBurst = getEpisodeEnumerations();
            // One observation reads each of the series' two grouped copies once.
            Assert(afterBurst - beforeBurst is > 0 and <= 2,
                $"Forty episode events of one series coalesce into one series observation ({afterBurst - beforeBurst} episode reads)");
            for (var repeat = 0; repeat < 20; repeat++)
                foreach (var episode in seriesEpisodes)
                    raise("updated", new ItemChangeEventArgs { Item = episode });
            await Task.Delay(2500);
            Assert(getEpisodeEnumerations() == afterBurst,
                "Updates that change no identity, path or playability do not trigger an observation");
        }
        finally
        {
            await listener.StopAsync(default);
            // A host may stop a hosted service twice (failed start, then shutdown); the second stop is a no-op.
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

    private static Jellyfin12Movie Movie12(int tmdbId, Guid libraryId)
    {
        var movie = new Jellyfin12Movie
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

    private sealed class Jellyfin12Movie : Movie
    {
        public new Guid? PrimaryVersionId { get; set; }
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
