global using TestLibrary = PhaseFiveFixtures.CollectionFolder;
global using TestSeries = PhaseFiveFixtures.Series;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>A test time source: the system clock plus an adjustable offset. Timers still run on real time.</summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

/// <summary>Collects every formatted log line so tests can prove secrets never reach logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}


internal sealed class TestRoot(Func<Guid, BaseItem[]> byUser) : Folder
{
    public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) =>
        byUser(user.Id);
}

/// <summary>
/// The Jellyfin library as the plugin sees it. The pinned host cannot run inside the SDK container, so its library
/// manager and library monitor are the one simulated boundary here, as in the Phase 2 and 3 suites: a targeted scan
/// request creates a native item for every new file under the reported path and raises ItemAdded, which the real
/// LibraryEventListener, native title source and ReconciliationService then process.
/// </summary>
internal sealed partial class NativeWorld
{
    private readonly ConcurrentDictionary<Guid, BaseItem> _items = new();
    private EventHandler<ItemChangeEventArgs>? _added;
    private EventHandler<ItemChangeEventArgs>? _updated;
    private EventHandler<ItemChangeEventArgs>? _removed;

    public required IReadOnlyList<TestLibrary> Libraries { get; init; }
    public required Func<Guid, BaseItem[]> UserLibraries { get; init; }
    public ConcurrentQueue<string> ScanRequests { get; } = new();
    public bool AutoScan { get; set; } = true;
    public int Scans;

    public IEnumerable<BaseItem> Items => _items.Values;

    private ILibraryManager? _library;
    private ILibraryMonitor? _monitor;

    public ILibraryManager Library => _library ??= Stub<ILibraryManager>.Create(LibraryCall);
    public ILibraryMonitor Monitor => _monitor ??= Stub<ILibraryMonitor>.Create((method, arguments) =>
    {
        if (method.Name == "ReportFileSystemChanged")
        {
            var path = (string)arguments![0]!;
            ScanRequests.Enqueue(path);
            if (AutoScan)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    try
                    {
                        Scan(path);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine("Simulated scan failed: " + error);
                    }
                });
        }

