using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Data;
using JellyfinMod.Services.Trakt;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TvEpisode = MediaBrowser.Controller.Entities.TV.Episode;

// P7.Q16, the Trakt indicator: a real Kestrel plugin host with authentication, authorization, MVC serialization, EF
// migrations and SQLite. The host's user-data manager raises UserDataSaved exactly as Jellyfin does when the stock
// Trakt plugin's SyncFromTraktTask saves with UserDataSaveReason.Import; the host's plugin list says whether that plugin
// is installed. The live proof with the real Trakt plugin is in PHASE7 "Q16 evidence".
var stopwatch = Stopwatch.StartNew();
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-seven-trakt-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var logs = new CapturingLoggerProvider();
try
{
    await RunAsync(folder, logs);
    Console.WriteLine($"PASS: Phase 7 Q16 Trakt observations, migration, endpoint, isolation and degradation ({stopwatch.Elapsed.TotalSeconds:F1}s)");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static async Task RunAsync(string folder, CapturingLoggerProvider logs)
{
    var media = Path.Combine(folder, "media");
    foreach (var directory in new[] { "movies", "movies2", "tv", "far" }) Directory.CreateDirectory(Path.Combine(media, directory));
    var world = new World
    {
        Admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() },
        SecondAdmin = new User("admin2", "auth", "reset") { Id = Guid.NewGuid() },
        RestrictedAdmin = new User("tvadmin", "auth", "reset") { Id = Guid.NewGuid() },
        Ordinary = new User("viewer", "auth", "reset") { Id = Guid.NewGuid() },
        Movies = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies") },
        Movies2 = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "movies2") },
        Tv = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.tvshows, Location = Path.Combine(media, "tv") },
        Far = new TestLibrary { Id = Guid.NewGuid(), CollectionType = Jellyfin.Data.Enums.CollectionType.movies, Location = Path.Combine(media, "far") },
        Folder = folder
    };

    // ---- Migration: the released 0.1.0.0 schema (last migration PhaseSevenProwlarr) upgrades and keeps its rows.
    var dbPath = Path.Combine(folder, "jellyfinmod.db");
    await using (var database = new ModDbContext(dbPath))
    {
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260924050103_PhaseSevenProwlarr");
        // A row written in the released schema's own shape; later migrations (retention's, then Q16's) must keep it.
        var entryId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId)
            VALUES ({entryId}, 'movie', 1, 'Existing', 4, 1, '2026-09-01T00:00:00', {world.Movies.Id})
            """);
        Assert(!(await TableNamesAsync(database)).Contains("TraktObservations"), "The 0.1.0.0 schema has no Trakt table");
        await database.Database.MigrateAsync();
        // Later phases add migrations after Q16's, so it is found by name, not as the last one applied.
        var applied = (await database.Database.GetAppliedMigrationsAsync()).ToList();
        var trakt = applied.FindIndex(name => name.EndsWith("_PhaseSevenTraktObservations", StringComparison.Ordinal));
        Assert((await TableNamesAsync(database)).Contains("TraktObservations") &&
            trakt > applied.IndexOf("20260924050103_PhaseSevenProwlarr"),
            "The Q16 migration applies on top of the released schema");
        Assert(await database.Entries.AnyAsync(entry => entry.Id == entryId), "Existing rows survive the Q16 migration");
        var indexes = await database.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_index_list('TraktObservations') WHERE \"unique\" = 1").ToListAsync();
        Assert(indexes.Contains("IX_TraktObservations_UserId_JellyfinItemId"), "One observation per user and item is enforced by a unique index");
        var integrity = await database.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").ToListAsync();
        Assert(integrity.SequenceEqual(["ok"]), "The migrated database passes its integrity check");
    }

    // ---- The host's items, their visibility per user, its user-data events and its plugin list.
    var movie = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Movie", Path = Path.Combine(media, "movies", "m.mkv") };
    var otherMovie = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Other", Path = Path.Combine(media, "movies", "o.mkv") };
    var series = new Series { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Series" };
    var season1 = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1, SeriesId = series.Id };
    var season2 = new Season { Id = Guid.NewGuid(), Name = "Season 2", IndexNumber = 2, SeriesId = series.Id };
    TvEpisode MakeEpisode(Season season, int number) => new()
    {
        Id = Guid.NewGuid(), Name = $"Episode {number}", Path = Path.Combine(media, "tv", $"e{season.IndexNumber}{number}.mkv"),
        IndexNumber = number, ParentIndexNumber = season.IndexNumber, SeriesId = series.Id, SeasonId = season.Id
    };
    var episode1 = MakeEpisode(season1, 1);
    var episode2 = MakeEpisode(season1, 2);
    var episode3 = MakeEpisode(season2, 1);
    var burst = Enumerable.Range(0, 300).Select(n => new Movie { Id = Guid.NewGuid(), Name = $"JellyfinMod Burst {n}" }).ToArray();
    // The gate: an item whose lookup by the listener can be held, so that what is raised meanwhile is read as one batch.
    var gate = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Gate" };
    var gone = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Gone" };
    var marker = new Movie { Id = Guid.NewGuid(), Name = "JellyfinMod Trakt Marker" };
    // Read by the listener's own thread while the test adds and removes items.
    var items = new ConcurrentDictionary<Guid, BaseItem>(new BaseItem[] { movie, otherMovie, series, season1, season2, episode1, episode2, episode3, gate, gone, marker }
        .Concat(burst).Select(item => KeyValuePair.Create(item.Id, item)));
    SemaphoreSlim? gateHold = null;
    var gateEntered = new SemaphoreSlim(0);
    BaseItem? Lookup(Guid id)
    {
        if (id == gate.Id && gateHold is { } hold)
        {
            gateEntered.Release();
            if (!hold.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("the gate was never opened");
        }

        return items.GetValueOrDefault(id);
    }
    // Every user sees every item, except the TV-only administrator who sees no movie; a per-test set hides more.
    var hidden = new HashSet<(Guid UserId, Guid ItemId)>();
    bool Visible(User user, BaseItem item) => !hidden.Contains((user.Id, item.Id)) &&
        !(user.Id == world.RestrictedAdmin.Id && item is Movie);
    EventHandler<ItemChangeEventArgs>? itemRemoved = null;
    var library = Stub<ILibraryManager>.Create((method, arguments) =>
    {
        if (method.Name == "add_ItemRemoved") itemRemoved += (EventHandler<ItemChangeEventArgs>)arguments![0]!;
        if (method.Name == "remove_ItemRemoved") itemRemoved -= (EventHandler<ItemChangeEventArgs>)arguments![0]!;
        return method.Name switch
        {
            "GetItemById" when arguments is [Guid id, User user] => items.GetValueOrDefault(id) is { } item && Visible(user, item) ? item : null,
            "GetItemById" when arguments is [Guid id] => Lookup(id),
            _ => null
        };
    });
    // The host's users, for the listener's pruning of deleted users; a test removes one.
    var existingUsers = new List<User> { world.Admin, world.SecondAdmin, world.RestrictedAdmin, world.Ordinary };
    var userManager = Stub<IUserManager>.Create((method, arguments) => method.Name switch
    {
        "GetUsers" => existingUsers.ToArray(),
        "GetUserById" when arguments?[0] is Guid id => existingUsers.SingleOrDefault(user => user.Id == id),
        _ => null
    });
    // The host's scheduled tasks: the Trakt plugin's import task, running or idle, beside another task.
    var traktTaskState = TaskState.Running;
    IScheduledTaskWorker Worker(string key, Func<TaskState> state) => Stub<IScheduledTaskWorker>.Create((method, _) => method.Name switch
    {
        "get_ScheduledTask" => Stub<IScheduledTask>.Create((inner, _) => inner.Name == "get_Key" ? key : null),
        "get_State" => state(),
        _ => null
    });
    // First, a task whose State getter throws, as Jellyfin's can when that task finishes between its two reads of its
    // cancellation source: only the Trakt task's own State may be read.
    var workers = new[]
    {
        Worker("Finishing", () => throw new NullReferenceException("a finishing task's cancellation source")),
        Worker("RefreshLibrary", () => TaskState.Running), Worker(TraktPluginState.SyncTaskKey, () => traktTaskState)
    };
    var taskManager = Stub<ITaskManager>.Create((method, _) => method.Name == "get_ScheduledTasks" ? workers : null);
    EventHandler<UserDataSaveEventArgs>? userDataSaved = null;
    var userData = Stub<IUserDataManager>.Create((method, arguments) =>
    {
        if (method.Name == "add_UserDataSaved") userDataSaved += (EventHandler<UserDataSaveEventArgs>)arguments![0]!;
        if (method.Name == "remove_UserDataSaved") userDataSaved -= (EventHandler<UserDataSaveEventArgs>)arguments![0]!;
        return null;
    });
    var installed = new List<LocalPlugin>();
    var pluginManager = Stub<IPluginManager>.Create((method, _) => method.Name == "get_Plugins" ? installed.ToArray() : null);
    LocalPlugin TraktPlugin(PluginStatus status, string version = "31.0.0.0") => new(Path.Combine(folder, "plugins", "Trakt_" + version), true,
        new PluginManifest { Id = TraktPluginState.PluginId, Name = "Trakt", Version = version, Status = status, TargetAbi = "12.0.0.0" });

    void Raise(User user, BaseItem item, UserDataSaveReason reason, bool played, long position = 0, int playCount = 0) =>
        userDataSaved!(null, new UserDataSaveEventArgs
        {
            UserId = user.Id, Item = item, SaveReason = reason, Keys = [],
            UserData = new UserItemData { Key = item.Id.ToString("N"), Played = played, PlaybackPositionTicks = position, PlayCount = playCount }
        });

    void Configure(IServiceCollection services)
    {
        services.AddSingleton(library);
        services.AddSingleton(userData);
        services.AddSingleton(pluginManager);
        services.AddSingleton(taskManager);
        services.AddSingleton(userManager);
        services.AddSingleton<TraktPluginState>();
        // Deleted users are looked for every 300 ms here instead of every five minutes.
        services.AddSingleton<IHostedService>(provider =>
            ActivatorUtilities.CreateInstance<TraktObservationListener>(provider, (TimeSpan?)TimeSpan.FromMilliseconds(300)));
    }

    var time = new ShiftedTimeProvider();
    var host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), new PluginConfiguration(), Configure);
    var anonymous = host.Client(null, false);
    var admin = host.Client(world.Admin, true);
    var viewer = host.Client(world.Ordinary, false);
    var tvAdmin = host.Client(world.RestrictedAdmin, true);
    async Task<(HttpStatusCode Status, JsonElement Body)> Get(HttpClient client, Guid itemId)
    {
        using var response = await client.GetAsync($"JellyfinMod/Trakt/Items/{itemId}");
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    async Task<int> CountAsync(Func<IQueryable<TraktObservation>, IQueryable<TraktObservation>>? filter = null)
    {
        await using var database = new ModDbContext(dbPath);
        return await (filter ?? (rows => rows))(database.TraktObservations.AsNoTracking()).CountAsync();
    }

    // The listener writes on its own reader; give it a bounded time, and prove a negative only after a later positive.
    async Task WaitFor(Func<Task<bool>> condition, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new Exception("FAIL (timeout): " + what);
    }

    // A null may be written or omitted, depending on the host serializer's ignore condition; both mean "none".
    static bool Status(JsonElement body, bool installedValue, bool history) =>
        body.GetProperty("installed").GetBoolean() == installedValue && body.GetProperty("hasHistory").GetBoolean() == history &&
        (history ? body.GetProperty("lastSyncedAt").ValueKind == JsonValueKind.String : IsNull(body, "lastSyncedAt"));
    static bool IsNull(JsonElement body, string name) => !body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    // ---- Authentication and visibility come first, whatever is installed.
    Assert((await Get(anonymous, movie.Id)).Status == HttpStatusCode.Unauthorized, "Anonymous callers get 401");
    Assert((await Get(tvAdmin, movie.Id)).Status == HttpStatusCode.NotFound, "An item the user cannot see is 404");
    Assert((await Get(viewer, Guid.NewGuid())).Status == HttpStatusCode.NotFound, "An unknown item is 404");

    // ---- Without the Trakt plugin: nothing is recorded and every visible item says so.
    var health = JsonDocument.Parse(await admin.GetStringAsync("JellyfinMod/Health")).RootElement;
    Assert(health.GetProperty("Capabilities").EnumerateArray().Any(value => value.GetString() == "trakt.history") &&
        !health.GetProperty("Trakt").GetProperty("Installed").GetBoolean() && IsNull(health.GetProperty("Trakt"), "Version"),
        "Health lists trakt.history and reports the Trakt plugin absent");
    Raise(world.Admin, movie, UserDataSaveReason.Import, played: true, playCount: 1);
    var absent = await Get(viewer, movie.Id);
    Assert(absent.Status == HttpStatusCode.OK && Status(absent.Body, false, false), "Without the plugin the item answers installed:false, hasHistory:false");

    // ---- Trakt waiting for a restart or disabled is not installed either.
    installed.Add(TraktPlugin(PluginStatus.Restart));
    Raise(world.Admin, movie, UserDataSaveReason.Import, played: true, playCount: 1);
    Assert(Status((await Get(admin, movie.Id)).Body, false, false), "A Trakt copy waiting for a restart does not count as installed");

    // ---- Installed and active: an Import that carries history is recorded, for that user only.
    installed.Clear();
    installed.Add(TraktPlugin(PluginStatus.Active));
    health = JsonDocument.Parse(await admin.GetStringAsync("JellyfinMod/Health")).RootElement;
    Assert(health.GetProperty("Trakt").GetProperty("Installed").GetBoolean() &&
        health.GetProperty("Trakt").GetProperty("Version").GetString() == "31.0.0.0", "Health reports the installed Trakt plugin and its version to an administrator");
    health = JsonDocument.Parse(await viewer.GetStringAsync("JellyfinMod/Health")).RootElement;
    Assert(health.GetProperty("Trakt").GetProperty("Installed").GetBoolean() && !health.GetProperty("Trakt").TryGetProperty("Version", out _),
        "An ordinary user's Health says Trakt is installed and never which version");
    var before = time.GetUtcNow().UtcDateTime;
    Raise(world.Admin, movie, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync() == 1, "the movie import is recorded");
    Assert(await CountAsync() == 1, "Imports raised while the plugin was absent or pending were never recorded");
    Assert(true, "Another task whose State throws does not stop the Trakt import being recorded");
    var adminMovie = await Get(admin, movie.Id);
    Assert(adminMovie.Status == HttpStatusCode.OK && Status(adminMovie.Body, true, true) &&
        adminMovie.Body.GetProperty("lastSyncedAt").GetDateTime().ToUniversalTime() >= before.AddSeconds(-1),
        "The importing user sees hasHistory:true with the time the history arrived");
    Assert(Status((await Get(viewer, movie.Id)).Body, true, false), "Another user never sees the first user's Trakt history");
    Assert(Status((await Get(admin, otherMovie.Id)).Body, true, false), "A title without an import has no Trakt history");

    // ---- An Import while the Trakt task is not running is someone else's (Jellyfin's NFO parser saves with Import too):
    // it neither records a title nor removes one.
    traktTaskState = TaskState.Idle;
    Raise(world.Admin, otherMovie, UserDataSaveReason.Import, played: true, playCount: 1);
    Raise(world.Admin, movie, UserDataSaveReason.Import, played: false, position: 0, playCount: 1);
    traktTaskState = TaskState.Cancelling;
    Raise(world.Admin, otherMovie, UserDataSaveReason.Import, played: true, playCount: 1);
    traktTaskState = TaskState.Running;
    // A marker import during the task proves the queue has been read past the idle ones before the negative is checked.
    Raise(world.SecondAdmin, burst[^1], UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.SecondAdmin.Id)) == 1, "the marker import is recorded");
    Assert(Status((await Get(admin, otherMovie.Id)).Body, true, false) && Status((await Get(admin, movie.Id)).Body, true, true),
        "An Import with the Trakt task idle or cancelling records nothing and removes nothing");

    // ---- Other save reasons are local activity, not Trakt history.
    foreach (var reason in new[] { UserDataSaveReason.TogglePlayed, UserDataSaveReason.PlaybackFinished, UserDataSaveReason.PlaybackProgress,
                 UserDataSaveReason.UpdateUserData, UserDataSaveReason.UpdateUserRating, UserDataSaveReason.PlaybackStart })
        Raise(world.Ordinary, movie, reason, played: true, position: 10_000_000, playCount: 2);
    // An import that carries no history (Trakt's "unwatched" import, which leaves a local play count alone) is not history.
    Raise(world.Ordinary, otherMovie, UserDataSaveReason.Import, played: false, position: 0, playCount: 3);
    // A resume point imported from Trakt's playback progress is history.
    Raise(world.Ordinary, movie, UserDataSaveReason.Import, played: false, position: 42_000_000);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.Ordinary.Id)) == 1, "the resume-point import is recorded");
    Assert(await CountAsync(rows => rows.Where(row => row.UserId == world.Ordinary.Id && row.JellyfinItemId == movie.Id)) == 1,
        "Only the Import with a resume point was recorded: no other save reason and no history-less import");
    Assert(Status((await Get(viewer, movie.Id)).Body, true, true) && Status((await Get(viewer, otherMovie.Id)).Body, true, false),
        "The second user now sees their own imported resume point, and nothing for the history-less import");

    // ---- Episodes: the episode, its season and its series answer; the other season does not.
    Raise(world.Admin, episode1, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.JellyfinItemId == episode1.Id)) == 1, "the episode import is recorded");
    await using (var database = new ModDbContext(dbPath))
    {
        var row = await database.TraktObservations.AsNoTracking().SingleAsync(candidate => candidate.JellyfinItemId == episode1.Id);
        Assert(row.SeriesId == series.Id && row.SeasonId == season1.Id && row.UserId == world.Admin.Id && row.FirstSyncedAt == row.LastSyncedAt,
            "The episode's observation carries its series and season, read off the episode");
    }

    Assert(Status((await Get(admin, episode1.Id)).Body, true, true), "The imported episode has Trakt history");
    Assert(Status((await Get(admin, season1.Id)).Body, true, true), "Its season aggregates it");
    Assert(Status((await Get(admin, series.Id)).Body, true, true), "Its series aggregates it");
    Assert(Status((await Get(admin, season2.Id)).Body, true, false) && Status((await Get(admin, episode2.Id)).Body, true, false),
        "The other season and an episode without an import have none");
    Assert(Status((await Get(viewer, series.Id)).Body, true, false) && Status((await Get(viewer, season1.Id)).Body, true, false),
        "Another user's series and season pages show nothing of the first user's import");
    Assert(Status((await Get(tvAdmin, series.Id)).Body, true, false), "A user with TV access and no import sees no history on the series");

    // An episode hidden from the user since its import no longer counts for its season or series, and is itself 404.
    hidden.Add((world.Admin.Id, episode1.Id));
    Assert((await Get(admin, episode1.Id)).Status == HttpStatusCode.NotFound &&
        Status((await Get(admin, season1.Id)).Body, true, false) && Status((await Get(admin, series.Id)).Body, true, false),
        "A hidden episode is 404 and stops counting for its season and series");
    hidden.Clear();

    // A later sync updates the time, never adds a second row.
    var first = (await Get(admin, episode1.Id)).Body.GetProperty("lastSyncedAt").GetDateTime();
    time.Offset = TimeSpan.FromHours(3);
    Raise(world.Admin, episode1, UserDataSaveReason.Import, played: true, playCount: 2);
    await WaitFor(async () => (await Get(admin, episode1.Id)).Body.GetProperty("lastSyncedAt").GetDateTime() > first.AddHours(2),
        "a later import moves lastSyncedAt");
    Assert(await CountAsync(rows => rows.Where(row => row.JellyfinItemId == episode1.Id)) == 1 &&
        (await Get(admin, series.Id)).Body.GetProperty("lastSyncedAt").GetDateTime() > first.AddHours(2),
        "A repeated import keeps one row per user and item and the series reports the newest time");

    // ---- Changes read in one batch apply in their order. The gate holds the reader on its lookup of the gate item
    // while the changes are raised, so they are all read together afterwards.
    async Task InOneBatch(Action raise)
    {
        var hold = new SemaphoreSlim(0);
        gateHold = hold;
        Raise(world.Ordinary, gate, UserDataSaveReason.Import, played: true, playCount: 1);
        if (!await gateEntered.WaitAsync(TimeSpan.FromSeconds(10))) throw new Exception("FAIL (timeout): the reader reaches the gate");
        gateHold = null;
        raise();
        hold.Release();
    }

    DateTime Utc(JsonElement body) => body.GetProperty("lastSyncedAt").GetDateTime().ToUniversalTime();
    async Task<TraktObservation?> RowAsync(Guid userId, Guid itemId)
    {
        await using var database = new ModDbContext(dbPath);
        return await database.TraktObservations.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.JellyfinItemId == itemId);
    }

    time.Offset = TimeSpan.FromHours(4);
    var fourHours = time.GetUtcNow().UtcDateTime;
    await InOneBatch(() =>
    {
        Raise(world.Admin, movie, UserDataSaveReason.Import, played: false, position: 0, playCount: 1);
        Raise(world.Admin, movie, UserDataSaveReason.Import, played: true, playCount: 1);
    });
    await WaitFor(async () => (await RowAsync(world.Admin.Id, movie.Id))?.LastSyncedAt >= fourHours.AddSeconds(-1),
        "an unwatched then watched import in one batch leaves the row with the newer time");
    var restored = await RowAsync(world.Admin.Id, movie.Id);
    Assert(await CountAsync(rows => rows.Where(row => row.UserId == world.Admin.Id && row.JellyfinItemId == movie.Id)) == 1 &&
        restored!.FirstSyncedAt == restored.LastSyncedAt && Status((await Get(admin, movie.Id)).Body, true, true) &&
        Utc((await Get(admin, movie.Id)).Body) >= fourHours.AddSeconds(-1),
        "Unwatched then watched in one batch: the row stays, as a new observation from the later import");

    time.Offset = TimeSpan.FromHours(5);
    var fiveHours = time.GetUtcNow().UtcDateTime;
    await InOneBatch(() =>
    {
        itemRemoved!(null, new ItemChangeEventArgs { Item = movie });
        Raise(world.Admin, movie, UserDataSaveReason.Import, played: true, playCount: 1);
        Raise(world.Ordinary, movie, UserDataSaveReason.Import, played: false, position: 42_000_000);
    });
    await WaitFor(async () => (await RowAsync(world.Admin.Id, movie.Id))?.LastSyncedAt >= fiveHours.AddSeconds(-1),
        "a removal then a re-import in one batch leaves the row with the newer time");
    Assert(await CountAsync(rows => rows.Where(row => row.JellyfinItemId == movie.Id)) == 2 &&
        (await RowAsync(world.Ordinary.Id, movie.Id))?.LastSyncedAt >= fiveHours.AddSeconds(-1) &&
        Status((await Get(admin, movie.Id)).Body, true, true) && Status((await Get(viewer, movie.Id)).Body, true, true),
        "Removed then re-imported in one batch (the item is back in the library): both users' rows stay, one each");

    // ---- An import handled only after its item left the library (its handler paused before queueing, the removal
    // written first) does not bring the row back.
    Raise(world.Ordinary, gone, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await RowAsync(world.Ordinary.Id, gone.Id) is not null, "the soon-gone movie's import is recorded");
    items.TryRemove(gone.Id, out _);
    itemRemoved!(null, new ItemChangeEventArgs { Item = gone });
    await WaitFor(async () => await RowAsync(world.Ordinary.Id, gone.Id) is null, "the removed movie's row goes");
    Raise(world.Ordinary, gone, UserDataSaveReason.Import, played: true, playCount: 1);
    Raise(world.Ordinary, marker, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await RowAsync(world.Ordinary.Id, marker.Id) is not null, "the marker import after the late one is recorded");
    Assert(await RowAsync(world.Ordinary.Id, gone.Id) is null, "A late import of an item that has left the library records nothing");

    // ---- A row whose item has gone without its removal being seen is found by the periodic check (every twelfth
    // user check: about 3.6 s here).
    var stray = Guid.NewGuid();
    await using (var database = new ModDbContext(dbPath))
    {
        database.TraktObservations.Add(new TraktObservation
        {
            UserId = world.Ordinary.Id, JellyfinItemId = stray, FirstSyncedAt = fiveHours, LastSyncedAt = fiveHours
        });
        await database.SaveChangesAsync();
    }

    await WaitFor(async () => await RowAsync(world.Ordinary.Id, stray) is null, "the stray row of a missing item goes");
    Assert(await RowAsync(world.Ordinary.Id, marker.Id) is not null && await RowAsync(world.Admin.Id, movie.Id) is not null,
        "The periodic check removes the rows of items that no longer exist and keeps the rest");

    // ---- Trakt reporting a title unwatched removes it; a burst of imports is recorded in full.
    Raise(world.Admin, movie, UserDataSaveReason.Import, played: false, position: 0, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.Admin.Id && row.JellyfinItemId == movie.Id)) == 0,
        "the unwatched import removes the movie");
    Assert(Status((await Get(admin, movie.Id)).Body, true, false), "After Trakt reports the movie unwatched it has no Trakt history");
    foreach (var item in burst) Raise(world.SecondAdmin, item, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.SecondAdmin.Id)) == burst.Length,
        "a 300-item sync is recorded in full");
    Assert(true, $"A {burst.Length}-item import burst is recorded in full, one row each");

    // ---- An item leaving the library takes its rows: a movie its own, a season and a series those of their episodes.
    Raise(world.Ordinary, episode2, UserDataSaveReason.Import, played: true, playCount: 1);
    Raise(world.Ordinary, episode3, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.Ordinary.Id && row.SeriesId == series.Id)) == 2,
        "the second user's episode imports are recorded");
    itemRemoved!(null, new ItemChangeEventArgs { Item = burst[0] });
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.JellyfinItemId == burst[0].Id)) == 0, "a removed movie's row goes");
    Assert(await CountAsync(rows => rows.Where(row => row.UserId == world.SecondAdmin.Id)) == burst.Length - 1,
        "A removed movie takes its own row and no other");
    itemRemoved!(null, new ItemChangeEventArgs { Item = season2 });
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.SeasonId == season2.Id)) == 0, "a removed season's rows go");
    Assert(await CountAsync(rows => rows.Where(row => row.SeasonId == season1.Id)) >= 2,
        "A removed season takes its episodes' rows, for every user, and leaves the other season's");
    itemRemoved!(null, new ItemChangeEventArgs { Item = series });
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.SeriesId == series.Id)) == 0, "a removed series' rows go");
    Assert(await CountAsync(rows => rows.Where(row => row.JellyfinItemId == movie.Id)) == 1,
        "A removed series takes every row of its episodes and leaves the movies'");

    // ---- A user who no longer exists loses their rows at the next check.
    existingUsers.Remove(world.SecondAdmin);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.SecondAdmin.Id)) == 0, "a deleted user's rows go");
    Assert(await CountAsync(rows => rows.Where(row => row.UserId == world.Ordinary.Id && row.JellyfinItemId == movie.Id)) == 1,
        "A deleted user's rows are pruned and every other user's stay");
    existingUsers.Add(world.SecondAdmin);

    // Put back what the series removal took, for the checks below.
    Raise(world.Admin, episode1, UserDataSaveReason.Import, played: true, playCount: 2);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.UserId == world.Admin.Id && row.JellyfinItemId == episode1.Id)) == 1,
        "the episode import is recorded again");

    // ---- Disabled from the Dashboard: installed:false and nothing new is recorded; enabled again: history is back.
    installed[0].Manifest.Status = PluginStatus.Disabled;
    Raise(world.Admin, otherMovie, UserDataSaveReason.Import, played: true, playCount: 1);
    Assert(Status((await Get(admin, episode1.Id)).Body, false, false) && Status((await Get(admin, series.Id)).Body, false, false),
        "With the plugin disabled every item answers installed:false and hasHistory:false");
    health = JsonDocument.Parse(await admin.GetStringAsync("JellyfinMod/Health")).RootElement;
    Assert(!health.GetProperty("Trakt").GetProperty("Installed").GetBoolean(), "Health reports a disabled Trakt plugin as not installed");
    installed[0].Manifest.Status = PluginStatus.Active;
    Raise(world.Admin, episode2, UserDataSaveReason.Import, played: true, playCount: 1);
    await WaitFor(async () => await CountAsync(rows => rows.Where(row => row.JellyfinItemId == episode2.Id)) == 1, "the post-enable import is recorded");
    Assert(await CountAsync(rows => rows.Where(row => row.UserId == world.Admin.Id && row.JellyfinItemId == otherMovie.Id)) == 0 &&
        Status((await Get(admin, episode1.Id)).Body, true, true), "An import while disabled was not recorded; earlier history returns when re-enabled");

    // ---- Uninstalled: the same as absent.
    installed.Clear();
    Assert(Status((await Get(admin, series.Id)).Body, false, false), "With the plugin uninstalled the series answers installed:false");

    // ---- A stop writes what is already queued: a burst raised right before the host stops is all there after it.
    installed.Add(TraktPlugin(PluginStatus.Active));
    var late = Enumerable.Range(0, 200).Select(n => new Movie { Id = Guid.NewGuid(), Name = $"JellyfinMod Late {n}" }).ToArray();
    foreach (var item in late) items[item.Id] = item;
    foreach (var item in late) Raise(world.Ordinary, item, UserDataSaveReason.Import, played: true, playCount: 1);
    await host.DisposeAsync();
    Assert(await CountAsync(rows => rows.Where(row => late.Select(item => item.Id).Contains(row.JellyfinItemId))) == late.Length,
        $"The {late.Length} imports queued when the host stopped were all written before it finished stopping");

    // ---- Observations persist across a restart of the host (SQLite, not memory).
    host = await PluginHost.StartAsync(world, dbPath, time, logs, TimeSpan.FromSeconds(5), new PluginConfiguration(), Configure);
    admin = host.Client(world.Admin, true);
    Assert(Status((await Get(admin, season1.Id)).Body, true, true), "Observations survive a restart");
    await host.DisposeAsync();

    Assert(!logs.Lines.Any(line => line.Contains("TraktObservationListener", StringComparison.Ordinal) &&
        (line.StartsWith("Warning", StringComparison.Ordinal) || line.StartsWith("Error", StringComparison.Ordinal))),
        "The listener logged no warning or error");
}

static async Task<List<string>> TableNamesAsync(ModDbContext database) =>
    await database.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("ok - " + message);
}
