using System.Collections;
using System.Reflection;
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
/// One Jellyfin user's link to Trakt as the stock Trakt plugin holds it in memory (P13.T1). Read-only, and never a secret:
/// only <em>whether</em> the plugin holds a token, never its value (user decision, Phase 13 Q2).
/// </summary>
/// <param name="UserId">The Jellyfin user.</param>
/// <param name="Linked">True while the plugin holds an access token for the user, so it scrobbles and exports for them.</param>
/// <param name="ReconnectNeeded">
/// True when the plugin has a refresh token but no access token: a token refresh failed and the plugin skips the user
/// until it is authorized again (<c>TraktApi.SetRequestHeaders</c> blanks the token before refreshing, v33).
/// </param>
/// <param name="TokenExpiresAt">When the plugin's access token expires (or expired), in UTC, or null.</param>
/// <param name="PostWatchedHistory">The plugin's "post watched history" switch: its export sends watched episodes.</param>
/// <param name="PostUnwatchedHistory">The plugin's "post unwatched history" switch: its export removes Trakt plays.</param>
/// <param name="SynchronizeCollections">The plugin's "synchronize collections" switch: its export updates the collection.</param>
public sealed record TraktLink(Guid UserId, bool Linked, bool ReconnectNeeded, DateTime? TokenExpiresAt,
    bool PostWatchedHistory, bool PostUnwatchedHistory, bool SynchronizeCollections);

