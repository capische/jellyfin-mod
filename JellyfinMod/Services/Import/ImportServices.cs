using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JellyfinMod.Services.Import;

/// <summary>The one registration of the Phase 5 import services, shared by the plugin and its integration host.</summary>
public static class ImportServices
{
    /// <summary>Registers import services. Host services (library manager and monitor, locks, database) come from elsewhere.</summary>
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<ImportTickGate>();
        services.AddSingleton<ClientSnapshotCache>();
        services.AddTransient<ClientSnapshotReader>();
        services.AddTransient<SeedReleaseService>();
        services.AddTransient<ImportService>();
        services.AddSingleton<ImportMonitor>();
    }

    /// <summary>Registers the download monitor.</summary>
    public static void AddHostedServices(IServiceCollection services) =>
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ImportMonitor>());
}
