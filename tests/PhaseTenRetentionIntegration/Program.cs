// Phase 10 retention, third review (RET3) and decision 12, through a real host and real SQLite.
//
// 1. Decision 12 migration: a movie scheduled by the Phase 3 rule from a watch before its floor stops being due when the
//    database is migrated, with History saying why; a movie scheduled from a later watch and every episode are untouched.
// 2. Switching retention on while an administrator previews (the `database is locked` 500 seen on 18096, 2026-09-24).
//
// A real Kestrel host with the plugin's controllers, authorization, serializer and a SQLite database built by the full
// migration chain. Several hundred tracked episodes and a few users, as on the isolated instance. Retention is switched
// on and the listener's work for that change (policy sync, then re-evaluating every target) runs exactly as
// RetentionEventListener runs it, while the administrator previews twice, keeps and un-keeps an episode, and a second
// switch-on arrives. Every request must answer 2xx, the Keep must win, and SqliteWriteDiagnostics must report who held
// the write lock for how long.
//
//   JFMOD_TARGETS  number of episodes (default 600)
//   JFMOD_ROUNDS   switch-on rounds (default 3)
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var targetCount = int.TryParse(Environment.GetEnvironmentVariable("JFMOD_TARGETS"), out var parsedTargets) ? parsedTargets : 600;
var rounds = int.TryParse(Environment.GetEnvironmentVariable("JFMOD_ROUNDS"), out var parsedRounds) ? parsedRounds : 3;
// A library scan writing Jellyfin's own database on the same disk, as on 18096 when the 500 was seen: optional.
var ioLoad = Environment.GetEnvironmentVariable("JFMOD_IO_LOAD") == "1";
var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-ten-concurrency-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
}).SetMinimumLevel(LogLevel.Warning));
SqliteWriteDiagnostics.Logger = loggerFactory.CreateLogger("JellyfinMod.Database");
try
{
    using var stopLoad = new CancellationTokenSource();
    var load = ioLoad ? Task.Run(() => DiskLoad(Path.Combine(folder, "load.bin"), stopLoad.Token)) : Task.CompletedTask;
    try
    {
        await VerifyDecisionTwelveMigrationAsync(folder);
        await RunAsync(folder, targetCount, rounds);
    }
    finally
    {
        stopLoad.Cancel();
        await load;
    }
    Console.WriteLine($"PASS: switching retention on while previewing, keeping and switching on again ({targetCount} targets, " +
        $"{rounds} rounds): every request answered, the Keep won, slow write-lock holds {SqliteWriteDiagnostics.SlowHolds}");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

static async Task RunAsync(string folder, int targetCount, int rounds)
{
    var databasePath = Path.Combine(folder, "concurrency.db");
    var now = DateTime.UtcNow;
    // The plugin's clock; decision 13's check moves it past a window that ran out while retention was off.
    var clock = new ShiftedClock();
    var admin = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
    List<User> allUsers = [admin, new User("second", "auth", "reset") { Id = Guid.NewGuid() },
        new User("third", "auth", "reset") { Id = Guid.NewGuid() }];
    var tvLibrary = new FixtureLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.tvshows };
    var movieLibrary = new FixtureLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.movies };
    var withAccess = allUsers.Select(user => user.Id).ToHashSet();
    var root = new FixtureRoot(withAccess, [tvLibrary, movieLibrary]);
    var series = new FixtureSeries { Id = Guid.NewGuid(), Name = "Concurrency series" };
    tvLibrary.Items.Add(series);
    var mediaRoot = Path.Combine(folder, "media");
    Directory.CreateDirectory(mediaRoot);
    var items = new Dictionary<Guid, BaseItem> { [series.Id] = series, [tvLibrary.Id] = tvLibrary };
    var natives = new List<MediaBrowser.Controller.Entities.TV.Episode>();
    for (var index = 0; index < targetCount; index++)
    {
        var path = Path.Combine(mediaRoot, $"Concurrency S01E{index + 1:000}.mkv");
        await File.WriteAllBytesAsync(path, new byte[64]);
        var native = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Name = $"Episode {index + 1}", Path = path, SeriesId = series.Id,
            ParentIndexNumber = 1, IndexNumber = index + 1
        };
        natives.Add(native);
        series.Items.Add(native);
        items[native.Id] = native;
    }

    // Decision 12: movies every user finished a week ago (the backlog), and one that will be watched after the switch-on.
    var movies = new List<MediaBrowser.Controller.Entities.Movies.Movie>();
    var movieRoot = Path.Combine(folder, "movies");
    Directory.CreateDirectory(movieRoot);
    for (var index = 0; index < 20; index++)
    {
        var path = Path.Combine(movieRoot, $"Backlog movie {index + 1}.mkv");
        await File.WriteAllBytesAsync(path, new byte[64]);
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = $"Backlog movie {index + 1}", Path = path };
        movies.Add(movie);
        movieLibrary.Items.Add(movie);
        items[movie.Id] = movie;
    }

    items[movieLibrary.Id] = movieLibrary;
    var freshMovie = movies[^1];
    DateTime? freshWatch = null;
    var watchedWhileOff = new Dictionary<Guid, DateTime>();
    var virtualFolders = new List<VirtualFolderInfo>
    {
        new() { Name = "Concurrency movies", ItemId = movieLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.movies, Locations = [movieRoot] },
        new() { Name = "Concurrency", ItemId = tvLibrary.Id.ToString(), CollectionType = CollectionTypeOptions.tvshows, Locations = [mediaRoot] }
    };
    var library = Stub<ILibraryManager>.Create((method, arguments) => method.Name switch
    {
        "GetUserRootFolder" => root,
        "GetVirtualFolders" => virtualFolders,
        "GetItemById" or "RetrieveItem" when arguments?[0] is Guid id => items.GetValueOrDefault(id),
        "GetLocalAlternateVersionIds" => Array.Empty<Guid>(),
        "GetLinkedAlternateVersions" => Array.Empty<MediaBrowser.Controller.Entities.Video>(),
        _ => null
    });
    var users = Stub<IUserManager>.Create((method, arguments) => method.Name switch
    {
        "GetUsers" => allUsers.ToArray(),
        "GetUserById" when arguments?[0] is Guid id => allUsers.SingleOrDefault(user => user.Id == id),
        _ => null
    });
    // Every user finished every episode a week ago: an old watch, which no rule lets count (the backlog).
    var userData = Stub<IUserDataManager>.Create((method, arguments) =>
        method.Name != "GetUserData" || arguments?[1] is not BaseItem item ? null
        : new UserItemData { Key = item.Id.ToString("N"), Played = true,
            LastPlayedDate = item.Id == freshMovie.Id && freshWatch is { } watched ? watched
                : watchedWhileOff.TryGetValue(item.Id, out var offWatch) ? offWatch : now.AddDays(-7) });
    var sessionManager = Stub<ISessionManager>.Create((method, _) => method.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : null);
    var taskManager = Stub<ITaskManager>.Create((_, _) => null);
    var localization = Stub<ILocalizationManager>.Create((_, _) => null);
    var settings = new PluginConfiguration
    {
        RetentionEnabled = false, ReclaimAfterDays = 14, RetentionWatchedUserMode = WatchedUserMode.AllUsers, ExemptFavourites = true
    };

    var apiBuilder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
    {
        ContentRootPath = folder,
        ApplicationName = typeof(RetentionController).Assembly.FullName
    });
    apiBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
    apiBuilder.Logging.ClearProviders();
    apiBuilder.Services.AddControllers().AddApplicationPart(typeof(RetentionController).Assembly);
    apiBuilder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", null);
    apiBuilder.Services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation, policy => policy.RequireRole("admin")));
    apiBuilder.Services.AddTransient(_ => new ModDbContext(databasePath));
    apiBuilder.Services.AddSingleton<DatabaseInitializer>();
    apiBuilder.Services.AddSingleton(users);
    apiBuilder.Services.AddSingleton(library);
    apiBuilder.Services.AddSingleton(userData);
    apiBuilder.Services.AddSingleton(sessionManager);
    apiBuilder.Services.AddSingleton(taskManager);
    apiBuilder.Services.AddSingleton(localization);
    var serverConfiguration = Stub<MediaBrowser.Controller.Configuration.IServerConfigurationManager>.Create((method, _) =>
        method.Name == "get_Configuration"
            ? new MediaBrowser.Model.Configuration.ServerConfiguration { SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = [] }
            : null);
    apiBuilder.Services.AddTransient(_ => new CatalogSortName(serverConfiguration));
    apiBuilder.Services.AddSingleton<TimeProvider>(clock);
    apiBuilder.Services.AddSingleton<MediaStorageIdentity>();
    apiBuilder.Services.AddSingleton<UnixFileInspector>();
    apiBuilder.Services.AddSingleton<ReconciliationLibraryLock>();
    apiBuilder.Services.AddTransient<ReconciliationService>();
    apiBuilder.Services.AddTransient<JellyfinNativeTitleSource>();
    apiBuilder.Services.AddSingleton<RetentionExecutionGate>();
    apiBuilder.Services.AddSingleton<RetentionRunGate>();
    apiBuilder.Services.AddSingleton<IHttpClientFactory, PlainHttpClientFactory>();
    apiBuilder.Services.AddTransient<LibraryAccess>();
    apiBuilder.Services.AddTransient<RetentionPolicyService>();
    apiBuilder.Services.AddSingleton(new RetentionConfigurationSource(() => settings));
    apiBuilder.Services.AddTransient<RetentionEvaluator>();
    apiBuilder.Services.AddTransient(provider => new TransmissionSeedClient(provider.GetRequiredService<IHttpClientFactory>(),
        () => settings, provider.GetRequiredService<UnixFileInspector>(), NullLogger<TransmissionSeedClient>.Instance));
    apiBuilder.Services.AddTransient<RetentionPreviewService>();
    apiBuilder.Services.AddTransient<RetentionCompletionService>();
    apiBuilder.Services.AddTransient<RetentionLiveCheck>();
    apiBuilder.Services.AddTransient<RetentionExecutor>();
    apiBuilder.Services.AddTransient<RetentionRunner>();
    apiBuilder.Services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(), () => settings,
        NullLogger<TmdbClient>.Instance));
    apiBuilder.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    await using var api = apiBuilder.Build();
    api.UseAuthentication();
    api.UseAuthorization();
    api.MapControllers();
    await api.Services.GetRequiredService<DatabaseInitializer>().StartAsync(default);

    var entry = new Entry
    {
        Id = Guid.NewGuid(), MediaType = "series", TmdbId = 910001, Title = "Concurrency series", State = FileState.OnDisk,
        TargetLibraryId = tvLibrary.Id, JellyfinItemId = series.Id
    };
    var storage = api.Services.GetRequiredService<MediaStorageIdentity>();
    var episodes = new List<JellyfinMod.Data.Episode>();
    await using (var database = new ModDbContext(databasePath))
    {
        foreach (var movie in movies)
        {
            var movieEntry = new Entry
            {
                Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 911000 + movies.IndexOf(movie), Title = movie.Name,
                State = FileState.OnDisk, TargetLibraryId = movieLibrary.Id, JellyfinItemId = movie.Id
            };
            database.Entries.Add(movieEntry);
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = movieEntry.Id, JellyfinItemId = movie.Id, TargetLibraryId = movieLibrary.Id, VersionGroupId = movie.Id,
                MediaPath = movie.Path, StorageIdentity = storage.Capture(movie.Path)
            });
        }

        database.Entries.Add(entry);
        foreach (var native in natives)
        {
            var episode = new JellyfinMod.Data.Episode
            {
                Id = Guid.NewGuid(), EntryId = entry.Id, TmdbId = 0, SeasonNumber = 1, EpisodeNumber = native.IndexNumber!.Value,
                Title = native.Name, State = FileState.OnDisk, JellyfinItemId = native.Id
            };
            episodes.Add(episode);
            database.Episodes.Add(episode);
            database.EpisodeBindings.Add(new EpisodeBinding
            {
                EpisodeId = episode.Id, JellyfinItemId = native.Id, SeriesItemId = series.Id, TargetLibraryId = tvLibrary.Id,
                MediaPath = native.Path, StorageIdentity = storage.Capture(native.Path)
            });
        }

        await database.SaveChangesAsync();
    }

    Guid freshMovieEntryId;
    await using (var database = new ModDbContext(databasePath))
        freshMovieEntryId = (await database.EntryBindings.AsNoTracking().SingleAsync(binding => binding.JellyfinItemId == freshMovie.Id)).EntryId;
    // Retention has never been on: every target is evaluated once as disabled, as on an instance that tracks titles.
    await EvaluateAllAsync(api.Services, settings, "setup");
    SqliteWriteDiagnostics.ResetStatistics();

    await api.StartAsync();
    try
    {
        var address = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(300) };
        http.DefaultRequestHeaders.Add("X-Test-User", admin.Id.ToString());
        http.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        var kept = episodes[targetCount / 2];
        for (var round = 1; round <= rounds; round++)
        {
            // Switching on: the listener syncs the policy and re-evaluates every target, as RetentionEventListener does.
            settings.RetentionEnabled = true;
            var clockStart = Stopwatch.StartNew();
            var listener = Task.Run(() => EvaluateAllAsync(api.Services, settings, "retention listener policy"));
            await Task.Delay(200);
            var secondSwitch = Task.Run(async () =>
            {
                await Task.Delay(500);
                await EvaluateAllAsync(api.Services, settings, "retention listener access");
            });
            var preview = TimedAsync("GET preview", () => http.GetAsync("/JellyfinMod/Retention/Preview"));
            var keep = TimedAsync("POST episode Keep",
                () => http.PostAsync($"/JellyfinMod/Entries/{entry.Id}/Episodes/{kept.Id}/Keep", null));
            var results = await Task.WhenAll(preview, keep);
            var second = await TimedAsync("GET preview again", () => http.GetAsync("/JellyfinMod/Retention/Preview"));
            await Task.WhenAll(listener, secondSwitch);
            Report($"round {round}");
            foreach (var (label, status, elapsed, body) in results.Append(second))
            {
                Console.WriteLine($"  round {round}: {label} -> {(int)status} in {elapsed.TotalSeconds:F1} s");
                Assert(status is HttpStatusCode.OK, $"{label} answers 200 while retention is switched on (round {round}): {body}");
            }

            await using (var database = new ModDbContext(databasePath))
            {
                var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(item => item.TargetId == kept.Id);
                Assert(evaluation.State == "blocked" && evaluation.Reason == "kept",
                    $"The Keep made during the switch-on wins over every concurrent evaluation (round {round}): {evaluation.State}/{evaluation.Reason}");
                var states = await database.RetentionEvaluations.AsNoTracking()
                    .Where(item => item.TargetId != kept.Id && (round == 1 || item.EntryId != freshMovieEntryId))
                    .GroupBy(item => item.State + "/" + item.Reason).Select(group => new { group.Key, Count = group.Count() })
                    .ToListAsync();
                Console.WriteLine($"  round {round}: evaluations {string.Join(", ", states.Select(state => $"{state.Key} {state.Count}"))} " +
                    $"after {clockStart.Elapsed.TotalSeconds:F1} s");
                Assert(states.Count == 1 && states[0].Key == $"waiting/waiting_for_completion",
                    "A backlog watched before tracking waits for a new watch; nothing became due by switching on");
            }

            if (round == 1)
            {
                // Decision 12: a movie watched after the switch-on counts; the movies watched before it stay waiting.
                freshWatch = DateTime.UtcNow;
                using (var scope = api.Services.CreateScope())
                {
                    foreach (var user in allUsers)
                        await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                            .RefreshAsync(user.Id, freshMovie.Id, "PlaybackFinished", CancellationToken.None);
                    await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
                        .EvaluateNativeItemAsync(freshMovie.Id, CancellationToken.None);
                }

                await using var database = new ModDbContext(databasePath);
                var movieStates = await database.RetentionEvaluations.AsNoTracking().Where(item => item.EpisodeId == null)
                    .Join(database.EntryBindings.AsNoTracking(), evaluation => evaluation.EntryId, binding => binding.EntryId,
                        (evaluation, binding) => new { binding.JellyfinItemId, evaluation.State, evaluation.Deadline })
                    .ToListAsync();
                var fresh = movieStates.Single(item => item.JellyfinItemId == freshMovie.Id);
                Assert(fresh.State == "scheduled" && fresh.Deadline > DateTime.UtcNow.AddDays(13),
                    $"A movie watched after the switch-on is scheduled one window out (decision 12): {fresh.State} {fresh.Deadline:o}");
                Assert(movieStates.Where(item => item.JellyfinItemId != freshMovie.Id).All(item => item.State == "waiting"),
                    "Switching retention on made none of the watched movie backlog due (decision 12)");
                Console.WriteLine($"  round {round}: movies: 19 backlog waiting, the one watched after the switch-on scheduled for {fresh.Deadline:o}");
            }

            using var unkeep = await http.DeleteAsync($"/JellyfinMod/Entries/{entry.Id}/Episodes/{kept.Id}/Keep");
            Assert(unkeep.IsSuccessStatusCode, "Un-Keep answers");
            // Switching off before the next round, so each round is a real switch-on.
            settings.RetentionEnabled = false;
            await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        }

        // RET3-N1 (found live on 18096): an access change starts a scheduled window over from now. Switching retention off
        // and on must keep that later date and announce nothing again; before the fix the date came back earlier than the
        // one announced, so a file could go before the date its warning had shown.
        async Task<(DateTime? Deadline, int Started)> FreshMovieAsync()
        {
            await using var database = new ModDbContext(databasePath);
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(item => item.TargetId == freshMovieEntryId);
            var started = await database.History.AsNoTracking()
                .CountAsync(history => history.EntryId == freshMovieEntryId && history.EventType == "retention_started");
            return (evaluation.State == "scheduled" ? evaluation.Deadline : null, started);
        }

        settings.RetentionEnabled = true;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        var before = await FreshMovieAsync();
        Assert(before.Deadline is not null, "The movie watched after the switch-on is scheduled again");
        // Access changes while retention is off (a user added), so the next switch-on starts the window over from then.
        settings.RetentionEnabled = false;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        await Task.Delay(1500);
        var newcomer = new User("newcomer", "auth", "reset") { Id = Guid.NewGuid() };
        allUsers.Add(newcomer);
        withAccess.Add(newcomer.Id);
        settings.RetentionEnabled = true;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        var pushed = await FreshMovieAsync();
        Assert(pushed.Deadline > before.Deadline && pushed.Started == before.Started + 1,
            $"An access change while retention was off starts the window over from the switch-on and announces it: " +
            $"{before.Deadline:o}/{before.Started} -> {pushed.Deadline:o}/{pushed.Started}");
        settings.RetentionEnabled = false;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        settings.RetentionEnabled = true;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        var again = await FreshMovieAsync();
        Assert(again.Deadline == pushed.Deadline && again.Started == pushed.Started,
            $"Switching retention off and on keeps the date the window was announced with and announces nothing again (RET3-N1): " +
            $"{pushed.Deadline:o}/{pushed.Started} -> {again.Deadline:o}/{again.Started}");
        settings.RetentionEnabled = false;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        Console.WriteLine($"  RET3-N1: the deadline {again.Deadline:o} survived an access change and a switch-off and on");

        // Decision 13 (RET4-R1, 2026-09-25): a title finished while retention is off gets a full window from the next
        // switch-on, announced, and is never due at once, however long ago it was watched; a countdown announced before the
        // switch-off keeps its date while it has not run out (Q9).
        var offMovie = movies[^2];
        var offMovieLate = movies[^3];
        var offEpisode = episodes[0];
        var offEpisodeNative = natives[0];
        Guid EntryOf(MediaBrowser.Controller.Entities.Movies.Movie movie)
        {
            using var database = new ModDbContext(databasePath);
            return database.EntryBindings.AsNoTracking().Single(binding => binding.JellyfinItemId == movie.Id).EntryId;
        }
        var offMovieEntry = EntryOf(offMovie);
        var offMovieLateEntry = EntryOf(offMovieLate);
        async Task<(string State, DateTime? Deadline, int Started)> TargetAsync(Guid targetId, Guid entryId)
        {
            await using var database = new ModDbContext(databasePath);
            var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleAsync(item => item.TargetId == targetId);
            var started = (await database.History.AsNoTracking()
                    .Where(history => history.EntryId == entryId && history.EventType == "retention_started").ToListAsync())
                .Count(history => targetId == entryId || (history.Data ?? "").Contains(targetId.ToString(), StringComparison.OrdinalIgnoreCase));
            return (evaluation.State, evaluation.Deadline, started);
        }
        async Task WatchWhileOffAsync(BaseItem item, DateTime at)
        {
            watchedWhileOff[item.Id] = at;
            using var scope = api.Services.CreateScope();
            foreach (var user in allUsers)
                await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
                    .RefreshAsync(user.Id, item.Id, "PlaybackFinished", CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>().EvaluateNativeItemAsync(item.Id, CancellationToken.None);
        }

        // (1) Watched while off, the switch-on soon after: one window from the switch-on, announced once; the countdown
        // announced before the switch-off (the fresh movie) keeps its date and is not announced again.
        await WatchWhileOffAsync(offMovie, DateTime.UtcNow);
        await WatchWhileOffAsync(offEpisodeNative, DateTime.UtcNow);
        var disabledRow = await TargetAsync(offMovieEntry, offMovieEntry);
        Assert(disabledRow is { State: "disabled", Started: 0 }, $"A watch while retention is off starts nothing: {disabledRow}");
        var switchOn = clock.GetUtcNow().UtcDateTime;
        settings.RetentionEnabled = true;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        var movieOn = await TargetAsync(offMovieEntry, offMovieEntry);
        var episodeOn = await TargetAsync(offEpisode.Id, entry.Id);
        foreach (var (label, row) in new[] { ("movie", movieOn), ("episode", episodeOn) })
            Assert(row.State == "scheduled" && row.Deadline >= switchOn.AddDays(14).AddSeconds(-1) && row.Started == 1,
                $"A {label} finished while retention was off is scheduled one full window from the switch-on and announced once " +
                $"(decision 13): {row}");
        var freshKept = await FreshMovieAsync();
        Assert(freshKept.Deadline == again.Deadline && freshKept.Started == again.Started,
            $"A countdown announced before the switch-off keeps its date (Q9): {again.Deadline:o} -> {freshKept.Deadline:o}");

        // (2) Off for longer than the window: every window above runs out while off, and a movie is finished during the off
        // period. At the switch-on none is due: each gets a full window from the switch-on, announced.
        settings.RetentionEnabled = false;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        await WatchWhileOffAsync(offMovieLate, DateTime.UtcNow);
        clock.Offset = TimeSpan.FromDays(20);
        switchOn = clock.GetUtcNow().UtcDateTime;
        settings.RetentionEnabled = true;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        var rows = new[]
        {
            ("movie watched while off, window run out", await TargetAsync(offMovieLateEntry, offMovieLateEntry), 1),
            ("movie whose announced window ran out while off", await TargetAsync(offMovieEntry, offMovieEntry), 2),
            ("episode whose announced window ran out while off", await TargetAsync(offEpisode.Id, entry.Id), 2),
            ("fresh movie whose announced window ran out while off", await TargetAsync(freshMovieEntryId, freshMovieEntryId), again.Started + 1)
        };
        foreach (var (label, row, started) in rows)
            Assert(row.State == "scheduled" && row.Deadline >= switchOn.AddDays(14).AddSeconds(-1) && row.Started == started,
                $"The {label} is not due at the switch-on: a full window from it, announced (decision 13): {row}, expected {started} announcements");
        using (var scope = api.Services.CreateScope())
        {
            var preview = await scope.ServiceProvider.GetRequiredService<RetentionPreviewService>().PreviewAsync(CancellationToken.None);
            Assert(preview.Due == 0, $"Nothing is due right after the switch-on (decision 13): {preview.Due} due");
        }
        settings.RetentionEnabled = false;
        await EvaluateAllAsync(api.Services, settings, "retention listener policy");
        clock.Offset = TimeSpan.Zero;
        Console.WriteLine($"  decision 13: watched while off and run out while off -> scheduled from the switch-on, " +
            $"{string.Join(", ", rows.Select(row => $"{row.Item2.Deadline:o}"))}");
    }
    finally
    {
        await api.StopAsync();
    }
}

