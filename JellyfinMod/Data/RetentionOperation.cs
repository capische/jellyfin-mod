using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>Durable intent and outcome for reclaiming one physical media representation.</summary>
public sealed class RetentionOperation
{
    /// <summary>Gets or sets the operation identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the physical unlink action shared by bindings that reference one exact path.</summary>
    public Guid ActionId { get; set; }

    /// <summary>Gets or sets the representation binding selected for reclamation.</summary>
    public Guid BindingId { get; set; }

    /// <summary>Gets or sets the owning catalog entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode identity, or null for a movie.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the native Jellyfin item identity observed before unlink.</summary>
    public Guid JellyfinItemId { get; set; }

    /// <summary>Gets or sets the library whose reconciliation is serialized with this operation.</summary>
    public Guid TargetLibraryId { get; set; }

    /// <summary>Gets or sets the policy revision revalidated for this operation.</summary>
    public long PolicyVersion { get; set; }

    /// <summary>Gets or sets the exact canonical media file path selected for unlink.</summary>
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the library mount identity observed for the media path.</summary>
    public string StorageIdentity { get; set; } = string.Empty;

    /// <summary>Gets or sets the device and inode identity observed before unlink.</summary>
    [MaxLength(96)]
    public string PhysicalIdentity { get; set; } = string.Empty;

    /// <summary>Gets or sets the logical file length observed before unlink.</summary>
    public long LogicalBytes { get; set; }

    /// <summary>Gets or sets the hardlink count observed before unlink.</summary>
    public long HardlinkCountBefore { get; set; }

    /// <summary>Gets or sets the operation state.</summary>
    [MaxLength(16)]
    public string State { get; set; } = RetentionOperationStates.Prepared;

    /// <summary>Gets or sets a stable outcome reason.</summary>
    [MaxLength(48)]
    public string? Reason { get; set; }

    /// <summary>Gets or sets bounded failure detail for administrators.</summary>
    [MaxLength(1024)]
    public string? Error { get; set; }

    /// <summary>Gets or sets when the durable intent was recorded.</summary>
    public DateTime PreparedAt { get; set; }

    /// <summary>Gets or sets when the exact media path was confirmed absent after unlink.</summary>
    public DateTime? UnlinkedAt { get; set; }

    /// <summary>Gets or sets when catalog state and history were committed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets physical bytes proven released. Zero means another hardlink remained; null means unknown.
    /// </summary>
    public long? PhysicalBytesReleased { get; set; }
}

/// <summary>Stable reclamation operation states.</summary>
internal static class RetentionOperationStates
{
    public const string Prepared = "prepared";
    public const string Unlinked = "unlinked";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
}
