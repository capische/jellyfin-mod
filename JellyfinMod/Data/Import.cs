using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>Stable import operation states (P5.I1).</summary>
public static class ImportStates
{
    /// <summary>The grab was accepted and the download is not complete yet.</summary>
    public const string Waiting = "waiting";

    /// <summary>The download is complete and the file that belongs to the grab is being identified.</summary>
    public const string Identifying = "identifying";

    /// <summary>The destination is recorded and the hardlink is being created.</summary>
    public const string Linking = "linking";

    /// <summary>The hardlink exists and was verified; no native binding is known yet.</summary>
    public const string Linked = "linked";

    /// <summary>A targeted scan was requested; the operation waits for reconciliation to bind the file.</summary>
    public const string Scanning = "scanning";

    /// <summary>Reconciliation bound the imported file.</summary>
    public const string Completed = "completed";

    /// <summary>Stopped with a stable reason; an administrator can retry after fixing the cause.</summary>
    public const string Blocked = "blocked";

    /// <summary>Terminal failure of this operation; a retry creates a new operation for the same grab.</summary>
    public const string Failed = "failed";

    /// <summary>Removed from the queue by an administrator.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Gets the states that still own their grab.</summary>
    public static IReadOnlyList<string> Open { get; } = [Waiting, Identifying, Linking, Linked, Scanning, Blocked];

    /// <summary>Gets the states the monitor advances on every tick.</summary>
    public static IReadOnlyList<string> Active { get; } = [Waiting, Identifying, Linking, Linked, Scanning];
}

/// <summary>Stable import reasons shared by the API and the web message table (P5.I1).</summary>
public static class ImportReasons
{
    /// <summary>No mapping turns the client's path into a path this server can open.</summary>
    public const string PathUnmapped = "path_unmapped";

    /// <summary>The mapped source file does not exist.</summary>
    public const string SourceMissing = "source_missing";

    /// <summary>The mapped source file has a different size from the one the client reports.</summary>
    public const string SourceSizeMismatch = "source_size_mismatch";

    /// <summary>The torrent contains no allow-listed video file.</summary>
    public const string NoVideoFile = "no_video_file";

    /// <summary>More than one file could be the title.</summary>
    public const string AmbiguousFiles = "ambiguous_files";

    /// <summary>The torrent holds an archive, which is never extracted.</summary>
    public const string ArchiveUnsupported = "archive_unsupported";

    /// <summary>The file's episode numbering differs from the grabbed episode.</summary>
    public const string EpisodeMismatch = "episode_mismatch";

    /// <summary>The episode already has a playable file.</summary>
    public const string TargetExists = "target_exists";

    /// <summary>The source and the destination are not on the same mount.</summary>
    public const string CrossFilesystem = "cross_filesystem";

    /// <summary>The destination folder cannot be written.</summary>
    public const string DestinationNotWritable = "destination_not_writable";

    /// <summary>A different file already has the destination name.</summary>
    public const string DestinationCollision = "destination_collision";

    /// <summary>The target library has no usable root on the source's mount.</summary>
    public const string LibraryRootMissing = "library_root_missing";

    /// <summary>The scan did not bind the file in time.</summary>
    public const string ScanTimeout = "scan_timeout";

    /// <summary>The scan finished without reconciliation binding the imported file.</summary>
    public const string BindingNotObserved = "binding_not_observed";

    /// <summary>The download client could not be reached.</summary>
    public const string ClientUnreachable = "client_unreachable";

    /// <summary>The download client no longer holds the torrent.</summary>
    public const string TorrentMissing = "torrent_missing";

    /// <summary>Importing is turned off in the plugin settings.</summary>
    public const string ImportDisabled = "import_disabled";

    /// <summary>Removed from the queue.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The catalog entry or episode the grab targeted no longer exists.</summary>
    public const string TargetMissing = "target_missing";

    /// <summary>The configured download client no longer exists.</summary>
    public const string ClientMissing = "client_missing";
}

/// <summary>One durable import of one accepted grab (P5.I2).</summary>
/// <remarks>
/// The operation records every physical fact before acting on it, so recovery after a crash inspects the recorded
/// source and destination instead of guessing. It never stores a credential or a credential-bearing URL.
/// </remarks>
public sealed class ImportOperation
{
    /// <summary>Gets or sets the operation identity; also the identity of its one <c>imported</c> history event.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the Phase 4 grab this operation imports.</summary>
    public Guid GrabId { get; set; }

