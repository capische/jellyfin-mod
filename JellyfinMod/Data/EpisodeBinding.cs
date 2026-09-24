namespace JellyfinMod.Data;

/// <summary>One durable native episode representation observed for a tracked episode.</summary>
public sealed class EpisodeBinding
{
    /// <summary>Gets or sets the binding identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the tracked episode identity.</summary>
    public Guid EpisodeId { get; set; }
    /// <summary>Gets or sets the native Jellyfin episode identity.</summary>
    public Guid JellyfinItemId { get; set; }
    /// <summary>Gets or sets the native series representation containing this episode.</summary>
    public Guid SeriesItemId { get; set; }
    /// <summary>Gets or sets the library that owned the native episode when it was observed.</summary>
    public Guid TargetLibraryId { get; set; }
    /// <summary>Gets or sets the native media path observed for this episode representation.</summary>
    public string? MediaPath { get; set; }
    /// <summary>Gets or sets the Linux mount identity observed for the media path.</summary>
    public string? StorageIdentity { get; set; }
    /// <summary>
    /// Gets or sets the file's fingerprint (physical identity, size and modification time) when reconciliation last saw it,
    /// or null before it was first read (RET3-R3). A different fingerprint at the same path is a new file.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(512)]
    public string? FileFingerprint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this file was bound to a TMDB-identified episode only by its season and episode
    /// number (RET2-R3). The file's own numbering may not be TMDB's, so nothing confirms it is that episode; an upgrade never
    /// replaces such a file. Reconciliation clears it once the native episode carries the same TMDB episode id, or its title
    /// or air date agrees with the TMDB episode's.
    /// </summary>
    public bool IdentityUnverified { get; set; }
}
