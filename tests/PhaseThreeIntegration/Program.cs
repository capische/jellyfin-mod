using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-three-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    await VerifyLegacyMigrationAsync(folder);
    await VerifyEventAndPolicyPersistenceAsync(folder);
    Console.WriteLine("PASS: Phase 3 policy and completion evidence survive real events, SQLite migration and restart");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static async Task VerifyLegacyMigrationAsync(string folder)
{
    var path = Path.Combine(folder, "legacy.db");
    await using (var database = new ModDbContext(path))
    {
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260914224459_PhaseTwoAbsenceSummary");
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, ReclaimAfterDays)
            VALUES ('10000000-0000-0000-0000-000000000001', 'movie', 1, 'Explicit', 4, 1, '2026-09-01T00:00:00', 42);
            INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, ReclaimAfterDays)
            VALUES ('10000000-0000-0000-0000-000000000002', 'movie', 2, 'Inherited', 4, 1, '2026-09-01T00:00:00', NULL);
            """);
        await migrator.MigrateAsync();
    }

    await using var migrated = new ModDbContext(path);
    var policies = await migrated.Entries.OrderBy(entry => entry.TmdbId).Select(entry => entry.RetentionPolicy).ToArrayAsync();
    Assert(policies.SequenceEqual([RetentionPolicy.Days, RetentionPolicy.Inherit]),
        "Migration preserves positive legacy overrides and maps null to inherit");
}

static async Task VerifyEventAndPolicyPersistenceAsync(string folder)
{
    var path = Path.Combine(folder, "events.db");
    var firstUser = new User("first", "auth", "reset") { Id = Guid.NewGuid() };
    var secondUser = new User("second", "auth", "reset") { Id = Guid.NewGuid() };
    List<User> allUsers = [firstUser, secondUser];
    var movieLibrary = new EmptyLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.movies };
    var tvLibrary = new EmptyLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.tvshows };
    var accessibleLibraries = new Dictionary<Guid, IReadOnlyList<BaseItem>>
    {
        [firstUser.Id] = [movieLibrary, tvLibrary],
        [secondUser.Id] = [movieLibrary, tvLibrary]
    };
    var root = new TestRoot(user => accessibleLibraries.GetValueOrDefault(user.Id, []));
    var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Path = "/fixture/movie.mkv" };
    var nativeEpisode = new MediaBrowser.Controller.Entities.TV.Episode
    {
        Id = Guid.NewGuid(), Name = "Episode", Path = "/fixture/episode.mkv", ParentIndexNumber = 1, IndexNumber = 1
    };
    var items = new Dictionary<Guid, BaseItem> { [movie.Id] = movie, [nativeEpisode.Id] = nativeEpisode };
    var states = new Dictionary<(Guid UserId, Guid ItemId), UserItemData>();
    EventHandler<UserDataSaveEventArgs>? userDataSaved = null;
    EventHandler<GenericEventArgs<User>>? userUpdated = null;
    var userData = Stub<IUserDataManager>.Create((method, arguments) => method.Name switch
    {
        "add_UserDataSaved" => AddHandler(arguments),
        "remove_UserDataSaved" => RemoveHandler(arguments),
        "GetUserData" when arguments is [User user, BaseItem item] => states.GetValueOrDefault((user.Id, item.Id)),
        _ => null
    });
    var users = Stub<IUserManager>.Create((method, arguments) => method.Name switch
    {
        "add_OnUserUpdated" => AddUserUpdatedHandler(arguments),
        "remove_OnUserUpdated" => RemoveUserUpdatedHandler(arguments),
        "GetUserById" when arguments?[0] is Guid id => allUsers.SingleOrDefault(user => user.Id == id),
        "GetUsers" => allUsers.ToArray(),
        _ => null
    });
    var library = Stub<ILibraryManager>.Create((method, arguments) => method.Name switch
    {
        "GetLocalAlternateVersionIds" => Array.Empty<Guid>(),
        "GetLinkedAlternateVersions" => Array.Empty<MediaBrowser.Controller.Entities.Video>(),
        "GetUserRootFolder" => root,
        "GetItemById" when arguments?[0] is Guid id => items.GetValueOrDefault(id),
        _ => null
    });
    var localization = Stub<ILocalizationManager>.Create((_, _) => null);
    var applicationPaths = Stub<IApplicationPaths>.Create((method, _) => method.Name switch
    {
        "get_DataPath" => folder,
        "get_PluginsPath" => folder,
        "get_PluginConfigurationsPath" => folder,
        _ => null
    });
    var persistedConfiguration = new PluginConfiguration();
    var serializer = Stub<IXmlSerializer>.Create((method, arguments) => method.Name switch
    {
        "DeserializeFromFile" => persistedConfiguration,
        "SerializeToFile" => SaveConfiguration(arguments),
        _ => null
    });
    var plugin = new Plugin(applicationPaths, serializer);

    // User decision 3: saved secrets leave the XML file for the 0600 store, references are server-owned,
    // an empty field keeps a secret and the clear marker removes it.
    plugin.UpdateConfiguration(new PluginConfiguration { TmdbReadAccessToken = "tmdb-secret-value", TransmissionPassword = "rpc-secret-value" });
    var secretsPath = Path.Combine(folder, "jellyfinmod", "acquisition-secrets.json");
    var saved = persistedConfiguration;
    Assert(saved.TmdbReadAccessToken.Length == 0 && saved.TransmissionPassword.Length == 0 &&
        saved.TmdbReadAccessTokenRef is not null && saved.TransmissionPasswordRef is not null &&
        File.ReadAllText(secretsPath).Contains("tmdb-secret-value", StringComparison.Ordinal) &&
        (OperatingSystem.IsWindows() || File.GetUnixFileMode(secretsPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)),
        "Saved credentials move to the 0600 secret store and the configuration keeps only references");
    var tokenReference = saved.TmdbReadAccessTokenRef;
    plugin.UpdateConfiguration(new PluginConfiguration { TmdbReadAccessTokenRef = "sec_forged", TransmissionPassword = Plugin.ClearSecret });
    Assert(persistedConfiguration.TmdbReadAccessTokenRef == tokenReference && persistedConfiguration.TransmissionPasswordRef is null &&
        !File.ReadAllText(secretsPath).Contains("rpc-secret-value", StringComparison.Ordinal) &&
        File.ReadAllText(secretsPath).Contains("tmdb-secret-value", StringComparison.Ordinal),
        "An empty field keeps its secret, submitted references are ignored and the clear marker removes one");
    plugin.UpdateConfiguration(new PluginConfiguration { TmdbReadAccessToken = Plugin.ClearSecret });
    var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
    var movieEntryId = Guid.NewGuid();
    var seriesEntryId = Guid.NewGuid();
    var episodeRecordId = Guid.NewGuid();

    await using (var setup = new ModDbContext(path))
    {
        await setup.Database.MigrateAsync();
        var movieEntry = new Entry
        {
            Id = movieEntryId, MediaType = "movie", TmdbId = 10, Title = "Movie", State = FileState.OnDisk,
            TargetLibraryId = movieLibrary.Id
        };
        var seriesEntry = new Entry
        {
            Id = seriesEntryId, MediaType = "series", TmdbId = 20, Title = "Series", State = FileState.OnDisk,
            TargetLibraryId = tvLibrary.Id
        };
        var episode = new Episode
        {
            Id = episodeRecordId, EntryId = seriesEntry.Id, TmdbId = 21, SeasonNumber = 1, EpisodeNumber = 1,
            Title = "Episode", State = FileState.OnDisk, JellyfinItemId = nativeEpisode.Id
        };
        setup.Entries.AddRange(movieEntry, seriesEntry);
        setup.Episodes.Add(episode);
        setup.EntryBindings.Add(new EntryBinding
        {
            EntryId = movieEntry.Id, JellyfinItemId = movie.Id, TargetLibraryId = movieLibrary.Id, VersionGroupId = movie.Id
        });
        setup.EpisodeBindings.Add(new EpisodeBinding
        {
            EpisodeId = episode.Id, JellyfinItemId = nativeEpisode.Id, SeriesItemId = Guid.NewGuid(), TargetLibraryId = tvLibrary.Id
        });
        await setup.SaveChangesAsync();
    }

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddTransient(_ => new ModDbContext(path));
    services.AddSingleton(users);
    services.AddSingleton(library);
    services.AddSingleton(userData);
    services.AddTransient(_ => new LibraryAccess(users, library, localization));
    services.AddSingleton<TimeProvider>(clock);
    services.AddTransient<RetentionPolicyService>();
    services.AddTransient<RetentionCompletionService>();
    services.AddTransient<RetentionEvaluator>();
    // A short access poll keeps the P3.T11 user-change check within the test's wait budget.
    services.AddSingleton(provider => new RetentionEventListener(userData, users,
        provider.GetRequiredService<IServiceScopeFactory>(),
        provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RetentionEventListener>>(),
        TimeSpan.FromMilliseconds(200)));
    await using var provider = services.BuildServiceProvider();
    var listener = provider.GetRequiredService<RetentionEventListener>();
    await listener.StartAsync(default);
    try
    {
        await WaitForAsync(path, async database => await database.RetentionPolicySnapshots.CountAsync() == 1,
            "Startup did not persist the disabled default All users policy");
        await WaitForAsync(path, async database => await database.RetentionEvaluations.CountAsync() == 2,
            "Startup did not evaluate the bound movie and episode");
        await using (var initial = new ModDbContext(path))
        {
            var policy = await initial.RetentionPolicySnapshots.SingleAsync();
            Assert(policy.Version == 1 && !policy.Enabled && policy.WatchedUserMode == WatchedUserMode.AllUsers &&
                policy.EnabledAt is null, "Default retention policy is disabled and uses All users");
            Assert(await initial.RetentionEvaluations.AllAsync(evaluation => evaluation.State == "disabled" && evaluation.Deadline == null),
                "Disabled retention clears every completion deadline");
        }

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.SelectedUser,
            RetentionSelectedUserId = secondUser.Id,
            ReclaimAfterDays = 21,
            ExemptFavourites = false
        });
        await WaitForAsync(path, async database => (await database.RetentionPolicySnapshots.SingleAsync()).Version == 2,
            "Configuration event did not advance the durable policy revision");
        await WaitForAsync(path, async database => await database.RetentionEvaluations.AllAsync(evaluation =>
                evaluation.PolicyVersion == 2 && evaluation.State == "blocked"),
            "Enabled policy did not block targets with missing completion evidence");
        await using (var configured = new ModDbContext(path))
        {
            var policy = await configured.RetentionPolicySnapshots.SingleAsync();
            Assert(policy.Enabled && policy.EnabledAt == clock.GetUtcNow().UtcDateTime &&
                policy.WatchedUserMode == WatchedUserMode.SelectedUser && policy.SelectedUserId == secondUser.Id &&
                policy.ReclaimAfterDays == 21 && !policy.ExemptFavourites,
                "Selected user policy persists its account, duration and enabled baseline");
        }

        var firstCompletion = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        states[(firstUser.Id, movie.Id)] = State(true, 0, firstCompletion);
        Raise(firstUser.Id, movie, UserDataSaveReason.TogglePlayed);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id, observation => observation.CompletedAt == firstCompletion);

        clock.Advance(TimeSpan.FromHours(1));
        states[(firstUser.Id, movie.Id)] = State(true, 0, clock.GetUtcNow().UtcDateTime);
        Raise(firstUser.Id, movie, UserDataSaveReason.PlaybackFinished);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id,
            observation => observation.ObservedAt == clock.GetUtcNow().UtcDateTime);
        await using (var duplicate = new ModDbContext(path))
            Assert((await duplicate.CompletionObservations.SingleAsync(observation => observation.UserId == firstUser.Id &&
                observation.TargetId == movieEntryId)).CompletedAt == firstCompletion,
                "Duplicate completed notifications do not move the original completion time");

        clock.Advance(TimeSpan.FromHours(1));
        states[(firstUser.Id, movie.Id)] = State(true, 500, clock.GetUtcNow().UtcDateTime);
        Raise(firstUser.Id, movie, UserDataSaveReason.PlaybackProgress);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id, observation => observation.CompletedAt is null);

        clock.Advance(TimeSpan.FromHours(1));
        var replayCompletion = clock.GetUtcNow().UtcDateTime;
        states[(firstUser.Id, movie.Id)] = State(true, 0, replayCompletion, favorite: true);
        Raise(firstUser.Id, movie, UserDataSaveReason.PlaybackFinished);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id,
            observation => observation.CompletedAt == replayCompletion && observation.IsFavorite);

        states[(secondUser.Id, movie.Id)] = State(false, 0, null);
        Raise(secondUser.Id, movie, UserDataSaveReason.TogglePlayed);
        states[(secondUser.Id, nativeEpisode.Id)] = State(true, 0, replayCompletion);
        Raise(secondUser.Id, nativeEpisode, UserDataSaveReason.PlaybackFinished);
        // P3.T8: evaluation reads missing users live, so the first user's episode evidence exists too.
        await WaitForAsync(path, async database => await database.CompletionObservations.CountAsync() == 4,
            "Movie and episode observations did not remain isolated by user and stable target");

        items.Remove(movie.Id);
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                .RefreshAsync(firstUser.Id, movie.Id, "Repair", default);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id,
            observation => !observation.EvidenceAvailable && observation.CompletedAt is null);
        items[movie.Id] = movie;

        var repairProgress = 0d;
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                .RefreshAllAsync(new InlineProgress(value => repairProgress = value), default);
            await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
        }
        Assert(repairProgress == 100, "Repair re-reads every bound target for every user");

        clock.Advance(TimeSpan.FromHours(1));
        var selectedCompletion = clock.GetUtcNow().UtcDateTime;
        states[(secondUser.Id, movie.Id)] = State(true, 0, selectedCompletion);
        Raise(secondUser.Id, movie, UserDataSaveReason.TogglePlayed);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "scheduled" &&
            evaluation.CompletionBasisAt == selectedCompletion && evaluation.EligibleAt == selectedCompletion &&
            evaluation.Deadline == selectedCompletion.AddDays(21),
            "Selected user did not create its own full retention window");
        DateTime selectedDeadline;
        await using (var selected = new ModDbContext(path))
            selectedDeadline = (await selected.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == movieEntryId)).Deadline!.Value;

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.AnyUser,
            ReclaimAfterDays = 7,
            ExemptFavourites = false
        });
        await WaitForAsync(path, async database => (await database.RetentionPolicySnapshots.SingleAsync()).Version == 3,
            "Any user policy did not advance the revision");
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "scheduled" &&
            evaluation.CompletionBasisAt == replayCompletion && evaluation.Deadline == selectedDeadline,
            "Any user did not use the first completion while preserving an existing longer grace period");

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.AnyUser,
            ReclaimAfterDays = 7,
            ExemptFavourites = true
        });
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "blocked" && evaluation.Reason == "favorite",
            "An accessible user's favourite did not block the completion deadline");

        await using (var keep = new ModDbContext(path))
        {
            var entry = await keep.Entries.SingleAsync(candidate => candidate.Id == movieEntryId);
            entry.RetentionPolicy = RetentionPolicy.Never;
            await keep.SaveChangesAsync();
        }
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>().EvaluateNativeItemAsync(movie.Id, default);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "blocked" && evaluation.Reason == "kept",
            "A Never entry override did not block retention before user-state evaluation");
        await using (var inherit = new ModDbContext(path))
        {
            var entry = await inherit.Entries.SingleAsync(candidate => candidate.Id == movieEntryId);
            entry.RetentionPolicy = RetentionPolicy.Inherit;
            await inherit.SaveChangesAsync();
        }

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.AllUsers,
            ReclaimAfterDays = 7,
            ExemptFavourites = false
        });
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "scheduled" &&
            evaluation.CompletionBasisAt == selectedCompletion,
            "All users did not wait for the last accessible completion");

        clock.Advance(TimeSpan.FromHours(1));
        states[(secondUser.Id, movie.Id)] = State(false, 0, null);
        Raise(secondUser.Id, movie, UserDataSaveReason.TogglePlayed);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "waiting" && evaluation.Deadline == null,
            "Marking one accessible user unwatched did not cancel All users eligibility");

        accessibleLibraries[secondUser.Id] = [];
        RaiseUserUpdated(secondUser);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "scheduled" &&
            evaluation.EligibleAt == clock.GetUtcNow().UtcDateTime && evaluation.Deadline == clock.GetUtcNow().UtcDateTime.AddDays(7),
            "Removing an unfinished user's access did not start a fresh full grace period");

        accessibleLibraries[secondUser.Id] = [movieLibrary, tvLibrary];
        RaiseUserUpdated(secondUser);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "waiting" && evaluation.Deadline == null,
            "Adding an unfinished user back did not block All users eligibility");

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.SelectedUser,
            RetentionSelectedUserId = secondUser.Id,
            ReclaimAfterDays = 7,
            ExemptFavourites = false
        });
        accessibleLibraries[secondUser.Id] = [];
        RaiseUserUpdated(secondUser);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "blocked" &&
            evaluation.Reason == "selected_user_inaccessible" && evaluation.Deadline == null,
            "An inaccessible selected user did not block retention");

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = false,
            RetentionWatchedUserMode = WatchedUserMode.AllUsers,
            ReclaimAfterDays = 14,
            ExemptFavourites = true
        });
        await WaitForAsync(path, async database => await database.RetentionEvaluations.AllAsync(evaluation =>
                evaluation.State == "disabled" && evaluation.Deadline == null),
            "Disabling retention did not clear every active countdown");

        clock.Advance(TimeSpan.FromDays(1));
        accessibleLibraries[secondUser.Id] = [];
        await using (var duration = new ModDbContext(path))
        {
            var entry = await duration.Entries.SingleAsync(candidate => candidate.Id == movieEntryId);
            entry.RetentionPolicy = RetentionPolicy.Days;
            entry.ReclaimAfterDays = 3;
            await duration.SaveChangesAsync();
        }
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.AnyUser,
            ReclaimAfterDays = 14,
            ExemptFavourites = false
        });
        // PHASE10 Q9 (2026-09-24): switching retention off and on keeps the countdown that was running; grace still
        // counts from the first switch-on, with the current per-entry window.
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.State == "scheduled" &&
            evaluation.EligibleAt < clock.GetUtcNow().UtcDateTime && evaluation.Deadline == evaluation.EligibleAt!.Value.AddDays(3),
            "Re-enable restarted the countdown instead of keeping the first switch-on");

        // P3.T11: a favourite series is persisted as blocked/favorite_series, and an unresolvable series blocks.
        states[(firstUser.Id, nativeEpisode.Id)] = State(true, 0, clock.GetUtcNow().UtcDateTime);
        Raise(firstUser.Id, nativeEpisode, UserDataSaveReason.TogglePlayed);
        await WaitForObservationAsync(path, firstUser.Id, nativeEpisode.Id, observation => observation.EvidenceAvailable);
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = true,
            RetentionWatchedUserMode = WatchedUserMode.AnyUser,
            ReclaimAfterDays = 14,
            ExemptFavourites = true
        });
        try
        {
            await WaitForEvaluationAsync(path, episodeRecordId, evaluation => evaluation.State == "blocked" &&
                evaluation.Reason == "series_unavailable", "An episode whose series cannot be resolved did not block");
        }
        catch (InvalidOperationException)
        {
            await using var debug = new ModDbContext(path);
            var actual = await debug.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == episodeRecordId);
            throw new InvalidOperationException($"series_unavailable expected, got {actual.State}/{actual.Reason} policy {actual.PolicyVersion}");
        }
        Guid seriesItemId;
        await using (var bindings = new ModDbContext(path))
            seriesItemId = (await bindings.EpisodeBindings.SingleAsync()).SeriesItemId;
        var nativeSeries = new MediaBrowser.Controller.Entities.TV.Series { Id = seriesItemId, Name = "Series" };
        items[seriesItemId] = nativeSeries;
        states[(firstUser.Id, seriesItemId)] = State(false, 0, null, favorite: true);
        Raise(firstUser.Id, nativeSeries, UserDataSaveReason.UpdateUserRating);
        await WaitForEvaluationAsync(path, episodeRecordId, evaluation => evaluation.State == "blocked" &&
            evaluation.Reason == "favorite_series",
            "A favourite series save did not persist blocked/favorite_series for its episodes");

        // Sustained playback progress after the first resume update writes nothing.
        states[(firstUser.Id, movie.Id)] = State(true, 500, clock.GetUtcNow().UtcDateTime);
        Raise(firstUser.Id, movie, UserDataSaveReason.PlaybackProgress);
        await WaitForObservationAsync(path, firstUser.Id, movie.Id, observation => observation.PlaybackPositionTicks == 500);
        DateTime observedAt;
        DateTime evaluatedAt;
        await using (var before = new ModDbContext(path))
        {
            observedAt = (await before.CompletionObservations.SingleAsync(observation =>
                observation.UserId == firstUser.Id && observation.TargetId == movieEntryId)).ObservedAt;
            evaluatedAt = (await before.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == movieEntryId)).EvaluatedAt;
        }

        clock.Advance(TimeSpan.FromMinutes(5));
        for (var position = 600; position < 900; position += 100)
        {
            states[(firstUser.Id, movie.Id)] = State(true, position, clock.GetUtcNow().UtcDateTime);
            Raise(firstUser.Id, movie, UserDataSaveReason.PlaybackProgress);
        }

        // The listener is first in, first out; a later episode event proves the progress events were handled.
        states[(firstUser.Id, nativeEpisode.Id)] = State(true, 0, clock.GetUtcNow().UtcDateTime);
        Raise(firstUser.Id, nativeEpisode, UserDataSaveReason.TogglePlayed);
        await WaitForObservationAsync(path, firstUser.Id, nativeEpisode.Id,
            observation => observation.ObservedAt == clock.GetUtcNow().UtcDateTime);
        await using (var after = new ModDbContext(path))
            Assert((await after.CompletionObservations.SingleAsync(observation =>
                    observation.UserId == firstUser.Id && observation.TargetId == movieEntryId)) is { PlaybackPositionTicks: 500 } quiet &&
                quiet.ObservedAt == observedAt &&
                (await after.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == movieEntryId)).EvaluatedAt == evaluatedAt,
                "Position-only playback progress leaves observation and evaluation rows unchanged");

        // A user created without any event is noticed by the periodic access fingerprint.
        string accessBefore;
        await using (var before = new ModDbContext(path))
            accessBefore = (await before.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == movieEntryId)).AccessFingerprint;
        var thirdUser = new User("third", "auth", "reset") { Id = Guid.NewGuid() };
        accessibleLibraries[thirdUser.Id] = [movieLibrary, tvLibrary];
        allUsers.Add(thirdUser);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.AccessFingerprint != accessBefore,
            "Creating a user without an event did not re-evaluate affected targets");
        allUsers.Remove(thirdUser);
        await WaitForEvaluationAsync(path, movieEntryId, evaluation => evaluation.AccessFingerprint == accessBefore,
            "Deleting a user without an event did not re-evaluate affected targets");
        await using (var cleanup = new ModDbContext(path))
        {
            cleanup.CompletionObservations.RemoveRange(cleanup.CompletionObservations.Where(observation => observation.UserId == thirdUser.Id));
            await cleanup.SaveChangesAsync();
        }

        // Concurrent first evaluations of one target do not fail on the unique target row.
        var raceMovie = new Movie { Id = Guid.NewGuid(), Name = "Race", Path = "/fixture/race.mkv" };
        items[raceMovie.Id] = raceMovie;
        var raceEntry = new Entry
        {
            MediaType = "movie", TmdbId = 30, Title = "Race", State = FileState.OnDisk, TargetLibraryId = movieLibrary.Id
        };
        await using (var race = new ModDbContext(path))
        {
            race.Entries.Add(raceEntry);
            race.EntryBindings.Add(new EntryBinding
            {
                EntryId = raceEntry.Id, JellyfinItemId = raceMovie.Id, TargetLibraryId = movieLibrary.Id, VersionGroupId = raceMovie.Id
            });
            await race.SaveChangesAsync();
        }

        async Task EvaluateRaceAsync()
        {
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>().EvaluateNativeItemAsync(raceMovie.Id, default);
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => EvaluateRaceAsync()));
        await using (var race = new ModDbContext(path))
        {
            Assert(await race.RetentionEvaluations.CountAsync(evaluation => evaluation.TargetId == raceEntry.Id) == 1,
                "Concurrent evaluations of a new target produce one row and no UNIQUE failure");
            race.CompletionObservations.RemoveRange(race.CompletionObservations.Where(observation => observation.EntryId == raceEntry.Id));
            race.Entries.Remove(await race.Entries.SingleAsync(entry => entry.Id == raceEntry.Id));
            await race.SaveChangesAsync();
        }

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            RetentionEnabled = false,
            RetentionWatchedUserMode = WatchedUserMode.AllUsers,
            ReclaimAfterDays = 14,
            ExemptFavourites = true
        });
        await WaitForAsync(path, async database => await database.RetentionEvaluations.AllAsync(evaluation =>
                evaluation.State == "disabled" && evaluation.Deadline == null),
            "Final disable did not clear re-enabled countdowns");
    }
    finally
    {
        await listener.StopAsync(default);
        // Stopping twice is a no-op, not an ObjectDisposedException from the disposed cancellation source.
        await listener.StopAsync(default);
    }

    await using var restarted = new ModDbContext(path);
    Assert(await restarted.CompletionObservations.CountAsync() == 4 &&
        await restarted.RetentionPolicySnapshots.CountAsync() == 1 &&
        await restarted.RetentionEvaluations.CountAsync() == 2,
        "Per-user evidence, policy and access-aware evaluations survive a real SQLite restart");

    object? AddHandler(object?[]? arguments)
    {
        userDataSaved += (EventHandler<UserDataSaveEventArgs>)arguments![0]!;
        return null;
    }

    object? RemoveHandler(object?[]? arguments)
    {
        userDataSaved -= (EventHandler<UserDataSaveEventArgs>)arguments![0]!;
        return null;
    }

    object? AddUserUpdatedHandler(object?[]? arguments)
    {
        userUpdated += (EventHandler<GenericEventArgs<User>>)arguments![0]!;
        return null;
    }

    object? RemoveUserUpdatedHandler(object?[]? arguments)
    {
        userUpdated -= (EventHandler<GenericEventArgs<User>>)arguments![0]!;
        return null;
    }

    object? SaveConfiguration(object?[]? arguments)
    {
        persistedConfiguration = (PluginConfiguration)arguments![0]!;
        return null;
    }

    void Raise(Guid userId, BaseItem item, UserDataSaveReason reason) => userDataSaved!(null, new UserDataSaveEventArgs
    {
        UserId = userId,
        Item = item,
        UserData = states[(userId, item.Id)],
        SaveReason = reason,
        Keys = []
    });

    void RaiseUserUpdated(User user) => userUpdated!(null, new GenericEventArgs<User>(user));
}

static UserItemData State(bool played, long position, DateTime? lastPlayedAt, bool favorite = false) => new()
{
    Key = Guid.NewGuid().ToString("N"),
    Played = played,
    PlaybackPositionTicks = position,
    LastPlayedDate = lastPlayedAt,
    IsFavorite = favorite
};

static Task WaitForObservationAsync(string path, Guid userId, Guid jellyfinItemId,
    Func<CompletionObservation, bool> condition) => WaitForAsync(path, async database =>
{
    var observation = await database.CompletionObservations.SingleOrDefaultAsync(candidate =>
        candidate.UserId == userId && candidate.JellyfinItemId == jellyfinItemId);
    return observation is not null && condition(observation);
}, "Timed out waiting for completion evidence");

static Task WaitForEvaluationAsync(string path, Guid targetId,
    Func<RetentionEvaluation, bool> condition, string message) => WaitForAsync(path, async database =>
{
    var evaluation = await database.RetentionEvaluations.SingleOrDefaultAsync(candidate => candidate.TargetId == targetId);
    return evaluation is not null && condition(evaluation);
}, message);

static async Task WaitForAsync(string path, Func<ModDbContext, Task<bool>> condition, string message)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!timeout.IsCancellationRequested)
    {
        await using var database = new ModDbContext(path);
        if (await condition(database)) return;
        await Task.Delay(20, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now = Now.Add(duration);
}

sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

sealed class TestRoot(Func<User, IReadOnlyList<BaseItem>> children) : Folder
{
    public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) =>
        children(user);
}

sealed class EmptyLibrary : CollectionFolder
{
    protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
        new() { Items = [], TotalRecordCount = 0 };
}

class Stub<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[]?, object?> callback = null!;

    public static T Create(Func<MethodInfo, object?[]?, object?> callback)
    {
        var instance = Create<T, Stub<T>>();
        ((Stub<T>)(object)instance).callback = callback;
        return instance;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments) => callback(targetMethod!, arguments);
}
