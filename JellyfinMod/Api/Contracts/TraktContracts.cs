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
/// <param name="UserId">The Jellyfin user's id, for the Trakt plugin's own Deauthorize (Disconnect, D48).</param>
/// <param name="TraktUsername">The Trakt username, read by the server (D49), or null.</param>
/// <param name="AvatarUrl">The Trakt avatar's http(s) address, or null.</param>
public sealed record TraktExportUserDto(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("since")] DateTime? Since,
    [property: JsonPropertyName("collections")] bool Collections,
    [property: JsonPropertyName("postWatched")] bool PostWatched,
    [property: JsonPropertyName("traktUsername")] string? TraktUsername,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl);

/// <summary>One export run of Settings › Trakt's Activity (Phase 13, D50, D73), newest first.</summary>
/// <param name="StartedAt">When it started, in UTC.</param>
/// <param name="EndedAt">When it ended, in UTC.</param>
/// <param name="Status">The host's result: Completed, Failed, Cancelled or Aborted.</param>
/// <param name="Clean">Completed with no Trakt error logged while it ran.</param>
/// <param name="Why"><c>watch</c>, <c>reconnect</c>, <c>daily</c>, <c>retry</c>, <c>sync_now</c>, <c>first</c>, or null when
/// JellyfinMod did not start it.</param>
/// <param name="EpisodesWatched">Episodes the plugin sent as watched (null when its log had no count lines for the run).</param>
/// <param name="MoviesWatched">Movies it sent as watched.</param>
/// <param name="EpisodesCollected">Episodes it added to the Trakt collection.</param>
/// <param name="MoviesCollected">Movies it added to the Trakt collection.</param>
/// <param name="Error">The Trakt error in a clause, or null.</param>
/// <param name="Kind"><c>import</c> for a run of the Trakt plugin's import (the watched counts are what it marked watched in
/// Jellyfin), left out for an export.</param>
/// <param name="ProgressUpdated">For an import: playback positions it set in Jellyfin.</param>
public sealed record TraktExportActivityDto(
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("endedAt")] DateTime EndedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("clean")] bool Clean,
    [property: JsonPropertyName("why")] string? Why,
    [property: JsonPropertyName("episodesWatched")] int? EpisodesWatched,
    [property: JsonPropertyName("moviesWatched")] int? MoviesWatched,
    [property: JsonPropertyName("episodesCollected")] int? EpisodesCollected,
    [property: JsonPropertyName("moviesCollected")] int? MoviesCollected,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("progressUpdated")] int? ProgressUpdated);

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
/// <param name="Activity">The last 20 runs, newest first (D50).</param>
/// <param name="RunningWhy">While a run JellyfinMod started goes: why it started it (as <c>why</c> above), or null.</param>
/// <param name="ImportEnabled">The "also import watches from Trakt" switch (on by default).</param>
/// <param name="ImportRefusal">Why JellyfinMod will not run the Trakt plugin's import, or null; <c>unwatched_import</c> when
/// a linked user's "skip unwatched import" is off in the Trakt plugin.</param>
/// <param name="ImportRunning">True while the Trakt plugin's import runs.</param>
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
    [property: JsonPropertyName("users")] IReadOnlyList<TraktExportUserDto> Users,
    [property: JsonPropertyName("activity")] IReadOnlyList<TraktExportActivityDto> Activity,
    [property: JsonPropertyName("runningWhy")] string? RunningWhy,
    [property: JsonPropertyName("importEnabled")] bool ImportEnabled,
    [property: JsonPropertyName("importRefusal")] string? ImportRefusal,
    [property: JsonPropertyName("importRunning")] bool ImportRunning);

/// <summary>The administrator's change to the export switches (Phase 13); a switch left out keeps its value.</summary>
/// <param name="AutoExport">Whether JellyfinMod runs the export by itself.</param>
/// <param name="Import">Whether JellyfinMod also runs the Trakt plugin's import.</param>
public sealed record TraktExportSettingsRequest(
    [property: JsonPropertyName("autoExport")] bool? AutoExport,
    [property: JsonPropertyName("import")] bool? Import = null);
