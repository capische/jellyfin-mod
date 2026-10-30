namespace JellyfinMod.Data;

/// <summary>
/// One title in Settings › Trakt's Activity (Phase 13, decisions D125–D127): its latest state in one direction, to Trakt
/// (a watch here, a live play) or from Trakt (the import), for one user; or a count row (collection adds, a burst of more
/// than 50 titles at once) with no item. Kept 30 days.
/// </summary>
public sealed class TraktActivityEntry
{
    /// <summary>Gets or sets the row identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the Jellyfin user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the movie or episode, or null for a count row.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets <c>to</c> (to Trakt) or <c>from</c> (from Trakt).</summary>
    public string Direction { get; set; } = "to";

    /// <summary>Gets or sets the movie's or series' name when the row was written, so a removed item still reads.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode's <c>S02E04</c>, or null for a movie or a count row.</summary>
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets the state: <c>playing</c>, <c>stopped</c>, <c>waiting</c>, <c>sent</c>, <c>failed</c> (to Trakt);
    /// <c>imported</c>, <c>position</c> (from Trakt); <c>collection</c>, <c>summary</c> (count rows).
    /// </summary>
    public string State { get; set; } = "waiting";

    /// <summary>Gets or sets a number the state reads with: minutes, a percentage, or a count.</summary>
    public int? Detail { get; set; }

    /// <summary>Gets or sets <c>episode</c> or <c>movie</c>.</summary>
    public string Kind { get; set; } = "episode";

    /// <summary>Gets or sets when it happened (the watch, the save, the run), in UTC.</summary>
    public DateTime At { get; set; }
}
