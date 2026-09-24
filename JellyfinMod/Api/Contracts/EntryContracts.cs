using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services;

namespace JellyfinMod.Api.Contracts;

/// <summary>The stable plugin wire representation, independent of host serializer defaults.</summary>
public sealed class EntryDto(Entry entry, JellyfinMod.Services.Import.ProjectedAcquisition? acquisition = null)
{
    // Acquisition is projected only onto file-less entries; an on-disk title keeps its native state (P5.I3).
    private readonly JellyfinMod.Services.Import.ProjectedAcquisition? _projected =
        entry.MediaType == "movie" && entry.State is FileState.None or FileState.Reclaimed ? acquisition : null;

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
    public string State => _projected?.State ?? FileStates.ToWire(entry.State);
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
    public int? Progress => _projected is null ? entry.Progress : _projected.Progress;
    /// <summary>Gets addedAt.</summary>
    [JsonPropertyName("addedAt")]
    public DateTime AddedAt { get; } = DateTime.SpecifyKind(entry.AddedAt, DateTimeKind.Utc);
    /// <summary>Gets watchedAt. Deprecated: never written; retention state is in <c>retention</c> (P1.P11).</summary>
    [JsonPropertyName("watchedAt")]
    public DateTime? WatchedAt { get; } = Utc(entry.WatchedAt);
    /// <summary>Gets reclaimAt. Deprecated: never written; the deadline is <c>retention.deadline</c> (P1.P11).</summary>
    [JsonPropertyName("reclaimAt")]
    public DateTime? ReclaimAt { get; } = Utc(entry.ReclaimAt);
    /// <summary>Gets reclaimAfterDays.</summary>
    [JsonPropertyName("reclaimAfterDays")]
    public int? ReclaimAfterDays { get; } = entry.ReclaimAfterDays;
    /// <summary>Gets retentionPolicy.</summary>
    [JsonPropertyName("retentionPolicy")]
    public string RetentionPolicy { get; } = RetentionPolicies.ToWire(entry.RetentionPolicy);
    /// <summary>Gets the assigned quality profile; null inherits the configured default (P4.A2).</summary>
    [JsonPropertyName("qualityProfileId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? QualityProfileId { get; } = entry.QualityProfileId;
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
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt)
{
    /// <summary>Gets the episode the event is about, or null for an event about the title (P10.E3).</summary>
    [JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? EpisodeId { get; init; }

    /// <summary>Reads the episode identity episode events carry in their structured data.</summary>
    public static Guid? EpisodeOf(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("episodeId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == System.Text.Json.JsonValueKind.String && property.Value.TryGetGuid(out var id))
                    return id;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }
}

/// <summary>One correctly paginated entry query.</summary>
public sealed record EntriesResult([property: JsonPropertyName("items")] IReadOnlyList<EntryDto> Items,
    [property: JsonPropertyName("totalRecordCount")] int TotalRecordCount);

/// <summary>Canonical result of an idempotent add.</summary>
public sealed record CreateEntryResult([property: JsonPropertyName("entry")] EntryDto Entry,
    [property: JsonPropertyName("created")] bool Created);

/// <summary>Entry metadata, history and individually tracked episodes.</summary>
public sealed record EntryDetail([property: JsonPropertyName("entry")] EntryDto Entry,
    [property: JsonPropertyName("history")] IReadOnlyList<HistoryDto> History,
    [property: JsonPropertyName("episodes")] IReadOnlyList<EpisodeDto> Episodes,
    [property: JsonPropertyName("retention")] RetentionSummaryDto Retention,
    [property: JsonPropertyName("acquisition"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] AcquisitionSummaryDto? Acquisition = null)
{
    /// <summary>Gets a movie's held versions for the version selector (P6.M8); empty for series and file-less titles.</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<VersionDto> Versions { get; init; } = [];

    /// <summary>Gets a movie's retention warning when its window is running (PHASE10 Q8, Q11); null otherwise.</summary>
    [JsonPropertyName("retentionWarning"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public RetentionWarningDto? RetentionWarning { get; init; }

    /// <summary>Gets a movie's upgrade state; administrators only, null otherwise.</summary>
    [JsonPropertyName("upgrade"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UpgradeStateDto? Upgrade { get; init; }
}

/// <summary>Filtered remote page with an explicit continuation, not a misleading remote total.</summary>
public sealed record DiscoveryResult([property: JsonPropertyName("items")] IReadOnlyList<TmdbMetadata> Items,
    [property: JsonPropertyName("nextPage")] int? NextPage,
    [property: JsonPropertyName("skipped")] int Skipped = 0);

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

/// <summary>The admin-only entry settings: monitoring and, for entries, the quality profile (P4.A2).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchEntryRequest
{
    private Guid? _qualityProfileId;

    /// <summary>Gets or sets future acquisition monitoring.</summary>
    [JsonPropertyName("monitored")]
    public bool? Monitored { get; set; }

    /// <summary>Gets or sets the assigned quality profile; an explicit null restores inheritance.</summary>
    [JsonPropertyName("qualityProfileId")]
    public Guid? QualityProfileId
    {
        get => _qualityProfileId;
        set
        {
            _qualityProfileId = value;
            QualityProfileIdSpecified = true;
        }
    }

    /// <summary>Gets a value indicating whether the request named a quality profile, including an explicit null.</summary>
    [JsonIgnore]
    public bool QualityProfileIdSpecified { get; private set; }

    /// <summary>Gets or sets a request to search on the next automation run, resetting backoff (P6.M3).</summary>
    [JsonPropertyName("searchNow")]
    public bool? SearchNow { get; set; }
}

/// <summary>An administrator's episode retention setting (PHASE10 Q4): inherit, days with a count, or never.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EpisodeRetentionRequest
{
    /// <summary>Gets or sets inherit, days or never.</summary>
    [JsonPropertyName("policy")]
    public string? Policy { get; set; }

    /// <summary>Gets or sets the episode's own window, only with <c>days</c>.</summary>
    [JsonPropertyName("reclaimAfterDays")]
    public int? ReclaimAfterDays { get; set; }
}

/// <summary>
/// A running retention window, shown to everyone who can see the title (PHASE10 Q8 and Q11, 2026-09-24): when its files
/// will be deleted unless kept, why the window started, and which files it applies to. Ordinary users get the date and the
/// cause only; the file names, which can name library folders, are for administrators (RET2-R10). A date that has already
/// passed is reported as overdue: the files go at the next retention run (RET2-R5).
/// </summary>
public sealed record RetentionWarningDto([property: JsonPropertyName("deadline")] DateTime Deadline,
    [property: JsonPropertyName("cause")] string Cause,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("overdue")] bool Overdue = false);

/// <summary>Whether one file is now kept (PHASE10 Q3).</summary>
public sealed record VersionKeepResult([property: JsonPropertyName("bindingId")] Guid BindingId,
    [property: JsonPropertyName("kept")] bool Kept);

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
