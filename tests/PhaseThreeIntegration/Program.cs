using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    User[] allUsers = [firstUser, secondUser];
    var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Path = "/fixture/movie.mkv" };
    var nativeEpisode = new MediaBrowser.Controller.Entities.TV.Episode
    {
        Id = Guid.NewGuid(), Name = "Episode", Path = "/fixture/episode.mkv", ParentIndexNumber = 1, IndexNumber = 1
    };
    var items = new Dictionary<Guid, BaseItem> { [movie.Id] = movie, [nativeEpisode.Id] = nativeEpisode };
    var states = new Dictionary<(Guid UserId, Guid ItemId), UserItemData>();
    EventHandler<UserDataSaveEventArgs>? userDataSaved = null;
    var userData = Stub<IUserDataManager>.Create((method, arguments) => method.Name switch
    {
        "add_UserDataSaved" => AddHandler(arguments),
        "remove_UserDataSaved" => RemoveHandler(arguments),
        "GetUserData" when arguments is [User user, BaseItem item] => states.GetValueOrDefault((user.Id, item.Id)),
        _ => null
    });
    var users = Stub<IUserManager>.Create((method, arguments) => method.Name switch
    {
        "GetUserById" when arguments?[0] is Guid id => allUsers.SingleOrDefault(user => user.Id == id),
        "GetUsers" => allUsers,
        _ => null
    });
    var library = Stub<ILibraryManager>.Create((method, arguments) => method.Name == "GetItemById" && arguments?[0] is Guid id
        ? items.GetValueOrDefault(id)
        : null);
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
    var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));

    await using (var setup = new ModDbContext(path))
    {
        await setup.Database.MigrateAsync();
        var movieEntry = new Entry { Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 10, Title = "Movie", State = FileState.OnDisk };
        var seriesEntry = new Entry { Id = Guid.NewGuid(), MediaType = "series", TmdbId = 20, Title = "Series", State = FileState.OnDisk };
        var episode = new Episode
        {
            Id = Guid.NewGuid(), EntryId = seriesEntry.Id, TmdbId = 21, SeasonNumber = 1, EpisodeNumber = 1,
            Title = "Episode", State = FileState.OnDisk, JellyfinItemId = nativeEpisode.Id
        };
        setup.Entries.AddRange(movieEntry, seriesEntry);
        setup.Episodes.Add(episode);
        setup.EntryBindings.Add(new EntryBinding
        {
            EntryId = movieEntry.Id, JellyfinItemId = movie.Id, TargetLibraryId = Guid.NewGuid(), VersionGroupId = movie.Id
        });
        setup.EpisodeBindings.Add(new EpisodeBinding
        {
            EpisodeId = episode.Id, JellyfinItemId = nativeEpisode.Id, SeriesItemId = Guid.NewGuid(), TargetLibraryId = Guid.NewGuid()
        });
        await setup.SaveChangesAsync();
    }

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddTransient(_ => new ModDbContext(path));
    services.AddSingleton(users);
    services.AddSingleton(library);
    services.AddSingleton(userData);
    services.AddSingleton<TimeProvider>(clock);
    services.AddTransient<RetentionPolicyService>();
    services.AddTransient<RetentionCompletionService>();
    services.AddSingleton<RetentionEventListener>();
    await using var provider = services.BuildServiceProvider();
    var listener = provider.GetRequiredService<RetentionEventListener>();
    await listener.StartAsync(default);
    try
    {
        await WaitForAsync(path, async database => await database.RetentionPolicySnapshots.CountAsync() == 1,
            "Startup did not persist the disabled default All users policy");
        await using (var initial = new ModDbContext(path))
        {
            var policy = await initial.RetentionPolicySnapshots.SingleAsync();
            Assert(policy.Version == 1 && !policy.Enabled && policy.WatchedUserMode == WatchedUserMode.AllUsers &&
                policy.EnabledAt is null, "Default retention policy is disabled and uses All users");
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
            Assert((await duplicate.CompletionObservations.SingleAsync(observation => observation.UserId == firstUser.Id)).CompletedAt == firstCompletion,
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
        await WaitForAsync(path, async database => await database.CompletionObservations.CountAsync() == 3,
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
            await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                .RefreshAllAsync(new InlineProgress(value => repairProgress = value), default);
        Assert(repairProgress == 100, "Repair re-reads every bound target for every user");
    }
    finally
    {
        await listener.StopAsync(default);
    }

    await using var restarted = new ModDbContext(path);
    Assert(await restarted.CompletionObservations.CountAsync() == 4 &&
        await restarted.RetentionPolicySnapshots.CountAsync() == 1,
        "Per-user evidence and policy revision survive a real SQLite restart");

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