    /// <summary>Gets or sets the grab while this operation is open; unique, so a grab has one open import.</summary>
    [MaxLength(32)]
    public string? OpenGrabKey { get; set; }

    /// <summary>Gets or sets the target entry. Detached, never cascaded, when an entry is removed.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>Gets or sets the target episode for series acquisition.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the library the file is imported into.</summary>
    public Guid TargetLibraryId { get; set; }

    /// <summary>Gets or sets the download client that holds the torrent.</summary>
    public Guid DownloadClientId { get; set; }

    /// <summary>Gets or sets the normalized BitTorrent v1 infohash.</summary>
    [MaxLength(40)]
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw release title copied from the grab.</summary>
    [MaxLength(1024)]
    public string ReleaseTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the acquisition intent: <c>acquire</c> or <c>addVersion</c> (P6.M6).</summary>
    [MaxLength(16)]
    public string Intent { get; set; } = "acquire";

    /// <summary>Gets or sets the state.</summary>
    [MaxLength(16)]
    public string State { get; set; } = ImportStates.Waiting;

    /// <summary>Gets or sets the stable reason while blocked, failed or cancelled.</summary>
    [MaxLength(48)]
    public string? Reason { get; set; }

    /// <summary>Gets or sets bounded, administrator-only error detail.</summary>
    [MaxLength(1024)]
    public string? Error { get; set; }

    /// <summary>Gets or sets the reason last written to history, so a blocked tick writes one event per reason.</summary>
    [MaxLength(48)]
    public string? HistoryReason { get; set; }

    /// <summary>Gets or sets the last observed download fraction, 0 to 1; null until observed.</summary>
    public double? Progress { get; set; }

    /// <summary>Gets or sets the last observed wanted size in bytes.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the last observed downloaded bytes.</summary>
    public long? DownloadedBytes { get; set; }

    /// <summary>Gets or sets the last observed download rate in bytes per second.</summary>
    public long? DownloadRateBytes { get; set; }

    /// <summary>Gets or sets the last observed ETA in seconds; null when the client reports none.</summary>
    public long? EtaSeconds { get; set; }

    /// <summary>Gets or sets the client's own status word at the last observation.</summary>
    [MaxLength(24)]
    public string? ClientStatus { get; set; }

    /// <summary>Gets or sets when the client state columns were last observed with a change.</summary>
    public DateTime? ObservedAt { get; set; }

    /// <summary>Gets or sets when progress last increased; the stalled clock starts here.</summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>Gets or sets when the download was first seen stalled.</summary>
    public DateTime? StalledSince { get; set; }

    /// <summary>Gets or sets the chosen file's path as the client reports it.</summary>
    [MaxLength(4096)]
    public string? SourceClientPath { get; set; }

    /// <summary>Gets or sets the chosen file's path as this server sees it. Administrator-only.</summary>
    [MaxLength(4096)]
    public string? SourceLocalPath { get; set; }

    /// <summary>Gets or sets the source's device and inode identity.</summary>
    [MaxLength(96)]
    public string? SourcePhysicalIdentity { get; set; }

    /// <summary>Gets or sets the source's mount identity.</summary>
    [MaxLength(2048)]
    public string? SourceMountIdentity { get; set; }

    /// <summary>Gets or sets the source's logical size in bytes.</summary>
    public long? SourceLogicalBytes { get; set; }

    /// <summary>Gets or sets the library root the destination was placed under.</summary>
    [MaxLength(4096)]
    public string? DestinationRoot { get; set; }

    /// <summary>Gets or sets the destination path, recorded before the link is created. Administrator-only.</summary>
    [MaxLength(4096)]
    public string? DestinationPath { get; set; }

    /// <summary>Gets or sets the destination's device and inode identity after linking.</summary>
    [MaxLength(96)]
    public string? DestinationPhysicalIdentity { get; set; }

    /// <summary>Gets or sets the hardlink count observed after linking.</summary>
    public long? HardlinkCountAfter { get; set; }

    /// <summary>Gets or sets the Jellyfin item reconciliation bound to the destination.</summary>
    public Guid? NativeItemId { get; set; }

    /// <summary>Gets or sets the binding reconciliation created for the destination.</summary>
    public Guid? BindingId { get; set; }

