using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>The fixed world the plugin host sees: users, libraries and their access.</summary>
internal sealed class World
{
    public required User Admin { get; init; }
    public required User SecondAdmin { get; init; }
    public required User RestrictedAdmin { get; init; }
    public required User Ordinary { get; init; }
    public required TestLibrary Movies { get; init; }
    public required TestLibrary Movies2 { get; init; }
    public required TestLibrary Tv { get; init; }
    public required TestLibrary Far { get; init; }
    public required string Folder { get; init; }
}

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

/// <summary>One running plugin host over real Kestrel, authentication, authorization, MVC and SQLite.</summary>
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

    public static async Task<PluginHost> StartAsync(World world, string dbPath, ShiftedTimeProvider time,
        CapturingLoggerProvider logs, TimeSpan hold, PluginConfiguration configuration, Action<IServiceCollection>? configure = null)
    {
        var users = Stub<IUserManager>.Create((method, args) => method.Name == "GetUserById"
            ? new[] { world.Admin, world.SecondAdmin, world.RestrictedAdmin, world.Ordinary }.SingleOrDefault(user => user.Id == (Guid)args![0]!)
            : null);
        var root = new TestRoot(new Dictionary<Guid, BaseItem[]>
        {
            [world.Admin.Id] = [world.Movies, world.Movies2, world.Tv],
            [world.SecondAdmin.Id] = [world.Movies, world.Movies2, world.Tv],
            [world.RestrictedAdmin.Id] = [world.Tv],
            [world.Ordinary.Id] = [world.Movies, world.Movies2, world.Tv]
        });
        var libraries = new[] { world.Movies, world.Movies2, world.Tv, world.Far };
        var library = Stub<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "GetUserRootFolder" => root,
            "GetItemById" => null,
            "GetVirtualFolders" => libraries.Select(folder => new VirtualFolderInfo
            {
                ItemId = folder.Id.ToString(), Name = folder.Id.ToString("N"), Locations = [folder.Location],
                CollectionType = folder.CollectionType == Jellyfin.Data.Enums.CollectionType.tvshows
                    ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies
            }).ToList(),
            "GetCollectionFolders" => new List<Folder>(),
            _ => null
        });
        var localization = Stub<ILocalizationManager>.Create((_, _) => null);
        var serverConfiguration = Stub<IServerConfigurationManager>.Create((method, _) => method.Name == "get_Configuration"
            ? new ServerConfiguration { SortRemoveWords = ["the", "a"], SortRemoveCharacters = [], SortReplaceCharacters = [] } : null);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = world.Folder, ApplicationName = typeof(ReleasesController).Assembly.FullName
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddControllers().AddApplicationPart(typeof(ReleasesController).Assembly).AddJsonOptions(options =>
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
        builder.Services.AddSingleton(new LibraryWriteBudget(TimeSpan.FromSeconds(2)));
        builder.Services.AddSingleton<RetentionExecutionGate>();
        builder.Services.AddSingleton<MediaStorageIdentity>();
        builder.Services.AddSingleton<UnixFileInspector>();
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(library);
        builder.Services.AddTransient(_ => new LibraryAccess(users, library, localization));
        builder.Services.AddTransient(provider => new RetentionEvaluator(provider.GetRequiredService<ModDbContext>(), users,
            provider.GetRequiredService<LibraryAccess>(), time));
        builder.Services.AddTransient(_ => new CatalogSortName(serverConfiguration));
        builder.Services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => configuration, provider.GetRequiredService<ILogger<TmdbClient>>()));
        builder.Services.AddSingleton(new RetentionConfigurationSource(() => configuration));
        builder.Services.AddSingleton<TimeProvider>(time);
        // The production acquisition registration, then only the documented bounds the test shortens.
        AcquisitionServices.Add(builder.Services, () => world.Folder);
        AcquisitionServices.AddHostedServices(builder.Services);
        builder.Services.AddSingleton(new GrabHoldOptions(hold));
        builder.Services.AddSingleton(new TorznabOptions(TimeSpan.FromSeconds(2), 5, 4));
        // A later suite may add or replace registrations; the last registration of a service wins.
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        for (var attempt = 0; !app.Services.GetRequiredService<DatabaseInitializer>().IsReady; attempt++)
        {
            if (attempt > 100) throw new InvalidOperationException("Database did not become ready");
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

/// <summary>A real movie/TV library folder with a real directory on disk.</summary>
internal sealed class TestLibrary : CollectionFolder
{
    public string Location { get; init; } = string.Empty;
    protected override MediaBrowser.Model.Querying.QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) => new() { Items = [] };
}

internal sealed class TestRoot(Dictionary<Guid, BaseItem[]> byUser) : Folder
{
    public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) =>
        byUser.GetValueOrDefault(user.Id, []);
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
