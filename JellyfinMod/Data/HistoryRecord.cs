using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>
/// The trail that makes an entry a record rather than a queue. Survives the media file.
/// </summary>
public class HistoryRecord
{
    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the entry this belongs to.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the event type, e.g. added, grabbed, imported, watched, reclaimed.</summary>
    [MaxLength(32)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable summary of the event.</summary>
    [MaxLength(1024)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets structured detail as JSON.</summary>
    public string? Data { get; set; }

    /// <summary>
    /// Gets or sets the binding of the one file the event is about (grab once bound, import, keep, stop keeping, remove,
    /// reclaim), or null for an event about the title or the episode. The detail page lists a file's events by it.
    /// </summary>
    public Guid? BindingId { get; set; }

    /// <summary>Gets or sets when the event happened.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