        return null;
    });

    /// <summary>Adds a native item for every file under <paramref name="path"/> that Jellyfin does not know yet.</summary>
    public void Scan(string path)
    {
        Interlocked.Increment(ref Scans);
        var files = File.Exists(path) ? [path] : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray() : [];
        foreach (var file in files.Where(file => !Path.GetFileName(file).StartsWith('.')).Order(StringComparer.Ordinal))
        {
            if (_items.Values.Any(item => item.Path == file)) continue;
            var library = Libraries.FirstOrDefault(value => file.StartsWith(value.Location + "/", StringComparison.Ordinal));
            if (library is null) continue;
            if (library.CollectionType == CollectionType.movies) AddMovie(library, file);
            else AddEpisode(library, file);
        }
    }

    public Movie AddMovie(TestLibrary library, string file, int? tmdbId = null)
    {
        var folder = Path.GetDirectoryName(file)!;
        var sibling = _items.Values.OfType<Movie>().Where(movie => Path.GetDirectoryName(movie.Path) == folder && folder != library.Location)
            .OrderBy(movie => movie.DateCreated).FirstOrDefault();
        var folderMatch = TmdbPattern().Match(Path.GetFileName(folder));
        var tmdb = tmdbId?.ToString(CultureInfo.InvariantCulture) ??
            (folderMatch.Success ? folderMatch.Groups[1].Value : sibling?.ProviderIds.GetValueOrDefault("Tmdb"));
        var movie = new Movie
        {
            Id = Guid.NewGuid(), Name = TitlePattern().Match(Path.GetFileName(folder)).Groups[1].Value.Trim(), Path = file,
            DateCreated = DateTime.UtcNow
        };
        if (tmdb is not null) movie.ProviderIds["Tmdb"] = tmdb;
        // Jellyfin groups files that start with their folder's name as alternate versions of the first one.
        if (sibling is not null)
            movie.PrimaryVersionId = sibling.PrimaryVersionId is { Length: > 0 } primary && _items.ContainsKey(Guid.Parse(primary))
                ? primary : sibling.Id.ToString("N");
        Add(library, movie);
        return movie;
    }

    private void AddEpisode(TestLibrary library, string file)
    {
        var parent = Path.GetDirectoryName(file)!;
        var parentName = Path.GetFileName(parent);
        var seriesFolder = parentName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || parentName == "Specials"
            ? Path.GetDirectoryName(parent)! : parent;
        var series = _items.Values.OfType<TestSeries>().FirstOrDefault(value => value.Path == seriesFolder);
        if (series is null)
        {
            series = new TestSeries { Id = Guid.NewGuid(), Name = TitlePattern().Match(Path.GetFileName(seriesFolder)).Groups[1].Value.Trim(), Path = seriesFolder };
            if (TmdbPattern().Match(Path.GetFileName(seriesFolder)) is { Success: true } match) series.ProviderIds["Tmdb"] = match.Groups[1].Value;
            Add(library, series);
        }

        var numbering = EpisodePattern().Match(Path.GetFileName(file));
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(file), Path = file, SeriesId = series.Id,
            ParentIndexNumber = numbering.Success ? int.Parse(numbering.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            IndexNumber = numbering.Success ? int.Parse(numbering.Groups[2].Value, CultureInfo.InvariantCulture) : null,
            DateCreated = DateTime.UtcNow
        };
        lock (series.Items) series.Items.Add(episode);
        _items[episode.Id] = episode;
        _added?.Invoke(this, new ItemChangeEventArgs { Item = episode });
    }

    public void Add(TestLibrary library, BaseItem item)
    {
        lock (library.Items) library.Items.Add(item);
        _items[item.Id] = item;
        _added?.Invoke(this, new ItemChangeEventArgs { Item = item });
    }

    public void Remove(BaseItem item)
    {
        _items.TryRemove(item.Id, out _);
        // Removing a primary version promotes the oldest remaining alternate, as a rescan of the folder would.
        var orphans = _items.Values.OfType<Movie>().Where(movie => movie.PrimaryVersionId == item.Id.ToString("N"))
            .OrderBy(movie => movie.DateCreated).ToArray();
        if (orphans.Length > 0)
        {
            orphans[0].PrimaryVersionId = null;
            foreach (var alternate in orphans.Skip(1)) alternate.PrimaryVersionId = orphans[0].Id.ToString("N");
        }

        foreach (var library in Libraries) lock (library.Items) library.Items.Remove(item);
        foreach (var series in _items.Values.OfType<TestSeries>()) lock (series.Items) series.Items.Remove(item);
        _removed?.Invoke(this, new ItemChangeEventArgs { Item = item });
    }

    private TestLibrary? LibraryOf(BaseItem item)
    {
        var path = item is MediaBrowser.Controller.Entities.TV.Episode episode && _items.GetValueOrDefault(episode.SeriesId) is { } series
            ? series.Path : item.Path;
        return Libraries.FirstOrDefault(library => path is not null && path.StartsWith(library.Location + "/", StringComparison.Ordinal));
    }

    private IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
    {
        IEnumerable<BaseItem> items = _items.Values;
        if (!query.ParentId.Equals(Guid.Empty)) items = items.Where(item => LibraryOf(item)?.Id == query.ParentId);
        if (query.IncludeItemTypes.Length > 0)
            items = items.Where(item => query.IncludeItemTypes.Contains(item switch
            {
                Movie => BaseItemKind.Movie,
                Series => BaseItemKind.Series,
                MediaBrowser.Controller.Entities.TV.Episode => BaseItemKind.Episode,
                _ => BaseItemKind.Folder
            }));
        if (query.AncestorIds.Length > 0)
            items = items.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Where(episode => query.AncestorIds.Contains(episode.SeriesId));
        if (query.ItemIds.Length > 0) items = items.Where(item => query.ItemIds.Contains(item.Id));
        if (query.HasAnyProviderId is { Count: > 0 } providers)
            items = items.Where(item => providers.Any(provider => item.ProviderIds.GetValueOrDefault(provider.Key) == provider.Value));
        if (query.PresentationUniqueKey is { } group)
            items = items.OfType<Movie>().Where(movie => (movie.PrimaryVersionId is { Length: > 0 } primary ? primary : movie.Id.ToString("N")) == group);
        return items.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
            .Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
    }

    private object? LibraryCall(MethodInfo method, object?[]? arguments)
    {
        switch (method.Name)
        {
            case "GetUserRootFolder": return new TestRoot(UserLibraries);
            case "GetVirtualFolders":
                return Libraries.Select(folder => new VirtualFolderInfo
                {
                    ItemId = folder.Id.ToString(), Name = folder.Name, Locations = [folder.Location],
                    CollectionType = folder.CollectionType == CollectionType.tvshows ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies
                }).ToList();
            case "GetItemById":
                var id = (Guid)arguments![0]!;
                return Libraries.FirstOrDefault(library => library.Id == id) as BaseItem ?? _items.GetValueOrDefault(id);
            case "GetItemList": return Query((InternalItemsQuery)arguments![0]!);
            case "GetCount": return Query((InternalItemsQuery)arguments![0]!).Count;
            case "GetCollectionFolders":
                return LibraryOf((BaseItem)arguments![0]!) is { } owner ? new List<Folder> { owner } : new List<Folder>();
            case "DeleteItem": Remove((BaseItem)arguments![0]!); return null;
            case "add_ItemAdded": _added += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            case "remove_ItemAdded": _added -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            case "add_ItemUpdated": _updated += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            case "remove_ItemUpdated": _updated -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            case "add_ItemRemoved": _removed += (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            case "remove_ItemRemoved": _removed -= (EventHandler<ItemChangeEventArgs>)arguments![0]!; return null;
            default: return null;
        }
    }

    [GeneratedRegex(@"\[tmdbid-(\d+)\]")]
    private static partial Regex TmdbPattern();

    [GeneratedRegex(@"^([^\[(]+)")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"S(\d{1,2})E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();
}

/// <summary>The users and libraries the plugin host sees.</summary>
internal sealed class World
{
    public required User Admin { get; init; }
    public required User RestrictedAdmin { get; init; }
    public required User Ordinary { get; init; }
    public required User MoviesOnly { get; init; }
    public required TestLibrary Movies { get; init; }
    public required TestLibrary Tv { get; init; }
    public required TestLibrary Far { get; init; }
    public required string Folder { get; init; }
    public required NativeWorld Native { get; init; }

    public IEnumerable<User> Users => [Admin, RestrictedAdmin, Ordinary, MoviesOnly];
}

/// <summary>One running plugin host over real Kestrel, authentication, authorization, MVC, EF migrations and SQLite.</summary>
internal sealed class PluginHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required Uri Address { get; init; }

    public HttpClient Client(User? user, bool admin)
    {
        var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(60) };
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user.Id.ToString());
        if (admin) client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        return client;
    }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static async Task<PluginHost> StartAsync(World world, string dbPath, ShiftedTimeProvider time, CapturingLoggerProvider logs,
        PluginConfiguration configuration, Func<IServiceProvider, ITaskManager>? taskManager = null)
    {
        var users = Stub<IUserManager>.Create((method, args) => method.Name switch
        {
            "GetUserById" => world.Users.SingleOrDefault(user => user.Id == (Guid)args![0]!),
            "GetUsers" => world.Users.ToArray(),
            _ => null
        });
        var localization = Stub<ILocalizationManager>.Create((_, _) => null);
        var serverConfiguration = Stub<IServerConfigurationManager>.Create((method, _) => method.Name == "get_Configuration"
            ? new ServerConfiguration { SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = [] } : null);
        // Every native item reads as finished by every user: completion evidence exists as soon as an item is bound.
        var userData = Stub<IUserDataManager>.Create((method, args) => method.Name == "GetUserData" && args?[1] is BaseItem item
            ? new UserItemData { Key = item.Id.ToString("N"), Played = true, LastPlayedDate = time.GetUtcNow().UtcDateTime }
            : null);
        var sessions = Stub<ISessionManager>.Create((method, _) => method.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : null);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = world.Folder, ApplicationName = typeof(QueueController).Assembly.FullName
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddControllers().AddApplicationPart(typeof(QueueController).Assembly).AddJsonOptions(options =>
        {
            // Match the pinned Jellyfin host's serializer defaults; plugin DTOs name their own properties.
            var defaults = Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions;
            options.JsonSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.JsonSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
            foreach (var converter in defaults.Converters) options.JsonSerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", null);
        builder.Services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation, policy => policy.RequireRole("admin")));
        builder.Services.AddHttpClient();
        builder.Services.AddTransient(_ => new ModDbContext(dbPath));
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DatabaseInitializer>());
        builder.Services.AddSingleton<ReconciliationLibraryLock>();
        builder.Services.AddSingleton<ReconciliationRunGate>();
        builder.Services.AddSingleton(new LibraryWriteBudget(TimeSpan.FromSeconds(2)));
        builder.Services.AddSingleton<RetentionExecutionGate>();
        builder.Services.AddSingleton<RetentionRunGate>();
        builder.Services.AddSingleton<MediaStorageIdentity>();
        builder.Services.AddSingleton<UnixFileInspector>();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(world.Native.Library);
        builder.Services.AddSingleton(world.Native.Monitor);
        builder.Services.AddSingleton(userData);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(localization);
        builder.Services.AddTransient(_ => new LibraryAccess(users, world.Native.Library, localization));
        builder.Services.AddTransient(_ => new CatalogSortName(serverConfiguration));
        builder.Services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => configuration, provider.GetRequiredService<ILogger<TmdbClient>>()));
        builder.Services.AddSingleton(new RetentionConfigurationSource(() => configuration));
        builder.Services.AddSingleton<TimeProvider>(time);
        // The production Phase 2 and 3 services that import relies on: the one matcher, retention and its gates.
        builder.Services.AddTransient<ReconciliationService>();
        builder.Services.AddTransient<JellyfinNativeTitleSource>();
        builder.Services.AddTransient<JellyfinItemReconciliationRunner>();
        builder.Services.AddTransient<RetentionPolicyService>();
        builder.Services.AddTransient<RetentionCompletionService>();
        builder.Services.AddTransient<RetentionEvaluator>();
        builder.Services.AddTransient(provider => new TransmissionSeedClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => configuration, provider.GetRequiredService<UnixFileInspector>(),
            provider.GetRequiredService<ILogger<TransmissionSeedClient>>()));
        builder.Services.AddTransient<RetentionPreviewService>();
        builder.Services.AddTransient<RetentionLiveCheck>();
        builder.Services.AddTransient<RetentionExecutor>();
        builder.Services.AddTransient<RetentionRunner>();
        if (taskManager is not null) builder.Services.AddSingleton(taskManager);
        else builder.Services.AddSingleton(Stub<ITaskManager>.Create((_, _) => null));
        builder.Services.AddSingleton(provider => new LibraryEventListener(world.Native.Library,
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<LibraryEventListener>>(),
            TimeSpan.FromMilliseconds(100), provider.GetRequiredService<DatabaseInitializer>()));
        builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LibraryEventListener>());
        // The production acquisition and import registrations, then only the documented bounds the test shortens.
        AcquisitionServices.Add(builder.Services, () => world.Folder);
        AcquisitionServices.AddHostedServices(builder.Services);
        ImportServices.Add(builder.Services);
        ImportServices.AddHostedServices(builder.Services);
        JellyfinMod.Services.Automation.AutomationServices.Add(builder.Services);
        builder.Services.AddSingleton(new GrabHoldOptions(TimeSpan.FromMilliseconds(300)));
        builder.Services.AddSingleton(new TorznabOptions(TimeSpan.FromSeconds(5), 3, 4));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        for (var attempt = 0; !app.Services.GetRequiredService<DatabaseInitializer>().IsReady; attempt++)
        {
            if (attempt > 200) throw new InvalidOperationException("Database did not become ready");
            await Task.Delay(50);
        }

        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return new PluginHost { App = app, Address = address };
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Loopback-only test authentication; never part of the plugin assembly.</summary>
public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Test-User"], out var userId)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString()),
            new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString())], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}

