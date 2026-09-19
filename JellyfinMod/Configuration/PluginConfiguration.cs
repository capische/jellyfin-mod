using MediaBrowser.Model.Plugins;

namespace JellyfinMod;

/// <summary>Whose completed playback starts a retention window.</summary>
public enum WatchedUserMode
{
    /// <summary>Every user with access to the library must finish the media.</summary>
    AllUsers = 0,

    /// <summary>One administrator-selected user must finish the media.</summary>
    SelectedUser = 1,

    /// <summary>The first user with access to finish the media starts the window.</summary>
    AnyUser = 2
}

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

    /// <summary>Gets or sets whose watched state starts the retention window.</summary>
    public WatchedUserMode RetentionWatchedUserMode { get; set; } = WatchedUserMode.AllUsers;

    /// <summary>Gets or sets the selected Jellyfin user when SelectedUser mode is active.</summary>
    public Guid? RetentionSelectedUserId { get; set; }

    /// <summary>Gets or sets a value indicating whether favourites are exempt from retention.</summary>
    public bool ExemptFavourites { get; set; } = true;

    /// <summary>Gets or sets the Transmission RPC endpoint used for read-only seed checks.</summary>
    public string TransmissionRpcUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Transmission RPC username.</summary>
    public string TransmissionUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Transmission RPC password.</summary>
    /// <remarks>Write-only: a submitted value moves to the secret store and this field is saved empty.</remarks>
    public string TransmissionPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret-store reference for the TMDB Read Access Token (user decision 3).</summary>
    public string? TmdbReadAccessTokenRef { get; set; }

    /// <summary>Gets or sets the secret-store reference for the TMDB API key.</summary>
    public string? TmdbApiKeyRef { get; set; }

    /// <summary>Gets or sets the secret-store reference for the Transmission RPC password.</summary>
    public string? TransmissionPasswordRef { get; set; }
}
