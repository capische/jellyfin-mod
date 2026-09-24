using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>One administrator-configured Torznab source (P4.A2).</summary>
/// <remarks>
/// Credentials are never stored on this row: <see cref="ApiKeySecretRef"/> is an opaque reference into the
/// acquisition secret store, so database backups, diagnostics and DTOs never carry the key itself.
/// </remarks>
public sealed class AcquisitionIndexer
{
    /// <summary>Gets or sets the stable identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the unique display name.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Torznab API endpoint, without credentials.</summary>
    [MaxLength(1024)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the opaque secret-store reference of the API key, when one is configured.</summary>
    [MaxLength(64)]
    public string? ApiKeySecretRef { get; set; }

    /// <summary>Gets or sets a value indicating whether searches include this source.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether automation may grab releases this indexer could only match by
    /// title and year. Off by default: most public trackers advertise no id search, so a text match is all they
    /// offer, and an automatic grab of a wrong match would download the wrong title (user decision 2026-09-20).
    /// </summary>
    public bool AutomateTitleMatches { get; set; }

    /// <summary>Gets or sets the comma-separated numeric Torznab categories searched.</summary>
    [MaxLength(512)]
    public string Categories { get; set; } = string.Empty;

    /// <summary>Gets or sets the lower-first priority used as a documented score tie-breaker.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets comma-separated extra hosts that may serve this indexer's torrent downloads.</summary>
    /// <remarks>The base endpoint's own host is always allowed; nothing else is fetched.</remarks>
    [MaxLength(1024)]
    public string DownloadHosts { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured minimum seed ratio floor, when the tracker requires one.</summary>
    public double? MinimumSeedRatio { get; set; }

    /// <summary>Gets or sets the configured minimum seeding time floor in minutes.</summary>
    public int? MinimumSeedMinutes { get; set; }

    /// <summary>Gets or sets the configuration revision; any change invalidates capabilities and searches.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>Gets or sets the minimum seconds between two searches of this indexer, manual ones included (P6.M2).</summary>
    public int MinIntervalSeconds { get; set; } = 10;

    /// <summary>Gets or sets the daily query budget, manual searches included (P6.M2).</summary>
    public int DailyQueryBudget { get; set; } = 200;

    /// <summary>Gets or sets the last verified capability snapshot as JSON.</summary>
    public string? CapabilitiesJson { get; set; }

    /// <summary>Gets or sets when capabilities were last verified.</summary>
    public DateTime? CapabilitiesFetchedAt { get; set; }

    /// <summary>Gets or sets the configuration revision the capabilities belong to.</summary>
    public int? VerifiedRevision { get; set; }

    /// <summary>Gets or sets the last stable capability failure code, when the latest check failed.</summary>
    [MaxLength(64)]
    public string? LastError { get; set; }
}

/// <summary>The download client that receives manual grabs (P4.A2). Phase 4 supports Transmission RPC.</summary>
public sealed class AcquisitionDownloadClient
{
    /// <summary>Gets or sets the stable identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the unique display name.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the driver kind that speaks to this client.</summary>
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the API base URL, without credentials.</summary>
    [MaxLength(1024)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional username. Not secret.</summary>
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the opaque secret-store reference of the password, when one is configured.</summary>
    [MaxLength(64)]
    public string? PasswordSecretRef { get; set; }

    /// <summary>Gets or sets a value indicating whether this client may receive grabs.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the label every grab is added under; the isolation marker in the client.</summary>
    [MaxLength(64)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the isolated download directory as the client sees it.</summary>
    [MaxLength(1024)]
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the same directory as this Jellyfin process sees it, used to prove that downloads and the
    /// library share one filesystem so Phase 5 can import by hardlink (user decision 6).
    /// </summary>
    [MaxLength(1024)]
    public string LocalDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the comma-separated libraries whose roots shared the directory's mount when verified.</summary>
    [MaxLength(4096)]
    public string VerifiedLibraryIds { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional credential-free link an administrator can open after a handoff.</summary>
    [MaxLength(1024)]
    public string? OpenUrl { get; set; }

    /// <summary>Gets or sets the configuration revision.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>Gets or sets the configuration revision last verified by a connection test.</summary>
    public int? VerifiedRevision { get; set; }

    /// <summary>Gets or sets the verified application version.</summary>
    [MaxLength(64)]
    public string? ClientVersion { get; set; }

    /// <summary>Gets or sets the verified API version.</summary>
    [MaxLength(64)]
    public string? ApiVersion { get; set; }

    /// <summary>Gets or sets when the client was last verified.</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Gets or sets the last stable connection failure code.</summary>
    [MaxLength(64)]
    public string? LastError { get; set; }
}

/// <summary>An ordered, revisioned quality profile for manual acquisition (P4.A2/A4).</summary>
public sealed class AcquisitionQualityProfile
{
    /// <summary>Gets or sets the stable identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the unique display name.</summary>
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordered allowed quality identifiers as a JSON array, best first.</summary>
    public string QualitiesJson { get; set; } = "[]";

    /// <summary>Gets or sets the optional minimum size per runtime hour.</summary>
    public long? MinimumBytesPerHour { get; set; }

    /// <summary>Gets or sets the optional maximum size per runtime hour.</summary>
    public long? MaximumBytesPerHour { get; set; }

    /// <summary>Gets or sets the profile revision; searches evaluated under an older revision cannot grab.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>Gets or sets the quality at which upgrades stop; one of the allowed qualities (P6.M2).</summary>
    [MaxLength(32)]
    public string? Cutoff { get; set; }

    /// <summary>Gets or sets a value indicating whether automation upgrades titles below the cutoff.</summary>
    public bool UpgradeAllowed { get; set; }

    /// <summary>Gets or sets <c>replace</c> (the new version replaces the old) or <c>add</c> (both are kept).</summary>
    [MaxLength(8)]
    public string UpgradeMode { get; set; } = "replace";

    /// <summary>Gets or sets the minimum score an automatic grab needs.</summary>
    public int? MinimumAutoScore { get; set; }

    /// <summary>Gets or sets the minimum seeders an automatic grab needs.</summary>
    public int? MinimumSeeders { get; set; }
}

/// <summary>Singleton acquisition enablement and defaults (P4.A2).</summary>
public sealed class AcquisitionSettings
{
    /// <summary>Gets the singleton identity.</summary>
    public static Guid SingletonId { get; } = Guid.Parse("86f7c391-0f54-4a11-bc30-b1d11b9061f4");

    /// <summary>Gets or sets the singleton identity.</summary>
    public Guid Id { get; set; } = SingletonId;

    /// <summary>Gets or sets a value indicating whether manual grabs are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the selected download client.</summary>
    public Guid? DownloadClientId { get; set; }

    /// <summary>Gets or sets the default quality profile inherited by entries without their own.</summary>
    public Guid? DefaultQualityProfileId { get; set; }

    /// <summary>Gets or sets the settings revision.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether completed downloads are imported (P5.I2).</summary>
    public bool ImportEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin removes a seeding copy once its goals are met (P5.I6).
    /// Off by default until the isolated acceptance run passes (PHASE5 open question 3).
    /// </summary>
    public bool SeedReleaseEnabled { get; set; }

    /// <summary>Gets or sets the global ratio floor; met when either floor component is reached.</summary>
    public double? SeedFloorRatio { get; set; } = 1.0;

    /// <summary>Gets or sets the global seeding-time floor in hours.</summary>
    public int? SeedFloorHours { get; set; } = 168;

    /// <summary>Gets or sets how often the import monitor polls the client while work is open.</summary>
    public int ImportPollSeconds { get; set; } = 15;

    /// <summary>Gets or sets the comma-separated allow-listed video file extensions.</summary>
    [MaxLength(256)]
    public string VideoExtensions { get; set; } = ImportDefaults.VideoExtensions;

    /// <summary>Gets or sets after how many hours without progress a download shows as stalled.</summary>
    public int StalledAfterHours { get; set; } = 24;

    /// <summary>Gets or sets how long a targeted scan may take before it is requested again.</summary>
    public int ScanTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether ordinary users see queue rows for libraries they can access
    /// (PHASE5 open question 2). Off by default: the queue is administrator-only like search and grab.
    /// </summary>
    public bool QueueVisibleToUsers { get; set; }

    /// <summary>Gets or sets the import settings revision.</summary>
    public int ImportRevision { get; set; } = 1;

    /// <summary>Gets or sets the automation master switch (P6.M2). Off after migration regardless of earlier state.</summary>
    public bool AutomationEnabled { get; set; }

    /// <summary>Gets or sets the hours between scheduled automation runs.</summary>
    public int AutomationIntervalHours { get; set; } = 6;

    /// <summary>Gets or sets how many due targets one run processes.</summary>
    public int AutomationBatchSize { get; set; } = 40;

    /// <summary>Gets or sets how long after air time an episode is first searched.</summary>
    public int NewEpisodeDelayMinutes { get; set; } = 120;

    /// <summary>Gets or sets the daily automatic grab budget.</summary>
    public int DailyAutoGrabBudget { get; set; } = 6;

    /// <summary>Gets or sets the most imports that may be open before automation stops grabbing.</summary>
    public int MaxConcurrentImports { get; set; } = 3;

    /// <summary>Gets or sets the free-space floor as a percentage of the library mount.</summary>
    public int FreeSpaceFloorPercent { get; set; } = 10;

    /// <summary>Gets or sets the free-space floor in bytes; the larger floor applies.</summary>
    public long FreeSpaceFloorBytes { get; set; } = 25_000_000_000;

    /// <summary>Gets or sets how many decisions the log keeps.</summary>
    public int DecisionLogCap { get; set; } = 2000;

    /// <summary>Gets or sets the secret-store reference for the TMDB Read Access Token (P7.S7).</summary>
    [MaxLength(64)]
    public string? TmdbReadAccessTokenRef { get; set; }

    /// <summary>Gets or sets the discovery settings revision.</summary>
    public int DiscoveryRevision { get; set; } = 1;

    /// <summary>Gets or sets the discovery revision the last passing TMDB test verified.</summary>
    public int? DiscoveryVerifiedRevision { get; set; }

    /// <summary>Gets or sets when the TMDB test last passed.</summary>
    public DateTime? DiscoveryVerifiedAt { get; set; }

    /// <summary>
    /// Gets or sets where seed protection reads Transmission from: <c>acquisitionClient</c> (the selected download
    /// client, PHASE7 default 12) or <c>separate</c> (its own endpoint below).
    /// </summary>
    [MaxLength(32)]
    public string SeedProtectionSource { get; set; } = SeedProtectionSources.AcquisitionClient;

    /// <summary>Gets or sets the separate seed-protection RPC endpoint.</summary>
    [MaxLength(2048)]
    public string? SeedProtectionRpcUrl { get; set; }

    /// <summary>Gets or sets the separate seed-protection RPC username.</summary>
    [MaxLength(256)]
    public string? SeedProtectionUsername { get; set; }

    /// <summary>Gets or sets the secret-store reference for the separate seed-protection password.</summary>
    [MaxLength(64)]
    public string? SeedProtectionPasswordRef { get; set; }

    /// <summary>Gets or sets the seed-protection settings revision.</summary>
    public int SeedProtectionRevision { get; set; } = 1;

    /// <summary>Gets or sets the revision of the XML-held retention settings edited through the typed endpoint.</summary>
    public int RetentionRevision { get; set; } = 1;

    /// <summary>Gets or sets when the first-run setup was first observed complete (P7.S10).</summary>
    public DateTime? SetupCompletedAt { get; set; }

    /// <summary>Gets or sets when an administrator dismissed the first-run setup banner.</summary>
    public DateTime? SetupDismissedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether episodes may be upgraded or gain versions. Off until the host is shown to
    /// group episode versions (PHASE6 M1 / open question 5).
    /// </summary>
    public bool EpisodeUpgradesEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether automation searches again for a monitored title after retention reclaimed
    /// it. Off by default, so a watched and reclaimed title is not downloaded again unasked (PHASE6 open question 4).
    /// </summary>
    public bool ReacquireReclaimed { get; set; }

    /// <summary>Gets or sets the automation settings revision.</summary>
    public int AutomationRevision { get; set; } = 1;
}

/// <summary>Where seed protection reads Transmission state from (P7.S7).</summary>
public static class SeedProtectionSources
{
    /// <summary>The selected acquisition download client.</summary>
    public const string AcquisitionClient = "acquisitionClient";

    /// <summary>A separately configured endpoint.</summary>
    public const string Separate = "separate";
}

/// <summary>Documented import defaults.</summary>
public static class ImportDefaults
{
    /// <summary>The default allow-listed video extensions.</summary>
    public const string VideoExtensions = "mkv,mp4,m4v,avi,mov,ts,m2ts,webm,wmv,mpg,mpeg";

    /// <summary>Archive extensions that are never extracted.</summary>
    public static IReadOnlySet<string> ArchiveExtensions { get; } =
        new HashSet<string>(["rar", "zip", "7z", "tar", "gz", "bz2", "xz", "iso", "img"], StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stable grab operation states (P4.A1).</summary>
public static class GrabStates
{
    /// <summary>Intent persisted and held; nothing has been sent to the client and it can still be cancelled.</summary>
    public const string Pending = "pending";

    /// <summary>The add is being sent; the outcome is not yet known.</summary>
    public const string Submitting = "submitting";

    /// <summary>The client holds the matching torrent with the requested settings.</summary>
    public const string Accepted = "accepted";

    /// <summary>The client definitely does not hold a torrent for this operation.</summary>
    public const string Failed = "failed";

    /// <summary>The add may have succeeded; blind resubmission is blocked until identity lookup resolves it.</summary>
    public const string Unknown = "unknown";

    /// <summary>Cancelled during the hold; nothing was sent to the client.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Gets the states that still own their target and hash.</summary>
    public static IReadOnlyList<string> Active { get; } = [Pending, Submitting, Unknown, Accepted];

    /// <summary>Gets the states awaiting resolution.</summary>
    public static IReadOnlyList<string> Unresolved { get; } = [Pending, Submitting, Unknown];
}

/// <summary>One durable manual acquisition handoff (P4.A5).</summary>
/// <remarks>
/// Persisted before the client is contacted. The download locator (which may embed a passkey) is never stored:
/// recovery uses the normalized infohash, so no credential outlives the request that fetched it.
/// </remarks>
public sealed class GrabOperation
{
    /// <summary>Gets or sets the stable operation identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the requesting Jellyfin user.</summary>
    public Guid RequestedBy { get; set; }

    /// <summary>Gets or sets the client-supplied idempotency key.</summary>
    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the fingerprint of the request payload bound to the idempotency key.</summary>
    [MaxLength(64)]
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the target entry. Detached, never cascaded, when an entry is removed after resolution.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>Gets or sets the target episode for series acquisition.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the target while this operation owns it; null once released.</summary>
    [MaxLength(40)]
    public string? ActiveTarget { get; set; }

    /// <summary>Gets or sets the client/hash pair while this operation owns it; null once released.</summary>
    [MaxLength(96)]
    public string? ActiveHash { get; set; }

    /// <summary>Gets or sets the search snapshot the release came from.</summary>
    public Guid SearchId { get; set; }

    /// <summary>Gets or sets the opaque release identity within that search.</summary>
    [MaxLength(64)]
    public string ReleaseId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source indexer.</summary>
    public Guid IndexerId { get; set; }

    /// <summary>Gets or sets the source indexer name at grab time.</summary>
    [MaxLength(128)]
    public string IndexerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the source GUID, namespaced by <see cref="IndexerId"/>. Never treated as a hash.</summary>
    [MaxLength(1024)]
    public string SourceGuid { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw release title.</summary>
    [MaxLength(1024)]
    public string RawTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the parsed release attributes as JSON.</summary>
    public string ParsedJson { get; set; } = "{}";

    /// <summary>Gets or sets the reported size in bytes, when known.</summary>
    public long? Size { get; set; }

    /// <summary>Gets or sets the evaluated quality profile.</summary>
    public Guid ProfileId { get; set; }

    /// <summary>Gets or sets the evaluated profile revision.</summary>
    public int ProfileRevision { get; set; }

    /// <summary>Gets or sets the scoring rules version.</summary>
    [MaxLength(32)]
    public string ScoringVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the evaluated score.</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets the receiving download client.</summary>
    public Guid DownloadClientId { get; set; }

    /// <summary>Gets or sets the download client revision the grab was validated against.</summary>
    public int DownloadClientRevision { get; set; }

    /// <summary>Gets or sets the normalized 40-character lower-case BitTorrent v1 infohash, once derived.</summary>
    [MaxLength(40)]
    public string? InfoHash { get; set; }

    /// <summary>Gets or sets the configured label requested from the client.</summary>
    [MaxLength(64)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the download directory requested from the client.</summary>
    [MaxLength(1024)]
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets when the cancellable hold ends and submission may start (user decision 2).</summary>
    public DateTime HoldUntil { get; set; }

    /// <summary>Gets or sets when the operation was cancelled during its hold.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Gets or sets who cancelled the operation.</summary>
    public Guid? CancelledBy { get; set; }

    /// <summary>Gets or sets the effective minimum seed ratio snapshot.</summary>
    public double? SeedRatio { get; set; }

    /// <summary>Gets or sets the effective minimum seeding time snapshot in minutes.</summary>
    public int? SeedMinutes { get; set; }

    /// <summary>Gets or sets the state.</summary>
    [MaxLength(16)]
    public string State { get; set; } = GrabStates.Pending;

    /// <summary>Gets or sets the sanitized stable failure code.</summary>
    [MaxLength(64)]
    public string? FailureCode { get; set; }

    /// <summary>Gets or sets when the operation was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the operation last changed.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the add was sent.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Gets or sets when the client acceptance was verified.</summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>Gets or sets <c>acquire</c> or <c>addVersion</c> (P6.M6).</summary>
    [MaxLength(16)]
    public string Intent { get; set; } = "acquire";

    /// <summary>Gets or sets a value indicating whether automation made this grab (P6.M3).</summary>
    public bool Automatic { get; set; }

    /// <summary>Gets or sets the upgrade this grab belongs to.</summary>
    public Guid? UpgradeOperationId { get; set; }
}
