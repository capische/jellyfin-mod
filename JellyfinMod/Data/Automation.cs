using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>Stable automation decision kinds (P6.M2).</summary>
public static class AutomationDecisionKinds
{
    /// <summary>The target was searched.</summary>
    public const string Searched = "searched";

    /// <summary>The target was not searched or nothing was grabbed, with a reason.</summary>
    public const string Skipped = "skipped";

    /// <summary>A release was grabbed automatically.</summary>
    public const string Grabbed = "grabbed";

    /// <summary>An upgrade was planned and its grab submitted.</summary>
    public const string UpgradePlanned = "upgrade_planned";

    /// <summary>An upgrade finished.</summary>
    public const string UpgradeCompleted = "upgrade_completed";

    /// <summary>An indexer's circuit breaker opened.</summary>
    public const string BreakerOpened = "breaker_opened";

    /// <summary>A budget stopped further work.</summary>
    public const string BudgetExhausted = "budget_exhausted";
}

/// <summary>Stable automation reasons (P6 API contract).</summary>
public static class AutomationReasons
{
    /// <summary>Not due yet.</summary>
    public const string NotDue = "not_due";

    /// <summary>The target is not monitored.</summary>
    public const string Unmonitored = "unmonitored";

    /// <summary>The episode has not aired, or aired less than the delay ago.</summary>
    public const string Unaired = "unaired";

    /// <summary>The held best quality is at or above the cutoff.</summary>
    public const string AlreadyHeldAtCutoff = "already_held_at_cutoff";

    /// <summary>The profile does not allow upgrades.</summary>
    public const string UpgradeNotAllowed = "upgrade_not_allowed";

    /// <summary>The daily automatic grab budget or the per-entry budget is used up.</summary>
    public const string BudgetGrabs = "budget_grabs";

    /// <summary>Every indexer is out of daily queries.</summary>
    public const string BudgetIndexer = "budget_indexer";

    /// <summary>Every indexer's circuit breaker is open.</summary>
    public const string BreakerOpen = "breaker_open";

    /// <summary>The library mount is below its free-space floor.</summary>
    public const string FreeSpaceFloor = "free_space_floor";

    /// <summary>Too many imports are open.</summary>
    public const string TooManyOpenImports = "too_many_open_imports";

    /// <summary>The download client could not be reached.</summary>
    public const string ClientUnreachable = "client_unreachable";

    /// <summary>No candidate passed the profile.</summary>
    public const string NoEligibleCandidate = "no_eligible_candidate";

    /// <summary>The best candidate scored below the profile's minimum.</summary>
    public const string BelowMinimumScore = "below_minimum_score";

    /// <summary>The best candidate had fewer seeders than the profile's minimum.</summary>
    public const string BelowMinimumSeeders = "below_minimum_seeders";

    /// <summary>A grab for the target is still active.</summary>
    public const string ActiveGrabExists = "active_grab_exists";

    /// <summary>The release is blocklisted.</summary>
    public const string Blocklisted = "blocklisted";

    /// <summary>Keep protects the entry from replacement.</summary>
    public const string KeptEntry = "kept_entry";

    /// <summary>The host does not group episode versions; episode upgrades are off.</summary>
    public const string EpisodeVersionsUnsupported = "episode_versions_unsupported";

    /// <summary>The held file's quality cannot be read from its name, so it is not replaced.</summary>
    public const string HeldQualityUnknown = "held_quality_unknown";

    /// <summary>A reclaimed title is not acquired again unless the administrator allows it.</summary>
    public const string Reclaimed = "reclaimed";

    /// <summary>Specials are not acquired automatically.</summary>
    public const string SpecialExcluded = "special_excluded";

    /// <summary>A release was grabbed.</summary>
    public const string Grabbed = "grabbed";

    /// <summary>An upgrade was planned.</summary>
    public const string UpgradePlanned = "upgrade_planned";

    /// <summary>Automatic acquisition is not ready (no indexer, client or profile).</summary>
    public const string AcquisitionNotReady = "acquisition_not_ready";

    /// <summary>The search could not be completed.</summary>
    public const string SearchFailed = "search_failed";

