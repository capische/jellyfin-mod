using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-three-protection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var downloads = Path.Combine(folder, "downloads");
var library = Path.Combine(folder, "library");
Directory.CreateDirectory(downloads);
Directory.CreateDirectory(library);
var source = Path.Combine(downloads, "fixture.mkv");
var media = Path.Combine(library, "fixture.mkv");
await File.WriteAllBytesAsync(source, new byte[4096]);
if (NativeTestMethods.Link(source, media) != 0) throw new IOException("Could not create the hardlink fixture.");

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
var app = builder.Build();
var ratio = 2.5;
var completed = 4096UL;
var seedScenario = SeedScenario.Ratio;
var sawSessionToken = false;
app.MapPost("/transmission/rpc", async context =>
{
    if (context.Request.Headers["X-Transmission-Session-Id"] != "fixture-session")
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers["X-Transmission-Session-Id"] = "fixture-session";
        return;
    }

    sawSessionToken = true;
    using var request = await JsonDocument.ParseAsync(context.Request.Body);
    var method = request.RootElement.GetProperty("method").GetString();
    context.Response.ContentType = "application/json";
    if (method == "session-get")
    {
        await context.Response.WriteAsJsonAsync(new
        {
            result = "success",
            arguments = new Dictionary<string, object>
            {
                ["version"] = "4.0.5",
                ["seedRatioLimited"] = true,
                ["seedRatioLimit"] = 2.0,
                ["idle-seeding-limit-enabled"] = false,
                ["idle-seeding-limit"] = 1440
            }
        });
        return;
    }

    await context.Response.WriteAsJsonAsync(new
    {
        result = "success",
        arguments = new
        {
            torrents = new[]
            {
                new
                {
                    id = 1,
                    hashString = "fixture",
                    downloadDir = downloads,
                    files = new[] { new { name = "fixture.mkv", length = 4096UL, bytesCompleted = completed } },
                    leftUntilDone = 4096UL - completed,
                    percentDone = completed / 4096d,
                    status = seedScenario == SeedScenario.IdlePending ? 0 : completed == 4096 ? 6 : 4,
                    uploadRatio = ratio,
                    secondsSeeding = 3600,
                    seedRatioMode = seedScenario == SeedScenario.Ratio ? 0 : 2,
                    seedRatioLimit = 1.0,
                    seedIdleMode = seedScenario is SeedScenario.IdlePending or SeedScenario.IdleSatisfied ? 1 : 2,
                    seedIdleLimit = 30,
                    etaIdle = seedScenario == SeedScenario.IdleSatisfied ? 0 : 600,
                    isFinished = seedScenario == SeedScenario.IdleSatisfied ||
                        seedScenario == SeedScenario.Ratio && ratio >= 2
                }
            }
        }
    });
});

await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var settings = new PluginConfiguration
    {
        TransmissionRpcUrl = address + "/transmission/rpc",
        RetentionEnabled = true,
        ReclaimAfterDays = 1,
        RetentionWatchedUserMode = WatchedUserMode.AllUsers,
        ExemptFavourites = true
    };
    var inspector = new UnixFileInspector();
    var client = new TransmissionSeedClient(new PlainHttpClientFactory(), () => settings, inspector,
        NullLogger<TransmissionSeedClient>.Instance);

    Assert(inspector.TryInspect(media, out var mediaFile) && mediaFile.HardlinkCount == 2 && mediaFile.LogicalBytes == 4096,
        "Linux file inspection returns inode, hardlink count and logical bytes");
    var snapshot = await client.GetSnapshotAsync(default);
    Assert(sawSessionToken && snapshot.Available && snapshot.CompleteFileIndex,
        "Transmission snapshot retries the real HTTP 409 session handshake and indexes every file");
    var seeded = snapshot.FilesByPhysicalIdentity[mediaFile.PhysicalIdentity].Single();
    Assert(seeded.FileComplete && seeded.HasFiniteSeedGoal && seeded.SeedGoalSatisfied && seeded.RatioGoal == 2 &&
        seeded.UploadRatio == ratio, "A hardlinked library path inherits its completed Transmission ratio goal");

    ratio = 1.5;
    seeded = (await client.GetSnapshotAsync(default)).FilesByPhysicalIdentity[mediaFile.PhysicalIdentity].Single();
    Assert(!seeded.SeedGoalSatisfied, "A torrent below its effective global ratio remains protected");

    completed = 2048;
    seeded = (await client.GetSnapshotAsync(default)).FilesByPhysicalIdentity[mediaFile.PhysicalIdentity].Single();
    Assert(!seeded.FileComplete, "A partially downloaded torrent file remains protected");

    var link = Path.Combine(library, "escape.mkv");
    File.CreateSymbolicLink(link, source);
    Assert(!inspector.TryInspect(link, out _), "A symbolic-link representation is never accepted as a deletion target");

    settings.TransmissionRpcUrl = string.Empty;
    Assert((await client.GetSnapshotAsync(default)).UnavailableReason == "transmission_unconfigured",
        "Missing Transmission configuration produces a stable deletion-blocking reason");
    settings.TransmissionRpcUrl = address + "/transmission/rpc";
    ratio = 2.5;
    completed = 4096;
    await VerifyPreviewHttpAsync(folder, library, media, settings, (newRatio, newCompleted, newScenario) =>
    {
        ratio = newRatio;
        completed = newCompleted;
        seedScenario = newScenario;
    });
    Console.WriteLine("PASS: real Linux hardlinks and Transmission HTTP seed protection");
}
finally
{
    await app.StopAsync();
    Directory.Delete(folder, true);
}

