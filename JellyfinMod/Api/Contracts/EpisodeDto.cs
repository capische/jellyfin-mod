using System.Text.Json.Serialization;
using JellyfinMod.Data;

namespace JellyfinMod.Api.Contracts;

/// <summary>One individually tracked episode in a series entry.</summary>
public sealed class EpisodeDto(Episode episode, RetentionSummaryDto? retention = null)
{
    /// <summary>Gets id.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; } = episode.Id;
    /// <summary>Gets entryId.</summary>
    [JsonPropertyName("entryId")]
    public Guid EntryId { get; } = episode.EntryId;
    /// <summary>Gets tmdbId.</summary>
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; } = episode.TmdbId;
    /// <summary>Gets seasonNumber.</summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; } = episode.SeasonNumber;
    /// <summary>Gets episodeNumber.</summary>
    [JsonPropertyName("episodeNumber")]
    public int EpisodeNumber { get; } = episode.EpisodeNumber;
    /// <summary>Gets title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; } = episode.Title;
    /// <summary>Gets overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; } = episode.Overview;
    /// <summary>Gets stillPath.</summary>
    [JsonPropertyName("stillPath")]
    public string? StillPath { get; } = episode.StillPath;
    /// <summary>Gets airDate.</summary>
    [JsonPropertyName("airDate")]
    public DateTime? AirDate { get; } = episode.AirDate.HasValue ? DateTime.SpecifyKind(episode.AirDate.Value, DateTimeKind.Utc) : null;
    /// <summary>Gets runtimeMinutes.</summary>
    [JsonPropertyName("runtimeMinutes")]
    public int? RuntimeMinutes { get; } = episode.RuntimeMinutes;
    /// <summary>Gets monitored.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; } = episode.Monitored;
    /// <summary>Gets state.</summary>
    [JsonPropertyName("state")]
    public string State { get; } = FileStates.ToWire(episode.State);
    /// <summary>Gets whether media is on disk, unaired, or aired without media.</summary>
    [JsonPropertyName("availability")]
    public string Availability { get; } = episode.State == FileState.OnDisk ? "onDisk" :
        episode.AirDate is { } airDate && airDate.Date > DateTime.UtcNow.Date ? "unaired" : "missing";
    /// <summary>Gets jellyfinItemId.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public Guid? JellyfinItemId { get; } = episode.JellyfinItemId;
    /// <summary>Gets the privacy-safe automatic retention state on entry detail responses.</summary>
    [JsonPropertyName("retention")]
    public RetentionSummaryDto? Retention { get; } = retention;
}