// Decision 12: the migration takes the Phase 3 movie backlog out of the schedule and says so; nothing else changes.
static async Task VerifyDecisionTwelveMigrationAsync(string folder)
{
    var path = Path.Combine(folder, "decision-12.db");
    var firstEnabled = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    var backlogMovie = new Entry { Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 920001, Title = "Backlog movie", State = FileState.OnDisk };
    var freshMovie = new Entry { Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 920002, Title = "Fresh movie", State = FileState.OnDisk };
    // RET4-R3: the Phase 3 rule stored the time it read an undated played flag as the basis, which can lie after the floor.
    var undatedMovie = new Entry { Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 920005, Title = "Undated movie", State = FileState.OnDisk };
    var series = new Entry { Id = Guid.NewGuid(), MediaType = "series", TmdbId = 920003, Title = "Series", State = FileState.OnDisk };
    var episode = new JellyfinMod.Data.Episode { Id = Guid.NewGuid(), EntryId = series.Id, TmdbId = 920004, SeasonNumber = 1, EpisodeNumber = 1 };
    await using (var database = new ModDbContext(path))
    {
        // The database as the previous release left it: every migration up to PhaseTenRetentionFixes.
        await database.GetService<IMigrator>().MigrateAsync("20260924110006_PhaseTenRetentionFixes");
        database.Entries.AddRange(backlogMovie, freshMovie, undatedMovie, series);
        database.Episodes.Add(episode);
        database.RetentionPolicySnapshots.Add(new RetentionPolicySnapshot
        {
            Id = RetentionPolicyService.PolicyId, Version = 3, Enabled = true, WatchedUserMode = WatchedUserMode.AllUsers,
            ReclaimAfterDays = 14, EnabledAt = firstEnabled.AddDays(2), FirstEnabledAt = firstEnabled, UpdatedAt = firstEnabled
        });
        RetentionEvaluation Scheduled(Guid entryId, Guid? episodeId, DateTime basis) => new()
        {
            EntryId = entryId, EpisodeId = episodeId, TargetId = episodeId ?? entryId, State = "scheduled",
            Reason = "completion_policy_satisfied", PolicyVersion = 3, BaselineAt = firstEnabled.AddDays(-30),
            EvaluatedAt = firstEnabled.AddDays(3), CompletionBasisAt = basis, EligibleAt = firstEnabled,
            Deadline = basis.AddDays(14), RequiresFreshCompletion = episodeId.HasValue
        };
        database.RetentionEvaluations.AddRange(
            Scheduled(backlogMovie.Id, null, firstEnabled.AddDays(-100)),
            Scheduled(freshMovie.Id, null, firstEnabled.AddDays(1)),
            Scheduled(undatedMovie.Id, null, firstEnabled.AddDays(1)),
            Scheduled(series.Id, episode.Id, firstEnabled.AddDays(-100)));
        CompletionObservation Observed(Guid entryId, DateTime? lastPlayed, DateTime observed) => new()
        {
            EntryId = entryId, TargetId = entryId, UserId = Guid.NewGuid(), JellyfinItemId = Guid.NewGuid(), EvidenceAvailable = true,
            Played = true, LastPlayedAt = lastPlayed, CompletedAt = lastPlayed ?? observed, ObservedAt = observed, SourceReason = "Import"
        };
        database.CompletionObservations.AddRange(
            Observed(freshMovie.Id, firstEnabled.AddDays(1), firstEnabled.AddDays(1)),
            Observed(undatedMovie.Id, null, firstEnabled.AddDays(1)));
        await database.SaveChangesAsync();
    }

    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    await using (var database = new ModDbContext(path))
    {
        await database.Database.MigrateAsync();
        var evaluations = await database.RetentionEvaluations.AsNoTracking().ToDictionaryAsync(evaluation => evaluation.TargetId);
        var backlog = evaluations[backlogMovie.Id];
        var fresh = evaluations[freshMovie.Id];
        var undated = evaluations[undatedMovie.Id];
        var episodic = evaluations[episode.Id];
        Assert(backlog is { State: "waiting", Reason: "waiting_for_completion", Deadline: null, CompletionBasisAt: null, RequiresFreshCompletion: true },
            $"A movie scheduled from a watch before its floor is no longer due after the migration (decision 12): {backlog.State} {backlog.Deadline}");
        Assert(fresh is { State: "scheduled", RequiresFreshCompletion: true } && fresh.Deadline == firstEnabled.AddDays(15),
            $"A movie scheduled from a watch after its floor keeps its schedule: {fresh.State} {fresh.Deadline}");
        Assert(undated is { State: "waiting", Deadline: null },
            $"A movie scheduled from an undated played flag read after its floor is no longer due either (RET4-R3): {undated.State} {undated.Deadline}");
        Assert(episodic is { State: "scheduled" } && episodic.Deadline == firstEnabled.AddDays(-86),
            "Episodes are untouched by the movie back-fill");
        var history = await database.History.AsNoTracking().Where(record => record.EventType == "retention_rule_changed").ToListAsync();
        Assert(history.Count == 2 && history.Select(record => record.EntryId).ToHashSet().SetEquals([backlogMovie.Id, undatedMovie.Id]) &&
            history.All(record => record.Summary.Contains("decision 12", StringComparison.Ordinal) &&
                JsonDocument.Parse(record.Data!).RootElement.GetProperty("reason").GetString() == "decision_12_movie_backlog" &&
                record.CreatedAt > DateTime.UtcNow.AddMinutes(-5)),
            "History records the rule change once on each movie it took out of the schedule");
    }

    Console.WriteLine("PASS: decision 12 migration takes the watched movie backlog out of the schedule and records it");
}

