using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>One automation run summary.</summary>
public sealed record AutomationRunDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("completedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CompletedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("targetsConsidered")] int TargetsConsidered,
    [property: JsonPropertyName("searched")] int Searched,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("grabbed")] int Grabbed,
    [property: JsonPropertyName("upgradesPlanned")] int UpgradesPlanned,
    [property: JsonPropertyName("queriesByIndexer")] IReadOnlyDictionary<string, int> QueriesByIndexer,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Detail);

/// <summary>Automation budgets as they stand now.</summary>
public sealed record AutomationBudgetsDto(
    [property: JsonPropertyName("autoGrabsUsedToday")] int AutoGrabsUsedToday,
    [property: JsonPropertyName("autoGrabBudget")] int AutoGrabBudget,
    [property: JsonPropertyName("openImports")] int OpenImports,
    [property: JsonPropertyName("maxConcurrentImports")] int MaxConcurrentImports,
    [property: JsonPropertyName("freeBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FreeBytes,
    [property: JsonPropertyName("freeFloorBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FreeFloorBytes);

/// <summary>One indexer's budget and breaker.</summary>
public sealed record AutomationIndexerDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("queriesUsedToday")] int QueriesUsedToday,
    [property: JsonPropertyName("dailyQueryBudget")] int DailyQueryBudget,
    [property: JsonPropertyName("minIntervalSeconds")] int MinIntervalSeconds,
    [property: JsonPropertyName("breakerOpenUntil"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? BreakerOpenUntil);

/// <summary>The response of <c>GET /JellyfinMod/Automation/Status</c>.</summary>
public sealed record AutomationStatusDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("pausedReasons")] IReadOnlyList<string> PausedReasons,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("nextRunAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? NextRunAt,
    [property: JsonPropertyName("lastRun"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] AutomationRunDto? LastRun,
    [property: JsonPropertyName("budgets")] AutomationBudgetsDto Budgets,
    [property: JsonPropertyName("indexers")] IReadOnlyList<AutomationIndexerDto> Indexers);

/// <summary>A compact automation state for the queue banner; readable by whoever may read the queue.</summary>
public sealed record QueueAutomationDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("pausedReasons")] IReadOnlyList<string> PausedReasons);

/// <summary>One automation decision.</summary>
public sealed record AutomationDecisionDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("targetId")] Guid TargetId,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Detail,
    [property: JsonPropertyName("indexerId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? IndexerId,
    [property: JsonPropertyName("grabId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? GrabId,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

/// <summary>A paged decision log.</summary>
public sealed record AutomationDecisionsDto(
    [property: JsonPropertyName("items")] IReadOnlyList<AutomationDecisionDto> Items,
    [property: JsonPropertyName("totalRecordCount")] int TotalRecordCount);

/// <summary>One target's schedule and upgrade state.</summary>
public sealed record AutomationTargetDto(
    [property: JsonPropertyName("targetId")] Guid TargetId,
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("nextSearchAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? NextSearchAt,
    [property: JsonPropertyName("lastSearchedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LastSearchedAt,
    [property: JsonPropertyName("consecutiveEmpty")] int ConsecutiveEmpty,
    [property: JsonPropertyName("lastOutcome"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastOutcome,
    [property: JsonPropertyName("searchNowRequested")] bool SearchNowRequested,
    [property: JsonPropertyName("heldBestQuality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? HeldBestQuality,
    [property: JsonPropertyName("cutoff"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Cutoff,
    [property: JsonPropertyName("upgradeEligible")] bool UpgradeEligible,
    [property: JsonPropertyName("blockedReason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BlockedReason);

/// <summary>Automation settings (P6.M2).</summary>
public sealed record AutomationSettingsDto(
    [property: JsonPropertyName("automationEnabled")] bool AutomationEnabled,
    [property: JsonPropertyName("automationIntervalHours")] int AutomationIntervalHours,
    [property: JsonPropertyName("automationBatchSize")] int AutomationBatchSize,
    [property: JsonPropertyName("newEpisodeDelayMinutes")] int NewEpisodeDelayMinutes,
    [property: JsonPropertyName("dailyAutoGrabBudget")] int DailyAutoGrabBudget,
    [property: JsonPropertyName("maxConcurrentImports")] int MaxConcurrentImports,
    [property: JsonPropertyName("freeSpaceFloorPercent")] int FreeSpaceFloorPercent,
    [property: JsonPropertyName("freeSpaceFloorBytes")] long FreeSpaceFloorBytes,
    [property: JsonPropertyName("decisionLogCap")] int DecisionLogCap,
    [property: JsonPropertyName("episodeUpgradesEnabled")] bool EpisodeUpgradesEnabled,
    [property: JsonPropertyName("reacquireReclaimed")] bool ReacquireReclaimed,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>Automation settings write contract; the request must echo the current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AutomationSettingsRequest
{
    /// <summary>Gets or sets the master switch.</summary>
    [JsonPropertyName("automationEnabled")] public bool AutomationEnabled { get; set; }

    /// <summary>Gets or sets the hours between runs.</summary>
    [Range(1, 168), JsonPropertyName("automationIntervalHours")] public int AutomationIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets the targets per run.</summary>
    [Range(1, 500), JsonPropertyName("automationBatchSize")] public int AutomationBatchSize { get; set; } = 40;

    /// <summary>Gets or sets the delay after air time.</summary>
    [Range(0, 10080), JsonPropertyName("newEpisodeDelayMinutes")] public int NewEpisodeDelayMinutes { get; set; } = 120;

    /// <summary>Gets or sets the daily automatic grab budget.</summary>
    [Range(0, 200), JsonPropertyName("dailyAutoGrabBudget")] public int DailyAutoGrabBudget { get; set; } = 6;

    /// <summary>Gets or sets the most open imports.</summary>
    [Range(0, 100), JsonPropertyName("maxConcurrentImports")] public int MaxConcurrentImports { get; set; } = 3;

    /// <summary>Gets or sets the percentage floor.</summary>
    [Range(0, 90), JsonPropertyName("freeSpaceFloorPercent")] public int FreeSpaceFloorPercent { get; set; } = 10;

    /// <summary>Gets or sets the byte floor.</summary>
    [Range(0, long.MaxValue), JsonPropertyName("freeSpaceFloorBytes")] public long FreeSpaceFloorBytes { get; set; } = 25_000_000_000;

    /// <summary>Gets or sets the decision log cap.</summary>
    [Range(100, 100_000), JsonPropertyName("decisionLogCap")] public int DecisionLogCap { get; set; } = 2000;

    /// <summary>Gets or sets whether episodes may be upgraded or gain versions.</summary>
    [JsonPropertyName("episodeUpgradesEnabled")] public bool EpisodeUpgradesEnabled { get; set; }

    /// <summary>Gets or sets whether a reclaimed monitored title is acquired again.</summary>
    [JsonPropertyName("reacquireReclaimed")] public bool ReacquireReclaimed { get; set; }

    /// <summary>Gets or sets the revision being replaced.</summary>
    [JsonPropertyName("revision")] public int? Revision { get; set; }
}

/// <summary>One held version of a movie or episode for the version selector (P6.M8).</summary>
public sealed record VersionDto(
    [property: JsonPropertyName("jellyfinItemId")] Guid JellyfinItemId,
    [property: JsonPropertyName("mediaSourceId")] string MediaSourceId,
    [property: JsonPropertyName("bindingId")] Guid BindingId,
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Label,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quality,
    [property: JsonPropertyName("resolution"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Resolution,
    [property: JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Width,
    [property: JsonPropertyName("height"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Height,
    [property: JsonPropertyName("videoCodec"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VideoCodec,
    [property: JsonPropertyName("videoRange"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? VideoRange,
    [property: JsonPropertyName("bitDepth"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? BitDepth,
    [property: JsonPropertyName("audioCodec"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AudioCodec,
    [property: JsonPropertyName("audioChannels"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? AudioChannels,
    [property: JsonPropertyName("sizeBytes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SizeBytes,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("retention"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] VersionRetentionDto? Retention)
{
    /// <summary>Gets a value indicating whether an administrator kept this file while other versions may go (P10).</summary>
    [JsonPropertyName("kept")]
    public bool Kept { get; init; }
}

/// <summary>A version's own retention state (P6.M7): seeding can hold one version while another is scheduled.</summary>
public sealed record VersionRetentionDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason);

/// <summary>Whether a title can be upgraded, and why not.</summary>
public sealed record UpgradeStateDto(
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("cutoff"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Cutoff,
    [property: JsonPropertyName("heldBest"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? HeldBest,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("blockedReason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BlockedReason,
    [property: JsonPropertyName("openUpgradeState"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OpenUpgradeState);
