using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>A write-only secret change. Reads never return a value, only whether one is configured.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SecretChangeRequest
{
    /// <summary>Gets or sets <c>unchanged</c>, <c>replace</c> or <c>clear</c>.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "unchanged";

    /// <summary>Gets or sets the replacement value; only valid with <c>replace</c>.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>One redacted Torznab indexer.</summary>
public sealed record IndexerSettingsDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("automateTitleMatches")] bool AutomateTitleMatches,
    [property: JsonPropertyName("categories")] IReadOnlyList<int> Categories,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("downloadHosts")] IReadOnlyList<string> DownloadHosts,
    [property: JsonPropertyName("minimumSeedRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? MinimumSeedRatio,
    [property: JsonPropertyName("minimumSeedMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MinimumSeedMinutes,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("capabilities"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] IndexerCapabilitiesDto? Capabilities,
    [property: JsonPropertyName("capabilitiesFetchedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CapabilitiesFetchedAt,
    [property: JsonPropertyName("lastError"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastError,
    [property: JsonPropertyName("minIntervalSeconds")] int MinIntervalSeconds = 10,
    [property: JsonPropertyName("dailyQueryBudget")] int DailyQueryBudget = 200,
    [property: JsonPropertyName("managedBy")] string ManagedBy = "manual",
    [property: JsonPropertyName("prowlarrSourceId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ProwlarrSourceId = null,
    [property: JsonPropertyName("prowlarrIndexerId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ProwlarrIndexerId = null,
    [property: JsonPropertyName("prowlarrRemovedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? ProwlarrRemovedAt = null,
    [property: JsonPropertyName("breakerOpenUntil"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? BreakerOpenUntil = null);

/// <summary>The safe subset of a verified Torznab capability document.</summary>
public sealed record IndexerCapabilitiesDto(
    [property: JsonPropertyName("movieSearch")] IReadOnlyList<string> MovieSearch,
    [property: JsonPropertyName("tvSearch")] IReadOnlyList<string> TvSearch,
    [property: JsonPropertyName("search")] IReadOnlyList<string> Search,
    [property: JsonPropertyName("categories")] IReadOnlyList<int> Categories,
    [property: JsonPropertyName("limitMax"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? LimitMax,
    [property: JsonPropertyName("limitDefault"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? LimitDefault);

/// <summary>Torznab indexer write contract. PATCH must echo the current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IndexerSettingsRequest
{
    /// <summary>Gets or sets the unique display name.</summary>
    [Required, MaxLength(128), JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Torznab endpoint, without credentials, query or fragment.</summary>
    [Required, MaxLength(1024), JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API-key change.</summary>
    [JsonPropertyName("apiKey")]
    public SecretChangeRequest ApiKey { get; set; } = new();

    /// <summary>Gets or sets whether searches use this indexer.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets whether automation may grab a release this indexer matched by title and year alone.
    /// </summary>
    [JsonPropertyName("automateTitleMatches")]
    public bool AutomateTitleMatches { get; set; }

    /// <summary>Gets or sets the numeric Torznab categories searched.</summary>
    [JsonPropertyName("categories")]
    public int[] Categories { get; set; } = [];

    /// <summary>Gets or sets the lower-first priority.</summary>
    [Range(0, 1000), JsonPropertyName("priority")]
    public int Priority { get; set; }

    /// <summary>Gets or sets extra host names allowed to serve torrent downloads.</summary>
    [JsonPropertyName("downloadHosts")]
    public string[] DownloadHosts { get; set; } = [];

    /// <summary>Gets or sets the tracker's minimum seed ratio.</summary>
    [Range(0.0, 100.0), JsonPropertyName("minimumSeedRatio")]
    public double? MinimumSeedRatio { get; set; }

    /// <summary>Gets or sets the tracker's minimum seeding time in minutes.</summary>
    [Range(0, 525600), JsonPropertyName("minimumSeedMinutes")]
    public int? MinimumSeedMinutes { get; set; }

    /// <summary>Gets or sets the minimum seconds between two searches of this indexer (P6.M2); null keeps the saved value.</summary>
    [Range(0, 3600), JsonPropertyName("minIntervalSeconds")]
    public int? MinIntervalSeconds { get; set; }

    /// <summary>Gets or sets the daily query budget (P6.M2); null keeps the saved value.</summary>
    [Range(0, 100000), JsonPropertyName("dailyQueryBudget")]
    public int? DailyQueryBudget { get; set; }

    /// <summary>Gets or sets the revision being replaced; required by PATCH.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>One redacted download client.</summary>
public sealed record DownloadClientSettingsDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("passwordConfigured")] bool PasswordConfigured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("downloadDirectory")] string DownloadDirectory,
    [property: JsonPropertyName("localDirectory")] string LocalDirectory,
    [property: JsonPropertyName("sameFilesystemLibraryIds")] IReadOnlyList<Guid> SameFilesystemLibraryIds,
    [property: JsonPropertyName("openUrl"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OpenUrl,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("clientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ClientVersion,
    [property: JsonPropertyName("apiVersion"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ApiVersion,
    [property: JsonPropertyName("verifiedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? VerifiedAt,
    [property: JsonPropertyName("lastError"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastError,
    [property: JsonPropertyName("pathMappings")] IReadOnlyList<PathMappingDto> PathMappings);

/// <summary>Download client write contract. PATCH must echo the current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DownloadClientSettingsRequest
{
    /// <summary>Gets or sets the unique display name.</summary>
    [Required, MaxLength(128), JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the driver kind; Phase 4 supports <c>transmission</c>.</summary>
    [Required, MaxLength(32), JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the RPC endpoint, without credentials, query or fragment.</summary>
    [Required, MaxLength(1024), JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional username.</summary>
    [MaxLength(256), JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password change.</summary>
    [JsonPropertyName("password")]
    public SecretChangeRequest Password { get; set; } = new();

    /// <summary>Gets or sets whether grabs may use this client.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the isolation label.</summary>
    [Required, MaxLength(64), JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the download directory as the client sees it.</summary>
    [Required, MaxLength(1024), JsonPropertyName("downloadDirectory")]
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the same directory as Jellyfin sees it.</summary>
    [Required, MaxLength(1024), JsonPropertyName("localDirectory")]
    public string LocalDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional credential-free web interface link.</summary>
    [MaxLength(1024), JsonPropertyName("openUrl")]
    public string? OpenUrl { get; set; }

    /// <summary>Gets or sets the ordered path mappings (P5.I2); null keeps the saved mappings.</summary>
    [JsonPropertyName("pathMappings")]
    public PathMappingRequest[]? PathMappings { get; set; }

    /// <summary>Gets or sets the revision being replaced; required by PATCH.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>One quality profile.</summary>
public sealed record QualityProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("qualities")] IReadOnlyList<string> Qualities,
    [property: JsonPropertyName("minimumBytesPerHour"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MinimumBytesPerHour,
    [property: JsonPropertyName("maximumBytesPerHour"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MaximumBytesPerHour,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("cutoff"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Cutoff = null,
    [property: JsonPropertyName("upgradeAllowed")] bool UpgradeAllowed = false,
    [property: JsonPropertyName("upgradeMode")] string UpgradeMode = "replace",
    [property: JsonPropertyName("minimumAutoScore"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MinimumAutoScore = null,
    [property: JsonPropertyName("minimumSeeders"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MinimumSeeders = null);

/// <summary>Quality profile write contract. PATCH must echo the current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QualityProfileRequest
{
    /// <summary>Gets or sets the unique display name.</summary>
    [Required, MaxLength(128), JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordered allowed quality identifiers, best first.</summary>
    [JsonPropertyName("qualities")]
    public string[] Qualities { get; set; } = [];

    /// <summary>Gets or sets the optional minimum size per runtime hour.</summary>
    [Range(1, long.MaxValue), JsonPropertyName("minimumBytesPerHour")]
    public long? MinimumBytesPerHour { get; set; }

    /// <summary>Gets or sets the optional maximum size per runtime hour.</summary>
    [Range(1, long.MaxValue), JsonPropertyName("maximumBytesPerHour")]
    public long? MaximumBytesPerHour { get; set; }

    /// <summary>Gets or sets the quality where upgrades stop; must be one of the allowed qualities (P6.M2).</summary>
    [MaxLength(32), JsonPropertyName("cutoff")]
    public string? Cutoff { get; set; }

    /// <summary>Gets or sets whether automation upgrades titles below the cutoff.</summary>
    [JsonPropertyName("upgradeAllowed")]
    public bool UpgradeAllowed { get; set; }

    /// <summary>Gets or sets <c>replace</c> or <c>add</c>.</summary>
    [JsonPropertyName("upgradeMode")]
    public string UpgradeMode { get; set; } = "replace";

    /// <summary>Gets or sets the minimum score an automatic grab needs.</summary>
    [Range(0, 100000), JsonPropertyName("minimumAutoScore")]
    public int? MinimumAutoScore { get; set; }

    /// <summary>Gets or sets the minimum seeders an automatic grab needs.</summary>
    [Range(0, 100000), JsonPropertyName("minimumSeeders")]
    public int? MinimumSeeders { get; set; }

    /// <summary>Gets or sets the revision being replaced; required by PATCH.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>The implemented quality vocabulary for the profile editor.</summary>
public sealed record QualityDefinitionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("resolution")] string Resolution);

/// <summary>Acquisition enablement, defaults and readiness.</summary>
public sealed record AcquisitionSettingsDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("downloadClientId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? DownloadClientId,
    [property: JsonPropertyName("defaultQualityProfileId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? DefaultQualityProfileId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers,
    [property: JsonPropertyName("holdSeconds")] int HoldSeconds,
    [property: JsonPropertyName("seedProtectionMatchesClient")] bool SeedProtectionMatchesClient,
    [property: JsonPropertyName("qualities")] IReadOnlyList<QualityDefinitionDto> Qualities);

/// <summary>Acquisition defaults write contract.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AcquisitionSettingsRequest
{
    /// <summary>Gets or sets whether manual grabs are enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the selected download client.</summary>
    [JsonPropertyName("downloadClientId")]
    public Guid? DownloadClientId { get; set; }

    /// <summary>Gets or sets the default quality profile.</summary>
    [JsonPropertyName("defaultQualityProfileId")]
    public Guid? DefaultQualityProfileId { get; set; }

    /// <summary>Gets or sets the revision being replaced.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>A bounded, credential-free connection test result.</summary>
public sealed record ConnectionTestDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version,
    [property: JsonPropertyName("apiVersion"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ApiVersion);

/// <summary>A Prowlarr source; the API key is write-only (P7.S9).</summary>
public sealed record ProwlarrSourceDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("syncIntervalMinutes")] int SyncIntervalMinutes,
    [property: JsonPropertyName("lastSyncAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LastSyncAt,
    [property: JsonPropertyName("lastSyncOutcome"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastSyncOutcome,
    [property: JsonPropertyName("lastError"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastError,
    [property: JsonPropertyName("consecutiveEmptySyncs")] int ConsecutiveEmptySyncs,
    [property: JsonPropertyName("indexerCount")] int IndexerCount,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>Creates or replaces a Prowlarr source.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProwlarrSourceRequest
{
    /// <summary>Gets or sets the unique name.</summary>
    [Required, MaxLength(64), JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL, without credentials or query.</summary>
    [Required, MaxLength(1024), JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API key change.</summary>
    [JsonPropertyName("apiKey")]
    public SecretChangeRequest ApiKey { get; set; } = new();

    /// <summary>Gets or sets whether the scheduled sync runs.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the minutes between scheduled syncs.</summary>
    [Range(15, 10080), JsonPropertyName("syncIntervalMinutes")]
    public int SyncIntervalMinutes { get; set; } = 360;

    /// <summary>Gets or sets the revision the change was made against.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}
