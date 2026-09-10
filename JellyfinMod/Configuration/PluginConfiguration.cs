using MediaBrowser.Model.Plugins;

namespace JellyfinMod;

/// <summary>
/// Plugin settings. Serialized as XML by the host, so no Dictionary&lt;,&gt; members.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the TMDB API Read Access Token used for discovery. Stays server-side.</summary>
    public string TmdbReadAccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB API key used for discovery. Stays server-side.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether retention may delete media files.</summary>
    public bool RetentionEnabled { get; set; }

    /// <summary>Gets or sets how many days after finishing a title its file is reclaimed.</summary>
    public int ReclaimAfterDays { get; set; } = 14;

    /// <summary>Gets or sets a value indicating whether favourites are exempt from retention.</summary>
    public bool ExemptFavourites { get; set; } = true;
}