static async Task VerifyPreviewHttpAsync(
    string folder,
    string libraryPath,
    string mediaPath,
    PluginConfiguration settings,
    Action<double, ulong, SeedScenario> setTorrent)
{
    var transmissionUrl = settings.TransmissionRpcUrl;
    var databasePath = Path.Combine(folder, "preview.db");
    var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    var clock = new FixedTimeProvider(now);
    var user = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
    var libraryFolder = new FixtureLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.movies };
    var sharedLibraryFolder = new FixtureLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.movies };
    var tvLibraryFolder = new FixtureLibrary { Id = Guid.NewGuid(), CollectionType = CollectionType.tvshows };
    var root = new FixtureRoot(user.Id, [libraryFolder, sharedLibraryFolder, tvLibraryFolder]);
    var downloadsPath = Path.Combine(folder, "downloads");
    var secondSource = Path.Combine(downloadsPath, "version2.mkv");
    var secondMedia = Path.Combine(libraryPath, "version2.mkv");
    await File.WriteAllBytesAsync(secondSource, new byte[2048]);
    if (NativeTestMethods.Link(secondSource, secondMedia) != 0) throw new IOException("Could not create version hardlink.");
    var episodeSource = Path.Combine(downloadsPath, "episode.mkv");
    var episodeMedia = Path.Combine(libraryPath, "episode.mkv");
    await File.WriteAllBytesAsync(episodeSource, new byte[1024]);
    if (NativeTestMethods.Link(episodeSource, episodeMedia) != 0) throw new IOException("Could not create episode hardlink.");
    var escapeSource = Path.Combine(downloadsPath, "escape-source.mkv");
    await File.WriteAllBytesAsync(escapeSource, new byte[512]);
    var escapeParent = Path.Combine(libraryPath, "escape-parent");
    Directory.CreateSymbolicLink(escapeParent, downloadsPath);
    var escapeMedia = Path.Combine(escapeParent, "escape-source.mkv");
    var movie = new Movie { Id = Guid.NewGuid(), Name = "Retention fixture", Path = mediaPath };
    var secondVersion = new Movie { Id = Guid.NewGuid(), Name = "Retention fixture 2", Path = secondMedia };
    var escapedVersion = new Movie { Id = Guid.NewGuid(), Name = "Escaped fixture", Path = escapeMedia };
    var series = new FixtureSeries { Id = Guid.NewGuid(), Name = "Favorite series" };
    var nativeEpisode = new MediaBrowser.Controller.Entities.TV.Episode
    {
        Id = Guid.NewGuid(), Name = "Favorite episode", Path = episodeMedia, SeriesId = series.Id,
        ParentIndexNumber = 1, IndexNumber = 1
    };
    libraryFolder.Items.AddRange([movie, secondVersion, escapedVersion]);
    tvLibraryFolder.Items.Add(series);
    series.Items.Add(nativeEpisode);
    var items = new Dictionary<Guid, BaseItem>
    {
        [movie.Id] = movie,
        [secondVersion.Id] = secondVersion,
        [escapedVersion.Id] = escapedVersion,
        [series.Id] = series,
        [nativeEpisode.Id] = nativeEpisode,
        [libraryFolder.Id] = libraryFolder,
        [sharedLibraryFolder.Id] = sharedLibraryFolder,
        [tvLibraryFolder.Id] = tvLibraryFolder
    };
    var virtualFolders = new List<VirtualFolderInfo>
    {
        new()
        {
            Name = "Retention fixtures",
            ItemId = libraryFolder.Id.ToString(),
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        },
        new()
        {
            Name = "Shared retention fixtures",
            ItemId = sharedLibraryFolder.Id.ToString(),
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        },
        new()
        {
            Name = "TV retention fixtures",
            ItemId = tvLibraryFolder.Id.ToString(),
            CollectionType = CollectionTypeOptions.tvshows,
            Locations = [libraryPath]
        }
    };
    var library = Stub<ILibraryManager>.Create((method, arguments) => method.Name switch
    {
        "GetUserRootFolder" => root,
        "GetVirtualFolders" => virtualFolders,
        "GetItemById" when arguments?[0] is Guid id => items.GetValueOrDefault(id),
        "DeleteItem" when arguments?[0] is BaseItem item => items.Remove(item.Id),
        _ => null
    });
    var users = Stub<IUserManager>.Create((method, arguments) => method.Name switch
    {
        "GetUsers" => new[] { user },
        "GetUserById" when arguments?[0] is Guid id && id == user.Id => user,
        _ => null
    });
    var localization = Stub<ILocalizationManager>.Create((_, _) => null);
    var userData = Stub<IUserDataManager>.Create((method, arguments) => method.Name == "GetUserData" &&
        arguments?[1] is BaseItem item && item.Id == series.Id
            ? new UserItemData { Key = "favorite-series", IsFavorite = true }
            : null);
    var sessionRows = new List<SessionInfo>();
    var sessionManager = Stub<ISessionManager>.Create((method, _) => method.Name == "get_Sessions"
        ? sessionRows.ToArray()
        : null);

    var apiBuilder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
    {
        ContentRootPath = folder,
        ApplicationName = typeof(RetentionController).Assembly.FullName
    });
    apiBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
    apiBuilder.Logging.ClearProviders();
    apiBuilder.Services.AddControllers().AddApplicationPart(typeof(RetentionController).Assembly);
    apiBuilder.Services.AddAuthentication("Preview")
        .AddScheme<AuthenticationSchemeOptions, PreviewAuthentication>("Preview", null);
    apiBuilder.Services.AddAuthorization(options =>
        options.AddPolicy(Policies.RequiresElevation, policy => policy.RequireRole("admin")));
    apiBuilder.Services.AddTransient(_ => new ModDbContext(databasePath));
    apiBuilder.Services.AddSingleton<DatabaseInitializer>();
    apiBuilder.Services.AddSingleton(users);
    apiBuilder.Services.AddSingleton(library);
    apiBuilder.Services.AddSingleton(userData);
    apiBuilder.Services.AddSingleton(sessionManager);
    apiBuilder.Services.AddSingleton(localization);
    // EntriesController needs the sort-name service since the P1.P7 carry-forward of fba11e3.
    var serverConfiguration = Stub<MediaBrowser.Controller.Configuration.IServerConfigurationManager>.Create((method, _) =>
        method.Name == "get_Configuration"
            ? new MediaBrowser.Model.Configuration.ServerConfiguration
            {
                SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = []
            }
            : null);
    apiBuilder.Services.AddTransient(_ => new CatalogSortName(serverConfiguration));
    apiBuilder.Services.AddSingleton<TimeProvider>(clock);
    apiBuilder.Services.AddSingleton<MediaStorageIdentity>();
    apiBuilder.Services.AddSingleton<UnixFileInspector>();
    apiBuilder.Services.AddSingleton<ReconciliationLibraryLock>();
    apiBuilder.Services.AddSingleton<RetentionExecutionGate>();
    apiBuilder.Services.AddSingleton<RetentionRunGate>();
    apiBuilder.Services.AddSingleton<IHttpClientFactory, PlainHttpClientFactory>();
    apiBuilder.Services.AddTransient<LibraryAccess>();
    apiBuilder.Services.AddTransient<RetentionPolicyService>();
    apiBuilder.Services.AddSingleton(new RetentionConfigurationSource(() => settings));
    apiBuilder.Services.AddTransient<RetentionEvaluator>();
    apiBuilder.Services.AddTransient(provider => new TransmissionSeedClient(
        provider.GetRequiredService<IHttpClientFactory>(), () => settings,
        provider.GetRequiredService<UnixFileInspector>(), NullLogger<TransmissionSeedClient>.Instance));
    apiBuilder.Services.AddTransient<RetentionPreviewService>();
    apiBuilder.Services.AddTransient<RetentionExecutor>();
    apiBuilder.Services.AddTransient<RetentionRunner>();
    apiBuilder.Services.AddTransient(provider => new TmdbClient(
        provider.GetRequiredService<IHttpClientFactory>(), () => settings,
        NullLogger<TmdbClient>.Instance));
    await using var api = apiBuilder.Build();
    api.UseAuthentication();
    api.UseAuthorization();
    api.MapControllers();
    await api.Services.GetRequiredService<DatabaseInitializer>().StartAsync(default);

    var storage = api.Services.GetRequiredService<MediaStorageIdentity>();
    var entry = new Entry
    {
        Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 900001, Title = "Retention fixture",
        State = FileState.OnDisk, TargetLibraryId = libraryFolder.Id, JellyfinItemId = movie.Id
    };
    var seriesEntry = new Entry
    {
        Id = Guid.NewGuid(), MediaType = "series", TmdbId = 900002, Title = "Favorite series",
        State = FileState.OnDisk, TargetLibraryId = tvLibraryFolder.Id, JellyfinItemId = series.Id
    };
    var episode = new JellyfinMod.Data.Episode
    {
        Id = Guid.NewGuid(), EntryId = seriesEntry.Id, TmdbId = 900003, SeasonNumber = 1, EpisodeNumber = 1,
        Title = "Favorite episode", State = FileState.OnDisk, JellyfinItemId = nativeEpisode.Id
    };
    await using (var database = new ModDbContext(databasePath))
    {
        database.Entries.AddRange(entry, seriesEntry);
        database.Episodes.Add(episode);
        database.EntryBindings.AddRange(
            new EntryBinding
            {
                EntryId = entry.Id, JellyfinItemId = movie.Id, TargetLibraryId = libraryFolder.Id,
                VersionGroupId = movie.Id, MediaPath = mediaPath, StorageIdentity = storage.Capture(mediaPath)
            },
            new EntryBinding
            {
                EntryId = entry.Id, JellyfinItemId = secondVersion.Id, TargetLibraryId = libraryFolder.Id,
                VersionGroupId = movie.Id, MediaPath = secondMedia, StorageIdentity = storage.Capture(secondMedia)
            },
            new EntryBinding
            {
                EntryId = entry.Id, JellyfinItemId = escapedVersion.Id, TargetLibraryId = libraryFolder.Id,
                VersionGroupId = movie.Id, MediaPath = escapeMedia, StorageIdentity = storage.Capture(escapeMedia)
            });
        database.EpisodeBindings.Add(new EpisodeBinding
        {
            EpisodeId = episode.Id, JellyfinItemId = nativeEpisode.Id, SeriesItemId = series.Id,
            TargetLibraryId = tvLibraryFolder.Id, MediaPath = episodeMedia, StorageIdentity = storage.Capture(episodeMedia)
        });
        database.RetentionPolicySnapshots.Add(new RetentionPolicySnapshot
        {
            Id = RetentionPolicyService.PolicyId,
            Version = 1,
            Enabled = true,
            WatchedUserMode = WatchedUserMode.AllUsers,
            ReclaimAfterDays = 1,
            ExemptFavourites = true,
            EnabledAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3)
        });
        database.CompletionObservations.Add(new CompletionObservation
        {
            EntryId = entry.Id,
            TargetId = entry.Id,
            UserId = user.Id,
            JellyfinItemId = movie.Id,
            EvidenceAvailable = true,
            Played = true,
            CompletedAt = now.AddDays(-3),
            LastPlayedAt = now.AddDays(-3),
            ObservedAt = now.AddDays(-3)
        });
        database.CompletionObservations.Add(new CompletionObservation
        {
            EntryId = seriesEntry.Id,
            EpisodeId = episode.Id,
            TargetId = episode.Id,
            UserId = user.Id,
            JellyfinItemId = nativeEpisode.Id,
            EvidenceAvailable = true,
            Played = true,
            CompletedAt = now.AddDays(-3),
            LastPlayedAt = now.AddDays(-3),
            ObservedAt = now.AddDays(-3)
        });
        // Both targets were already tracked when retention was enabled three days ago (P3.T7 grace
        // floors at a target's first evaluation, so the fixture records that earlier evaluation).
        database.RetentionEvaluations.AddRange(
            new RetentionEvaluation
            {
                EntryId = entry.Id, TargetId = entry.Id, State = "disabled", Reason = "retention_disabled",
                PolicyVersion = 1, EvaluatedAt = now.AddDays(-3), BaselineAt = now.AddDays(-3)
            },
            new RetentionEvaluation
            {
                EntryId = seriesEntry.Id, EpisodeId = episode.Id, TargetId = episode.Id, State = "disabled",
                Reason = "retention_disabled", PolicyVersion = 1, EvaluatedAt = now.AddDays(-3),
                BaselineAt = now.AddDays(-3)
            });
        await database.SaveChangesAsync();
    }

    await api.StartAsync();
    try
    {
        var apiAddress = api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(apiAddress), Timeout = TimeSpan.FromSeconds(5) };
        Assert((await http.GetAsync("/JellyfinMod/Retention/Preview")).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous retention preview is rejected by real authentication middleware");
        http.DefaultRequestHeaders.Add("X-Preview-User", user.Id.ToString());
        Assert((await http.GetAsync("/JellyfinMod/Retention/Preview")).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot read physical retention paths or activity diagnostics");
        Assert((await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Keep", null)).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot write Keep through the real authorization policy");
        http.DefaultRequestHeaders.Add("X-Preview-Role", "admin");
        using var response = await http.GetAsync("/JellyfinMod/Retention/Preview");
        var body = await response.Content.ReadAsStringAsync();
        Assert(response.IsSuccessStatusCode, "Admin preview succeeds through HTTP: " + body);
        using var json = JsonDocument.Parse(body);
        var preview = json.RootElement;
        var row = FindRow(preview, movie.Id);
        var secondRow = FindRow(preview, secondVersion.Id);
        var escapedRow = FindRow(preview, escapedVersion.Id);
        var episodeRow = FindRow(preview, nativeEpisode.Id);
        Assert(preview.GetProperty("due").GetInt32() == 2 && row.GetProperty("state").GetString() == "due" &&
            row.GetProperty("reason").GetString() == "eligible" && row.GetProperty("torrentManaged").GetBoolean() &&
            row.GetProperty("hardlinkCount").GetUInt32() == 2 && row.GetProperty("path").GetString() == mediaPath,
            "Admin HTTP preview exposes a deterministic due representation with seed and hardlink evidence: " + body);
        Assert(secondRow.GetProperty("state").GetString() == "due" &&
            !secondRow.GetProperty("torrentManaged").GetBoolean(),
            "Each movie version is evaluated separately and a complete RPC index verifies a non-torrent copy");
        Assert(escapedRow.GetProperty("reason").GetString() == "symlink_escape",
            "A parent symlink that resolves outside its library root is blocked");
        Assert(episodeRow.GetProperty("reason").GetString() == "favorite_series",
            "A favorite series protects its otherwise eligible episode representation");

        using var kept = await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Keep", null);
        var keptBody = await kept.Content.ReadAsStringAsync();
        Assert(kept.IsSuccessStatusCode, "An administrator can keep a series: " +
            kept.StatusCode + " " + keptBody);
        using var keptAgain = await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Keep", null);
        Assert(keptAgain.IsSuccessStatusCode, "Keeping an already-kept series is idempotent");
        using var keptDetail = await http.GetFromJsonAsync<JsonDocument>($"/JellyfinMod/Entries/{seriesEntry.Id}");
        Assert(keptDetail!.RootElement.GetProperty("retention").GetProperty("reason").GetString() == "kept" &&
            keptDetail.RootElement.GetProperty("episodes")[0].GetProperty("retention")
                .GetProperty("reason").GetString() == "kept",
            "Series detail reports that Keep protects the series and every child episode");
        await using (var keptDatabase = new ModDbContext(databasePath))
        {
            Assert(await keptDatabase.History.CountAsync(history => history.EntryId == seriesEntry.Id &&
                history.EventType == "retention_kept") == 1,
                "Repeated Keep writes exactly one durable history event");
        }

        sessionRows.Add(new SessionInfo(sessionManager, NullLogger.Instance)
        {
            FullNowPlayingItem = movie
        });
        using var active = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(active!.RootElement, movie.Id).GetProperty("reason").GetString() == "active_session",
            "An active native playback blocks the same physical representation");
        sessionRows.Clear();

        setTorrent(1.5, 4096, SeedScenario.Ratio);
        using var belowRatio = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(belowRatio!.RootElement, movie.Id).GetProperty("reason").GetString() == "seed_goal_unmet",
            "A due hardlink remains blocked while Transmission is below its effective ratio goal");

        setTorrent(2.5, 2048, SeedScenario.Ratio);
        using var incomplete = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(incomplete!.RootElement, movie.Id).GetProperty("reason").GetString() == "seeding_incomplete",
            "A due hardlink remains blocked while its torrent file is incomplete");

        setTorrent(2.5, 4096, SeedScenario.IdlePending);
        using var idlePending = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(idlePending!.RootElement, movie.Id).GetProperty("reason").GetString() == "seed_goal_unmet",
            "A paused torrent below its finite idle goal remains protected");

        setTorrent(2.5, 4096, SeedScenario.IdleSatisfied);
        using var idleSatisfied = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(idleSatisfied!.RootElement, movie.Id).GetProperty("state").GetString() == "due",
            "A completed torrent whose finite idle goal is satisfied can become due");

        setTorrent(2.5, 4096, SeedScenario.Unbounded);
        using var unbounded = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(unbounded!.RootElement, movie.Id).GetProperty("reason").GetString() == "seed_goal_unbounded",
            "A torrent with no finite ratio or idle goal remains protected");

        setTorrent(2.5, 4096, SeedScenario.Ratio);
        settings.TransmissionRpcUrl = string.Empty;
        using var unknownSeed = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(unknownSeed!.RootElement, movie.Id).GetProperty("reason").GetString() ==
            "transmission_unconfigured", "Missing client configuration never proves that a due file is non-torrent");

        settings.TransmissionRpcUrl = transmissionUrl.Replace("/transmission/rpc", "/unreachable", StringComparison.Ordinal);
        using var unreachableSeed = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(unreachableSeed!.RootElement, movie.Id).GetProperty("reason").GetString() ==
            "transmission_unreachable", "An unreachable configured client blocks due media");

        settings.TransmissionRpcUrl = transmissionUrl;
        await using (var database = new ModDbContext(databasePath))
        {
            var binding = await database.EntryBindings.SingleAsync(candidate =>
                candidate.JellyfinItemId == secondVersion.Id);
            binding.StorageIdentity = "changed-storage";
            await database.SaveChangesAsync();
        }
        using var changedStorage = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(changedStorage!.RootElement, secondVersion.Id).GetProperty("reason").GetString() ==
            "storage_unavailable", "A representation whose persisted storage identity changed is blocked");

        var sharedMovie = new Movie { Id = Guid.NewGuid(), Name = "Shared physical fixture", Path = mediaPath };
        items[sharedMovie.Id] = sharedMovie;
        sharedLibraryFolder.Items.Add(sharedMovie);
        var sharedEntry = new Entry
        {
            Id = Guid.NewGuid(), MediaType = "movie", TmdbId = 900004, Title = "Shared physical fixture",
            State = FileState.OnDisk, TargetLibraryId = sharedLibraryFolder.Id, JellyfinItemId = sharedMovie.Id
        };
        await using (var database = new ModDbContext(databasePath))
        {
            database.Entries.Add(sharedEntry);
            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = sharedEntry.Id, JellyfinItemId = sharedMovie.Id,
                TargetLibraryId = sharedLibraryFolder.Id, VersionGroupId = sharedMovie.Id,
                MediaPath = mediaPath, StorageIdentity = storage.Capture(mediaPath)
            });
            database.CompletionObservations.Add(new CompletionObservation
            {
                EntryId = sharedEntry.Id,
                TargetId = sharedEntry.Id,
                UserId = user.Id,
                JellyfinItemId = sharedMovie.Id,
                EvidenceAvailable = true,
                Played = false,
                ObservedAt = now
            });
            await database.SaveChangesAsync();
        }
        using var sharedPath = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
        Assert(FindRow(sharedPath!.RootElement, sharedMovie.Id).GetProperty("reason").GetString() ==
            "waiting_for_completion", "An ineligible cross-library reference to a shared file stays waiting");
        Assert(FindRow(sharedPath.RootElement, movie.Id).GetProperty("reason").GetString() ==
            "shared_path_not_all_eligible", "A due representation is blocked while another library reference is ineligible");
        Assert(sharedPath.RootElement.GetProperty("inspected").GetInt32() ==
            sharedPath.RootElement.GetProperty("due").GetInt32() +
            sharedPath.RootElement.GetProperty("blocked").GetInt32() +
            sharedPath.RootElement.GetProperty("scheduled").GetInt32() +
            sharedPath.RootElement.GetProperty("waiting").GetInt32() +
            sharedPath.RootElement.GetProperty("disabled").GetInt32(),
            "Preview summary accounts for every representation state");

        await VerifyReclamationAsync(api.Services, http, databasePath, libraryPath, storage, clock, settings,
            entry, movie, secondVersion, sharedEntry, sharedMovie, user.Id, libraryFolder.Id, items);
    }
    finally
    {
        await api.StopAsync();
    }
}

