using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JellyfinMod.Services.Acquisition;

/// <summary>The one registration of the Phase 4 acquisition services, shared by the plugin and its integration host.</summary>
public static class AcquisitionServices
{
    /// <summary>Registers acquisition services. Host services (library manager, database, locks) come from elsewhere.</summary>
    public static void Add(IServiceCollection services, Func<string> dataPath)
    {
        // Indexer traffic uses a named client without automatic redirects, so every hop is checked against the
        // indexer's allowed hosts before a feed can make the plugin fetch anything (P4.A3).
        services.AddHttpClient(TorznabClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            });
        services.AddSingleton(_ => new AcquisitionSecretStore(dataPath()));
        services.AddSingleton(GrabHoldOptions.Default);
        services.AddSingleton(TorznabOptions.Default);
        services.AddSingleton<ReleaseSearchCache>();
        services.AddSingleton<GrabPayloadVault>();
        services.AddSingleton<GrabLocks>();
        services.AddTransient<TorznabClient>();
        services.AddTransient<IDownloadClientDriver, TransmissionDriver>();
        services.AddTransient<DownloadClientDrivers>();
        services.AddTransient<DownloadDestinationValidator>();
        services.AddTransient<AcquisitionConfiguration>();
        services.AddTransient<ReleaseSearchService>();
        services.AddTransient<GrabService>();
        services.AddSingleton<GrabDispatcher>();
    }

    /// <summary>Registers the dispatcher that submits held grabs and recovers unresolved ones after a restart.</summary>
    public static void AddHostedServices(IServiceCollection services) =>
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<GrabDispatcher>());
}