    /// <summary>Gets or sets the version label placed in the file name, for movies.</summary>
    [MaxLength(64)]
    public string? VersionLabel { get; set; }

    /// <summary>Gets or sets how many targeted scans were requested.</summary>
    public int ScanAttempts { get; set; }

    /// <summary>Gets or sets when the operation was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the operation last changed state.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Gets or sets when the client first reported the download complete.</summary>
    public DateTime? CompletedDownloadAt { get; set; }

    /// <summary>Gets or sets when the hardlink was verified.</summary>
    public DateTime? LinkedAt { get; set; }

    /// <summary>Gets or sets when the last targeted scan was requested.</summary>
    public DateTime? ScanRequestedAt { get; set; }

    /// <summary>Gets or sets when reconciliation's binding was attributed to this operation.</summary>
    public DateTime? BoundAt { get; set; }

    /// <summary>Gets or sets when the operation completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets who cancelled the operation.</summary>
    public Guid? CancelledBy { get; set; }

    /// <summary>Gets or sets the operation this one replaced through Retry.</summary>
    public Guid? RetryOfId { get; set; }
}

/// <summary>Stable seed release states (P5.I6).</summary>
public static class SeedReleaseStates
{
    /// <summary>Seeding towards the effective goal.</summary>
    public const string Waiting = "waiting";

    /// <summary>The client was asked to remove the torrent and its data; the outcome is being inspected.</summary>
    public const string Removing = "removing";

    /// <summary>Terminal: released, or the seeding copy was already gone.</summary>
    public const string Completed = "completed";

    /// <summary>A precondition failed; nothing was removed.</summary>
    public const string Blocked = "blocked";

    /// <summary>Terminal: removed from the queue by an administrator.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Gets the open states.</summary>
    public static IReadOnlyList<string> Open { get; } = [Waiting, Removing, Blocked];
}

/// <summary>Stable seed release reasons.</summary>
public static class SeedReleaseReasons
{
    /// <summary>The effective goal is not met yet.</summary>
    public const string GoalUnmet = "seed_goal_unmet";

    /// <summary>The download is not complete in the client.</summary>
    public const string Incomplete = "seeding_incomplete";

    /// <summary>Seed release is turned off.</summary>
    public const string Disabled = "seed_release_disabled";

    /// <summary>The torrent does not carry this plugin's ownership labels.</summary>
    public const string NotOwned = "torrent_not_owned";

    /// <summary>A retention operation on the same file is still open.</summary>
    public const string RetentionOpen = "retention_operation_open";

    /// <summary>The seeding path lies inside a library root.</summary>
    public const string SeedingInsideLibrary = "seeding_path_inside_library";

    /// <summary>The library link is gone without a retention operation that removed it.</summary>
    public const string LibraryLinkUnexpected = "library_link_unexpected";

    /// <summary>The seeding file could not be inspected.</summary>
    public const string SeedingUnavailable = "seeding_path_unavailable";

    /// <summary>The download client could not be reached.</summary>
    public const string ClientUnreachable = "client_unreachable";

    /// <summary>The client removed the torrent but the seeding file is still on disk.</summary>
    public const string SeedingSurvived = "seeding_copy_survived";

    /// <summary>Released by the plugin after the goal.</summary>
    public const string Released = "released";

    /// <summary>The client no longer held the torrent; the plugin deleted nothing.</summary>
    public const string CopyMissing = "seeding_copy_missing";

    /// <summary>Removed from the queue.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>The plugin's ownership of one seeding copy after its import (P5.I6).</summary>
public sealed class SeedReleaseOperation
{
    /// <summary>Gets or sets the identity; also the identity of its one <c>seeding_released</c> history event.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the completed import this seeding copy belongs to; unique.</summary>
    public Guid ImportOperationId { get; set; }

    /// <summary>Gets or sets the grab that added the torrent.</summary>
    public Guid GrabId { get; set; }

    /// <summary>Gets or sets the entry.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>Gets or sets the episode.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the infohash.</summary>
    [MaxLength(40)]
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the client that holds the torrent.</summary>
    public Guid DownloadClientId { get; set; }

    /// <summary>Gets or sets the state.</summary>
    [MaxLength(16)]
    public string State { get; set; } = SeedReleaseStates.Waiting;

    /// <summary>Gets or sets the stable reason for the current state.</summary>
    [MaxLength(48)]
    public string? Reason { get; set; }