static JsonElement FindRow(JsonElement preview, Guid jellyfinItemId) =>
    preview.GetProperty("items").EnumerateArray()
        .Single(item => Guid.Parse(item.GetProperty("jellyfinItemId").GetString()!) == jellyfinItemId)
        .Clone();

static async Task VerifyReclamationAsync(
    IServiceProvider services,
    HttpClient http,
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    PluginConfiguration settings,
    Entry entry,
    Movie primaryMovie,
    Movie secondVersion,
    Entry sharedEntry,
    Movie sharedMovie,
    Guid userId,
    Guid libraryId,
    IDictionary<Guid, BaseItem> nativeItems)
{
    settings.TransmissionRpcUrl = settings.TransmissionRpcUrl.Replace("/unreachable", "/transmission/rpc",
        StringComparison.Ordinal);
    Guid secondBindingId;
    await using (var database = new ModDbContext(databasePath))
    {
        var binding = await database.EntryBindings.SingleAsync(candidate =>
            candidate.JellyfinItemId == secondVersion.Id);
        binding.StorageIdentity = storage.Capture(secondVersion.Path);
        secondBindingId = binding.Id;
        await database.SaveChangesAsync();
    }

    var secondSidecar = Path.ChangeExtension(secondVersion.Path, ".srt");
    await File.WriteAllTextAsync(secondSidecar, "sidecar survives");
    await using (var heldRun = await services.GetRequiredService<RetentionRunGate>().TryAcquireAsync(default))
    {
        Assert(heldRun is not null, "The retention run gate can be acquired for the overlap fixture");
        using var overlap = await http.PostAsync("/JellyfinMod/Retention/Run", null);
        Assert(overlap.StatusCode == HttpStatusCode.Conflict,
            "A concurrent manual retention invocation is rejected without overlapping");
    }

    using var runResponse = await http.PostAsync("/JellyfinMod/Retention/Run", null);
    var runBody = await runResponse.Content.ReadAsStringAsync();
    Assert(runResponse.IsSuccessStatusCode, "Admin manual retention run succeeds through HTTP: " + runBody);
    using var runJson = JsonDocument.Parse(runBody);
    Assert(runJson.RootElement.GetProperty("status").GetString() == "completed" &&
        runJson.RootElement.GetProperty("reclaimed").GetInt32() == 1 &&
        runJson.RootElement.GetProperty("logicalBytesUnlinked").GetInt64() == 2048 &&
        runJson.RootElement.GetProperty("physicalBytesReleased").GetInt64() == 0,
        "The bounded runner reports one physical hardlink action and separate logical/physical space");
    using var latestRun = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Runs/Latest");
    Assert(latestRun!.RootElement.GetProperty("id").GetGuid() == runJson.RootElement.GetProperty("id").GetGuid(),
        "The latest-run endpoint returns the durable manual run summary");
    RetentionOperation savedOperation;
    await using (var operationDatabase = new ModDbContext(databasePath))
    {
        savedOperation = await operationDatabase.RetentionOperations.SingleAsync(operation =>
            operation.BindingId == secondBindingId);
    }
    var source = Path.Combine(Path.GetDirectoryName(libraryPath)!, "downloads", "version2.mkv");
    Assert(savedOperation.State == "completed" && savedOperation.Reason == "reclaimed" &&
        savedOperation.LogicalBytes == 2048 && savedOperation.PhysicalBytesReleased == 0 &&
        !File.Exists(secondVersion.Path) && File.Exists(source) && File.Exists(secondSidecar),
        "Exact-file reclamation preserves its source hardlink and sidecar and claims zero physical bytes");
    await using (var database = new ModDbContext(databasePath))
    {
        var saved = await database.Entries.SingleAsync(candidate => candidate.Id == entry.Id);
        Assert(saved.State == FileState.OnDisk && saved.JellyfinItemId == primaryMovie.Id &&
            await database.EntryBindings.CountAsync(candidate => candidate.EntryId == entry.Id) == 2 &&
            await database.History.CountAsync(history => history.Id == savedOperation.Id) == 1 &&
            (await database.RetentionOperations.SingleAsync(operation => operation.Id == savedOperation.Id)).State ==
                "completed",
            "A reclaimed alternate version leaves surviving bindings playable and records one durable history event");
    }

    Guid primaryBindingId;
    var primarySidecar = Path.ChangeExtension(primaryMovie.Path, ".nfo");
    await File.WriteAllTextAsync(primarySidecar, "shared sidecar survives");
    await using (var database = new ModDbContext(databasePath))
    {
        primaryBindingId = await database.EntryBindings.Where(candidate => candidate.JellyfinItemId == primaryMovie.Id)
            .Select(candidate => candidate.Id).SingleAsync();
        var completion = await database.CompletionObservations.SingleAsync(candidate =>
            candidate.TargetId == sharedEntry.Id);
        completion.Played = true;
        completion.CompletedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3);
        completion.LastPlayedAt = completion.CompletedAt;
        completion.ObservedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync();
    }

    _ = await services.GetRequiredService<RetentionPreviewService>().PreviewAsync(default);
    clock.Advance(TimeSpan.FromDays(2));
    var sharedPreview = await services.GetRequiredService<RetentionPreviewService>().PreviewAsync(default);
    var sharedRows = sharedPreview.Items.Where(candidate =>
        candidate.JellyfinItemId == primaryMovie.Id || candidate.JellyfinItemId == sharedMovie.Id).ToArray();
    Assert(sharedRows.All(candidate => candidate.State == "due"),
        "Every exact-path binding becomes due after the fresh grace period: " +
        string.Join(", ", sharedRows.Select(candidate => candidate.JellyfinItemId + "=" +
            candidate.State + "/" + candidate.Reason + "/" + candidate.Deadline)));

    var executor = services.GetRequiredService<RetentionExecutor>();
    var sharedResult = await executor.ReclaimAsync(primaryBindingId, default);
    Assert(sharedResult.State == "completed" && !File.Exists(primaryMovie.Path) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(libraryPath)!, "downloads", "fixture.mkv")) &&
        File.Exists(primarySidecar),
        $"One exact unlink safely resolves every eligible catalog binding to the same canonical path: " +
        $"state={sharedResult.State} reason={sharedResult.Reason} media={File.Exists(primaryMovie.Path)} " +
        $"source={File.Exists(Path.Combine(Path.GetDirectoryName(libraryPath)!, "downloads", "fixture.mkv"))} " +
        $"sidecar={File.Exists(primarySidecar)}");
    await using (var database = new ModDbContext(databasePath))
    {
        var selectedOperation = await database.RetentionOperations.SingleAsync(operation =>
            operation.Id == sharedResult.OperationId);
        var groupedOperations = await database.RetentionOperations.Where(operation =>
            operation.ActionId == selectedOperation.ActionId).ToArrayAsync();
        var savedShared = await database.Entries.SingleAsync(candidate => candidate.Id == sharedEntry.Id);
        Assert(groupedOperations.Length == 2 && groupedOperations.All(operation => operation.State == "completed") &&
            groupedOperations.Select(operation => operation.BindingId).Distinct().Count() == 2 &&
            await database.History.CountAsync(history => groupedOperations.Select(operation => operation.Id)
                .Contains(history.Id)) == 2 && savedShared.State == FileState.Reclaimed &&
            !savedShared.JellyfinItemId.HasValue && !nativeItems.ContainsKey(sharedMovie.Id),
            "Shared-path reclamation persists one physical action with per-entry bindings and history");
    }

    await VerifyReacquiredMediaStartsFreshAsync(services, databasePath, clock, sharedEntry, userId, libraryId, nativeItems);

    var before = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900101, "before-unlink", RetentionOperationStatesForTest.Prepared, keep: false);
    var beforeResult = (await executor.RecoverAsync(default)).Single(result => result.OperationId == before.OperationId);
    Assert(beforeResult.State == "completed" && !File.Exists(before.MediaPath) && File.Exists(before.SidecarPath),
        "Restart recovery revalidates and completes an operation interrupted before unlink");

    var after = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900102, "after-unlink", RetentionOperationStatesForTest.Unlinked, keep: false);
    File.Delete(after.MediaPath);
    var afterResult = (await executor.RecoverAsync(default)).Single(result => result.OperationId == after.OperationId);
    Assert(afterResult.State == "completed" && File.Exists(after.SidecarPath),
        "Restart recovery completes catalog state when the exact file was already unlinked");

    Assert((await executor.RecoverAsync(default)).Count == 0,
        "Completed recovery is idempotent and leaves no interrupted operation");
    await using (var database = new ModDbContext(databasePath))
    {
        Assert(await database.History.CountAsync(history =>
                history.Id == before.OperationId || history.Id == after.OperationId) == 2,
            "Repeated recovery writes exactly one reclaimed history event per operation");
    }

    var cancellationFirst = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900106, "cancel-first", RetentionOperationStatesForTest.Prepared, keep: false);
    var cancellationSecond = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900107, "cancel-second", RetentionOperationStatesForTest.Prepared, keep: false);
    using (var cancellation = new CancellationTokenSource())
    {
        var cancelled = false;
        try
        {
            await services.GetRequiredService<RetentionRunner>().RunAsync(
                new InlineProgress(value =>
                {
                    if (value > 0) cancellation.Cancel();
                }), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled && File.Exists(cancellationFirst.MediaPath) != File.Exists(cancellationSecond.MediaPath),
            "Cancellation is honored between exact physical operations and leaves the next file untouched");
    }
    await using (var database = new ModDbContext(databasePath))
    {
        var cancelledRun = await database.RetentionRuns.OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id).FirstAsync();
        Assert(cancelledRun.Status == "cancelled" && cancelledRun.Interrupted == 1,
            "The interrupted batch persists its cancellation and completed recovery count");
    }
    var cancellationRecovery = await executor.RecoverAsync(default);
    Assert(cancellationRecovery.Count(result => result.OperationId == cancellationFirst.OperationId ||
        result.OperationId == cancellationSecond.OperationId) == 1,
        "Restart recovery finishes only the physical operation left by cancellation");

    var replaced = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900103, "replaced", RetentionOperationStatesForTest.Prepared, keep: false);
    var replacement = replaced.MediaPath + ".replacement";
    await File.WriteAllBytesAsync(replacement, new byte[1536]);
    File.Delete(replaced.MediaPath);
    File.Move(replacement, replaced.MediaPath);
    var replacedResult = (await executor.RecoverAsync(default)).Single(result => result.OperationId == replaced.OperationId);
    Assert(replacedResult.State == "failed" && replacedResult.Reason == "media_identity_changed" &&
        File.Exists(replaced.MediaPath),
        "Recovery never unlinks a path whose inode changed after intent was persisted");

    var kept = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900104, "kept", RetentionOperationStatesForTest.Prepared, keep: true);
    var keptResult = (await executor.RecoverAsync(default)).Single(result => result.OperationId == kept.OperationId);
    Assert(keptResult.State == "blocked" && keptResult.Reason == "kept" && File.Exists(kept.MediaPath),
        "Keep received before unlink wins during recovery revalidation");

    var disabled = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900105, "disabled", RetentionOperationStatesForTest.Prepared, keep: false);
    settings.RetentionEnabled = false;
    using var disabledRun = await http.PostAsync("/JellyfinMod/Retention/Run", null);
    using var disabledRunJson = JsonDocument.Parse(await disabledRun.Content.ReadAsStringAsync());
    Assert(disabledRun.IsSuccessStatusCode &&
        disabledRunJson.RootElement.GetProperty("status").GetString() == "disabled" &&
        File.Exists(disabled.MediaPath),
        "A disabled manual run records its status and changes no media");

    var disabledResult = (await executor.RecoverAsync(default)).Single(result => result.OperationId == disabled.OperationId);
    Assert(disabledResult.State == "blocked" && disabledResult.Reason == "retention_disabled" &&
        File.Exists(disabled.MediaPath), "Disabling retention before unlink wins during recovery revalidation");
    Console.WriteLine("PASS: recoverable exact-file reclamation, history and physical-space accounting");
}

