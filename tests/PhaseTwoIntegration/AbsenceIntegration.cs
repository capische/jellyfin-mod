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
        var overlapLibrary = new CollectionFolder
        {
            Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows
        };
        Series? overlapSeries = null;
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
                query.ParentId == tvLibrary.Id ? series :
                query.ParentId == overlapLibrary.Id ? series.Where(candidate => candidate == overlapSeries) : AllItems();
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
                : arguments[0] == overlapSeries
                    ? new List<Folder> { tvLibrary, overlapLibrary }
                    : new List<Folder> { tvLibrary },
            "GetItemById" when (Guid)arguments![0]! == movieLibrary.Id => movieLibrary,
            "GetItemById" when (Guid)arguments![0]! == tvLibrary.Id => tvLibrary,
            "GetItemById" when (Guid)arguments![0]! == overlapLibrary.Id => overlapLibrary,
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
        var partialEpisode = Episode(9499, Guid.Empty, "Partially scanned episode", tvPath, 3);
        episodes.Add(partialEpisode);
        Assert(!provider.GetRequiredService<JellyfinNativeTitleSource>()
                .GetItemWorkItems(partialEpisode.Id, default).Any(),
            "An episode event observed before Jellyfin assigns its series is deferred without querying an empty identity");
        episodes.Remove(partialEpisode);
        File.Delete(partialEpisode.Path);

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
        Assert(incomplete.IncompleteLibraries == 0 && incomplete.MissingItems == 0 &&
            movieEntry.State == FileState.OnDisk && movieEntry.JellyfinItemId == movieB.Id &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == movieEntry.Id) == 1 &&
            incomplete.DiagnosticsJson?.Contains("Absence was not confirmed for this title", StringComparison.Ordinal) == true,
            "A disappeared nested media mount preserves the last playable binding even when its stale root is non-empty, " +
            "excluding only that title from absence confirmation");

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

        // P2.R6: incompleteness belongs to one title, not to the library.
        var dailySeries = Series(9700, "Daily series", tvPath);
        var dailyNumbered = Episode(9701, dailySeries.Id, "Daily numbered", tvPath);
        var dailyUnnumbered = Episode(9702, dailySeries.Id, "Daily 2026-09-19", tvPath, 2);
        dailyUnnumbered.ParentIndexNumber = null;
        dailyUnnumbered.IndexNumber = null;
        var keptSeries = Series(9710, "Kept series", tvPath);
        var keptOne = Episode(9711, keptSeries.Id, "Kept one", tvPath);
        var keptTwo = Episode(9712, keptSeries.Id, "Kept two", tvPath, 2);
        var losingSeries = Series(9720, "Losing identity", tvPath);
        var losingEpisode = Episode(9721, losingSeries.Id, "Losing identity episode", tvPath);
        series.AddRange([dailySeries, keptSeries, losingSeries]);
        episodes.AddRange([dailyNumbered, dailyUnnumbered, keptOne, keptTwo, losingEpisode]);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        var r6Run = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        var dailyEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9700);
        Assert(r6Run.IncompleteLibraries == 0 && r6Run.FailedItems == 0 && dailyEntry.State == FileState.OnDisk &&
            await database.Episodes.AnyAsync(episode => episode.EntryId == dailyEntry.Id && episode.TmdbId == 9701 &&
                episode.State == FileState.OnDisk) &&
            !await database.Episodes.AnyAsync(episode => episode.TmdbId == 9702) &&
            r6Run.DiagnosticsJson?.Contains("no season and episode number", StringComparison.Ordinal) == true,
            "An unnumbered episode is diagnosed on its own while its series' numbered episodes still bind");

        var losingEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9720);
        var keptEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9710);
        var keptTwoEpisode = await database.Episodes.SingleAsync(episode => episode.TmdbId == 9712);
        episodes.Remove(keptTwo);
        File.Delete(keptTwo.Path);
        losingSeries.ProviderIds.Remove("Tmdb");
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        r6Run = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        losingEntry = await database.Entries.SingleAsync(entry => entry.Id == losingEntry.Id);
        keptTwoEpisode = await database.Episodes.SingleAsync(episode => episode.Id == keptTwoEpisode.Id);
        Assert(r6Run.IncompleteLibraries == 0 && r6Run.UnmatchedItems == 1 && r6Run.MissingItems == 1 &&
            keptTwoEpisode.State == FileState.None &&
            await database.History.CountAsync(history => history.EntryId == keptEntry.Id &&
                history.EventType == "episode_media_missing") == 1,
            "A hand-deleted episode is confirmed missing while an unnumbered episode and an unmatched series share its library");
        Assert(losingEntry.State == FileState.OnDisk && losingEntry.JellyfinItemId == losingSeries.Id &&
            await database.EntryBindings.CountAsync(binding => binding.EntryId == losingEntry.Id) == 1 &&
            await database.EpisodeBindings.CountAsync(binding => binding.TargetLibraryId == tvLibrary.Id &&
                binding.JellyfinItemId == losingEpisode.Id) == 1 &&
            !await database.History.AnyAsync(history => history.EntryId == losingEntry.Id &&
                (history.EventType == "media_missing" || history.EventType == "episode_media_missing")),
            "A bound series that loses its TMDB identity keeps its bindings and state with no missing events");

        // A binding recorded without a storage identity, or from a retired location, is resolved by a direct
        // existence check; an existing file Jellyfin no longer lists is kept.
        var keptOneBinding = await database.EpisodeBindings.SingleAsync(binding => binding.JellyfinItemId == keptOne.Id);
        keptOneBinding.StorageIdentity = null;
        var retiredLocation = Path.Combine(folder, "retired-location");
        Directory.CreateDirectory(retiredLocation);
        await File.WriteAllTextAsync(Path.Combine(retiredLocation, "still-mounted"), "present");
        var dailyBinding = await database.EpisodeBindings.SingleAsync(binding => binding.JellyfinItemId == dailyNumbered.Id);
        dailyBinding.MediaPath = Path.Combine(retiredLocation, "Daily numbered.mkv");
        var unlistedMovie = pagedMovies[0];
        var unlistedBinding = await database.EntryBindings.SingleAsync(binding => binding.JellyfinItemId == unlistedMovie.Id);
        unlistedBinding.MediaPath = Path.Combine(retiredLocation, "still-mounted");
        await database.SaveChangesAsync();
        episodes.Remove(keptOne);
        File.Delete(keptOne.Path);
        episodes.Remove(dailyNumbered);
        File.Delete(dailyNumbered.Path);
        movies.Remove(unlistedMovie);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        r6Run = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        var unlistedEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9600);
        Assert(r6Run.IncompleteLibraries == 0 &&
            await database.Episodes.AnyAsync(episode => episode.TmdbId == 9711 && episode.State == FileState.None) &&
            await database.Episodes.AnyAsync(episode => episode.TmdbId == 9701 && episode.State == FileState.None) &&
            !await database.EpisodeBindings.AnyAsync(binding =>
                binding.JellyfinItemId == keptOne.Id || binding.JellyfinItemId == dailyNumbered.Id),
            "Bindings with no storage identity or outside current locations are confirmed absent by a direct existence check");
        Assert(unlistedEntry.State == FileState.OnDisk &&
            await database.EntryBindings.AnyAsync(binding => binding.EntryId == unlistedEntry.Id) &&
            r6Run.DiagnosticsJson?.Contains("still exists although Jellyfin no longer lists it", StringComparison.Ordinal) == true,
            "A media file that still exists is not treated as absent, and only that title is excluded");
        movies.Add(unlistedMovie);

        // A reboot or USB re-enumeration changes only the device number and source of the same mount.
        await WriteMountInfoAsync(mountInfoPath, folder, moviePath, tvPath, true, true, "3:9", "/dev/sdb1");
        series.Remove(keptSeries);
        Directory.Delete(keptSeries.Path, true);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        r6Run = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        keptEntry = await database.Entries.SingleAsync(entry => entry.Id == keptEntry.Id);
        Assert(r6Run.IncompleteLibraries == 0 && keptEntry.State == FileState.None &&
            !await database.EntryBindings.AnyAsync(binding => binding.EntryId == keptEntry.Id) &&
            await database.EpisodeBindings.Where(binding => binding.TargetLibraryId == tvLibrary.Id)
                .AllAsync(binding => binding.StorageIdentity != null && binding.StorageIdentity.StartsWith("3:9|")) &&
            await database.EntryBindings.Where(binding => binding.TargetLibraryId == tvLibrary.Id)
                .AllAsync(binding => binding.StorageIdentity != null && binding.StorageIdentity.StartsWith("3:9|")),
            "A device-number change re-baselines every binding in the library before absence is confirmed");

        // P2.R7: re-adding the TV path under a new name gives the library a new identity.
        dailyEntry = await database.Entries.SingleAsync(entry => entry.Id == dailyEntry.Id);
        dailyEntry.RetentionPolicy = RetentionPolicy.Never;
        dailyEntry.Monitored = true;
        await database.SaveChangesAsync();
        var oldTvLibraryId = tvLibrary.Id;
        tvLibrary = new CollectionFolder { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows };
        folders.Remove(tvFolder);
        folders.Add(new VirtualFolderInfo
        {
            Name = "TV re-added", ItemId = tvLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.tvshows,
            Locations = [tvPath]
        });
        // A user wanted the same title in the new library before reconciliation re-homed it.
        var wanted = new Entry
        {
            MediaType = "series", TmdbId = 9700, TargetLibraryId = tvLibrary.Id, Title = "Daily series",
            Monitored = false
        };
        database.Entries.Add(wanted);
        database.Episodes.Add(new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 9799, SeasonNumber = 2, EpisodeNumber = 1 });
        database.History.Add(new HistoryRecord { EntryId = wanted.Id, EventType = "added", Summary = "Added" });
        await database.SaveChangesAsync();
        // A second library shares the TV path and lists one series as well.
        overlapSeries = Series(9730, "Shared path series", tvPath);
        series.Add(overlapSeries);
        episodes.Add(Episode(9731, overlapSeries.Id, "Shared path episode", tvPath));
        folders.Add(new VirtualFolderInfo
        {
            Name = "Zz overlapping TV", ItemId = overlapLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.tvshows,
            Locations = [tvPath]
        });
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        var r7Run = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt).FirstAsync();
        dailyEntry = await database.Entries.SingleAsync(entry => entry.Id == dailyEntry.Id);
        Assert(r7Run.ConflictedItems == 0 && r7Run.IncompleteLibraries == 0 &&
            dailyEntry.TargetLibraryId == tvLibrary.Id && dailyEntry.RetentionPolicy == RetentionPolicy.Never &&
            dailyEntry.Monitored && dailyEntry.State == FileState.OnDisk &&
            await database.History.CountAsync(history => history.EntryId == dailyEntry.Id &&
                history.EventType == "library_moved") == 1 &&
            await database.EntryBindings.Where(binding => binding.EntryId == dailyEntry.Id)
                .AllAsync(binding => binding.TargetLibraryId == tvLibrary.Id),
            "A re-created library re-homes its entries with one library_moved event and Keep and monitoring preserved");
        Assert(!await database.Entries.AnyAsync(entry => entry.Id == wanted.Id) &&
            await database.Episodes.AnyAsync(episode => episode.EntryId == dailyEntry.Id && episode.TmdbId == 9799) &&
            await database.History.AnyAsync(history => history.EntryId == dailyEntry.Id && history.EventType == "added"),
            "A file-less entry for the same title in the new library is merged into the re-homed entry");
        Assert(await database.Entries.CountAsync(entry => entry.TmdbId == 9730) == 1 &&
            await database.Entries.AnyAsync(entry => entry.TmdbId == 9730 && entry.TargetLibraryId == tvLibrary.Id) &&
            r7Run.DiagnosticsJson?.Contains("owned by another library", StringComparison.Ordinal) == true,
            "Overlapping libraries follow one rule: the first library by name owns the title and the other reports an overlap");
        Assert((await database.Entries.SingleAsync(entry => entry.Id == losingEntry.Id)).TargetLibraryId == oldTvLibraryId,
            "An unmatched title is not re-homed and stays listed as an orphan");

        // P2.R10: a post-scan absence pass skipped behind an active run runs right after it.
        var deferredMovie = pagedMovies[1];
        var deferredEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9601);
        movies.Remove(deferredMovie);
        File.Delete(deferredMovie.Path);
        var gate = provider.GetRequiredService<ReconciliationRunGate>();
        await using (await gate.TryAcquireAsync(default))
            await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        Assert((await database.Entries.SingleAsync(entry => entry.Id == deferredEntry.Id)).State == FileState.OnDisk,
            "A post-scan pass that finds a run active changes nothing itself");
        using (var manualScope = provider.CreateScope())
            await manualScope.ServiceProvider.GetRequiredService<CatalogBackfillRunner>()
                .RunAsync(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        Assert((await database.Entries.SingleAsync(entry => entry.Id == deferredEntry.Id)).State == FileState.None &&
            await database.History.CountAsync(history => history.EntryId == deferredEntry.Id &&
                history.EventType == "media_missing") == 1,
            "The deferred absence pass runs as soon as the active run finishes");

        // Run rows are pruned to the most recent 50, and a row left running by a stopped process is interrupted.
        for (var index = 0; index < 60; index++)
            database.ReconciliationRuns.Add(new ReconciliationRun
            {
                StartedAt = DateTime.UtcNow.AddDays(-30).AddMinutes(index), Status = "completed"
            });
        var stranded = new ReconciliationRun { StartedAt = DateTime.UtcNow };
        database.ReconciliationRuns.Add(stranded);
        await database.SaveChangesAsync();
        var initializer = new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
        var notReady = new CatalogBackfillRunner(database, provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<JellyfinNativeTitleSource>(), gate,
            provider.GetRequiredService<ReconciliationLibraryLock>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogBackfillRunner>.Instance, initializer);
        var runsBefore = await database.ReconciliationRuns.CountAsync();
        var refused = false;
        try
        {
            await notReady.RunAsync(new InlineProgress(), default);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("not ready", StringComparison.Ordinal))
        {
            refused = true;
        }

        Assert(refused && await database.ReconciliationRuns.CountAsync() == runsBefore,
            "Reconciliation performs no writes while the plugin database is not ready");
        await initializer.StartAsync(default);
        database.ChangeTracker.Clear();
        Assert(initializer.IsReady &&
            (await database.ReconciliationRuns.SingleAsync(run => run.Id == stranded.Id)).Status == "interrupted" &&
            await database.ReconciliationRuns.CountAsync() == 50,
            "Startup marks a stranded running row interrupted and keeps only the most recent 50 runs");
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
        bool tvMounted,
        string tvDevice = "3:1",
        string tvSource = "/dev/tv")
    {
        var lines = new List<string>
        {
            $"10 1 1:1 / {EscapeMount(parent)} rw - apfs /dev/root rw"
        };
        if (movieMounted)
            lines.Add($"11 10 2:1 /library/movies {EscapeMount(moviePath)} rw - ext4 /dev/movies rw");
        if (tvMounted)
            lines.Add($"12 10 {tvDevice} /library/tv {EscapeMount(tvPath)} rw - ext4 {tvSource} rw");
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
