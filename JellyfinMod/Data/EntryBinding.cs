namespace JellyfinMod.Data;

/// <summary>One durable native title representation observed for a catalog entry.</summary>
public sealed class EntryBinding
{
    /// <summary>Gets or sets the binding identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the catalog entry identity.</summary>
    public Guid EntryId { get; set; }
    /// <summary>Gets or sets the native Jellyfin title identity.</summary>
    public Guid JellyfinItemId { get; set; }
    /// <summary>Gets or sets the library that owned the native representation when it was observed.</summary>
    public Guid TargetLibraryId { get; set; }
    /// <summary>Gets or sets Jellyfin's primary version identity, or this item's identity when it is not an alternate.</summary>
    public Guid VersionGroupId { get; set; }
}
