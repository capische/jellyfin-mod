using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Installs the packaged web bundle once, at startup, before anything asks to be served (P7.S3).
/// </summary>
/// <remarks>
/// Extraction failure is never fatal. A server whose plugin cannot unpack its interface still runs Jellyfin, and
/// the reason is reported through Health rather than through a crash on boot.
/// </remarks>
public sealed class WebBundleInstaller(WebBundleStore store, ILogger<WebBundleInstaller> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var graceDays = Plugin.Instance?.Configuration.WebBundleGraceDays ?? 14;
            await store.InstallAsync(graceDays, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "JellyfinMod could not install its web bundle; the stock interface is unchanged");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
