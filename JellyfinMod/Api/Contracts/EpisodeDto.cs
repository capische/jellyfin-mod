using System.Text.Json.Serialization;
using JellyfinMod.Data;

namespace JellyfinMod.Api.Contracts;

/// <summary>One individually tracked episode in a series entry.</summary>
public sealed class EpisodeDto(Episode episode, RetentionSummaryDto? retention = null, AcquisitionSummaryDto? acquisition = null,
    JellyfinMod.Services.Import.ProjectedAcquisition? projected = null)
{
    // A downloading episode keeps its file availability; only its state and progress are projected (P5.I3).
    private readonly JellyfinMod.Services.Import.ProjectedAcquisition? _projected = episode.State == FileState.OnDisk ? null : projected;

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
    /// <summary>Gets the episode's own retention override: inherit, days or never (P10.E3).</summary>
    [JsonPropertyName("retentionPolicy")]
    public string RetentionPolicy { get; } = RetentionPolicies.ToWire(episode.RetentionPolicy);
    /// <summary>Gets the episode's own retention window when <see cref="RetentionPolicy"/> is days.</summary>
    [JsonPropertyName("reclaimAfterDays")]
    public int? ReclaimAfterDays { get; } = episode.ReclaimAfterDays;
    /// <summary>Gets monitored.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; } = episode.Monitored;
    /// <summary>Gets state.</summary>
    [JsonPropertyName("state")]
    public string State => _projected?.State ?? FileStates.ToWire(episode.State);

    /// <summary>Gets the projected download progress 0–100 while the episode is downloading; null otherwise.</summary>
    [JsonPropertyName("progress"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Progress => _projected?.Progress;
    /// <summary>Gets whether media is on disk, reclaimed after watching, unaired, or aired without media.</summary>
    [JsonPropertyName("availability")]
    public string Availability { get; } = episode.State == FileState.OnDisk ? "onDisk" :
        episode.State == FileState.Reclaimed ? "reclaimed" :
        episode.AirDate is { } airDate && airDate.Date > DateTime.UtcNow.Date ? "unaired" : "missing";
    /// <summary>Gets jellyfinItemId.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public Guid? JellyfinItemId { get; } = episode.JellyfinItemId;
    /// <summary>Gets the privacy-safe automatic retention state on entry detail responses.</summary>
    [JsonPropertyName("retention")]
    public RetentionSummaryDto? Retention { get; } = retention;
    /// <summary>Gets the episode's held versions on entry detail responses (P6.M8).</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<VersionDto> Versions { get; init; } = [];

    /// <summary>Gets the newest grab for this episode on entry detail responses (P4.A6); null when none.</summary>
    [JsonPropertyName("acquisition"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public AcquisitionSummaryDto? Acquisition { get; } = acquisition;
}