/// <summary>
/// Reads the host's own plugin list and scheduled tasks for the stock Trakt plugin, and (Phase 13, user decision Q2) the
/// plugin's per-user link state read-only. JellyfinMod never calls Trakt for the plugin; it reads a token's value for one
/// call only, the linked account's profile (decision D49, <see cref="ReadProfileAccess"/>), on the server.
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
    /// The <c>Key</c> of the Trakt plugin's <c>SyncLibraryTask</c>, "Export library to trakt.tv": for every linked user it
    /// sends each episode played in Jellyfin that has no play on Trakt, with Jellyfin's <c>LastPlayedDate</c> (P13).
    /// </summary>
    public const string ExportTaskKey = "TraktSyncLibraryTask";

    /// <summary>
    /// The oldest Trakt plugin whose export JellyfinMod runs (Phase 13 Q6): 33 carries the pagination and export-matching
    /// fixes that keep the export from posting plays Trakt already has. It needs Jellyfin 12.1 or later.
    /// </summary>
    public static readonly Version MinimumExportVersion = new(33, 0, 0, 0);

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

    /// <summary>Reads a public property only when it exists with exactly the type <typeparamref name="T"/>.</summary>
    private static bool TryRead<T>(object source, string name, out T value)
    {
        value = default!;
        var property = source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(T) || property.GetIndexParameters().Length > 0) return false;
        var read = property.GetValue(source);
        if (read is null)
        {
            // Only a string may be null (a token never set); a value type cannot be.
            return typeof(T) == typeof(string);
        }

        if (read is not T typed) return false;
        value = typed;
        return true;
    }

    /// <summary>The host's worker for the Trakt plugin's export task, or null when the plugin or its task is absent.</summary>
    public IScheduledTaskWorker? FindExportTask()
    {
        try
        {
            return tasks.ScheduledTasks.ToArray()
                .FirstOrDefault(worker => string.Equals(worker.ScheduledTask?.Key, ExportTaskKey, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException or ObjectDisposedException)
        {
            logger.LogDebug(error, "JellyfinMod could not read the host's scheduled tasks; the Trakt export is reported absent");
            return null;
        }
    }

    /// <summary>
    /// The per-user links the active Trakt plugin holds in memory, or null when the plugin is absent or its settings do not
    /// have the shape of v31–v33 (then JellyfinMod reports "unknown" and starts nothing). Read through reflection on the
    /// plugin's own configuration object; nothing is written and no token value leaves this method.
    /// </summary>
    public IReadOnlyList<TraktLink>? ReadLinks()
    {
        try
        {
            var plugin = plugins.Plugins.ToArray()
                .Where(candidate => candidate.Id == PluginId && candidate.Manifest.Status == PluginStatus.Active)
                .OrderByDescending(candidate => candidate.Version).FirstOrDefault();
            if (plugin?.Instance is not IHasPluginConfiguration configured) return null;
            var configuration = configured.Configuration;
            if (configuration?.GetType().GetProperty("TraktUsers", BindingFlags.Public | BindingFlags.Instance)?.GetValue(configuration)
                is not IEnumerable users) return null;
            var links = new List<TraktLink>();
            foreach (var user in users)
            {
                if (user is null) continue;
                // Every field this relies on must be there with the type of v31–v33, or nothing is read (Codex review
                // P2-3): a missing or retyped "post unwatched history" must never read as off.
                if (!TryRead(user, "LinkedMbUserId", out Guid id) || !TryRead(user, "AccessToken", out string? access) ||
                    !TryRead(user, "RefreshToken", out string? refresh) || !TryRead(user, "AccessTokenExpiration", out DateTime expires) ||
                    !TryRead(user, "PostWatchedHistory", out bool postWatched) || !TryRead(user, "PostUnwatchedHistory", out bool postUnwatched) ||
                    !TryRead(user, "SynchronizeCollections", out bool collections))
                {
                    return null;
                }

                if (id == Guid.Empty) continue;
                var hasAccess = !string.IsNullOrWhiteSpace(access);
                var hasRefresh = !string.IsNullOrWhiteSpace(refresh);
                if (!hasAccess && !hasRefresh) continue; // added on the plugin's page but never authorized
                DateTime? expiresAt = expires == DateTime.MinValue ? null
                    : expires.Kind == DateTimeKind.Utc ? expires : DateTime.SpecifyKind(expires, DateTimeKind.Local).ToUniversalTime();
                links.Add(new TraktLink(id, hasAccess, !hasAccess, expiresAt, postWatched, postUnwatched, collections));
            }

            return links;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException or TargetException or TargetInvocationException or MethodAccessException)
        {
            logger.LogDebug(error, "JellyfinMod could not read the Trakt plugin's links; they are reported unknown");
            return null;
        }
    }

    /// <summary>
    /// What one read-only call to Trakt for a user's profile needs (Phase 13, decision D49): the plugin's public client id
    /// (its <c>TraktUris.ClientId</c> constant, an application identifier it sends with every request) and the access token
    /// the plugin holds for <paramref name="userId"/>. Server-side only: neither value is logged, returned or stored.
    /// Null when the plugin, its constant or a token is not there.
    /// </summary>
    internal (string ClientId, string AccessToken)? ReadProfileAccess(Guid userId)
    {
        try
        {
            var plugin = plugins.Plugins.ToArray()
                .Where(candidate => candidate.Id == PluginId && candidate.Manifest.Status == PluginStatus.Active)
                .OrderByDescending(candidate => candidate.Version).FirstOrDefault();
            if (plugin?.Instance is not IHasPluginConfiguration configured) return null;
            var clientId = plugin.Instance.GetType().Assembly.GetTypes()
                .FirstOrDefault(type => type.Name == "TraktUris")?
                .GetField("ClientId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(clientId)) return null;
            var configuration = configured.Configuration;
            if (configuration?.GetType().GetProperty("TraktUsers", BindingFlags.Public | BindingFlags.Instance)?.GetValue(configuration)
                is not IEnumerable traktUsers) return null;
            foreach (var user in traktUsers)
            {
                if (user is null || !TryRead(user, "LinkedMbUserId", out Guid id) || id != userId) continue;
                return TryRead(user, "AccessToken", out string? access) && !string.IsNullOrWhiteSpace(access) ? (clientId, access!) : null;
            }

            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException or TargetException or TargetInvocationException or MethodAccessException or ReflectionTypeLoadException or FieldAccessException)
        {
            logger.LogDebug("JellyfinMod could not read what the Trakt profile read needs: {Error}", error.GetType().Name);
            return null;
        }
    }
}
