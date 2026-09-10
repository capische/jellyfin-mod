using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>Where a title's media file currently stands. Not a workflow — just the file.</summary>
public enum FileState
{
    /// <summary>Wanted; nothing on disk and nothing in flight.</summary>
    None = 0,

    /// <summary>Searching indexers for a release that passes the profile.</summary>
    Searching = 1,

    /// <summary>A release was grabbed and is queued at the download client.</summary>
    Grabbed = 2,

    /// <summary>Downloading.</summary>
    Downloading = 3,

    /// <summary>On disk and playable.</summary>
    OnDisk = 4,

    /// <summary>Watched, then reclaimed. A placeholder remains so the item survives.</summary>
    Reclaimed = 5
}

/// <summary>
/// One entry per title. May or may not have a media file behind it — that is the whole point.
/// </summary>
public class Entry
{
    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the media type: movie or series.</summary>
    [MaxLength(16)]
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id — the catalog's primary identity.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id, when known.</summary>
    [MaxLength(16)]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the TMDB poster path, used only when there is no Jellyfin item.</summary>
    [MaxLength(256)]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the durable TMDB metadata snapshot, including regional ratings.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Gets or sets the current file state.</summary>
    public FileState State { get; set; } = FileState.None;

    /// <summary>Gets or sets a value indicating whether this entry is searched automatically.</summary>
    public bool Monitored { get; set; } = true;

    /// <summary>Gets or sets the Jellyfin item id, set from OnDisk onward.</summary>
    public Guid? JellyfinItemId { get; set; }

    /// <summary>
    /// Gets or sets the library this entry belongs to. Needed because a file-less entry has no
    /// Jellyfin item to derive one from, and /movies is per-library.
    /// </summary>
    public Guid? TargetLibraryId { get; set; }

    /// <summary>Gets or sets download progress, 0-100, while downloading.</summary>
    public int? Progress { get; set; }

    /// <summary>Gets or sets when this entry was added.</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the title was finished.</summary>
    public DateTime? WatchedAt { get; set; }

    /// <summary>Gets or sets the absolute time the file becomes eligible for reclaim.</summary>
    public DateTime? ReclaimAt { get; set; }

    /// <summary>Gets or sets a per-entry override of the global retention window.</summary>
    public int? ReclaimAfterDays { get; set; }
}
