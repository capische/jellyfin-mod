using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>One rating, in its own scale (PHASE9 "One rating model"). A missing source is absent, never zero.</summary>
public sealed record RatingDto(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("scale")] string Scale,
    [property: JsonPropertyName("votes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Votes,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("fetchedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? FetchedAt,
    [property: JsonPropertyName("stale")] bool Stale);

/// <summary>A native item's ratings, for its detail page (P9.R5).</summary>
public sealed record ItemRatingsDto(
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EntryId,
    [property: JsonPropertyName("ratings")] IReadOnlyList<RatingDto> Ratings);

/// <summary>What every signed-in user may know about ratings: whether they are on and the administrator's default order.</summary>
public sealed record RatingsDefaultsDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("defaultSources")] IReadOnlyList<string> DefaultSources,
    [property: JsonPropertyName("availableSources")] IReadOnlyList<string> AvailableSources,
    [property: JsonPropertyName("refreshDays")] int RefreshDays);

/// <summary>The ratings settings; the key is write-only (P9.R2).</summary>
public sealed record RatingsSettingsDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured,
    [property: JsonPropertyName("refreshDays")] int RefreshDays,
    [property: JsonPropertyName("dailyBudget")] int DailyBudget,
    [property: JsonPropertyName("defaultSources")] IReadOnlyList<string> DefaultSources,
    [property: JsonPropertyName("availableSources")] IReadOnlyList<string> AvailableSources,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("verifiedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? VerifiedAt,
    [property: JsonPropertyName("providerOverride")] bool ProviderOverride,
    [property: JsonPropertyName("revision")] int Revision);

/// <summary>A change to the ratings settings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RatingsSettingsRequest
{
    /// <summary>Gets or sets whether ratings are fetched and shown.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets the refresh window in days.</summary>
    [JsonPropertyName("refreshDays")]
    public int? RefreshDays { get; set; }

    /// <summary>Gets or sets the most calls per UTC day.</summary>
    [JsonPropertyName("dailyBudget")]
    public int? DailyBudget { get; set; }

    /// <summary>Gets or sets the default source order.</summary>
    [JsonPropertyName("defaultSources")]
    public string[]? DefaultSources { get; set; }

    /// <summary>Gets or sets the MDBList key change.</summary>
    [JsonPropertyName("apiKey")]
    public SecretChangeRequest ApiKey { get; set; } = new();

    /// <summary>Gets or sets the revision the change was made against.</summary>
    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

/// <summary>The outcome of the Test button: a code and a sentence, and the sources that arrived.</summary>
public sealed record RatingsTestDto(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources);

/// <summary>The fetcher's state for administrators (P9.R3).</summary>
public sealed record RatingsStatusDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("apiKeyConfigured")] bool ApiKeyConfigured,
    [property: JsonPropertyName("blocker"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Blocker,
    [property: JsonPropertyName("breaker")] RatingsBreakerDto Breaker,
    [property: JsonPropertyName("budget")] RatingsBudgetDto Budget,
    [property: JsonPropertyName("lastRun"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] RatingsRunDto? LastRun,
    [property: JsonPropertyName("entries")] int Entries,
    [property: JsonPropertyName("entriesWithoutRatings")] int EntriesWithoutRatings,
    [property: JsonPropertyName("queued")] int Queued,
    [property: JsonPropertyName("running"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] RatingsActivityDto? Running);

/// <summary>A pass in progress (user decision 8): what started it, when, and how many titles it still has before it.</summary>
public sealed record RatingsActivityDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("remaining")] int Remaining);

/// <summary>The breaker.</summary>
public sealed record RatingsBreakerDto(
    [property: JsonPropertyName("open")] bool Open,
    [property: JsonPropertyName("until"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? Until,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("consecutiveFailures")] int ConsecutiveFailures);

/// <summary>Today's budget (UTC day).</summary>
public sealed record RatingsBudgetDto(
    [property: JsonPropertyName("day")] DateTime Day,
    [property: JsonPropertyName("used")] int Used,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>The last run.</summary>
public sealed record RatingsRunDto(
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("finishedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? FinishedAt,
    [property: JsonPropertyName("fetched")] int Fetched,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("stopReason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StopReason);

/// <summary>A manual refresh was queued.</summary>
public sealed record RatingsRefreshQueuedDto([property: JsonPropertyName("queued")] bool Queued);
