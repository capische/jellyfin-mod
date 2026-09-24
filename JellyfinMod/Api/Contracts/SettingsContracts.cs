using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>Discovery (TMDB) settings; the token is write-only (P7.S7).</summary>
public sealed record DiscoverySettingsDto(
    [property: JsonPropertyName("tokenConfigured")] bool TokenConfigured,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("verifiedAt")] DateTime? VerifiedAt,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>A change to the discovery settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DiscoverySettingsRequest
{
    /// <summary>Gets or sets the token change.</summary>
    [JsonPropertyName("token")]
    public SecretChangeRequest Token { get; set; } = new();

    /// <summary>Gets or sets the revision the change was made against.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>Where seed protection reads Transmission from; the password is write-only (P7.S7).</summary>
public sealed record SeedProtectionSettingsDto(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("rpcUrl")] string? RpcUrl,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("passwordConfigured")] bool PasswordConfigured,
    [property: JsonPropertyName("effectiveRpcUrl")] string? EffectiveRpcUrl,
    [property: JsonPropertyName("matchesAcquisitionClient")] bool MatchesAcquisitionClient,
    [property: JsonPropertyName("legacyXmlEndpoint")] bool LegacyXmlEndpoint,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>A change to the seed-protection source.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SeedProtectionSettingsRequest
{
    /// <summary>Gets or sets <c>acquisitionClient</c> or <c>separate</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the separate RPC endpoint.</summary>
    [JsonPropertyName("rpcUrl")]
    public string? RpcUrl { get; set; }

    /// <summary>Gets or sets the separate RPC username.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets the separate password change.</summary>
    [JsonPropertyName("password")]
    public SecretChangeRequest Password { get; set; } = new();

    /// <summary>Gets or sets the revision the change was made against.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>Retention settings, stored in the XML configuration behind a typed endpoint (P7.S7).</summary>
public sealed record RetentionSettingsDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("reclaimAfterDays")] int ReclaimAfterDays,
    [property: JsonPropertyName("watchedUserMode")] string WatchedUserMode,
    [property: JsonPropertyName("selectedUserId")] Guid? SelectedUserId,
    [property: JsonPropertyName("selectedUserName")] string? SelectedUserName,
    [property: JsonPropertyName("selectedUserMissing")] bool SelectedUserMissing,
    [property: JsonPropertyName("exemptFavourites")] bool ExemptFavourites,
    [property: JsonPropertyName("testWindowMinutes")] int TestWindowMinutes,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>A change to the retention settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RetentionSettingsRequest
{
    /// <summary>Gets or sets whether retention may reclaim files.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the days after finishing before a file is reclaimed.</summary>
    [JsonPropertyName("reclaimAfterDays")]
    public int ReclaimAfterDays { get; set; }

    /// <summary>Gets or sets <c>allUsers</c>, <c>selectedUser</c> or <c>anyUser</c>.</summary>
    [JsonPropertyName("watchedUserMode")]
    public string WatchedUserMode { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected user for <c>selectedUser</c>.</summary>
    [JsonPropertyName("selectedUserId")]
    public Guid? SelectedUserId { get; set; }

    /// <summary>Gets or sets whether favourites are exempt.</summary>
    [JsonPropertyName("exemptFavourites")]
    public bool ExemptFavourites { get; set; }

    /// <summary>Gets or sets the revision the change was made against.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>One settings area's readiness in the Overview.</summary>
public sealed record SettingsAreaDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers,
    [property: JsonPropertyName("revision")] int? Revision);

/// <summary>One first-run setup step (P7.S10).</summary>
public sealed record SetupStepDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("optional")] bool Optional,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

/// <summary>The first-run setup state, derived from readiness rather than stored progress.</summary>
public sealed record SetupStateDto(
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt,
    [property: JsonPropertyName("dismissedAt")] DateTime? DismissedAt,
    [property: JsonPropertyName("steps")] IReadOnlyList<SetupStepDto> Steps);
