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
        var movedEpisode = new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 9799, SeasonNumber = 2, EpisodeNumber = 1 };
        database.Episodes.Add(movedEpisode);
        // A moved episode keeps its evaluation; left on the wanted entry it would go with that entry (Codex round 2 P1).
        database.RetentionEvaluations.Add(new RetentionEvaluation { EntryId = wanted.Id, EpisodeId = movedEpisode.Id,
            TargetId = movedEpisode.Id, State = "waiting", Reason = "waiting_for_completion", EvaluatedAt = DateTime.UtcNow,
            BaselineAt = DateTime.UtcNow, GraceNotBefore = DateTime.UtcNow });
        // Whole-review P1 3: the wanted entry kept the episode the re-homed entry already tracks, and holds position-identity
        // rows (TMDB id 0) at other positions than the re-homed entry's own position row.
        database.Episodes.Add(new JellyfinMod.Data.Episode
        {
            EntryId = wanted.Id, TmdbId = 9701, SeasonNumber = 1, EpisodeNumber = 1, RetentionPolicy = RetentionPolicy.Never
        });
        database.Episodes.Add(new JellyfinMod.Data.Episode { EntryId = dailyEntry.Id, TmdbId = 0, SeasonNumber = 4, EpisodeNumber = 1 });
        database.Episodes.Add(new JellyfinMod.Data.Episode
        {
            EntryId = wanted.Id, TmdbId = 0, SeasonNumber = 4, EpisodeNumber = 1, RetentionPolicy = RetentionPolicy.Days,
            ReclaimAfterDays = 90
        });
        database.Episodes.Add(new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 0, SeasonNumber = 4, EpisodeNumber = 2 });
        // Codex re-review P2-d: the surviving episode inherits the global window (14 days); the duplicate's explicit day is
        // shorter and must not replace it. P1-b: a surviving episode without an evaluation takes the duplicate's longer window
        // with a grace of its own, and a duplicate un-kept an hour ago hands its grace to the survivor.
        var inheritingSurvivor = new JellyfinMod.Data.Episode { EntryId = dailyEntry.Id, TmdbId = 9702, SeasonNumber = 5, EpisodeNumber = 2 };
        var unevaluatedSurvivor = new JellyfinMod.Data.Episode { EntryId = dailyEntry.Id, TmdbId = 9703, SeasonNumber = 5, EpisodeNumber = 3 };
        var graceSurvivor = new JellyfinMod.Data.Episode { EntryId = dailyEntry.Id, TmdbId = 9704, SeasonNumber = 5, EpisodeNumber = 4 };
        var unkeptDuplicate = new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 9704, SeasonNumber = 5, EpisodeNumber = 4 };
        database.Episodes.AddRange(inheritingSurvivor, unevaluatedSurvivor, graceSurvivor, unkeptDuplicate,
            new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 9702, SeasonNumber = 5, EpisodeNumber = 2,
                RetentionPolicy = RetentionPolicy.Days, ReclaimAfterDays = 1 },
            new JellyfinMod.Data.Episode { EntryId = wanted.Id, TmdbId = 9703, SeasonNumber = 5, EpisodeNumber = 3,
                RetentionPolicy = RetentionPolicy.Days, ReclaimAfterDays = 30 });
        var unkeptAt = DateTime.UtcNow.AddHours(-1);
        database.RetentionEvaluations.AddRange(
            new RetentionEvaluation { EntryId = dailyEntry.Id, EpisodeId = graceSurvivor.Id, TargetId = graceSurvivor.Id,
                State = "waiting", Reason = "waiting_for_completion",
                EvaluatedAt = unkeptAt.AddDays(-5), BaselineAt = unkeptAt.AddDays(-5) },
            new RetentionEvaluation { EntryId = wanted.Id, EpisodeId = unkeptDuplicate.Id, TargetId = unkeptDuplicate.Id,
                State = "waiting", Reason = "waiting_for_completion",
                EvaluatedAt = unkeptAt, BaselineAt = unkeptAt, GraceNotBefore = unkeptAt });
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
        var mergedEpisodes = await database.Episodes.AsNoTracking().Where(episode => episode.EntryId == dailyEntry.Id).ToListAsync();
        Assert(mergedEpisodes.Count(episode => episode.TmdbId == 9701) == 1 &&
            mergedEpisodes.Single(episode => episode.TmdbId == 9701).RetentionPolicy == RetentionPolicy.Never,
            "A Keep on the merged entry's copy of a tracked episode carries over to the surviving episode (whole-review P1 3)");
        Assert(mergedEpisodes.Count(episode => episode.TmdbId == 0 && episode.SeasonNumber == 4 && episode.EpisodeNumber == 1) == 1 &&
            mergedEpisodes.Single(episode => episode is { TmdbId: 0, SeasonNumber: 4, EpisodeNumber: 1 }) is
                { RetentionPolicy: RetentionPolicy.Days, ReclaimAfterDays: 90 } &&
            mergedEpisodes.Any(episode => episode is { TmdbId: 0, SeasonNumber: 4, EpisodeNumber: 2 }),
            "Position-identity episodes merge by season and number: a matching row's longer window carries over and another " +
            "position is moved, not dropped (whole-review P1 3): " + string.Join(", ", mergedEpisodes.Select(episode =>
                $"{episode.TmdbId}/S{episode.SeasonNumber}E{episode.EpisodeNumber}/{episode.RetentionPolicy}")));
        var inheriting = mergedEpisodes.Single(episode => episode.Id == inheritingSurvivor.Id);
        Assert(inheriting.RetentionPolicy == RetentionPolicy.Inherit,
            $"A duplicate's shorter explicit window never replaces the survivor's longer inherited one (Codex re-review P2-d): " +
            $"{inheriting.RetentionPolicy}/{inheriting.ReclaimAfterDays}");
        var unevaluated = mergedEpisodes.Single(episode => episode.Id == unevaluatedSurvivor.Id);
        var createdEvaluation = await database.RetentionEvaluations.AsNoTracking().SingleOrDefaultAsync(value => value.TargetId == unevaluatedSurvivor.Id);
        var carried = await database.RetentionEvaluations.AsNoTracking().SingleOrDefaultAsync(value => value.TargetId == graceSurvivor.Id);
        Assert(unevaluated is { RetentionPolicy: RetentionPolicy.Days, ReclaimAfterDays: 30 } && createdEvaluation?.GraceNotBefore is not null &&
            carried?.GraceNotBefore is { } carriedGrace && Math.Abs((carriedGrace - unkeptAt).TotalSeconds) < 1,
            $"A survivor without an evaluation gets one with a grace, and a duplicate's recent grace is carried over (Codex re-review P1-b): " +
            $"{unevaluated.RetentionPolicy}/{unevaluated.ReclaimAfterDays}, created {createdEvaluation?.GraceNotBefore:O}, carried {carried?.GraceNotBefore:O}");
        Assert(await database.RetentionEvaluations.AnyAsync(value => value.TargetId == movedEpisode.Id && value.EntryId == dailyEntry.Id),
            "A wanted episode moved by a merge keeps its evaluation and its grace (Codex round 2 P1)");
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

        // Dead bindings (fix/dead-bindings): a media path move gave Jellyfin new items, and the plugin kept rows for ids Jellyfin
        // now answers 404 for. A row whose file is the same file (device, inode, birth time) as a live binding is dropped, never
        // touching a file or a retention window; every other dead row is left to absence confirmation.
        var liveMovie = Movie(9800, "Dead binding movie", moviePath);
        movies.Add(liveMovie);
        database.ChangeTracker.Clear();
        await task.Run(new InlineProgress(), default);
        database.ChangeTracker.Clear();
        var deadEntry = await database.Entries.SingleAsync(entry => entry.TmdbId == 9800);
        var liveBinding = await database.EntryBindings.SingleAsync(binding => binding.EntryId == deadEntry.Id);
        var scheduledAt = DateTime.UtcNow.AddDays(-2);
        var deadline = DateTime.UtcNow.AddDays(5);
        var evaluation = await database.RetentionEvaluations.SingleOrDefaultAsync(candidate => candidate.TargetId == deadEntry.Id);
        if (evaluation is null)
        {
            evaluation = new RetentionEvaluation { EntryId = deadEntry.Id, TargetId = deadEntry.Id };
            database.RetentionEvaluations.Add(evaluation);
        }

        evaluation.State = "scheduled";
        evaluation.Reason = "grace_period";
        evaluation.EligibleAt = scheduledAt;
        evaluation.Deadline = deadline;
        evaluation.BaselineAt = scheduledAt.AddDays(-1);
        evaluation.CompletionBasisAt = scheduledAt;
        var goneId = Guid.NewGuid();
        var otherFileId = Guid.NewGuid();
        var movedMountId = Guid.NewGuid();
        var watcherId = Guid.NewGuid();
        var watchedAt = DateTime.UtcNow.AddDays(-2);
        // What the dead row's item recorded when it was watched: the schedule above was built on it.
        database.CompletionObservations.Add(new CompletionObservation
        {
            EntryId = deadEntry.Id, TargetId = deadEntry.Id, UserId = watcherId, JellyfinItemId = goneId,
            EvidenceAvailable = true, Played = true, LastPlayedAt = watchedAt, CompletedAt = watchedAt, ObservedAt = watchedAt,
            SourceReason = "event"
        });
        var oldMount = Path.Combine(moviePath, "old-mount");
        Directory.CreateDirectory(oldMount);
        var otherFile = Path.Combine(oldMount, "Other file.mkv");
        await File.WriteAllTextAsync(otherFile, "a different file that only looks like a version");
        database.EntryBindings.AddRange(
            // The file is gone, which absence confirmation (not a library event) proves on the library's mounts.
            new EntryBinding
            {
                EntryId = deadEntry.Id, JellyfinItemId = goneId, TargetLibraryId = movieLibrary.Id, VersionGroupId = goneId,
                MediaPath = Path.Combine(oldMount, "Dead binding movie.mkv"), StorageIdentity = liveBinding.StorageIdentity
            },
            new EntryBinding
            {
                EntryId = deadEntry.Id, JellyfinItemId = movedMountId, TargetLibraryId = movieLibrary.Id, VersionGroupId = movedMountId,
                MediaPath = Path.Combine(oldMount, "Dead binding movie other mount.mkv"),
                StorageIdentity = "9:9|/elsewhere|" + moviePath + "|ext4|/dev/elsewhere"
            },
            // A file that exists and is not any live representation's: it may be the one Jellyfin has not reported yet.
            new EntryBinding
            {
                EntryId = deadEntry.Id, JellyfinItemId = otherFileId, TargetLibraryId = movieLibrary.Id, VersionGroupId = otherFileId,
                MediaPath = otherFile, StorageIdentity = null
            });
        await database.SaveChangesAsync();
        var deadFilesBefore = Directory.EnumerateFiles(moviePath, "*", SearchOption.AllDirectories).Order().ToArray();
        await provider.GetRequiredService<JellyfinItemReconciliationRunner>().ReconcileAsync(liveMovie.Id, default);
        database.ChangeTracker.Clear();
        var afterEvent = await database.EntryBindings.Where(binding => binding.EntryId == deadEntry.Id)
            .Select(binding => binding.JellyfinItemId).ToListAsync();
        Assert(afterEvent.Count == 4 && afterEvent.Contains(goneId) && afterEvent.Contains(movedMountId) &&
            afterEvent.Contains(otherFileId) &&
            !await database.History.AnyAsync(history => history.EntryId == deadEntry.Id && history.EventType == "dead_binding_removed") &&
            (await database.CompletionObservations.SingleAsync(candidate => candidate.TargetId == deadEntry.Id)).JellyfinItemId == goneId,
            "A library event leaves a dead binding whose file is gone, on another mount or a different file to absence confirmation");

        if (OperatingSystem.IsLinux())
        {
            // The very same file under the old mount point: one inode, two paths. The old row duplicates the live one.
            var aliasId = Guid.NewGuid();
            var aliasPath = Path.Combine(oldMount, "Dead binding movie alias.mkv");
            Assert(new UnixFileInspector().Link(liveMovie.Path, aliasPath).Linked, "The alias path can be linked for the test");
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = deadEntry.Id, JellyfinItemId = aliasId, TargetLibraryId = movieLibrary.Id, VersionGroupId = aliasId,
                MediaPath = aliasPath, StorageIdentity = null
            });
            (await database.CompletionObservations.SingleAsync(candidate => candidate.TargetId == deadEntry.Id)).JellyfinItemId = aliasId;
            await database.SaveChangesAsync();
            await task.Run(new InlineProgress(), default);
            database.ChangeTracker.Clear();
            var aliasEvents = await database.History.Where(history => history.EntryId == deadEntry.Id &&
                history.EventType == "dead_binding_removed").ToListAsync();
            var followed = await database.CompletionObservations.SingleAsync(candidate => candidate.TargetId == deadEntry.Id);
            var afterScan = await database.EntryBindings.Where(binding => binding.EntryId == deadEntry.Id)
                .Select(binding => binding.JellyfinItemId).ToListAsync();
            Assert(!afterScan.Contains(aliasId) && afterScan.Contains(liveMovie.Id) && afterScan.Contains(otherFileId) &&
                afterScan.Contains(movedMountId) && afterScan.Contains(goneId) &&
                aliasEvents.Count == 1 && aliasEvents[0].Data!.Contains("same_file_as_live_binding", StringComparison.Ordinal) &&
                aliasEvents[0].Data!.Contains(aliasId.ToString(), StringComparison.OrdinalIgnoreCase),
                "A scan drops a dead binding that is the same file (device, inode, birth time) as a live binding and records why");
            Assert(followed.JellyfinItemId == liveMovie.Id && followed.Played && followed.CompletedAt == watchedAt &&
                followed.UserId == watcherId,
                "The completion read through the dead item follows the title's live item, so the running window keeps its evidence");
            var afterEvaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == deadEntry.Id);
            Assert(afterEvaluation.State == "scheduled" && afterEvaluation.Deadline == deadline &&
                afterEvaluation.EligibleAt == scheduledAt && afterEvaluation.BaselineAt == scheduledAt.AddDays(-1) &&
                afterEvaluation.CompletionBasisAt == scheduledAt &&
                !await database.History.AnyAsync(history => history.EntryId == deadEntry.Id && history.EventType == "retention_reset") &&
                File.Exists(aliasPath) && File.Exists(liveMovie.Path) && File.Exists(otherFile) &&
                deadFilesBefore.Concat([aliasPath]).Order().SequenceEqual(Directory.EnumerateFiles(moviePath, "*", SearchOption.AllDirectories).Order()),
                "Dropping a dead binding keeps the title's retention schedule and every file on disk");
            await task.Run(new InlineProgress(), default);
            database.ChangeTracker.Clear();
            Assert(await database.History.CountAsync(history => history.EntryId == deadEntry.Id &&
                    history.EventType == "dead_binding_removed") == 1,
                "A repeat scan records nothing twice");

            // The first reconciliation after a library moved: the new item brings the same file under a new path. It is not a new
            // file, so the title's window must keep running (the old row's evidence moves with the file).
            var movedOld = Path.Combine(oldMount, "Moved movie.mkv");
            await File.WriteAllTextAsync(movedOld, "the file the library moved");
            var movedNewPath = Path.Combine(moviePath, "Moved movie.mkv");
            Assert(new UnixFileInspector().Link(movedOld, movedNewPath).Linked, "The moved path can be linked for the test");
            var movedNative = new Movie { Id = Guid.NewGuid(), Name = "Moved movie", Path = movedNewPath };
            movedNative.ProviderIds["Tmdb"] = "9810";
            var movedDeadId = Guid.NewGuid();
            var movedEntry = new Entry
            {
                MediaType = "movie", TmdbId = 9810, TargetLibraryId = movieLibrary.Id, Title = "Moved movie", State = FileState.OnDisk,
                JellyfinItemId = movedDeadId, Monitored = false
            };
            database.Entries.Add(movedEntry);
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = movedEntry.Id, JellyfinItemId = movedDeadId, TargetLibraryId = movieLibrary.Id, VersionGroupId = movedDeadId,
                MediaPath = movedOld, StorageIdentity = liveBinding.StorageIdentity
            });
            database.RetentionEvaluations.Add(new RetentionEvaluation
            {
                EntryId = movedEntry.Id, TargetId = movedEntry.Id, State = "scheduled", Reason = "grace_period", EligibleAt = scheduledAt,
                Deadline = deadline, BaselineAt = scheduledAt.AddDays(-1), CompletionBasisAt = scheduledAt
            });
            database.CompletionObservations.Add(new CompletionObservation
            {
                EntryId = movedEntry.Id, TargetId = movedEntry.Id, UserId = watcherId, JellyfinItemId = movedDeadId,
                EvidenceAvailable = true, Played = true, LastPlayedAt = watchedAt, CompletedAt = watchedAt, ObservedAt = watchedAt,
                SourceReason = "event"
            });
            await database.SaveChangesAsync();
            movies.Add(movedNative);
            await provider.GetRequiredService<JellyfinItemReconciliationRunner>().ReconcileAsync(movedNative.Id, default);
            database.ChangeTracker.Clear();
            var movedBindings = await database.EntryBindings.Where(binding => binding.EntryId == movedEntry.Id)
                .Select(binding => binding.JellyfinItemId).ToListAsync();
            var movedEvaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == movedEntry.Id);
            Assert(movedBindings.SequenceEqual([movedNative.Id]) &&
                (await database.CompletionObservations.SingleAsync(candidate => candidate.TargetId == movedEntry.Id)).JellyfinItemId == movedNative.Id &&
                movedEvaluation.State == "scheduled" && movedEvaluation.Deadline == deadline && movedEvaluation.BaselineAt == scheduledAt.AddDays(-1) &&
                !await database.History.AnyAsync(history => history.EntryId == movedEntry.Id && history.EventType == "retention_reset") &&
                File.Exists(movedOld) && File.Exists(movedNewPath),
                "A moved library's first reconciliation keeps the title's running retention window: the same file is not a new file");
            movies.Remove(movedNative);

            // The same inode rewritten in place before the library moved: the new item's file is not the file whose watch scheduled the
            // window, so the replacement path resets it and the dead row (whose evidence belongs to the old content) stays.
            var rewrittenOld = Path.Combine(oldMount, "Rewritten movie.mkv");
            await File.WriteAllTextAsync(rewrittenOld, "the original content");
            Assert(new UnixFileInspector().TryInspect(rewrittenOld, out var original), "The original file can be fingerprinted");
            await File.WriteAllTextAsync(rewrittenOld, "content written in place after it was watched, and longer");
            var rewrittenNewPath = Path.Combine(moviePath, "Rewritten movie.mkv");
            Assert(new UnixFileInspector().Link(rewrittenOld, rewrittenNewPath).Linked, "The rewritten path can be linked for the test");
            var rewrittenNative = new Movie { Id = Guid.NewGuid(), Name = "Rewritten movie", Path = rewrittenNewPath };
            rewrittenNative.ProviderIds["Tmdb"] = "9811";
            var rewrittenDeadId = Guid.NewGuid();
            var rewrittenEntry = new Entry
            {
                MediaType = "movie", TmdbId = 9811, TargetLibraryId = movieLibrary.Id, Title = "Rewritten movie", State = FileState.OnDisk,
                JellyfinItemId = rewrittenDeadId, Monitored = false
            };
            database.Entries.Add(rewrittenEntry);
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = rewrittenEntry.Id, JellyfinItemId = rewrittenDeadId, TargetLibraryId = movieLibrary.Id,
                VersionGroupId = rewrittenDeadId, MediaPath = rewrittenOld, StorageIdentity = liveBinding.StorageIdentity,
                FileFingerprint = original.FileFingerprint
            });
            database.RetentionEvaluations.Add(new RetentionEvaluation
            {
                EntryId = rewrittenEntry.Id, TargetId = rewrittenEntry.Id, State = "scheduled", Reason = "grace_period",
                EligibleAt = scheduledAt, Deadline = deadline, BaselineAt = scheduledAt.AddDays(-1), CompletionBasisAt = scheduledAt
            });
            await database.SaveChangesAsync();
            movies.Add(rewrittenNative);
            await provider.GetRequiredService<JellyfinItemReconciliationRunner>().ReconcileAsync(rewrittenNative.Id, default);
            database.ChangeTracker.Clear();
            var rewrittenEvaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == rewrittenEntry.Id);
            Assert(rewrittenEvaluation.Deadline is null && rewrittenEvaluation.BaselineAt > scheduledAt &&
                await database.History.AnyAsync(history => history.EntryId == rewrittenEntry.Id && history.EventType == "retention_reset") &&
                await database.EntryBindings.AnyAsync(binding => binding.JellyfinItemId == rewrittenDeadId) &&
                await database.EntryBindings.AnyAsync(binding => binding.JellyfinItemId == rewrittenNative.Id),
                "A file rewritten in place before the library moved is a new file: its window restarts and the old row keeps its evidence");
            movies.Remove(rewrittenNative);
        }
        else
            Console.WriteLine("SKIP: the same-file dead binding and the moved-library window need statx (Linux); the Pi run covers them");

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
