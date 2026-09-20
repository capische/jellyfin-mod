using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Installs the packaged web bundle once, at startup, before anything asks to be served (P7.S3).
/// </summary>
/// <remarks>
/// Extraction failure is never fatal. A server whose plugin cannot unpack its interface still runs Jellyfin, and
/// the reason is reported through Health rather than through a crash on boot.
/// </remarks>
public sealed class WebBundleInstaller(
    WebBundleStore store,
    WebRootTakeover takeover,
    IApplicationPaths paths,
    ILogger<WebBundleInstaller> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            await store.InstallAsync(configuration?.WebBundleGraceDays ?? 14, cancellationToken).ConfigureAwait(false);

            // Reconciled at every startup, not only when something changed: a host upgrade replaces index.html
            // behind our back, and a container recreate resets the whole web directory.
            await takeover.ReconcileAsync(
                configuration?.UiTakeoverEnabled ?? true,
                (paths as IServerApplicationPaths)?.WebPath,
                "startup",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "JellyfinMod could not install its web bundle; the stock interface is unchanged");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