    /// <summary>The grab was refused by the acquisition engine.</summary>
    public const string GrabRefused = "grab_refused";

    /// <summary>The upgrade's replacement is waiting for the retention executor's protections.</summary>
    public const string ReplacementBlocked = "replacement_blocked";
}

/// <summary>Per-target search schedule and backoff (P6.M2). One row per movie entry or episode, created lazily.</summary>
public sealed class AutomationTargetState
{
    /// <summary>Gets or sets the target: the episode id for an episode, otherwise the entry id.</summary>
    [Key]
    public Guid TargetId { get; set; }

    /// <summary>Gets or sets the entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets when the target is next due.</summary>
    public DateTime NextSearchAt { get; set; }

    /// <summary>Gets or sets when it was last searched.</summary>
    public DateTime? LastSearchedAt { get; set; }

    /// <summary>Gets or sets how many consecutive searches found nothing to grab.</summary>
    public int ConsecutiveEmpty { get; set; }

    /// <summary>Gets or sets the last decision reason.</summary>
    [MaxLength(48)]
    public string? LastOutcome { get; set; }

    /// <summary>Gets or sets the last automatic grab.</summary>
    public Guid? LastGrabId { get; set; }

    /// <summary>Gets or sets when the last automatic grab was made.</summary>
    public DateTime? LastAutoGrabAt { get; set; }

    /// <summary>Gets or sets the profile revision the backoff was computed under; a change resets it.</summary>
    public int ProfileRevisionSeen { get; set; }

    /// <summary>Gets or sets the air date the backoff was computed under; a change resets it.</summary>
    public DateTime? AirDateSeen { get; set; }

    /// <summary>Gets or sets an administrator's request to search on the next run, ignoring backoff.</summary>
    public DateTime? SearchNowRequestedAt { get; set; }

    /// <summary>Gets or sets when this series' metadata was last refreshed by automation (series entries only).</summary>
    public DateTime? MetadataRefreshedAt { get; set; }
}

/// <summary>One recorded automation decision (P6.M2). Pruned to the configured cap.</summary>
public sealed class AutomationDecision
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the run.</summary>
    public Guid RunId { get; set; }

    /// <summary>Gets or sets the target.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Gets or sets the entry.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>Gets or sets the episode.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the kind.</summary>
    [MaxLength(24)]
    public string Kind { get; set; } = AutomationDecisionKinds.Skipped;

    /// <summary>Gets or sets the stable reason.</summary>
    [MaxLength(48)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets bounded, administrator-only detail.</summary>
    [MaxLength(1024)]
    public string? Detail { get; set; }

    /// <summary>Gets or sets the indexer involved.</summary>
    public Guid? IndexerId { get; set; }

    /// <summary>Gets or sets the grab made.</summary>
    public Guid? GrabId { get; set; }

    /// <summary>Gets or sets when the decision was made.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Stable automation run statuses.</summary>
public static class AutomationRunStatuses
{
    /// <summary>In progress.</summary>
    public const string Running = "running";

    /// <summary>Finished.</summary>
    public const string Completed = "completed";

    /// <summary>The master switch was off; no indexer was touched.</summary>
    public const string Disabled = "disabled";

    /// <summary>Paused before searching, for example because the client is unreachable.</summary>
    public const string Paused = "paused";

    /// <summary>The process stopped before the run finished.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Stopped by an unexpected error.</summary>
    public const string Failed = "failed";
}

/// <summary>One automation run summary (P6.M2), in the same family as retention and reconciliation runs.</summary>
public sealed class AutomationRun
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets what started the run: <c>scheduled</c> or <c>manual</c>.</summary>
    [MaxLength(16)]
    public string Trigger { get; set; } = "scheduled";

    /// <summary>Gets or sets when it started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets when it ended.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the status.</summary>
    [MaxLength(16)]
    public string Status { get; set; } = AutomationRunStatuses.Running;

    /// <summary>Gets or sets how many targets were considered.</summary>
    public int TargetsConsidered { get; set; }

    /// <summary>Gets or sets how many targets were searched.</summary>
    public int Searched { get; set; }

    /// <summary>Gets or sets how many targets were skipped.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets how many grabs were made.</summary>
    public int Grabbed { get; set; }

    /// <summary>Gets or sets how many upgrades were planned.</summary>
    public int UpgradesPlanned { get; set; }

    /// <summary>Gets or sets the indexer queries made, per indexer, as a JSON object.</summary>
    public string QueriesByIndexerJson { get; set; } = "{}";

    /// <summary>Gets or sets bounded detail.</summary>
    [MaxLength(1024)]
    public string? Detail { get; set; }
}

