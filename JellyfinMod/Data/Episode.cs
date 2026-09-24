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

/// <summary>
/// Whether two descriptions of an episode are evidently the same episode (RET-R2, RET2-R3): the same air date within a day,
/// or the same title ignoring case, punctuation and spacing. A number alone is never evidence, because a library's numbering
/// (TVDB, scene) and TMDB's can differ.
/// </summary>
internal static class EpisodeIdentityEvidence
{
    /// <summary>
    /// Returns true when the titles agree, or when the air dates agree within a day and no other episode the series lists
    /// airs within a day of the file's date (RET3-R5). A daily show airs its episodes a day apart, so there a date a day
    /// off, or even the same date shared by two episodes, names a neighbour as readily as the episode itself.
    /// </summary>
    /// <param name="title">The file's (native) title.</param>
    /// <param name="airDate">The file's (native) air date.</param>
    /// <param name="otherTitle">The listed episode's title.</param>
    /// <param name="otherAirDate">The listed episode's air date.</param>
    /// <param name="listedAirDates">Every air date the series lists, this episode's included; null when unknown.</param>
    public static bool Agrees(string? title, DateTime? airDate, string? otherTitle, DateTime? otherAirDate,
        IReadOnlyCollection<DateTime?>? listedAirDates = null)
    {
        var key = Key(title);
        if (key.Length > 0 && key == Key(otherTitle)) return true;
        if (airDate is not { } local || otherAirDate is not { } listed || Math.Abs((local.Date - listed.Date).TotalDays) > 1)
            return false;
        // Unknown neighbours are treated as a daily show: only the exact date is then evidence.
        if (listedAirDates is null) return local.Date == listed.Date;
        return listedAirDates.Count(date => date is { } other && Math.Abs((local.Date - other.Date).TotalDays) <= 1) == 1;
    }

    private static string Key(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