static async Task<RecoveryFixture> SeedRecoveryFixtureAsync(
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    IDictionary<Guid, BaseItem> nativeItems,
    Guid userId,
    Guid libraryId,
    int tmdbId,
    string name,
    string state,
    bool keep)
{
    var mediaPath = Path.Combine(libraryPath, name + ".mkv");
    var sidecarPath = Path.Combine(libraryPath, name + ".nfo");
    await File.WriteAllBytesAsync(mediaPath, new byte[1536]);
    await File.WriteAllTextAsync(sidecarPath, "sidecar survives");
    var inspector = new UnixFileInspector();
    Assert(inspector.TryInspect(mediaPath, out var observed), "Recovery fixture has Linux inode evidence");
    var movie = new Movie { Id = Guid.NewGuid(), Name = name, Path = mediaPath };
    nativeItems[movie.Id] = movie;
    var entry = new Entry
    {
        Id = Guid.NewGuid(), MediaType = "movie", TmdbId = tmdbId, Title = name,
        State = FileState.OnDisk, TargetLibraryId = libraryId,
        JellyfinItemId = movie.Id, RetentionPolicy = keep ? RetentionPolicy.Never : RetentionPolicy.Inherit
    };
    var binding = new EntryBinding
    {
        Id = Guid.NewGuid(), EntryId = entry.Id, JellyfinItemId = movie.Id,
        TargetLibraryId = entry.TargetLibraryId!.Value, VersionGroupId = movie.Id, MediaPath = mediaPath,
        StorageIdentity = storage.Capture(mediaPath)
    };
    var operationId = Guid.NewGuid();
    var operation = new RetentionOperation
    {
        Id = operationId, ActionId = operationId, BindingId = binding.Id, EntryId = entry.Id, JellyfinItemId = movie.Id,
        TargetLibraryId = binding.TargetLibraryId, PolicyVersion = 1, MediaPath = observed.CanonicalPath,
        StorageIdentity = binding.StorageIdentity!, PhysicalIdentity = observed.PhysicalIdentity,
        LogicalBytes = checked((long)observed.LogicalBytes), HardlinkCountBefore = observed.HardlinkCount,
        State = state, Reason = state == RetentionOperationStatesForTest.Unlinked ? "unlinked" : "eligible",
        PreparedAt = clock.GetUtcNow().UtcDateTime.AddMinutes(-1),
        UnlinkedAt = state == RetentionOperationStatesForTest.Unlinked
            ? clock.GetUtcNow().UtcDateTime.AddSeconds(-1)
            : null
    };
    await using var database = new ModDbContext(databasePath);
    database.Entries.Add(entry);
    database.EntryBindings.Add(binding);
    database.CompletionObservations.Add(new CompletionObservation
    {
        EntryId = entry.Id, TargetId = entry.Id, UserId = userId,
        JellyfinItemId = movie.Id, EvidenceAvailable = true, Played = true,
        CompletedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3),
        LastPlayedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3),
        ObservedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3)
    });
    // A prepared operation implies the target was already tracked and due (P3.T7 floors grace at the
    // target's first evaluation), so record that earlier evaluation under the operation's policy.
    database.RetentionEvaluations.Add(new RetentionEvaluation
    {
        EntryId = entry.Id, TargetId = entry.Id, State = "disabled", Reason = "retention_disabled",
        PolicyVersion = operation.PolicyVersion, EvaluatedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3),
        BaselineAt = clock.GetUtcNow().UtcDateTime.AddDays(-3)
    });
    database.RetentionOperations.Add(operation);
    await database.SaveChangesAsync();
    return new(operation.Id, mediaPath, sidecarPath);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// P3.T7: a fully reclaimed target must not hand its old schedule or completion to re-acquired media.
