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

internal static class AbsenceIntegration
{
    public static async Task RunAsync(string folder)
    {
        var dbPath = Path.Combine(folder, "absence.db");
        var moviePath = Path.Combine(folder, "movie-storage");
        var tvPath = Path.Combine(folder, "tv-storage");
        var mountInfoPath = Path.Combine(folder, "mountinfo");
        Directory.CreateDirectory(moviePath);
        Directory.CreateDirectory(tvPath);
        await File.WriteAllTextAsync(Path.Combine(moviePath, "mounted"), "present");
        await File.WriteAllTextAsync(Path.Combine(tvPath, "mounted"), "present");
        await WriteMountInfoAsync(mountInfoPath, folder, moviePath, tvPath, true, true);

        var movieLibrary = new CollectionFolder
        {
            Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies
        };
        var tvLibrary = new CollectionFolder
        {
            Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows
        };
        var movieFolder = new VirtualFolderInfo
        {
            Name = "Movies", ItemId = movieLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.movies,
            Locations = [moviePath]
        };
        var tvFolder = new VirtualFolderInfo
        {
            Name = "TV", ItemId = tvLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.tvshows,
            Locations = [tvPath]
        };
        var movieA = Movie(9200, "Movie copy A", moviePath);
        var movieB = Movie(9200, "Movie copy B", moviePath);
        var seriesA = Series(9300, "Series copy A", tvPath);
        var seriesB = Series(9300, "Series copy B", tvPath);
        var episodeA = Episode(9400, seriesA.Id, "Episode copy A", tvPath);
        var episodeB = Episode(9400, seriesB.Id, "Episode copy B", tvPath);
        var episodeOnly = Episode(9401, seriesB.Id, "Episode only", tvPath, 2);
        var pagingVictim = Movie(9500, "A paging victim", moviePath);
        var pagedMovies = Enumerable.Range(0, 55)
            .Select(index => Movie(9600 + index, $"Paged movie {index:00}", moviePath)).ToArray();
        var movies = new List<Movie> { movieA, movieB, pagingVictim };
        movies.AddRange(pagedMovies);
        var series = new List<Series> { seriesA, seriesB };
        var episodes = new List<MediaBrowser.Controller.Entities.TV.Episode> { episodeA, episodeB, episodeOnly };
        var folders = new List<VirtualFolderInfo> { movieFolder, tvFolder };
        var mutatePagedObservation = false;
        var pageFiftyReads = 0;

        IReadOnlyList<VirtualFolderInfo> ReadFolders()
        {
            return folders;
        }

        IEnumerable<BaseItem> AllItems() => movies.Cast<BaseItem>().Concat(series).Concat(episodes);
        IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
        {
            if (mutatePagedObservation && query.ParentId == movieLibrary.Id && query.StartIndex == 50 &&
                ++pageFiftyReads == 2)
            {
                movies.Remove(pagingVictim);
                File.Delete(pagingVictim.Path);
                mutatePagedObservation = false;
            }

            IEnumerable<BaseItem> items = query.ParentId == movieLibrary.Id ? movies :
                query.ParentId == tvLibrary.Id ? series : AllItems();
            if (query.AncestorIds.Length > 0)
                items = episodes.Where(episode => query.AncestorIds.Contains(episode.SeriesId));
            if (query.ItemIds.Length > 0)
                items = items.Where(item => query.ItemIds.Contains(item.Id));
            if (query.HasAnyProviderId is { } providers)
                items = items.Where(item => providers.Any(provider =>
                    item.ProviderIds.GetValueOrDefault(provider.Key) == provider.Value));
            if (query.PresentationUniqueKey is { } versionGroup)
                items = items.OfType<Movie>().Where(movie => movie.Id.ToString("N") == versionGroup);
            if (query.IncludeItemTypes.Length > 0)
                items = items.Where(item => query.IncludeItemTypes.Contains(item.GetBaseItemKind()));
            return items.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
                .Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
        }

        object? LibraryCall(MethodInfo method, object?[]? arguments) => method.Name switch
        {
            "GetVirtualFolders" => ReadFolders(),
            "GetItemList" => Query((InternalItemsQuery)arguments![0]!),
            "GetCount" => Query((InternalItemsQuery)arguments![0]!).Count,
            "GetCollectionFolders" => ((BaseItem)arguments![0]!) is Movie
                ? new List<Folder> { movieLibrary }
                : new List<Folder> { tvLibrary },
            "GetItemById" when (Guid)arguments![0]! == movieLibrary.Id => movieLibrary,
            "GetItemById" when (Guid)arguments![0]! == tvLibrary.Id => tvLibrary,
            "GetItemById" => AllItems().FirstOrDefault(item => item.Id == (Guid)arguments![0]!),
            _ => throw new NotSupportedException(method.ToString())
        };

        var library = Stub<ILibraryManager>.Create(LibraryCall);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(library);
        services.AddTransient(_ => new ModDbContext(dbPath));
        services.AddSingleton<ReconciliationLibraryLock>();
        services.AddSingleton<ReconciliationRunGate>();
        services.AddSingleton(new MediaStorageIdentity(mountInfoPath));
        services.AddTransient<ReconciliationService>();
        services.AddTransient<JellyfinNativeTitleSource>();
        services.AddTransient<JellyfinItemReconciliationRunner>();
        services.AddTransient<CatalogBackfillRunner>();
        services.AddTransient<CatalogPostScanTask>();
        await using var provider = services.BuildServiceProvider();
        await using (var setup = new ModDbContext(dbPath)) await setup.Database.MigrateAsync();
        var task = provider.GetRequiredService<CatalogPostScanTask>();

        await task.Run(new InlineProgress(), default);
        await using var database = new ModDbContext(dbPath);
        var movieEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9200);
        var seriesEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9300);
        var trackedEpisode = await database.Episodes.SingleAsync(episode =>
            episode.EntryId == seriesEntry.Id && episode.TmdbId == 9400);
        var episodeOnlyEntry = await database.Episodes.SingleAsync(episode =>
            episode.EntryId == seriesEntry.Id && episode.TmdbId == 9401);
        var pagingVictimEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9500);
        Assert(await database.EntryBindings.CountAsync(binding => binding.EntryId == movieEntry.Id) == 2 &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == seriesEntry.Id) == 2 &&
            await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == trackedEpisode.Id) == 2 &&
            await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == episodeOnlyEntry.Id) == 1 &&
            await database.EntryBindings.AllAsync(binding =>
                binding.MediaPath != null && binding.StorageIdentity != null) &&
            await database.EpisodeBindings.AllAsync(binding =>
                binding.MediaPath != null && binding.StorageIdentity != null),
            "A successful post-scan observation records every title and episode copy before checking absence");

        mutatePagedObservation = true;
        pageFiftyReads = 0;
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        pagingVictimEntry = await database.Entries.SingleAsync(entry => entry.Id == pagingVictimEntry.Id);
        var changingLibrary = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(changingLibrary.IncompleteLibraries == 1 && changingLibrary.MissingItems == 0 &&
            pagingVictimEntry.State == FileState.OnDisk && pagingVictimEntry.JellyfinItemId == pagingVictim.Id &&
            await database.EntryBindings.AnyAsync(binding => binding.EntryId == pagingVictimEntry.Id) &&
            await database.Entries.CountAsync(entry => entry.TmdbId >= 9600 && entry.TmdbId < 9655 &&
                entry.State == FileState.OnDisk) == 55,
            "A mutation between pages makes the final observations disagree and preserves every live binding");

        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        pagingVictimEntry = await database.Entries.SingleAsync(entry => entry.Id == pagingVictimEntry.Id);
        var stablePagedLibrary = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(stablePagedLibrary.IncompleteLibraries == 0 && stablePagedLibrary.MissingItems == 1 &&
            pagingVictimEntry.State == FileState.None && pagingVictimEntry.JellyfinItemId is null &&
            !await database.EntryBindings.AnyAsync(binding => binding.EntryId == pagingVictimEntry.Id) &&
            await database.Entries.CountAsync(entry => entry.TmdbId >= 9600 && entry.TmdbId < 9655 &&
                entry.State == FileState.OnDisk) == 55,
            "A later stable observation confirms only the genuinely removed title");

        movies.Remove(movieA);
        File.Delete(movieA.Path);
        series.Remove(seriesA);
        Directory.Delete(seriesA.Path, true);
        episodes.Remove(episodeA);
        File.Delete(episodeA.Path);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        movieEntry = await database.Entries.SingleAsync(entry => entry.Id == movieEntry.Id);
        seriesEntry = await database.Entries.SingleAsync(entry => entry.Id == seriesEntry.Id);
        trackedEpisode = await database.Episodes.SingleAsync(episode => episode.Id == trackedEpisode.Id);
        Assert(movieEntry.State == FileState.OnDisk && movieEntry.JellyfinItemId == movieB.Id &&
            seriesEntry.State == FileState.OnDisk && seriesEntry.JellyfinItemId == seriesB.Id &&
            trackedEpisode.State == FileState.OnDisk && trackedEpisode.JellyfinItemId == episodeB.Id &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == movieEntry.Id) == 1 &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == seriesEntry.Id) == 1 &&
            await database.EpisodeBindings.CountAsync(binding => binding.EpisodeId == trackedEpisode.Id) == 1 &&
            !await database.History.AnyAsync(history =>
                (history.EntryId == movieEntry.Id || history.EntryId == seriesEntry.Id) &&
                history.EventType == "media_missing"),
            "Removing one representation clears only stale bindings while surviving copies remain playable");

        episodes.Remove(episodeOnly);
        File.Delete(episodeOnly.Path);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        episodeOnlyEntry = await database.Episodes.SingleAsync(episode => episode.Id == episodeOnlyEntry.Id);
        seriesEntry = await database.Entries.SingleAsync(entry => entry.Id == seriesEntry.Id);
        Assert(seriesEntry.State == FileState.OnDisk && episodeOnlyEntry.State == FileState.None &&
            episodeOnlyEntry.JellyfinItemId is null &&
            await database.History.CountAsync(history => history.EntryId == seriesEntry.Id &&
                history.EventType == "episode_media_missing" &&
                history.Data != null && history.Data.Contains(episodeOnlyEntry.Id.ToString())) == 1,
            "Removing one episode preserves the playable series and records the episode identity once");

        var returnedEpisodeOnly = Episode(9401, seriesB.Id, "Episode only returned", tvPath, 2);
        episodes.Add(returnedEpisodeOnly);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        episodeOnlyEntry = await database.Episodes.SingleAsync(episode => episode.Id == episodeOnlyEntry.Id);
        Assert(episodeOnlyEntry.State == FileState.OnDisk &&
            episodeOnlyEntry.JellyfinItemId == returnedEpisodeOnly.Id &&
            await database.History.CountAsync(history => history.EntryId == seriesEntry.Id &&
                history.EventType == "episode_media_available" &&
                history.Data != null && history.Data.Contains(episodeOnlyEntry.Id.ToString())) == 1,
            "A returned episode reuses its stable catalog identity and records one availability transition");

        movies.Remove(movieB);
        File.Delete(movieB.Path);
        await WriteMountInfoAsync(mountInfoPath, folder, moviePath, tvPath, false, true);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        movieEntry = await database.Entries.SingleAsync(entry => entry.Id == movieEntry.Id);
        var incomplete = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(incomplete.IncompleteLibraries == 1 && incomplete.MissingItems == 0 &&
            movieEntry.State == FileState.OnDisk && movieEntry.JellyfinItemId == movieB.Id &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == movieEntry.Id) == 1,
            "A disappeared nested media mount preserves the last playable binding even when its stale root is non-empty");

        await WriteMountInfoAsync(mountInfoPath, folder, moviePath, tvPath, true, true);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        movieEntry = await database.Entries.SingleAsync(entry => entry.Id == movieEntry.Id);
        var missing = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        Assert(missing.MissingItems == 1 && missing.IncompleteLibraries == 0 &&
            movieEntry.State == FileState.None && movieEntry.JellyfinItemId is null &&
            !await database.EntryBindings.AnyAsync(binding => binding.EntryId == movieEntry.Id) &&
            await database.History.CountAsync(history => history.EntryId == movieEntry.Id &&
                history.EventType == "media_missing") == 1,
            "A complete post-scan observation on available storage preserves the entry while marking absent media missing");

        await task.Run(new InlineProgress(), default);
        Assert((await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync()).MissingItems == 0 &&
            await database.History.CountAsync(history => history.EntryId == movieEntry.Id &&
                history.EventType == "media_missing") == 1,
            "Repeated confirmed absence does not duplicate transition history");

        var availabilityEvents = await database.History.CountAsync(history => history.EntryId == movieEntry.Id &&
            history.EventType == "media_available");
        var returnedMovie = Movie(9200, "Returned movie", moviePath);
        movies.Add(returnedMovie);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        movieEntry = await database.Entries.SingleAsync(entry => entry.Id == movieEntry.Id);
        Assert(movieEntry.State == FileState.OnDisk && movieEntry.JellyfinItemId == returnedMovie.Id &&
            await database.History.CountAsync(history => history.EntryId == movieEntry.Id &&
                history.EventType == "media_available") == availabilityEvents + 1,
            "Reappearing media restores the existing entry and records one availability transition");

        series.Remove(seriesB);
        Directory.Delete(seriesB.Path, true);
        episodes.Remove(episodeB);
        File.Delete(episodeB.Path);
        episodes.Remove(returnedEpisodeOnly);
        File.Delete(returnedEpisodeOnly.Path);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        seriesEntry = await database.Entries.SingleAsync(entry => entry.Id == seriesEntry.Id);
        trackedEpisode = await database.Episodes.SingleAsync(episode => episode.Id == trackedEpisode.Id);
        Assert(seriesEntry.State == FileState.None && seriesEntry.JellyfinItemId is null &&
            trackedEpisode.State == FileState.None && trackedEpisode.JellyfinItemId is null &&
            !await database.EntryBindings.AnyAsync(binding => binding.EntryId == seriesEntry.Id) &&
            !await database.EpisodeBindings.AnyAsync(binding => binding.EpisodeId == trackedEpisode.Id),
            "Series availability and individual episode bindings clear only after their final playable copy disappears");
    }

    private static Movie Movie(int tmdbId, string name, string root)
    {
        var path = Path.Combine(root, name + ".mkv");
        File.WriteAllText(path, name);
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, Path = path };
        movie.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return movie;
    }

    private static Series Series(int tmdbId, string name, string root)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        var series = new Series { Id = Guid.NewGuid(), Name = name, Path = path };
        series.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return series;
    }

    private static MediaBrowser.Controller.Entities.TV.Episode Episode(
        int tmdbId,
        Guid seriesId,
        string name,
        string root,
        int episodeNumber = 1)
    {
        var path = Path.Combine(root, name + ".mkv");
        File.WriteAllText(path, name);
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), SeriesId = seriesId, Name = name, ParentIndexNumber = 1, IndexNumber = episodeNumber,
            Path = path
        };
        episode.ProviderIds["Tmdb"] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return episode;
    }

    private static Task WriteMountInfoAsync(
        string mountInfoPath,
        string parent,
        string moviePath,
        string tvPath,
        bool movieMounted,
        bool tvMounted)
    {
        var lines = new List<string>
        {
            $"10 1 1:1 / {EscapeMount(parent)} rw - apfs /dev/root rw"
        };
        if (movieMounted)
            lines.Add($"11 10 2:1 /library/movies {EscapeMount(moviePath)} rw - ext4 /dev/movies rw");
        if (tvMounted)
            lines.Add($"12 10 3:1 /library/tv {EscapeMount(tvPath)} rw - ext4 /dev/tv rw");
        return File.WriteAllLinesAsync(mountInfoPath, lines);
    }

    private static string EscapeMount(string path) => path.Replace("\\", @"\134", StringComparison.Ordinal)
        .Replace(" ", @"\040", StringComparison.Ordinal);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public void Report(double value)
        {
        }
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
