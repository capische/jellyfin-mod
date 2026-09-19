using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Repairs bindings after Jellyfin has completed a successful full library scan.</summary>
public sealed class CatalogPostScanTask(
    IServiceScopeFactory scopeFactory,
    ILogger<CatalogPostScanTask> logger,
    ReconciliationRunGate? runGate = null) : ILibraryPostScanTask
{
    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CatalogBackfillRunner>()
                .RunAsync(progress, cancellationToken, confirmAbsence: true).ConfigureAwait(false);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("already active", StringComparison.Ordinal))
        {
            // The active run confirms absence for this scan as soon as it finishes (P2.R10).
            runGate?.RequestAbsencePass();
            logger.LogInformation("JellyfinMod deferred post-scan absence checking until the active run finishes");
        }
    }
}
