using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Repairs bindings after Jellyfin has completed a successful full library scan.</summary>
public sealed class CatalogPostScanTask(
    IServiceScopeFactory scopeFactory,
    ILogger<CatalogPostScanTask> logger) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CatalogBackfillRunner>()
                .RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("already active", StringComparison.Ordinal))
        {
            logger.LogInformation("JellyfinMod skipped post-scan reconciliation because a full run is already active");
        }
    }
}