static void DiskLoad(string path, CancellationToken stop)
{
    var block = new byte[1 << 20];
    Random.Shared.NextBytes(block);
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);
    while (!stop.IsCancellationRequested)
    {
        if (stream.Length > (256L << 20)) stream.SetLength(0);
        stream.Write(block);
        stream.Flush(true);
    }
}

static void Report(string phase)
{
    foreach (var (operation, (count, total, max)) in SqliteWriteDiagnostics.Snapshot().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {phase}: writes by {operation}: {count}, {total:F1} s in all, longest {max:F2} s (lock wait included)");
    SqliteWriteDiagnostics.ResetStatistics();
}

static async Task EvaluateAllAsync(IServiceProvider services, PluginConfiguration settings, string operation)
{
    using var scope = services.CreateScope();
    using var named = SqliteWriteDiagnostics.Operation(operation);
    await scope.ServiceProvider.GetRequiredService<RetentionPolicyService>().SyncAsync(settings, CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(CancellationToken.None);
}

static async Task<(string Label, HttpStatusCode Status, TimeSpan Elapsed, string Body)> TimedAsync(string label,
    Func<Task<HttpResponseMessage>> request)
{
    var watch = Stopwatch.StartNew();
    using var response = await request();
    var body = await response.Content.ReadAsStringAsync();
    return (label, response.StatusCode, watch.Elapsed, body.Length > 600 ? body[..600] : body);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Test-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("Jellyfin-UserId", userId.ToString()) };
        if (Request.Headers["X-Test-Role"] == "admin") claims.Add(new(ClaimTypes.Role, "admin"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

internal sealed class PlainHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
}

internal sealed class FixtureRoot(IReadOnlySet<Guid> userIds, IReadOnlyList<BaseItem> libraries) : Folder
{
    public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) =>
        userIds.Contains(user.Id) ? libraries : [];
}

internal sealed class FixtureLibrary : CollectionFolder
{
    public List<BaseItem> Items { get; } = [];

    protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
        new() { Items = Items.ToArray(), TotalRecordCount = Items.Count };
}

internal sealed class FixtureSeries : MediaBrowser.Controller.Entities.TV.Series
{
    public List<BaseItem> Items { get; } = [];

    protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) =>
        new() { Items = Items.ToArray(), TotalRecordCount = Items.Count };
}

internal class Stub<T> : DispatchProxy where T : class
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

internal sealed class ShiftedClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