/// <summary>Minimal host interface test double for Jellyfin services the plugin reads.</summary>
public class Stub<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[]?, object?> _callback = null!;

    /// <summary>Creates a test double.</summary>
    public static T Create(Func<MethodInfo, object?[]?, object?> callback)
    {
        var instance = DispatchProxy.Create<T, Stub<T>>();
        ((Stub<T>)(object)instance)._callback = callback;
        return instance;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _callback(targetMethod!, args);
}

namespace PhaseFiveFixtures
{
    // Jellyfin derives an item's kind from its type name (BaseItem.GetBaseItemKind), so the fixtures keep the names of
    // the host types they extend.

    /// <summary>A real movie/TV library folder backed by a real directory on disk.</summary>
    internal sealed class CollectionFolder : MediaBrowser.Controller.Entities.CollectionFolder
    {
        public string Location { get; init; } = string.Empty;
        public List<BaseItem> Items { get; } = [];

        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            BaseItem[] items;
            lock (Items) items = Items.ToArray();
            return new() { Items = items, TotalRecordCount = items.Length };
        }
    }

    /// <summary>A native series whose episodes the fixture adds as the scan finds them.</summary>
    internal sealed class Series : MediaBrowser.Controller.Entities.TV.Series
    {
        public List<BaseItem> Items { get; } = [];

        protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            BaseItem[] items;
            lock (Items) items = Items.ToArray();
            return new() { Items = items, TotalRecordCount = items.Length };
        }
    }
}