static async Task VerifyReacquiredMediaStartsFreshAsync(
    IServiceProvider services,
    string databasePath,
    FixedTimeProvider clock,
    Entry sharedEntry,
    Guid userId,
    Guid libraryId,
    IDictionary<Guid, BaseItem> nativeItems)
{
    DateTime resetAt;
    await using (var database = new ModDbContext(databasePath))
    {
        var reset = await database.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == sharedEntry.Id);
        Assert(reset.State == "waiting" && reset.Reason == "representation_reset" && reset.Deadline is null &&
            reset.RequiresFreshCompletion && reset.BaselineAt == clock.GetUtcNow().UtcDateTime,
            "Reclaiming the last representation resets the target's schedule and requires a fresh completion");
        resetAt = reset.BaselineAt;
    }

    // Re-acquire: a new native item is bound and Jellyfin reattaches the old played state to it.
    clock.Advance(TimeSpan.FromHours(6));
    var reacquired = new Movie { Id = Guid.NewGuid(), Name = "Reacquired", Path = "/fixture/reacquired.mkv" };
    nativeItems[reacquired.Id] = reacquired;
    var oldCompletion = resetAt.AddDays(-5);
    await using (var database = new ModDbContext(databasePath))
    {
        var entry = await database.Entries.SingleAsync(candidate => candidate.Id == sharedEntry.Id);
        entry.State = FileState.OnDisk;
        entry.JellyfinItemId = reacquired.Id;
        database.EntryBindings.Add(new EntryBinding
        {
            EntryId = sharedEntry.Id, JellyfinItemId = reacquired.Id, TargetLibraryId = libraryId,
            VersionGroupId = reacquired.Id, MediaPath = reacquired.Path
        });
        var observation = await database.CompletionObservations.SingleAsync(candidate =>
            candidate.TargetId == sharedEntry.Id && candidate.UserId == userId);
        observation.JellyfinItemId = reacquired.Id;
        observation.EvidenceAvailable = true;
        observation.Played = true;
        observation.PlaybackPositionTicks = 0;
        observation.CompletedAt = oldCompletion;
        observation.LastPlayedAt = oldCompletion;
        observation.ObservedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync();
    }

    await services.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var stale = await database.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == sharedEntry.Id);
        Assert(stale.State == "waiting" && stale.Reason == "waiting_for_completion" && stale.Deadline is null,
            $"Re-acquired media with only reattached old completion is not scheduled: {stale.State}/{stale.Reason}/{stale.Deadline}");
    }

    // The user genuinely finishes it again after it came back.
    clock.Advance(TimeSpan.FromHours(1));
    var rewatched = clock.GetUtcNow().UtcDateTime;
    await using (var database = new ModDbContext(databasePath))
    {
        var observation = await database.CompletionObservations.SingleAsync(candidate =>
            candidate.TargetId == sharedEntry.Id && candidate.UserId == userId);
        observation.LastPlayedAt = rewatched;
        observation.ObservedAt = rewatched;
        await database.SaveChangesAsync();
    }

    await services.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var fresh = await database.RetentionEvaluations.SingleAsync(evaluation => evaluation.TargetId == sharedEntry.Id);
        Assert(fresh.State == "scheduled" && fresh.CompletionBasisAt == rewatched && fresh.Deadline >= rewatched.AddDays(1) &&
            fresh.Deadline > clock.GetUtcNow().UtcDateTime,
            $"A completion after re-acquisition schedules a full window from the rewatch: {fresh.State}/{fresh.CompletionBasisAt}/{fresh.Deadline}");

        // Leave the fixture unbound again so later scenarios see the same catalog state as before.
        database.EntryBindings.RemoveRange(database.EntryBindings.Where(binding => binding.JellyfinItemId == reacquired.Id));
        var entry = await database.Entries.SingleAsync(candidate => candidate.Id == sharedEntry.Id);
        entry.State = FileState.Reclaimed;
        entry.JellyfinItemId = null;
        fresh.State = "waiting";
        fresh.Reason = "representation_reset";
        fresh.CompletionBasisAt = null;
        fresh.EligibleAt = null;
        fresh.Deadline = null;
        fresh.BaselineAt = clock.GetUtcNow().UtcDateTime;
        fresh.RequiresFreshCompletion = true;
        await database.SaveChangesAsync();
    }

    nativeItems.Remove(reacquired.Id);

    // A title bound only now, whose native last-played date predates the retention baseline, gets a
    // full window from its first evaluation instead of being due at once.
    var newlyBound = new Movie { Id = Guid.NewGuid(), Name = "Newly bound", Path = "/fixture/newly-bound.mkv" };
    var newEntryId = Guid.NewGuid();
    await using (var database = new ModDbContext(databasePath))
    {
        var enabledAt = (await database.RetentionPolicySnapshots.SingleAsync()).EnabledAt!.Value;
        database.Entries.Add(new Entry
        {
            Id = newEntryId, MediaType = "movie", TmdbId = 990001, Title = "Newly bound", State = FileState.OnDisk,
            TargetLibraryId = libraryId, JellyfinItemId = newlyBound.Id
        });
        database.EntryBindings.Add(new EntryBinding
        {
            EntryId = newEntryId, JellyfinItemId = newlyBound.Id, TargetLibraryId = libraryId,
            VersionGroupId = newlyBound.Id, MediaPath = newlyBound.Path
        });
        database.CompletionObservations.Add(new CompletionObservation
        {
            EntryId = newEntryId, TargetId = newEntryId, UserId = userId, JellyfinItemId = newlyBound.Id,
            EvidenceAvailable = true, Played = true, CompletedAt = enabledAt.AddDays(-30),
            LastPlayedAt = enabledAt.AddDays(-30), ObservedAt = clock.GetUtcNow().UtcDateTime, SourceReason = "Repair"
        });
        await database.SaveChangesAsync();
    }

    var firstEvaluatedAt = clock.GetUtcNow().UtcDateTime;
    await services.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var evaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == newEntryId);
        Assert(evaluation.State == "scheduled" && evaluation.BaselineAt == firstEvaluatedAt &&
            evaluation.EligibleAt >= firstEvaluatedAt && evaluation.Deadline >= firstEvaluatedAt.AddDays(1),
            $"A newly bound target's grace starts no earlier than its first evaluation: {evaluation.EligibleAt}/{evaluation.Deadline}");
        database.Entries.Remove(await database.Entries.SingleAsync(candidate => candidate.Id == newEntryId));
        await database.SaveChangesAsync();
    }
}

