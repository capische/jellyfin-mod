using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>The durable per-user state used to evaluate one movie or episode.</summary>
public sealed class CompletionObservation
{
    /// <summary>Gets or sets the observation identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the parent catalog entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode identity, or null for a movie.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the stable entry or episode identity.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Gets or sets the Jellyfin user whose state was observed.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the native Jellyfin item read for this observation.</summary>
    public Guid JellyfinItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether authoritative user data was available.</summary>
    public bool EvidenceAvailable { get; set; }

    /// <summary>Gets or sets a value indicating whether Jellyfin currently reports completion.</summary>
    public bool Played { get; set; }

    /// <summary>Gets or sets a value indicating whether Jellyfin currently reports a favourite.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Gets or sets the current native resume position.</summary>
    public long PlaybackPositionTicks { get; set; }

    /// <summary>Gets or sets the authoritative native last-played value.</summary>
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>Gets or sets when the current completed-and-not-resumable state began.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets when native state was last read.</summary>
    public DateTime ObservedAt { get; set; }

    /// <summary>Gets or sets the native save reason which prompted the read.</summary>
    [MaxLength(32)]
    public string SourceReason { get; set; } = string.Empty;
}
