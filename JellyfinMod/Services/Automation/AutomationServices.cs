using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Services.Automation;

/// <summary>The one registration of the Phase 6 automation services, shared by the plugin and its integration host.</summary>
public static class AutomationServices
{
    /// <summary>Registers automation services. Phase 3, 4 and 5 services come from their own registrations.</summary>
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<AutomationRunGate>();
        services.AddTransient<AutomationRunner>();
        services.AddTransient<UpgradeService>();
        services.AddTransient<SeriesMetadataRefresher>();
        services.AddTransient<AutomationStatusService>();
    }
}
