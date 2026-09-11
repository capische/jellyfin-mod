using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod;

/// <summary>
/// Registers this plugin's services into the host's own container.
/// </summary>
/// <remarks>
/// Some host services are scoped rather than singleton, so nothing registered here may capture
/// one in a singleton. The database context is transient for the same reason.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddTransient(_ =>
            new ModDbContext(Path.Combine(Plugin.Instance!.DataPath, "jellyfinmod.db")));
        services.AddTransient<LibraryAccess>();
        services.AddTransient<CatalogSortName>();
        services.AddSingleton<ReconciliationLibraryLock>();
        services.AddTransient<ReconciliationService>();
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => Plugin.Instance!.Configuration, provider.GetRequiredService<ILogger<TmdbClient>>()));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DatabaseInitializer>());
    }
}
