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
using MediaBrowser.Model.Tasks;
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
// P3.T17: an optional second, still-downloading multi-file torrent using the incomplete dir.
var multiFile = false;
var incomplete = Path.Combine(folder, "incomplete");
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
                ["idle-seeding-limit"] = 1440,
                ["incomplete-dir-enabled"] = multiFile,
                ["incomplete-dir"] = incomplete,
                ["rename-partial-files"] = true
            }
        });
        return;
    }

    await context.Response.WriteAsJsonAsync(new
    {
        result = "success",
        arguments = new
        {
            torrents = new object[]
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
            }.Concat(multiFile
                ? new object[]
                {
                    new
                    {
                        id = 2,
                        hashString = "multi",
                        downloadDir = downloads,
                        files = new[]
                        {
                            new { name = "multi/done.mkv", length = 1024UL, bytesCompleted = 1024UL },
                            new { name = "multi/partial.mkv", length = 2048UL, bytesCompleted = 512UL },
                            new { name = "multi/unwanted.mkv", length = 4096UL, bytesCompleted = 0UL }
                        },
                        fileStats = new[] { new { wanted = true }, new { wanted = true }, new { wanted = false } },
                        leftUntilDone = 1536UL,
                        percentDone = 0.25,
                        status = 4,
                        uploadRatio = 3.0,
                        secondsSeeding = 0,
                        seedRatioMode = 1,
                        seedRatioLimit = 1.0,
                        seedIdleMode = 2,
                        seedIdleLimit = 30,
                        etaIdle = -1,
                        isFinished = false
                    }
                }
                : []).ToArray()
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

    // P3.T17: normal downloading must not make the index incomplete. The finished file of an unfinished
    // torrent stays protected even though its ratio already meets the goal.
    Directory.CreateDirectory(Path.Combine(downloads, "multi"));
    Directory.CreateDirectory(Path.Combine(incomplete, "multi"));
    var done = Path.Combine(downloads, "multi", "done.mkv");
    await File.WriteAllBytesAsync(done, new byte[1024]);
    var partial = Path.Combine(incomplete, "multi", "partial.mkv.part");
    await File.WriteAllBytesAsync(partial, new byte[512]);
    multiFile = true;
    var downloading = await client.GetSnapshotAsync(default);
    Assert(inspector.TryInspect(done, out var doneFile) && downloading.CompleteFileIndex && downloading.UnresolvedFiles == 0 &&
        !downloading.FilesByPhysicalIdentity[doneFile.PhysicalIdentity].Single().FileComplete,
        "A .part file in the incomplete dir and an unwanted missing file keep the index complete, and a finished file of an unfinished torrent is not complete");
    File.Delete(partial);
    var lost = await client.GetSnapshotAsync(default);
    Assert(!lost.CompleteFileIndex && lost.UnresolvedFiles == 1,
        "A wanted, partly downloaded file that cannot be found makes the index incomplete with a count");
    multiFile = false;

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
    // Real Jellyfin never returns null user data; items default to the fixtures' played state, and
    // liveStates overrides individual items for the P3.T8 live-revalidation scenarios.
    var liveStates = new Dictionary<Guid, UserItemData>();
    var userData = Stub<IUserDataManager>.Create((method, arguments) =>
        method.Name != "GetUserData" || arguments?[1] is not BaseItem item ? null
        : liveStates.TryGetValue(item.Id, out var live) ? live
        : item.Id == series.Id ? new UserItemData { Key = "favorite-series", IsFavorite = true }
        : new UserItemData { Key = item.Id.ToString("N"), Played = true, LastPlayedDate = now.AddDays(-3) });
    var sessionRows = new List<SessionInfo>();
    var sessionManager = Stub<ISessionManager>.Create((method, _) => method.Name == "get_Sessions"
        ? sessionRows.ToArray()
        : null);

    // POST /Retention/Run queues the native task (P3.T9); this stands in for Jellyfin's task manager
    // and runs the real batch on its own uncancellable task.
    IServiceProvider? hostServices = null;
    var taskManager = Stub<ITaskManager>.Create((method, arguments) =>
    {
        if (method.Name == "QueueScheduledTask")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // The real scheduled-task entry point, as Jellyfin's task manager invokes it (P3.T18).
                    await new RetentionReclamationTask(hostServices!.GetRequiredService<IServiceScopeFactory>())
                        .ExecuteAsync(new Progress<double>(), CancellationToken.None);
                }
                catch (RetentionRunAlreadyActiveException)
                {
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("Queued retention run failed: " + error);
                }
            });
        }

        return null;
    });

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
    apiBuilder.Services.AddSingleton(taskManager);
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
    // The retention runner re-baselines storage identity through reconciliation before its preview (P2.R6).
    apiBuilder.Services.AddTransient<ReconciliationService>();
    apiBuilder.Services.AddTransient<JellyfinNativeTitleSource>();
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
    apiBuilder.Services.AddTransient<RetentionCompletionService>();
    apiBuilder.Services.AddTransient<RetentionLiveCheck>();
    apiBuilder.Services.AddTransient<RetentionExecutor>();
    apiBuilder.Services.AddTransient<RetentionRunner>();
    apiBuilder.Services.AddTransient(provider => new TmdbClient(
        provider.GetRequiredService<IHttpClientFactory>(), () => settings,
        NullLogger<TmdbClient>.Instance));
    await using var api = apiBuilder.Build();
    hostServices = api.Services;
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
        using var http = new HttpClient { BaseAddress = new Uri(apiAddress), Timeout = TimeSpan.FromSeconds(60) };
        Assert((await http.GetAsync("/JellyfinMod/Retention/Preview")).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous retention preview is rejected by real authentication middleware");
        http.DefaultRequestHeaders.Add("X-Preview-User", user.Id.ToString());
        Assert((await http.GetAsync("/JellyfinMod/Retention/Preview")).StatusCode == HttpStatusCode.Forbidden,
            "Ordinary users cannot read physical retention paths or activity diagnostics");
        Assert((await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Keep", null)).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot write Keep through the real authorization policy");
        Assert((await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Episodes/{episode.Id}/Keep", null)).StatusCode ==
            HttpStatusCode.Forbidden, "Ordinary users cannot keep an episode through the real authorization policy (P10.E2)");
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

        // A read-only mount is the case the executor already refused; the preview must say so too, or an
        // administrator reads "due" for media that can never be deleted (P3.T18).
        var readOnlyDirectory = Path.Combine(libraryPath, "readonly");
        Directory.CreateDirectory(readOnlyDirectory);
        var readOnlyMedia = Path.Combine(readOnlyDirectory, "version2.mkv");
        File.Move(secondMedia, readOnlyMedia);
        secondVersion.Path = readOnlyMedia;
        await using (var repoint = new ModDbContext(databasePath))
        {
            var binding = await repoint.EntryBindings.SingleAsync(candidate =>
                candidate.JellyfinItemId == secondVersion.Id);
            binding.MediaPath = readOnlyMedia;
            await repoint.SaveChangesAsync();
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(readOnlyDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        // root ignores directory permissions, so only an unprivileged run can observe this. Jellyfin runs as an
        // ordinary user, and the Pi suites run unprivileged for the same reason.
        var writableAnyway = true;
        try
        {
            var probe = Path.Combine(readOnlyDirectory, "probe.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            writableAnyway = false;
        }

        try
        {
            if (writableAnyway)
            {
                Console.WriteLine(
                    "SKIP: read-only preview reason needs an unprivileged run (this process can write anyway)");
            }
            else
            {
                using var readOnlyPreview = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview");
                var readOnlyRow = FindRow(readOnlyPreview!.RootElement, secondVersion.Id);
                Assert(readOnlyRow.GetProperty("state").GetString() == "blocked" &&
                    readOnlyRow.GetProperty("reason").GetString() == "media_not_writable",
                    "Media in a directory this process cannot write is reported as blocked, not due: " +
                    readOnlyRow.GetRawText());
                Assert(File.Exists(readOnlyMedia), "The read-only representation is still on disk after the preview");
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(readOnlyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            File.Move(readOnlyMedia, secondMedia);
            secondVersion.Path = secondMedia;
            await using var restore = new ModDbContext(databasePath);
            var binding = await restore.EntryBindings.SingleAsync(candidate =>
                candidate.JellyfinItemId == secondVersion.Id);
            binding.MediaPath = secondMedia;
            await restore.SaveChangesAsync();
            Directory.Delete(readOnlyDirectory, true);
        }
        Assert(episodeRow.GetProperty("reason").GetString() == "favorite_series",
            "A favorite series protects its otherwise eligible episode representation");

        // P10.E2: an administrator keeps one episode; repeating it is idempotent and writes one history event that
        // names the episode (P10.E3).
        using (var anonymous = new HttpClient { BaseAddress = http.BaseAddress })
            Assert((await anonymous.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Episodes/{episode.Id}/Keep", null))
                .StatusCode == HttpStatusCode.Unauthorized, "Anonymous episode Keep is rejected");
        using var episodeKept = await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Episodes/{episode.Id}/Keep", null);
        var episodeKeptBody = await episodeKept.Content.ReadAsStringAsync();
        Assert(episodeKept.IsSuccessStatusCode && episodeKeptBody.Contains("\"retentionPolicy\":\"never\"") &&
            episodeKeptBody.Contains("\"reason\":\"kept\""),
            "An administrator can keep one episode: " + episodeKept.StatusCode + " " + episodeKeptBody);
        Assert((await http.PostAsync($"/JellyfinMod/Entries/{seriesEntry.Id}/Episodes/{episode.Id}/Keep", null)).IsSuccessStatusCode,
            "Keeping an already-kept episode is idempotent");
        using (var episodeDetail = await http.GetFromJsonAsync<JsonDocument>($"/JellyfinMod/Entries/{seriesEntry.Id}"))
        {
            var keptEpisode = episodeDetail!.RootElement.GetProperty("episodes")[0];
            var keptEvents = episodeDetail.RootElement.GetProperty("history").EnumerateArray()
                .Where(item => item.GetProperty("eventType").GetString() == "episode_kept").ToArray();
            Assert(keptEpisode.GetProperty("retentionPolicy").GetString() == "never" &&
                keptEpisode.GetProperty("retention").GetProperty("reason").GetString() == "kept" &&
                episodeDetail.RootElement.GetProperty("entry").GetProperty("retentionPolicy").GetString() == "inherit" &&
                keptEvents.Length == 1 && keptEvents[0].GetProperty("episodeId").GetGuid() == episode.Id,
                "Episode Keep protects only that episode, is recorded once and names the episode: " +
                episodeDetail.RootElement.GetRawText());
        }
        using (var keptPreview = await http.GetFromJsonAsync<JsonDocument>("/JellyfinMod/Retention/Preview"))
            Assert(FindRow(keptPreview!.RootElement, nativeEpisode.Id).GetProperty("state").GetString() == "blocked",
                "A kept episode's representation is never due");

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
        await VerifyLiveStateRevalidationAsync(api.Services, databasePath, libraryPath, storage, clock, settings, items,
            user.Id, libraryFolder.Id, liveStates, sessionRows, sessionManager);
        await VerifyHonestRecoveryAsync(api.Services, databasePath, libraryPath, storage, clock, items,
            user.Id, libraryFolder.Id);
        await VerifyRemoveGuardsAsync(api.Services, http, databasePath, libraryPath, storage, clock, items,
            user.Id, libraryFolder.Id, entry.Id);
        await VerifyPinnedExecutionAsync(api.Services, databasePath, libraryPath, storage, clock, settings, items,
            user.Id, libraryFolder.Id);
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
        var rejected = false;
        try
        {
            await services.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), default);
        }
        catch (RetentionRunAlreadyActiveException)
        {
            rejected = true;
        }

        Assert(rejected, "A concurrent retention batch is rejected without overlapping");
    }

    // The fixture clock is frozen; each run needs a later StartedAt for Runs/Latest to find it.
    clock.Advance(TimeSpan.FromSeconds(1));
    var previousRunId = await LatestRunIdAsync(http);
    using var runResponse = await http.PostAsync("/JellyfinMod/Retention/Run", null);
    var runBody = await runResponse.Content.ReadAsStringAsync();
    Assert(runResponse.StatusCode == HttpStatusCode.Accepted && runBody.Contains("\"queued\"", StringComparison.Ordinal),
        "Admin manual retention run is queued on the native task through HTTP: " + runBody);
    using var runJson = await WaitForRunAsync(http, previousRunId);
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
    clock.Advance(TimeSpan.FromSeconds(1));
    var beforeDisabledRunId = await LatestRunIdAsync(http);
    using var disabledRun = await http.PostAsync("/JellyfinMod/Retention/Run", null);
    using var disabledRunJson = await WaitForRunAsync(http, beforeDisabledRunId);
    Assert(disabledRun.StatusCode == HttpStatusCode.Accepted &&
        disabledRunJson.RootElement.GetProperty("status").GetString() == "disabled" &&
        File.Exists(disabled.MediaPath),
        "A disabled manual run records its status and changes no media");

    // The disabled batch still recovers open operations first; revalidation blocks the prepared one.
    await using (var database = new ModDbContext(databasePath))
    {
        var disabledOperation = await database.RetentionOperations.SingleAsync(operation => operation.Id == disabled.OperationId);
        Assert(disabledOperation.State == "blocked" && disabledOperation.Reason == "retention_disabled" &&
            File.Exists(disabled.MediaPath), "Disabling retention before unlink wins during recovery revalidation");
    }
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

// P3.T10: removing an entry can never race an unlink, erase the reclaim audit, or drop Keep.
static async Task VerifyRemoveGuardsAsync(
    IServiceProvider services,
    HttpClient http,
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    IDictionary<Guid, BaseItem> nativeItems,
    Guid userId,
    Guid libraryId,
    Guid boundEntryId)
{
    // Still bound to native media: reconciliation would recreate it without its settings.
    using (var bound = await http.DeleteAsync($"/JellyfinMod/Entries/{boundEntryId}"))
        Assert(bound.StatusCode == HttpStatusCode.Conflict,
            $"Removing an entry whose media is still bound is refused: {bound.StatusCode}");

    // An open retention operation on the same entry is reported before the binding check.
    var openOperation = new RetentionOperation
    {
        ActionId = Guid.NewGuid(), BindingId = Guid.NewGuid(), EntryId = boundEntryId, JellyfinItemId = Guid.NewGuid(),
        TargetLibraryId = libraryId, PolicyVersion = 1, MediaPath = "/fixture/t10-open.mkv", StorageIdentity = "fixture",
        PhysicalIdentity = "fixture", LogicalBytes = 1, HardlinkCountBefore = 1,
        State = RetentionOperationStatesForTest.Prepared, Reason = "eligible", PreparedAt = clock.GetUtcNow().UtcDateTime
    };
    await using (var database = new ModDbContext(databasePath))
    {
        database.RetentionOperations.Add(openOperation);
        await database.SaveChangesAsync();
    }

    using (var inProgress = await http.DeleteAsync($"/JellyfinMod/Entries/{boundEntryId}"))
    {
        var body = await inProgress.Content.ReadAsStringAsync();
        Assert(inProgress.StatusCode == HttpStatusCode.Conflict && body.Contains("in progress", StringComparison.Ordinal),
            $"Removing an entry with an open retention operation is refused: {inProgress.StatusCode} {body}");
    }

    await using (var database = new ModDbContext(databasePath))
    {
        database.RetentionOperations.Remove(await database.RetentionOperations.SingleAsync(operation => operation.Id == openOperation.Id));
        await database.SaveChangesAsync();
    }

    // Fully reclaimed: removal is allowed and the reclaim audit survives with a detached entry.
    var reclaimed = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900402, "t10-reclaimed", RetentionOperationStatesForTest.Unlinked, keep: false);
    File.Delete(reclaimed.MediaPath);
    var result = (await services.GetRequiredService<RetentionExecutor>().RecoverAsync(default))
        .Single(candidate => candidate.OperationId == reclaimed.OperationId);
    Guid reclaimedEntryId;
    await using (var database = new ModDbContext(databasePath))
    {
        reclaimedEntryId = (await database.RetentionOperations.SingleAsync(operation => operation.Id == reclaimed.OperationId)).EntryId!.Value;
        // A real entry always carries its TMDB snapshot, which is what file-less visibility is checked against.
        (await database.Entries.SingleAsync(entry => entry.Id == reclaimedEntryId)).MetadataJson = JsonSerializer.Serialize(
            new TmdbMetadata("movie", 900402, "t10-reclaimed", null, null, null, null, null, null, false, null, null, [], [], []));
        await database.SaveChangesAsync();
    }
    using (var removed = await http.DeleteAsync($"/JellyfinMod/Entries/{reclaimedEntryId}"))
        Assert(result.State == "completed" && removed.StatusCode == HttpStatusCode.NoContent,
            $"A fully reclaimed entry can be removed: {result.State} {removed.StatusCode}");
    await using (var database = new ModDbContext(databasePath))
    {
        var audit = await database.RetentionOperations.SingleAsync(operation => operation.Id == reclaimed.OperationId);
        Assert(audit.EntryId is null && audit.State == "completed" && audit.LogicalBytes > 0 &&
            !await database.Entries.AnyAsync(entry => entry.Id == reclaimedEntryId),
            "Removing an entry detaches its reclaim operations instead of cascading the audit away");

    }

    File.Delete(reclaimed.SidecarPath);
}

// P3.T9: the runner recovers interrupted work first and records only what it can prove.
static async Task VerifyHonestRecoveryAsync(
    IServiceProvider services,
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    IDictionary<Guid, BaseItem> nativeItems,
    Guid userId,
    Guid libraryId)
{
    Guid deadRunId;
    await using (var database = new ModDbContext(databasePath))
    {
        var dead = new RetentionRun { StartedAt = clock.GetUtcNow().UtcDateTime.AddHours(-1) };
        database.RetentionRuns.Add(dead);
        await database.SaveChangesAsync();
        deadRunId = dead.Id;
    }

    // Unlinked by this plugin before a crash: the preview can no longer call it due, but the run finishes it.
    var unlinked = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900301, "t9-unlinked", RetentionOperationStatesForTest.Unlinked, keep: false);
    File.Delete(unlinked.MediaPath);

    // Prepared, then removed by something else: not a reclamation.
    var vanished = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900302, "t9-vanished", RetentionOperationStatesForTest.Prepared, keep: false);
    File.Delete(vanished.MediaPath);

    var run = await services.GetRequiredService<RetentionRunner>().RunAsync(new Progress<double>(), default);
    await using (var database = new ModDbContext(databasePath))
    {
        var dead = await database.RetentionRuns.SingleAsync(candidate => candidate.Id == deadRunId);
        Assert(dead.Status == "interrupted" && dead.CompletedAt is not null,
            $"A run left 'running' by a stopped process is marked interrupted: {dead.Status}");

        var completed = await database.RetentionOperations.SingleAsync(operation => operation.Id == unlinked.OperationId);
        var completedEntry = await database.Entries.SingleAsync(entry => entry.Id == completed.EntryId);
        Assert(completed.State == "completed" && completedEntry.State == FileState.Reclaimed &&
            await database.History.CountAsync(history => history.Id == completed.Id && history.EventType == "reclaimed") == 1 &&
            !await database.History.AnyAsync(history => history.EntryId == completed.EntryId && history.EventType == "media_missing"),
            $"The runner completes an unlinked operation with one reclaimed event: {completed.State}/{completedEntry.State}");

        var gone = await database.RetentionOperations.SingleAsync(operation => operation.Id == vanished.OperationId);
        Assert(gone.State == "vanished" && gone.Reason == "media_vanished" && gone.UnlinkedAt is null &&
            !await database.History.AnyAsync(history => history.Id == gone.Id),
            $"A prepared file removed by something else ends vanished, with no reclaimed history: {gone.State}/{gone.Reason}");

        Assert(run.Interrupted == 2 && run.Reclaimed == 1 && run.LogicalBytesUnlinked == completed.LogicalBytes,
            $"Interrupted counts resolved actions and only the proven unlink counts as reclaimed: " +
            $"{run.Interrupted}/{run.Reclaimed}/{run.LogicalBytesUnlinked}");

        foreach (var entryId in new[] { completed.EntryId, gone.EntryId })
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == entryId));
        await database.SaveChangesAsync();
    }

    File.Delete(unlinked.SidecarPath);
    File.Delete(vanished.SidecarPath);
}

// P3.T12: the unlink is pinned to the verified file, and a binding added after prepare stops the action.
static async Task VerifyPinnedExecutionAsync(
    IServiceProvider services,
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    PluginConfiguration settings,
    IDictionary<Guid, BaseItem> nativeItems,
    Guid userId,
    Guid libraryId)
{
    // Re-enabling starts a fresh grace period, so let it elapse (as the live-state scenario does).
    settings.RetentionEnabled = true;
    var policy = await services.GetRequiredService<RetentionPolicyService>().SyncAsync(settings, default);
    clock.Advance(TimeSpan.FromDays(2));
    var prepared = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900401, "t12-binding-set", RetentionOperationStatesForTest.Prepared, keep: false);
    // An overlapping library now lists the same file, and its binding is due as well.
    var overlap = new Movie { Id = Guid.NewGuid(), Name = "t12-overlap", Path = prepared.MediaPath };
    nativeItems[overlap.Id] = overlap;
    var overlapEntry = new Entry
    {
        MediaType = "movie", TmdbId = 900402, Title = "t12-overlap", State = FileState.OnDisk,
        TargetLibraryId = libraryId, JellyfinItemId = overlap.Id
    };
    await using (var database = new ModDbContext(databasePath))
    {
        database.Entries.Add(overlapEntry);
        database.EntryBindings.Add(new EntryBinding
        {
            EntryId = overlapEntry.Id, JellyfinItemId = overlap.Id, TargetLibraryId = libraryId,
            VersionGroupId = overlap.Id, MediaPath = prepared.MediaPath, StorageIdentity = storage.Capture(prepared.MediaPath)
        });
        database.CompletionObservations.Add(new CompletionObservation
        {
            EntryId = overlapEntry.Id, TargetId = overlapEntry.Id, UserId = userId, JellyfinItemId = overlap.Id,
            EvidenceAvailable = true, Played = true, CompletedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3),
            LastPlayedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3), ObservedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3)
        });
        database.RetentionEvaluations.Add(new RetentionEvaluation
        {
            EntryId = overlapEntry.Id, TargetId = overlapEntry.Id, State = "disabled", Reason = "retention_disabled",
            PolicyVersion = policy.Version, EvaluatedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3),
            BaselineAt = clock.GetUtcNow().UtcDateTime.AddDays(-3)
        });
        var preparedOperation = await database.RetentionOperations.SingleAsync(candidate => candidate.Id == prepared.OperationId);
        preparedOperation.PolicyVersion = policy.Version;
        (await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == preparedOperation.EntryId))
            .PolicyVersion = policy.Version;
        await database.SaveChangesAsync();
    }

    await services.GetRequiredService<RetentionExecutor>().RecoverAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var operation = await database.RetentionOperations.SingleAsync(candidate => candidate.Id == prepared.OperationId);
        Assert(operation.State == "blocked" && operation.Reason == "binding_set_changed" && File.Exists(prepared.MediaPath),
            $"A binding added after prepare blocks the action with binding_set_changed: {operation.State}/{operation.Reason}");
        settings.RetentionEnabled = false;
        foreach (var entryId in new[] { operation.EntryId, overlapEntry.Id })
            database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == entryId));
        await database.SaveChangesAsync();
    }

    File.Delete(prepared.MediaPath);
    File.Delete(prepared.SidecarPath);

    // A parent directory swapped for a symlink after the check cannot redirect the unlink.
    var inspector = new UnixFileInspector();
    var season = Path.Combine(libraryPath, "t12-show", "season");
    var decoy = Path.Combine(libraryPath, "t12-decoy");
    Directory.CreateDirectory(season);
    Directory.CreateDirectory(decoy);
    var episode = Path.Combine(season, "episode.mkv");
    await File.WriteAllBytesAsync(episode, new byte[512]);
    await File.WriteAllBytesAsync(Path.Combine(decoy, "episode.mkv"), new byte[512]);
    Assert(inspector.TryInspect(episode, out var verified), "The pinned-unlink fixture has inode evidence");
    Directory.Move(season, season + ".moved");
    Directory.CreateSymbolicLink(season, decoy);
    var swapped = inspector.UnlinkPinned(verified.CanonicalPath, verified.PhysicalIdentity);
    Assert(!swapped.Removed && swapped.IsReplacement && File.Exists(Path.Combine(decoy, "episode.mkv")) &&
        File.Exists(Path.Combine(season + ".moved", "episode.mkv")),
        $"A parent directory swapped for a symlink cannot redirect the unlink: {swapped.Detail}");
    File.Delete(season);
    Directory.Move(season + ".moved", season);

    // A same-name replacement is a different inode and is not deleted.
    File.Delete(episode);
    await File.WriteAllBytesAsync(episode, new byte[512]);
    var replaced = inspector.UnlinkPinned(verified.CanonicalPath, verified.PhysicalIdentity);
    Assert(!replaced.Removed && replaced.IsReplacement && File.Exists(episode),
        $"A same-name replacement is detected by inode and kept: {replaced.Detail}");

    Assert(inspector.TryInspect(episode, out var current) &&
        inspector.UnlinkPinned(current.CanonicalPath, current.PhysicalIdentity).Removed && !File.Exists(episode),
        "The pinned unlink removes exactly the verified file");
    Directory.Delete(Path.Combine(libraryPath, "t12-show"), true);
    Directory.Delete(decoy, true);

    // P3.T18: an unreadable library root (here a dropped mount leaving an empty directory) blocks the unlink.
    settings.RetentionEnabled = true;
    var offlinePolicy = await services.GetRequiredService<RetentionPolicyService>().SyncAsync(settings, default);
    var offline = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
        userId, libraryId, 900403, "t18-offline-root", RetentionOperationStatesForTest.Prepared, keep: false);
    await using (var database = new ModDbContext(databasePath))
    {
        var operation = await database.RetentionOperations.SingleAsync(candidate => candidate.Id == offline.OperationId);
        operation.PolicyVersion = offlinePolicy.Version;
        (await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == operation.EntryId))
            .PolicyVersion = offlinePolicy.Version;
        await database.SaveChangesAsync();
    }

    var parked = libraryPath + ".offline";
    Directory.Move(libraryPath, parked);
    Directory.CreateDirectory(libraryPath);
    try
    {
        await services.GetRequiredService<RetentionExecutor>().RecoverAsync(default);
    }
    finally
    {
        Directory.Delete(libraryPath);
        Directory.Move(parked, libraryPath);
        settings.RetentionEnabled = false;
    }

    await using (var database = new ModDbContext(databasePath))
    {
        var operation = await database.RetentionOperations.SingleAsync(candidate => candidate.Id == offline.OperationId);
        Assert(operation.State != "completed" && operation.UnlinkedAt is null && File.Exists(offline.MediaPath),
            $"An unavailable library root never lets retention unlink: {operation.State}/{operation.Reason}");
        database.Entries.Remove(await database.Entries.SingleAsync(entry => entry.Id == operation.EntryId));
        await database.SaveChangesAsync();
    }

    File.Delete(offline.MediaPath);
    File.Delete(offline.SidecarPath);
}

