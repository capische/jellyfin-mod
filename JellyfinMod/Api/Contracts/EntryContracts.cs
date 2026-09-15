using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services;

namespace JellyfinMod.Api.Contracts;

/// <summary>The stable plugin wire representation, independent of host serializer defaults.</summary>
public sealed class EntryDto(Entry entry)
{
    /// <summary>Gets id.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; } = entry.Id;
    /// <summary>Gets mediaType.</summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; } = entry.MediaType;
    /// <summary>Gets tmdbId.</summary>
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; } = entry.TmdbId;
    /// <summary>Gets imdbId.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; } = entry.ImdbId;
    /// <summary>Gets title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; } = entry.Title;
    /// <summary>Gets year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; } = entry.Year;
    /// <summary>Gets overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; } = entry.Overview;
    /// <summary>Gets posterPath.</summary>
    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; } = entry.PosterPath;
    /// <summary>Gets state.</summary>
    [JsonPropertyName("state")]
    public string State { get; } = FileStates.ToWire(entry.State);
    /// <summary>Gets monitored.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; } = entry.Monitored;
    /// <summary>Gets jellyfinItemId.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public Guid? JellyfinItemId { get; } = entry.JellyfinItemId;
    /// <summary>Gets targetLibraryId.</summary>
    [JsonPropertyName("targetLibraryId")]
    public Guid? TargetLibraryId { get; } = entry.TargetLibraryId;
    /// <summary>Gets progress.</summary>
    [JsonPropertyName("progress")]
    public int? Progress { get; } = entry.Progress;
    /// <summary>Gets addedAt.</summary>
    [JsonPropertyName("addedAt")]
    public DateTime AddedAt { get; } = DateTime.SpecifyKind(entry.AddedAt, DateTimeKind.Utc);
    /// <summary>Gets watchedAt.</summary>
    [JsonPropertyName("watchedAt")]
    public DateTime? WatchedAt { get; } = Utc(entry.WatchedAt);
    /// <summary>Gets reclaimAt.</summary>
    [JsonPropertyName("reclaimAt")]
    public DateTime? ReclaimAt { get; } = Utc(entry.ReclaimAt);
    /// <summary>Gets reclaimAfterDays.</summary>
    [JsonPropertyName("reclaimAfterDays")]
    public int? ReclaimAfterDays { get; } = entry.ReclaimAfterDays;
    /// <summary>Gets retentionPolicy.</summary>
    [JsonPropertyName("retentionPolicy")]
    public string RetentionPolicy { get; } = RetentionPolicies.ToWire(entry.RetentionPolicy);
    /// <summary>Gets metadata.</summary>
    [JsonPropertyName("metadata")]
    public TmdbMetadata? Metadata { get; } = entry.MetadataJson is { } json ? JsonSerializer.Deserialize<TmdbMetadata>(json) : null;
    private static DateTime? Utc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}

/// <summary>Explicit stable retention-policy names.</summary>
public static class RetentionPolicies
{
    /// <summary>Converts an internal retention policy into the public contract.</summary>
    public static string ToWire(RetentionPolicy policy) => policy switch
    {
        RetentionPolicy.Inherit => "inherit",
        RetentionPolicy.Days => "days",
        RetentionPolicy.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };
}

/// <summary>One durable history event.</summary>
public sealed record HistoryDto([property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

/// <summary>One correctly paginated entry query.</summary>
public sealed record EntriesResult([property: JsonPropertyName("items")] IReadOnlyList<EntryDto> Items,
    [property: JsonPropertyName("totalRecordCount")] int TotalRecordCount);

/// <summary>Canonical result of an idempotent add.</summary>
public sealed record CreateEntryResult([property: JsonPropertyName("entry")] EntryDto Entry,
    [property: JsonPropertyName("created")] bool Created);

/// <summary>Entry metadata, history and individually tracked episodes.</summary>
public sealed record EntryDetail([property: JsonPropertyName("entry")] EntryDto Entry,
    [property: JsonPropertyName("history")] IReadOnlyList<HistoryDto> History,
    [property: JsonPropertyName("episodes")] IReadOnlyList<EpisodeDto> Episodes);

/// <summary>Filtered remote page with an explicit continuation, not a misleading remote total.</summary>
public sealed record DiscoveryResult([property: JsonPropertyName("items")] IReadOnlyList<TmdbMetadata> Items,
    [property: JsonPropertyName("nextPage")] int? NextPage);

/// <summary>Only these fields may be supplied when adding a title.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateEntryRequest
{
    /// <summary>Gets or sets movie or series.</summary>
    [Required, RegularExpression("^(movie|series)$"), JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;
    /// <summary>Gets or sets a positive TMDB identity.</summary>
    [Range(1, int.MaxValue), JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }
    /// <summary>Gets or sets the explicitly chosen destination library.</summary>
    [JsonPropertyName("targetLibraryId")]
    public Guid TargetLibraryId { get; set; }
}

/// <summary>The admin-only settings available before acquisition and retention.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchEntryRequest
{
    /// <summary>Gets or sets future acquisition monitoring.</summary>
    [JsonPropertyName("monitored")]
    public bool? Monitored { get; set; }
}

/// <summary>Explicit stable file-state names; integer serialization is never exposed.</summary>
public static class FileStates
{
    /// <summary>Converts an internal state into the public contract.</summary>
    public static string ToWire(FileState state) => state switch
    {
        FileState.None => "none", FileState.Searching => "searching", FileState.Grabbed => "grabbed",
        FileState.Downloading => "downloading", FileState.OnDisk => "onDisk", FileState.Reclaimed => "reclaimed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