    /// <summary>Gets or sets bounded, administrator-only error detail.</summary>
    [MaxLength(1024)]
    public string? Error { get; set; }

    /// <summary>Gets or sets the effective ratio goal; null when no ratio component applies.</summary>
    public double? GoalRatio { get; set; }

    /// <summary>Gets or sets where the ratio goal came from: indexer, client or floor.</summary>
    [MaxLength(16)]
    public string? GoalRatioSource { get; set; }

    /// <summary>Gets or sets the effective seeding-time goal in seconds.</summary>
    public long? GoalSeconds { get; set; }

    /// <summary>Gets or sets where the seeding-time goal came from.</summary>
    [MaxLength(16)]
    public string? GoalSecondsSource { get; set; }

    /// <summary>Gets or sets the indexer ratio requirement snapshot from the grab.</summary>
    public double? IndexerRatio { get; set; }

    /// <summary>Gets or sets the indexer seeding-time requirement snapshot from the grab, in seconds.</summary>
    public long? IndexerSeconds { get; set; }

    /// <summary>Gets or sets the last observed ratio.</summary>
    public double? ObservedRatio { get; set; }

    /// <summary>Gets or sets the last observed seeding time in seconds.</summary>
    public long? ObservedSeedingSeconds { get; set; }

    /// <summary>Gets or sets when the effective goal was first observed met.</summary>
    public DateTime? GoalMetAt { get; set; }

    /// <summary>Gets or sets the seeding file inside the client's download directory. Administrator-only.</summary>
    [MaxLength(4096)]
    public string SeedingPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the seeding file's device and inode identity at import.</summary>
    [MaxLength(96)]
    public string SeedingPhysicalIdentity { get; set; } = string.Empty;

    /// <summary>Gets or sets the library hardlink created by the import. Administrator-only.</summary>
    [MaxLength(4096)]
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the logical size of the file.</summary>
    public long SourceLogicalBytes { get; set; }

    /// <summary>Gets or sets the link count of the seeding file right before removal.</summary>
    public long? HardlinkCountBefore { get; set; }

    /// <summary>Gets or sets physical bytes proven released. Zero while the library link remains; null unknown.</summary>
    public long? PhysicalBytesReleased { get; set; }

    /// <summary>Gets or sets the earlier retention reclaim this release freed the bytes of.</summary>
    public Guid? CreditedRetentionOperationId { get; set; }

    /// <summary>Gets or sets when the release was created.</summary>
    public DateTime PreparedAt { get; set; }

    /// <summary>Gets or sets when the operation last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Gets or sets when the client was asked to remove the torrent.</summary>
    public DateTime? RemovingAt { get; set; }

    /// <summary>Gets or sets when the removal was confirmed.</summary>
    public DateTime? RemovedAt { get; set; }

    /// <summary>Gets or sets when the operation ended.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>One ordered client-to-local path prefix mapping for a download client (P5.I2).</summary>
public sealed class DownloadClientPathMapping
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the download client.</summary>
    public Guid DownloadClientId { get; set; }

    /// <summary>Gets or sets the order; unique per client.</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the path prefix as the client reports it.</summary>
    [MaxLength(1024)]
    public string ClientPathPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the same prefix as this server sees it.</summary>
    [MaxLength(1024)]
    public string LocalPathPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets when a hardlink probe last succeeded.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Gets or sets the stable reason the last probe failed.</summary>
    [MaxLength(64)]
    public string? VerificationReason { get; set; }
}

/// <summary>A release an administrator never wants grabbed again (P5.I7).</summary>
public sealed class ReleaseBlocklistEntry
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the blocked infohash.</summary>
    [MaxLength(40)]
    public string? InfoHash { get; set; }

    /// <summary>Gets or sets the indexer that namespaces <see cref="SourceGuid"/>.</summary>
    public Guid? IndexerId { get; set; }

    /// <summary>Gets or sets the indexer-namespaced GUID.</summary>
    [MaxLength(1024)]
    public string? SourceGuid { get; set; }

    /// <summary>Gets or sets the raw release title.</summary>
    [MaxLength(1024)]
    public string RawTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry it was blocked for.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>Gets or sets the episode it was blocked for.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the stable reason.</summary>
    [MaxLength(48)]
    public string Reason { get; set; } = "queue_removed";

    /// <summary>Gets or sets when the entry was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the administrator who created it.</summary>
    public Guid CreatedByUserId { get; set; }
}
