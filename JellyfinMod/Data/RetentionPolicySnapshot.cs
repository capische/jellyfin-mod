namespace JellyfinMod.Data;

/// <summary>The last persisted retention configuration and its monotonic revision.</summary>
public sealed class RetentionPolicySnapshot
{
    /// <summary>Gets or sets the singleton row identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the monotonic policy revision.</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets a value indicating whether automatic retention is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whose completion starts the retention window.</summary>
    public WatchedUserMode WatchedUserMode { get; set; }

    /// <summary>Gets or sets the configured selected user.</summary>
    public Guid? SelectedUserId { get; set; }

    /// <summary>Gets or sets the global retention duration.</summary>
    public int ReclaimAfterDays { get; set; }

    /// <summary>Gets or sets the test-only minute window; zero means the day windows apply.</summary>
    public int TestWindowMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether favourites are protected.</summary>
    public bool ExemptFavourites { get; set; }

    /// <summary>Gets or sets when the current enabled period began.</summary>
    public DateTime? EnabledAt { get; set; }

    /// <summary>Gets or sets when this snapshot was persisted.</summary>
    public DateTime UpdatedAt { get; set; }
}