static async Task<Guid?> LatestRunIdAsync(HttpClient http)
{
    using var response = await http.GetAsync("/JellyfinMod/Retention/Runs/Latest");
    if (response.StatusCode == HttpStatusCode.NotFound) return null;
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("id").GetGuid();
}

static async Task<JsonDocument> WaitForRunAsync(HttpClient http, Guid? previousRunId)
{
    for (var attempt = 0; attempt < 1200; attempt++)
    {
        using var response = await http.GetAsync("/JellyfinMod/Retention/Runs/Latest");
        if (response.IsSuccessStatusCode)
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (root.GetProperty("id").GetGuid() != previousRunId && root.GetProperty("status").GetString() != "running")
                return json;
            json.Dispose();
        }

        await Task.Delay(50);
    }

    throw new InvalidOperationException("The queued retention run did not finish");
}

// P3.T8: the executor re-reads live Jellyfin state immediately before unlinking.
static async Task VerifyLiveStateRevalidationAsync(
    IServiceProvider services,
    string databasePath,
    string libraryPath,
    MediaStorageIdentity storage,
    FixedTimeProvider clock,
    PluginConfiguration settings,
    IDictionary<Guid, BaseItem> nativeItems,
    Guid userId,
    Guid libraryId,
    Dictionary<Guid, UserItemData> liveStates,
    List<SessionInfo> sessionRows,
    ISessionManager sessionManager)
{
    var executor = services.GetRequiredService<RetentionExecutor>();
    var fixtures = new List<RecoveryFixture>();

    // The previous scenario disabled retention. Re-enabling starts a fresh grace period, so let it elapse.
    settings.RetentionEnabled = true;
    var policy = await services.GetRequiredService<RetentionPolicyService>().SyncAsync(settings, default);
    clock.Advance(TimeSpan.FromDays(2));

    async Task<(RecoveryFixture Fixture, Guid ItemId, Guid EntryId)> SeedAsync(int tmdbId, string name)
    {
        var fixture = await SeedRecoveryFixtureAsync(databasePath, libraryPath, storage, clock, nativeItems,
            userId, libraryId, tmdbId, name, RetentionOperationStatesForTest.Prepared, keep: false);
        fixtures.Add(fixture);
        await using var database = new ModDbContext(databasePath);
        var operation = await database.RetentionOperations.SingleAsync(candidate => candidate.Id == fixture.OperationId);
        operation.PolicyVersion = policy.Version;
        (await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == operation.EntryId))
            .PolicyVersion = policy.Version;
        await database.SaveChangesAsync();
        return (fixture, operation.JellyfinItemId, operation.EntryId!.Value);
    }

    async Task<RetentionExecutionResult> RecoverAsync(RecoveryFixture fixture) =>
        (await executor.RecoverAsync(default)).Single(result => result.OperationId == fixture.OperationId);

    // A favourite that the stored observation missed blocks the unlink.
    var favorite = await SeedAsync(900201, "live-favorite");
    liveStates[favorite.ItemId] = new UserItemData { Key = "live-favorite", Played = true, IsFavorite = true };
    var favoriteResult = await RecoverAsync(favorite.Fixture);
    Assert(favoriteResult.State == "blocked" && favoriteResult.Reason == "live_favorite" &&
        File.Exists(favorite.Fixture.MediaPath),
        $"A live favourite missed by stored evidence blocks the unlink: {favoriteResult.State}/{favoriteResult.Reason}");

    // Marking the title unwatched after the stored completion blocks the unlink.
    var unwatched = await SeedAsync(900202, "live-unwatched");
    liveStates[unwatched.ItemId] = new UserItemData { Key = "live-unwatched", Played = false };
    var unwatchedResult = await RecoverAsync(unwatched.Fixture);
    Assert(unwatchedResult.State == "blocked" && unwatchedResult.Reason == "live_not_completed" &&
        File.Exists(unwatched.Fixture.MediaPath),
        $"A live unwatched state blocks the unlink: {unwatchedResult.State}/{unwatchedResult.Reason}");

    // Another version of the same title playing, reported only through its media source, protects every version.
    var grouped = await SeedAsync(900203, "live-version");
    var versionB = new Movie { Id = Guid.NewGuid(), Name = "live-version-b", Path = Path.Combine(libraryPath, "live-version-b.mkv") };
    nativeItems[versionB.Id] = versionB;
    await using (var database = new ModDbContext(databasePath))
    {
        database.EntryBindings.Add(new EntryBinding
        {
            EntryId = grouped.EntryId, JellyfinItemId = versionB.Id, TargetLibraryId = libraryId,
            VersionGroupId = grouped.ItemId, MediaPath = versionB.Path
        });
        await database.SaveChangesAsync();
    }

    sessionRows.Add(new SessionInfo(sessionManager, NullLogger.Instance)
    {
        PlayState = new MediaBrowser.Model.Session.PlayerStateInfo { MediaSourceId = versionB.Id.ToString("N") }
    });
    var groupedResult = await RecoverAsync(grouped.Fixture);
    sessionRows.Clear();
    Assert(groupedResult.State == "blocked" && groupedResult.Reason is "active_session" or "live_active_session" &&
        File.Exists(grouped.Fixture.MediaPath),
        $"Playing another version protects the whole version group: {groupedResult.State}/{groupedResult.Reason}");

    // Conflicting per-version user data aggregates to protected: a resume on version B protects version A.
    liveStates[grouped.ItemId] = new UserItemData { Key = "version-a", Played = true, LastPlayedDate = clock.GetUtcNow().UtcDateTime };
    liveStates[versionB.Id] = new UserItemData { Key = "version-b", PlaybackPositionTicks = 5000 };
    await services.GetRequiredService<RetentionCompletionService>().RefreshAsync(userId, grouped.ItemId, "Repair", default);
    await services.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var observation = await database.CompletionObservations.SingleAsync(candidate =>
            candidate.TargetId == grouped.EntryId && candidate.UserId == userId);
        var evaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == grouped.EntryId);
        Assert(observation.PlaybackPositionTicks == 5000 && observation.CompletedAt is null &&
            evaluation.State == "blocked" && evaluation.Reason == "active_resume",
            $"A resume on any version protects the title: {observation.PlaybackPositionTicks}/{evaluation.State}/{evaluation.Reason}");
    }

    // A user with no stored observation is read live instead of blocking until a manual repair.
    var missing = await SeedAsync(900204, "live-missing");
    await using (var database = new ModDbContext(databasePath))
    {
        database.CompletionObservations.RemoveRange(database.CompletionObservations.Where(candidate =>
            candidate.TargetId == missing.EntryId));
        database.RetentionOperations.RemoveRange(database.RetentionOperations.Where(candidate =>
            candidate.Id == missing.Fixture.OperationId));
        await database.SaveChangesAsync();
    }

    await services.GetRequiredService<RetentionEvaluator>().EvaluateAllAsync(default);
    await using (var database = new ModDbContext(databasePath))
    {
        var observation = await database.CompletionObservations.SingleOrDefaultAsync(candidate =>
            candidate.TargetId == missing.EntryId && candidate.UserId == userId);
        var evaluation = await database.RetentionEvaluations.SingleAsync(candidate => candidate.TargetId == missing.EntryId);
        Assert(observation is { EvidenceAvailable: true, SourceReason: "Evaluate" } &&
            evaluation.Reason != "completion_evidence_missing",
            $"Missing evidence is read live without a repair run: {observation?.SourceReason}/{evaluation.State}/{evaluation.Reason}");
    }

    // P3.T16: a stacked multi-part movie is never reclaimed; unlinking one part would strand the rest.
    var multiPart = await SeedAsync(900205, "t16-multipart");
    var secondPart = Path.Combine(libraryPath, "t16-multipart-cd2.mkv");
    await File.WriteAllBytesAsync(secondPart, new byte[512]);
    ((Movie)nativeItems[multiPart.ItemId]).AdditionalParts = [secondPart];
    var multiPartResult = await RecoverAsync(multiPart.Fixture);
    Assert(multiPartResult.State == "blocked" && multiPartResult.Reason == "multi_part_unsupported" &&
        File.Exists(multiPart.Fixture.MediaPath) && File.Exists(secondPart),
        $"A stacked multi-part movie is blocked and both parts survive: {multiPartResult.State}/{multiPartResult.Reason}");
    File.Delete(secondPart);

    // Leave the suite's later state unchanged.
    settings.RetentionEnabled = false;
    await services.GetRequiredService<RetentionPolicyService>().SyncAsync(settings, default);
    liveStates.Clear();
    nativeItems.Remove(versionB.Id);
    await using (var database = new ModDbContext(databasePath))
    {
        foreach (var entryId in new[] { favorite.EntryId, unwatched.EntryId, grouped.EntryId, missing.EntryId, multiPart.EntryId })
            database.Entries.Remove(await database.Entries.SingleAsync(candidate => candidate.Id == entryId));
        await database.SaveChangesAsync();
    }

    foreach (var fixture in fixtures)
    {
        File.Delete(fixture.MediaPath);
        File.Delete(fixture.SidecarPath);
    }
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
