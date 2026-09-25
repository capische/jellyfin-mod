using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Trakt;

/// <summary>What JellyfinMod knows about the stock Trakt plugin on this server (P7.Q16, PHASE7 §7.1.3).</summary>
/// <param name="Installed">True while the Trakt plugin is loaded and active.</param>
/// <param name="Version">The installed plugin's version, or null when it is absent.</param>
public sealed record TraktPluginInfo(bool Installed, string? Version);

/// <summary>
/// Reads the host's own plugin list and scheduled tasks for the stock Trakt plugin. JellyfinMod never reads that
/// plugin's configuration or credentials and never calls Trakt; it only asks the host what is installed and running.
/// </summary>
public sealed class TraktPluginState(IPluginManager plugins, ITaskManager tasks, ILogger<TraktPluginState> logger)
{
    /// <summary>
    /// The id of <c>jellyfin/jellyfin-plugin-trakt</c> (its <c>build.yaml</c> <c>guid</c>), confirmed from an installed
    /// copy's <c>GET /Plugins</c> entry in the Q16 evidence.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("4fe3201e-d6ae-4f2e-8917-e12bda571281");

    /// <summary>
    /// The <c>Key</c> of the Trakt plugin's <c>SyncFromTraktTask</c>, "Import watched states and playback progress from
    /// trakt.tv" — the only thing that writes Trakt's history into Jellyfin.
    /// </summary>
    public const string SyncTaskKey = "TraktSyncFromTraktTask";

    /// <summary>
    /// Describes the plugin as the host reports it now. Only an <see cref="PluginStatus.Active"/> copy counts: a copy
    /// disabled from the Dashboard, uninstalled, waiting for a restart, unsupported or failed is not writing history.
    /// </summary>
    /// <remarks>
    /// The host's plugin list is a live, mutable collection; it is copied before it is read, and a read that fails
    /// anyway answers "not installed" rather than failing Health or a detail page.
    /// </remarks>
    public TraktPluginInfo Describe()
    {
        try
        {
            var active = plugins.Plugins.ToArray()
                .Where(plugin => plugin.Id == PluginId && plugin.Manifest.Status == PluginStatus.Active)
                .OrderByDescending(plugin => plugin.Version).FirstOrDefault();
            return active is null ? new TraktPluginInfo(false, null) : new TraktPluginInfo(true, active.Version.ToString());
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            logger.LogDebug(error, "JellyfinMod could not read the host's plugin list; Trakt reported as not installed");
            return new TraktPluginInfo(false, null);
        }
    }

    /// <summary>
    /// True while the host runs the Trakt plugin's import task. Jellyfin saves user data with
    /// <c>UserDataSaveReason.Import</c> for other reasons too — its NFO parser does, when an NFO user is configured — so an
    /// <c>Import</c> is attributed to Trakt only while this task is running. Host state only.
    /// </summary>
    public bool IsImporting()
    {
        try
        {
            // The key first: a worker's State getter reads its cancellation source twice, so another task finishing
            // between the reads can throw. Only the Trakt task's own State is read.
            return tasks.ScheduledTasks.ToArray()
                .Where(worker => string.Equals(worker.ScheduledTask?.Key, SyncTaskKey, StringComparison.Ordinal))
                .Any(worker => worker.State == TaskState.Running);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException or ObjectDisposedException)
        {
            logger.LogDebug(error, "JellyfinMod could not read the host's scheduled tasks; the import is not attributed to Trakt");
            return false;
        }
    }
}
