using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>Whether watch history for one title arrived from Trakt for the requesting user (P7.Q16).</summary>
/// <param name="Installed">True while the stock Trakt plugin is installed and active.</param>
/// <param name="HasHistory">
/// True when JellyfinMod saw Trakt's history import for this movie or episode, or for any visible episode of this
/// series or season, for the requesting user. Always false while the plugin is absent.
/// </param>
/// <param name="LastSyncedAt">When that history last arrived, in UTC, or null.</param>
public sealed record TraktItemStatusDto(
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("hasHistory")] bool HasHistory,
    [property: JsonPropertyName("lastSyncedAt")] DateTime? LastSyncedAt);

/// <summary>One linked Jellyfin user as the Trakt plugin holds them (Phase 13). Never a token.</summary>
/// <param name="Name">The Jellyfin user name.</param>
/// <param name="State"><c>linked</c>, or <c>reconnect</c> when a token refresh failed and the plugin skips the user.</param>
/// <param name="Since">For <c>reconnect</c>: when the plugin's sign-in expired, in UTC.</param>
/// <param name="Collections">The plugin's "synchronize collections" switch for this user.</param>
/// <param name="PostWatched">The plugin's "post watched history" switch for this user.</param>
public sealed record TraktExportUserDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("since")] DateTime? Since,
    [property: JsonPropertyName("collections")] bool Collections,
    [property: JsonPropertyName("postWatched")] bool PostWatched);

/// <summary>The stock Trakt plugin's last export run, as the host recorded it (Phase 13).</summary>
/// <param name="Status">The host's <c>TaskCompletionStatus</c>: Completed, Failed, Cancelled or Aborted.</param>
/// <param name="StartedAt">When it started, in UTC.</param>
/// <param name="EndedAt">When it ended, in UTC.</param>
public sealed record TraktExportRunDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("endedAt")] DateTime EndedAt);

/// <summary>The missed-watch export's state for an administrator (Phase 13, <c>GET /JellyfinMod/Trakt/Export</c>).</summary>
/// <param name="Installed">True while the stock Trakt plugin is installed and active.</param>
/// <param name="Version">Its version, or null.</param>
/// <param name="MinimumVersion">The oldest Trakt plugin whose export JellyfinMod runs.</param>
/// <param name="AutoExport">The "send missed watches automatically" switch.</param>
/// <param name="Running">True while the plugin's export runs.</param>
/// <param name="Refusal">Why JellyfinMod will not start the export, or null (see <c>TraktExportRefusal</c>).</param>
/// <param name="LastRun">The plugin's last export run, or null when it never ran.</param>
/// <param name="LastRunConfirmed">
/// Whether the last run looked clean, best effort: the host said Completed and the plugin logged no Trakt error while it
/// ran (the plugin completes its task even when Trakt refuses a write). Null when it never ran or its log is still being
/// written. It only paces retries and tells the administrator; the next export sends whatever Trakt still lacks.
/// </param>
/// <param name="NextAttemptAt">After a run that was not clean, with the switch on: when JellyfinMod tries again, in UTC.</param>
/// <param name="Failures">Runs in a row that were not clean (this server start).</param>
/// <param name="QuietMinutes">How long after an episode ends JellyfinMod waits before it starts the export.</param>
/// <param name="Users">The Jellyfin users the plugin holds a Trakt sign-in for.</param>
public sealed record TraktExportStatusDto(
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("minimumVersion")] string MinimumVersion,
    [property: JsonPropertyName("autoExport")] bool AutoExport,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("refusal")] string? Refusal,
    [property: JsonPropertyName("lastRun")] TraktExportRunDto? LastRun,
    [property: JsonPropertyName("lastRunConfirmed")] bool? LastRunConfirmed,
    [property: JsonPropertyName("nextAttemptAt")] DateTime? NextAttemptAt,
    [property: JsonPropertyName("failures")] int Failures,
    [property: JsonPropertyName("quietMinutes")] int QuietMinutes,
    [property: JsonPropertyName("users")] IReadOnlyList<TraktExportUserDto> Users);

/// <summary>The administrator's change to the export switch (Phase 13).</summary>
/// <param name="AutoExport">Whether JellyfinMod runs the export by itself.</param>
public sealed record TraktExportSettingsRequest([property: JsonPropertyName("autoExport")] bool AutoExport);
