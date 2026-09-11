using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Services;

/// <summary>Jellyfin scheduled-task surface for full catalog backfill and repair.</summary>
public sealed class CatalogBackfillTask(IServiceScopeFactory scopeFactory) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Reconcile JellyfinMod catalog";

    /// <inheritdoc />
    public string Key => "JellyfinModCatalogReconciliation";

    /// <inheritdoc />
    public string Description => "Backfills movies and series and repairs their native Jellyfin bindings.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CatalogBackfillRunner>()
            .RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
