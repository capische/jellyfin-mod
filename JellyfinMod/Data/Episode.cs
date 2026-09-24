namespace JellyfinMod.Data;

/// <summary>A durable episode record within one library's series entry.</summary>
public sealed class Episode
{
    /// <summary>Gets or sets the stable plugin episode identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the parent entry identity.</summary>
    public Guid EntryId { get; set; }
    /// <summary>Gets or sets the TMDB episode identity.</summary>
    public int TmdbId { get; set; }
    /// <summary>Gets or sets the season number, including season zero for specials.</summary>
    public int SeasonNumber { get; set; }
    /// <summary>Gets or sets the episode number within the season.</summary>
    public int EpisodeNumber { get; set; }
    /// <summary>Gets or sets the episode title.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Gets or sets the synopsis.</summary>
    public string? Overview { get; set; }
    /// <summary>Gets or sets the TMDB still path.</summary>
    public string? StillPath { get; set; }
    /// <summary>Gets or sets the air date when known.</summary>
    public DateTime? AirDate { get; set; }
    /// <summary>Gets or sets the duration in minutes when known.</summary>
    public int? RuntimeMinutes { get; set; }
    /// <summary>Gets or sets whether future acquisition should monitor this episode.</summary>
    public bool Monitored { get; set; } = true;
    /// <summary>Gets or sets the current file availability.</summary>
    public FileState State { get; set; }
    /// <summary>Gets or sets an accessible native episode binding.</summary>
    public Guid? JellyfinItemId { get; set; }

    /// <summary>
    /// Gets or sets this episode's own retention override (P10.E2). Keep on the series or on the episode protects it;
    /// otherwise the episode's days, then the series' days, then the global window apply.
    /// </summary>
    public RetentionPolicy RetentionPolicy { get; set; }

    /// <summary>Gets or sets this episode's own retention window in days, used with <see cref="RetentionPolicy.Days"/>.</summary>
    public int? ReclaimAfterDays { get; set; }

    /// <summary>
    /// Gets a value indicating whether this row is identified by its season and episode position rather than a TMDB
    /// episode id (P10.E1). Libraries scraped from TVDB carry no TMDB episode ids; <see cref="TmdbId"/> is then zero.
    /// </summary>
    public bool IsPositionIdentity => TmdbId <= 0;
}