/// <summary>Per-indexer daily query budget and circuit breaker (P6.M2). Manual searches count too.</summary>
public sealed class IndexerBudgetState
{
    /// <summary>Gets or sets the indexer.</summary>
    [Key]
    public Guid IndexerId { get; set; }

    /// <summary>Gets or sets the UTC day the counter belongs to.</summary>
    public DateTime Day { get; set; }

    /// <summary>Gets or sets the queries made that day.</summary>
    public int QueriesUsed { get; set; }

    /// <summary>Gets or sets when the last query was sent.</summary>
    public DateTime? LastQueryAt { get; set; }

    /// <summary>Gets or sets consecutive failed searches.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets until when the breaker keeps the indexer out of searches.</summary>
    public DateTime? BreakerOpenUntil { get; set; }
}

/// <summary>Stable upgrade states (P6.M5).</summary>
public static class UpgradeStates
{
    /// <summary>The grab for the better release was made.</summary>
    public const string Planned = "planned";

    /// <summary>Phase 5 is importing it.</summary>
    public const string Importing = "importing";

    /// <summary>The new version is bound.</summary>
    public const string Imported = "imported";

    /// <summary>A retention operation is replacing the superseded version.</summary>
    public const string Replacing = "replacing";

    /// <summary>Done: replaced, or added only.</summary>
    public const string Completed = "completed";

    /// <summary>Waiting on a protection; retried on the next tick.</summary>
    public const string Blocked = "blocked";

    /// <summary>The grab or the import failed.</summary>
    public const string Failed = "failed";

    /// <summary>Gets the open states.</summary>
    public static IReadOnlyList<string> Open { get; } = [Planned, Importing, Imported, Replacing, Blocked];
}

/// <summary>The bridge from an upgrade grab to its import and to the Phase 3 operation that replaces the old version (P6.M5).</summary>
public sealed class UpgradeOperation
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the target while open; unique, so a target has one open upgrade.</summary>
    [MaxLength(32)]
    public string? OpenTargetKey { get; set; }

    /// <summary>Gets or sets the binding the upgrade supersedes.</summary>
    public Guid SupersededBindingId { get; set; }

    /// <summary>Gets or sets the quality it supersedes.</summary>
    [MaxLength(32)]
    public string? SupersededQuality { get; set; }

    /// <summary>Gets or sets the new quality.</summary>
    [MaxLength(32)]
    public string? NewQuality { get; set; }

    /// <summary>Gets or sets the grab of the better release.</summary>
    public Guid NewGrabId { get; set; }

    /// <summary>Gets or sets the import of the better release.</summary>
    public Guid? NewImportOperationId { get; set; }

    /// <summary>Gets or sets the retention operation that removed the superseded version.</summary>
    public Guid? ReplacementRetentionOperationId { get; set; }

    /// <summary>Gets or sets <c>replace</c> or <c>add</c>.</summary>
    [MaxLength(8)]
    public string Mode { get; set; } = "replace";

    /// <summary>Gets or sets the state.</summary>
    [MaxLength(16)]
    public string State { get; set; } = UpgradeStates.Planned;

    /// <summary>Gets or sets the stable reason.</summary>
    [MaxLength(48)]
    public string? Reason { get; set; }

    /// <summary>Gets or sets when it was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when it last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Gets or sets when it ended.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Stable retention operation provenances (P6.M5).</summary>
public static class RetentionProvenances
{
    /// <summary>A normal reclaim after the watched rule and the window.</summary>
    public const string Retention = "retention";

    /// <summary>The superseded version of a completed upgrade.</summary>
    public const string UpgradeReplaced = "upgrade_replaced";
}
