namespace JellyfinMod.Data;

/// <summary>
/// One title whose watch history arrived from Trakt for one user (P7.Q16): JellyfinMod saw Jellyfin save that user's
/// data for the item with the <c>Import</c> reason while the Trakt plugin was installed. Only identities and times are
/// kept; the history itself stays in Jellyfin's user data and on Trakt.
/// </summary>
public sealed class TraktObservation
{
    /// <summary>Gets or sets the observation identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the Jellyfin user whose data was imported.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the native movie or episode.</summary>
    public Guid JellyfinItemId { get; set; }

    /// <summary>Gets or sets the episode's series when the item is an episode, so a series page can ask.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the episode's season when the item is an episode, so a season page can ask.</summary>
    public Guid? SeasonId { get; set; }

    /// <summary>Gets or sets when JellyfinMod first saw history for this item arrive from Trakt.</summary>
    public DateTime FirstSyncedAt { get; set; }

    /// <summary>Gets or sets when JellyfinMod last saw history for this item arrive from Trakt.</summary>
    public DateTime LastSyncedAt { get; set; }
}
