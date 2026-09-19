using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
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
        services.AddSingleton<ReconciliationRunGate>();
        services.AddSingleton<RetentionExecutionGate>();
        services.AddSingleton<RetentionRunGate>();
        services.AddSingleton<MediaStorageIdentity>();
        services.AddSingleton<UnixFileInspector>();
        services.AddTransient<ReconciliationService>();
        services.AddTransient<JellyfinNativeTitleSource>();
        services.AddTransient<CatalogBackfillRunner>();
        services.AddTransient<JellyfinItemReconciliationRunner>();
        services.AddTransient<RetentionPolicyService>();
        services.AddSingleton(_ => new RetentionConfigurationSource(() => Plugin.Instance!.Configuration));
        services.AddTransient<RetentionCompletionService>();
        services.AddTransient<RetentionEvaluator>();
        services.AddTransient<RetentionPreviewService>();
        services.AddTransient<RetentionLiveCheck>();
        services.AddTransient<RetentionExecutor>();
        services.AddTransient<RetentionRunner>();
        services.AddTransient(provider => new TransmissionSeedClient(
            provider.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration,
            provider.GetRequiredService<UnixFileInspector>(), provider.GetRequiredService<ILogger<TransmissionSeedClient>>()));
        services.AddSingleton(TimeProvider.System);
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => Plugin.Instance!.Configuration, provider.GetRequiredService<ILogger<TmdbClient>>()));
        AcquisitionServices.Add(services, () => Plugin.Instance!.DataPath);
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DatabaseInitializer>());
        AcquisitionServices.AddHostedServices(services);
        services.AddSingleton<LibraryEventListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LibraryEventListener>());
        services.AddSingleton<RetentionEventListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RetentionEventListener>());
    }
}
