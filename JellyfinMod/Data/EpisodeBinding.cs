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
}