file sealed record RecoveryFixture(Guid OperationId, string MediaPath, string SidecarPath);

file sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

file static class RetentionOperationStatesForTest
{
    public const string Prepared = "prepared";
    public const string Unlinked = "unlinked";
}

file sealed class PlainHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
}

internal sealed class PreviewAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Preview-User"], out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("Jellyfin-UserId", userId.ToString()) };
        if (Request.Headers["X-Preview-Role"] == "admin") claims.Add(new(ClaimTypes.Role, "admin"));
        var identity = new ClaimsIdentity(claims, "Preview");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), "Preview")));
    }
}

internal sealed class FixedTimeProvider(DateTime value) : TimeProvider
{
    private DateTime value = value;

    public override DateTimeOffset GetUtcNow() => new(value);

    public void Advance(TimeSpan duration) => value = value.Add(duration);
}

internal enum SeedScenario
{
    Ratio,
    IdlePending,
    IdleSatisfied,
    Unbounded
}

internal sealed class FixtureRoot(Guid userId, IReadOnlyList<BaseItem> libraries) : Folder
{
    public override IReadOnlyList<BaseItem> GetChildren(
        User user,
        bool includeLinkedChildren,
        InternalItemsQuery? query = null) => user.Id == userId ? libraries : [];
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
    private Func<System.Reflection.MethodInfo, object?[]?, object?> callback = null!;

    public static T Create(Func<System.Reflection.MethodInfo, object?[]?, object?> callback)
    {
        var instance = Create<T, Stub<T>>();
        ((Stub<T>)(object)instance).callback = callback;
        return instance;
    }

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? arguments) =>
        callback(targetMethod!, arguments);
}

internal static partial class NativeTestMethods
{
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string existingPath, string newPath);
}
