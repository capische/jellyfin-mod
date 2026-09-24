using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Automation;
using JellyfinMod.Services.Import;
using JellyfinMod.Services.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
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
        services.AddSingleton(_ => new RetentionConfigurationSource(() => Plugin.Instance!.Configuration,
            configuration => Plugin.Instance!.UpdateConfiguration(configuration)));
        services.AddSingleton(provider => new SettingsXmlImportSource(provider.GetRequiredService<RetentionConfigurationSource>()));
        services.AddTransient<RetentionCompletionService>();
        services.AddTransient<RetentionEvaluator>();
        services.AddTransient<RetentionPreviewService>();
        services.AddTransient<RetentionLiveCheck>();
        services.AddTransient<RetentionExecutor>();
        services.AddTransient<RetentionRunner>();
        services.AddTransient(provider => new TransmissionSeedClient(
            provider.GetRequiredService<IHttpClientFactory>(), () => Plugin.Instance!.Configuration,
            provider.GetRequiredService<UnixFileInspector>(), provider.GetRequiredService<ILogger<TransmissionSeedClient>>(),
            provider.GetRequiredService<AcquisitionSecretStore>()));
        services.AddSingleton(TimeProvider.System);
        services.AddTransient(provider => new TmdbClient(provider.GetRequiredService<IHttpClientFactory>(),
            () => Plugin.Instance!.Configuration, provider.GetRequiredService<ILogger<TmdbClient>>(),
            provider.GetRequiredService<AcquisitionSecretStore>(), () => provider.GetRequiredService<ModDbContext>()));
        AcquisitionServices.Add(services, () => Plugin.Instance!.DataPath);
        ImportServices.Add(services);
        AutomationServices.Add(services);
        services.AddSingleton(provider => new WebBundleStore(
            Plugin.Instance!.DataPath,
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? Plugin.Instance!.DataPath,
            provider.GetRequiredService<ILogger<WebBundleStore>>()));
        services.AddSingleton<TakeoverHistory>();
        services.AddSingleton(provider => new WebRootTakeover(
            Plugin.Instance!.DataPath,
            provider.GetRequiredService<WebBundleStore>(),
            provider.GetRequiredService<ILogger<WebRootTakeover>>(),
            provider.GetRequiredService<IServerConfigurationManager>().GetNetworkConfiguration().BaseUrl,
            provider.GetRequiredService<TakeoverHistory>().RecordAsync));
        services.AddSingleton(provider => new PluginRepository(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? Plugin.Instance!.DataPath,
            Plugin.Instance!.DataPath,
            provider.GetRequiredService<ILogger<PluginRepository>>()));
        // The database first: the takeover's first patch writes its history row (§4.6 "after the database is ready").
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DatabaseInitializer>());
        services.AddSingleton<IHostedService, WebBundleInstaller>();
        AcquisitionServices.AddHostedServices(services);
        ImportServices.AddHostedServices(services);
        services.AddSingleton<LibraryEventListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LibraryEventListener>());
        services.AddSingleton<RetentionEventListener>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RetentionEventListener>());
    }
}
