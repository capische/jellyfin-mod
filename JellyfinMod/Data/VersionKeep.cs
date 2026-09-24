using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>
/// An administrator's Keep on one file of a movie or an episode (P10, PHASE10 Q3 answered 2026-09-24). It is keyed by the
/// file, not by the binding: Jellyfin re-identifies the remaining versions of a title when one of them goes, and a Keep
/// held on the old binding would silently disappear with it. A file matches by its path or by its physical identity
/// (device, inode and birth time): a rename or move within one filesystem keeps the Keep, and a new file written at the
/// kept path is protected too. A move to another filesystem changes both, so the Keep no longer applies and must be made
/// again (RET2-R9). One path can be kept once per entry; the same folder in two libraries is two entries.
/// </summary>
public sealed class VersionKeep
{
    /// <summary>Gets or sets the identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the catalog entry the file belongs to.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode the file belongs to, or null for a movie.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the kept file's media path as the binding recorded it.</summary>
    [MaxLength(4096)]
    public string MediaPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the kept file's device and inode identity when it could be read.</summary>
    [MaxLength(256)]
    public string? PhysicalIdentity { get; set; }

    /// <summary>Gets or sets when the Keep was made.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
