namespace JellyfinMod.Data;

/// <summary>
/// A bound native episode whose provider identity now disagrees with its tracked episode (P2.R9). Only this
/// episode is skipped; an administrator rebinds it to the observed identity or keeps the current one.
/// </summary>
public sealed class EpisodeConflict
{
    /// <summary>Gets or sets the conflict identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the series entry.</summary>
    public Guid EntryId { get; set; }
    /// <summary>Gets or sets the tracked episode the native episode is bound to.</summary>
    public Guid EpisodeId { get; set; }
    /// <summary>Gets or sets the native Jellyfin episode.</summary>
    public Guid JellyfinItemId { get; set; }
    /// <summary>Gets or sets the TMDB episode identity Jellyfin now reports.</summary>
    public int ObservedTmdbId { get; set; }
    /// <summary>Gets or sets the season number Jellyfin now reports.</summary>
    public int ObservedSeasonNumber { get; set; }
    /// <summary>Gets or sets the episode number Jellyfin now reports.</summary>
    public int ObservedEpisodeNumber { get; set; }
    /// <summary>Gets or sets <c>open</c>, or <c>kept</c> after an administrator kept the current identity.</summary>
    public string State { get; set; } = EpisodeConflictStates.Open;
    /// <summary>Gets or sets when the disagreement was first seen.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Stable episode conflict states.</summary>
public static class EpisodeConflictStates
{
    /// <summary>Awaiting an administrator decision.</summary>
    public const string Open = "open";
    /// <summary>The administrator kept the current identity for this observed identity.</summary>
    public const string Kept = "kept";
}
