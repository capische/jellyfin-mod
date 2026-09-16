using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>Durable summary of one automatic or manually triggered retention run.</summary>
public sealed class RetentionRun
{
    /// <summary>Gets or sets the run identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets when inspection began.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets when the run reached a terminal state.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the run state.</summary>
    [MaxLength(16)]
    public string Status { get; set; } = RetentionRunStatuses.Running;

    /// <summary>Gets or sets the number of representations inspected.</summary>
    public int Inspected { get; set; }

    /// <summary>Gets or sets the number of due representations observed.</summary>
    public int Eligible { get; set; }

    /// <summary>Gets or sets the number blocked by preview or final revalidation.</summary>
    public int Blocked { get; set; }

    /// <summary>Gets or sets the number of physical unlink actions completed.</summary>
    public int Reclaimed { get; set; }

    /// <summary>Gets or sets the number of physical actions that failed.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of interrupted representation operations recovered.</summary>
    public int Interrupted { get; set; }

    /// <summary>Gets or sets logical bytes unlinked once per physical action.</summary>
    public long LogicalBytesUnlinked { get; set; }

    /// <summary>Gets or sets physical bytes proven released.</summary>
    public long PhysicalBytesReleased { get; set; }

    /// <summary>Gets or sets physical actions whose released bytes cannot be proven.</summary>
    public int PhysicalBytesUnknown { get; set; }

    /// <summary>Gets or sets bounded administrator-facing terminal detail.</summary>
    [MaxLength(1024)]
    public string? Detail { get; set; }
}

/// <summary>Stable run status names.</summary>
internal static class RetentionRunStatuses
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Disabled = "disabled";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}
