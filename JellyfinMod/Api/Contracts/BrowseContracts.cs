using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Dto;

namespace JellyfinMod.Api.Contracts;

/// <summary>One combined browse operation; filtering precedes sorting and pagination.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BrowseRequest
{
    /// <summary>Gets or sets movie or series.</summary>
    [Required, RegularExpression("^(movie|series)$"), JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "movie";
    /// <summary>Gets or sets an optional accessible library scope.</summary>
    [JsonPropertyName("targetLibraryId")]
    public Guid? TargetLibraryId { get; set; }
    /// <summary>Gets or sets a title search.</summary>
    [MaxLength(200), JsonPropertyName("query")]
    public string? Query { get; set; }
    /// <summary>Gets or sets the OR group of file states.</summary>
    [JsonPropertyName("state")]
    public string[] State { get; set; } = [];
    /// <summary>Gets or sets the exact sort field sequence.</summary>
    [JsonPropertyName("sortBy")]
    public string[] SortBy { get; set; } = ["SortName"];
    /// <summary>Gets or sets the direction shared by the sort sequence.</summary>
    [RegularExpression("^(Ascending|Descending)$"), JsonPropertyName("sortOrder")]
    public string SortOrder { get; set; } = "Ascending";
    /// <summary>Gets or sets a stable seed shared by all pages of a combined random ordering.</summary>
    [MaxLength(128), JsonPropertyName("randomSeed")]
    public string? RandomSeed { get; set; }
    /// <summary>Gets or sets the first row after filtering and sorting.</summary>
    [Range(0, int.MaxValue), JsonPropertyName("startIndex")]
    public int StartIndex { get; set; }
    /// <summary>Gets or sets the page size; null preserves the user's unpaged setting.</summary>
    [Range(1, int.MaxValue), JsonPropertyName("limit")]
    public int? Limit { get; set; }
    /// <summary>Gets or sets selected metadata and user-state filters.</summary>
    [JsonPropertyName("filters")]
    public BrowseFilters Filters { get; set; } = new();
    /// <summary>Gets or sets the alphabetical starting letter, or # for titles before A.</summary>
    [MaxLength(8), JsonPropertyName("alphabet")]
    public string? Alphabet { get; set; }
}

/// <summary>Catalog-aware equivalents of the Movies and TV filter groups.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BrowseFilters
{
    /// <summary>Gets or sets genres.</summary>
    [JsonPropertyName("genres")] public string[] Genres { get; set; } = [];
    /// <summary>Gets or sets release years.</summary>
    [JsonPropertyName("years")] public int[] Years { get; set; } = [];
    /// <summary>Gets or sets native official ratings.</summary>
    [JsonPropertyName("officialRatings")] public string[] OfficialRatings { get; set; } = [];
    /// <summary>Gets or sets administrator-assigned tags.</summary>
    [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    /// <summary>Gets or sets native studio identities.</summary>
    [JsonPropertyName("studioIds")] public Guid[] StudioIds { get; set; } = [];
    /// <summary>Gets or sets played, unplayed, favorite or resumable filters.</summary>
    [JsonPropertyName("status")] public string[] Status { get; set; } = [];
    /// <summary>Gets or sets series statuses.</summary>
    [JsonPropertyName("seriesStatus")] public string[] SeriesStatus { get; set; } = [];
    /// <summary>Gets or sets media features.</summary>
    [JsonPropertyName("features")] public string[] Features { get; set; } = [];
    /// <summary>Gets or sets SD, HD, 4K and 3D filters.</summary>
    [JsonPropertyName("videoBasicFilter")] public string[] VideoBasicFilter { get; set; } = [];
    /// <summary>Gets or sets native video types.</summary>
    [JsonPropertyName("videoTypes")] public string[] VideoTypes { get; set; } = [];
    /// <summary>Gets or sets audio languages.</summary>
    [JsonPropertyName("audioLanguages")] public string[] AudioLanguages { get; set; } = [];
    /// <summary>Gets or sets subtitle languages.</summary>
    [JsonPropertyName("subtitleLanguages")] public string[] SubtitleLanguages { get; set; } = [];
}

/// <summary>A real native item or a file-less entry, never a synthetic BaseItem.</summary>
public sealed record BrowseRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("nativeItem")] BaseItemDto? NativeItem,
    [property: JsonPropertyName("entry")] EntryDto? Entry);

/// <summary>One correctly paginated combined result with its exact filtered total.</summary>
public sealed record BrowseResult(
    [property: JsonPropertyName("items")] IReadOnlyList<BrowseRow> Items,
    [property: JsonPropertyName("totalRecordCount")] int TotalRecordCount,
    [property: JsonPropertyName("hasCatalogEntries")] bool HasCatalogEntries);
