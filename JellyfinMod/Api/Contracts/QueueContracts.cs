using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>The title a queue row belongs to.</summary>
public sealed record QueueEntryDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Year,
    [property: JsonPropertyName("posterPath"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PosterPath,
    [property: JsonPropertyName("jellyfinItemId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? JellyfinItemId,
    [property: JsonPropertyName("targetLibraryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? TargetLibraryId);

/// <summary>The episode a queue row belongs to, for series.</summary>
public sealed record QueueEpisodeDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("episodeNumber")] int EpisodeNumber,
    [property: JsonPropertyName("title")] string Title);

/// <summary>The download client of a row. Administrators only; never carries a credential.</summary>
public sealed record QueueClientDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("openUrl"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OpenUrl);

/// <summary>The seeding copy of a completed import, until it is released.</summary>
public sealed record QueueSeedingDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("ratio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Ratio,
    [property: JsonPropertyName("goalRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? GoalRatio,
    [property: JsonPropertyName("seedingSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SeedingSeconds,
    [property: JsonPropertyName("goalSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? GoalSeconds,
    [property: JsonPropertyName("waitingFor")] IReadOnlyList<string> WaitingFor,
    [property: JsonPropertyName("goalMetAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? GoalMetAt,
    [property: JsonPropertyName("libraryLinkPresent"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? LibraryLinkPresent,
    [property: JsonPropertyName("releaseEnabled")] bool ReleaseEnabled);

/// <summary>Administrator-only physical detail of a row.</summary>
public sealed record QueueAdminDetailDto(
    [property: JsonPropertyName("sourcePath"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SourcePath,
    [property: JsonPropertyName("sourceClientPath"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SourceClientPath,
    [property: JsonPropertyName("destinationPath"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DestinationPath,
    [property: JsonPropertyName("sourcePhysicalIdentity"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SourcePhysicalIdentity,
    [property: JsonPropertyName("destinationPhysicalIdentity"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DestinationPhysicalIdentity,
    [property: JsonPropertyName("hardlinkCountAfter"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? HardlinkCountAfter,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error,
    [property: JsonPropertyName("infoHash")] string InfoHash);

/// <summary>One queue row: a download, an import or a seeding copy (P5.I7).</summary>
public sealed record QueueRowDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("grabId")] Guid GrabId,
    [property: JsonPropertyName("entry"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] QueueEntryDto? Entry,
    [property: JsonPropertyName("episode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] QueueEpisodeDto? Episode,
    [property: JsonPropertyName("releaseTitle")] string ReleaseTitle,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("importState")] string ImportState,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Message,
    [property: JsonPropertyName("progress"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Progress,
    [property: JsonPropertyName("sizeBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SizeBytes,
    [property: JsonPropertyName("downloadedBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DownloadedBytes,
    [property: JsonPropertyName("downloadRateBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DownloadRateBytes,
    [property: JsonPropertyName("etaSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? EtaSeconds,
    [property: JsonPropertyName("stalledSince"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? StalledSince,
    [property: JsonPropertyName("observedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? ObservedAt,
    [property: JsonPropertyName("versionLabel"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VersionLabel,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("client"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] QueueClientDto? Client,
    [property: JsonPropertyName("seeding"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] QueueSeedingDto? Seeding,
    [property: JsonPropertyName("admin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueueAdminDetailDto? Admin);

/// <summary>Whether the download client answered the latest read.</summary>
public sealed record QueueClientStatusDto(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("checkedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CheckedAt,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason);

/// <summary>The response of <c>GET /JellyfinMod/Queue</c>.</summary>
public sealed record QueueResultDto(
    [property: JsonPropertyName("items")] IReadOnlyList<QueueRowDto> Items,
    [property: JsonPropertyName("totalRecordCount")] int TotalRecordCount,
    [property: JsonPropertyName("generatedAt")] DateTime GeneratedAt,
    [property: JsonPropertyName("clientStatus")] QueueClientStatusDto ClientStatus,
    [property: JsonPropertyName("importEnabled")] bool ImportEnabled,
    [property: JsonPropertyName("seedReleaseEnabled")] bool SeedReleaseEnabled)
{
    /// <summary>Gets whether automation runs and why not, for the queue banner (P6.M8); null on a host without automation.</summary>
    [JsonPropertyName("automation"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public QueueAutomationDto? Automation { get; init; }
}

/// <summary>The body of <c>DELETE /JellyfinMod/Queue/{id}</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class QueueRemoveRequest
{
    /// <summary>Gets or sets a value indicating whether the torrent and its data are removed from the client.</summary>
    [JsonPropertyName("removeFromClient")]
    public bool RemoveFromClient { get; set; }

    /// <summary>Gets or sets a value indicating whether the release is blocklisted.</summary>
    [JsonPropertyName("blocklist")]
    public bool Blocklist { get; set; }
}

/// <summary>Import settings (P5.I2). Stored in SQLite beside the acquisition settings.</summary>
public sealed record ImportSettingsDto(
    [property: JsonPropertyName("importEnabled")] bool ImportEnabled,
    [property: JsonPropertyName("seedReleaseEnabled")] bool SeedReleaseEnabled,
    [property: JsonPropertyName("seedFloorRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? SeedFloorRatio,
    [property: JsonPropertyName("seedFloorHours"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeedFloorHours,
    [property: JsonPropertyName("importPollSeconds")] int ImportPollSeconds,
    [property: JsonPropertyName("videoExtensions")] IReadOnlyList<string> VideoExtensions,
    [property: JsonPropertyName("stalledAfterHours")] int StalledAfterHours,
    [property: JsonPropertyName("scanTimeoutMinutes")] int ScanTimeoutMinutes,
    [property: JsonPropertyName("queueVisibleToUsers")] bool QueueVisibleToUsers,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>Import settings write contract; PATCH must echo the current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ImportSettingsRequest
{
    /// <summary>Gets or sets whether completed downloads are imported.</summary>
    [JsonPropertyName("importEnabled")] public bool ImportEnabled { get; set; } = true;

    /// <summary>Gets or sets whether seeding copies are released after their goal.</summary>
    [JsonPropertyName("seedReleaseEnabled")] public bool SeedReleaseEnabled { get; set; }

    /// <summary>Gets or sets the global ratio floor; null disables the ratio floor.</summary>
    [Range(0.0, 100.0), JsonPropertyName("seedFloorRatio")] public double? SeedFloorRatio { get; set; }

    /// <summary>Gets or sets the global seeding-time floor in hours; null disables the time floor.</summary>
    [Range(0, 8760), JsonPropertyName("seedFloorHours")] public int? SeedFloorHours { get; set; }

    /// <summary>Gets or sets the monitor's poll interval.</summary>
    [Range(1, 3600), JsonPropertyName("importPollSeconds")] public int ImportPollSeconds { get; set; } = 15;

    /// <summary>Gets or sets the allow-listed video extensions.</summary>
    [JsonPropertyName("videoExtensions")] public string[] VideoExtensions { get; set; } = [];

    /// <summary>Gets or sets after how many hours without progress a download shows as stalled.</summary>
    [Range(1, 8760), JsonPropertyName("stalledAfterHours")] public int StalledAfterHours { get; set; } = 24;

    /// <summary>Gets or sets how long a targeted scan may take.</summary>
    [Range(1, 1440), JsonPropertyName("scanTimeoutMinutes")] public int ScanTimeoutMinutes { get; set; } = 10;

    /// <summary>Gets or sets whether ordinary users see queue rows.</summary>
    [JsonPropertyName("queueVisibleToUsers")] public bool QueueVisibleToUsers { get; set; }

    /// <summary>Gets or sets the revision being replaced.</summary>
    [JsonPropertyName("revision")] public int? Revision { get; set; }
}

/// <summary>One path mapping of a download client.</summary>
public sealed record PathMappingDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("clientPathPrefix")] string ClientPathPrefix,
    [property: JsonPropertyName("localPathPrefix")] string LocalPathPrefix,
    [property: JsonPropertyName("verifiedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? VerifiedAt,
    [property: JsonPropertyName("verificationReason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VerificationReason);

/// <summary>One path mapping in a write; the list order is the mapping order.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PathMappingRequest
{
    /// <summary>Gets or sets the prefix as the client reports it.</summary>
    [Required, MaxLength(1024), JsonPropertyName("clientPathPrefix")] public string ClientPathPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the same prefix as Jellyfin sees it.</summary>
    [Required, MaxLength(1024), JsonPropertyName("localPathPrefix")] public string LocalPathPrefix { get; set; } = string.Empty;
}

/// <summary>Replaces a client's ordered path mappings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PathMappingsRequest
{
    /// <summary>Gets or sets the mappings in order.</summary>
    [JsonPropertyName("pathMappings")] public PathMappingRequest[] PathMappings { get; set; } = [];

    /// <summary>Gets or sets the client revision being replaced.</summary>
    [JsonPropertyName("revision")] public int? Revision { get; set; }
}

/// <summary>The body of <c>POST /Settings/DownloadClients/{id}/TestImportPath</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ImportPathTestRequest
{
    /// <summary>Gets or sets a folder or file path as the client reports it.</summary>
    [Required, MaxLength(4096), JsonPropertyName("clientPath")] public string ClientPath { get; set; } = string.Empty;
}

/// <summary>One library root's result in an import path test.</summary>
public sealed record ImportPathLibraryDto(
    [property: JsonPropertyName("libraryId")] Guid LibraryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("sameMount")] bool SameMount,
    [property: JsonPropertyName("linkProbe")] string LinkProbe);

/// <summary>The result of an import path test. Administrator-only; nothing is added to the client.</summary>
public sealed record ImportPathTestDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("localPath"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LocalPath,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("mountIdentity"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MountIdentity,
    [property: JsonPropertyName("libraries")] IReadOnlyList<ImportPathLibraryDto> Libraries);

/// <summary>An open seed release, for the disk-honesty view.</summary>
public sealed record SeedingDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("importOperationId")] Guid ImportOperationId,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("infoHash")] string InfoHash,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("goalRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? GoalRatio,
    [property: JsonPropertyName("goalRatioSource"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GoalRatioSource,
    [property: JsonPropertyName("goalSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? GoalSeconds,
    [property: JsonPropertyName("goalSecondsSource"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GoalSecondsSource,
    [property: JsonPropertyName("observedRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? ObservedRatio,
    [property: JsonPropertyName("observedSeedingSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ObservedSeedingSeconds,
    [property: JsonPropertyName("goalMetAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? GoalMetAt,
    [property: JsonPropertyName("waitingFor")] IReadOnlyList<string> WaitingFor,
    [property: JsonPropertyName("logicalBytes")] long LogicalBytes,
    [property: JsonPropertyName("libraryLinkPresent")] bool LibraryLinkPresent,
    [property: JsonPropertyName("seedingPath")] string SeedingPath,
    [property: JsonPropertyName("libraryPath")] string LibraryPath);

/// <summary>The public view of one import operation.</summary>
public sealed record ImportOperationDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("grabId")] Guid GrabId,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Message,
    [property: JsonPropertyName("releaseTitle")] string ReleaseTitle,
    [property: JsonPropertyName("versionLabel"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VersionLabel,
    [property: JsonPropertyName("progress"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Progress,
    [property: JsonPropertyName("nativeItemId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? NativeItemId,
    [property: JsonPropertyName("scanAttempts")] int ScanAttempts,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("completedDownloadAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CompletedDownloadAt,
    [property: JsonPropertyName("linkedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LinkedAt,
    [property: JsonPropertyName("scanRequestedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? ScanRequestedAt,
    [property: JsonPropertyName("boundAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? BoundAt,
    [property: JsonPropertyName("completedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CompletedAt,
    [property: JsonPropertyName("retryOfId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? RetryOfId,
    [property: JsonPropertyName("admin"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueueAdminDetailDto? Admin);
