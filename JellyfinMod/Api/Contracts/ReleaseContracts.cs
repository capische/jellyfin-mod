using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;

namespace JellyfinMod.Api.Contracts;

/// <summary>The searched target.</summary>
public sealed record ReleaseTargetDto(
    [property: JsonPropertyName("entryId")] Guid EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Year,
    [property: JsonPropertyName("seasonNumber"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeasonNumber,
    [property: JsonPropertyName("episodeNumber"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? EpisodeNumber);

/// <summary>The profile a search was evaluated under.</summary>
public sealed record ReleaseProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("inherited")] bool Inherited);

/// <summary>Whether a grab can start from this search, and why not.</summary>
public sealed record GrabAvailabilityDto(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonPropertyName("holdSeconds")] int HoldSeconds,
    [property: JsonPropertyName("activeOperationId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ActiveOperationId);

/// <summary>One per-indexer search outcome.</summary>
public sealed record IndexerOutcomeDto(
    [property: JsonPropertyName("indexerId")] Guid IndexerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Message,
    [property: JsonPropertyName("resultCount")] int ResultCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("retryAfterSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? RetryAfterSeconds);

/// <summary>How the row matched the target.</summary>
public sealed record ReleaseMatchDto(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("method")] string Method);

/// <summary>One evaluated release. The download locator is never included.</summary>
public sealed record ReleaseCandidateDto(
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("indexerId")] Guid IndexerId,
    [property: JsonPropertyName("indexerName")] string IndexerName,
    [property: JsonPropertyName("rawTitle")] string RawTitle,
    [property: JsonPropertyName("parsed")] ParsedRelease Parsed,
    [property: JsonPropertyName("match")] ReleaseMatchDto Match,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Size,
    [property: JsonPropertyName("seeders"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Seeders,
    [property: JsonPropertyName("peers"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Peers,
    [property: JsonPropertyName("publishedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? PublishedAt,
    [property: JsonPropertyName("freeleech"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Freeleech,
    [property: JsonPropertyName("proper")] bool Proper,
    [property: JsonPropertyName("repack")] bool Repack,
    [property: JsonPropertyName("infoHash"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InfoHash,
    [property: JsonPropertyName("sameHashReleaseIds")] IReadOnlyList<string> SameHashReleaseIds,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("contributions")] IReadOnlyList<ScoreContribution> Contributions,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("rejections")] IReadOnlyList<ReleaseRejection> Rejections,
    [property: JsonPropertyName("seedRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? SeedRatio,
    [property: JsonPropertyName("seedMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeedMinutes);

/// <summary>The response of <c>GET /JellyfinMod/Releases</c>.</summary>
public sealed record ReleaseSearchDto(
    [property: JsonPropertyName("searchId")] Guid SearchId,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt,
    [property: JsonPropertyName("target")] ReleaseTargetDto Target,
    [property: JsonPropertyName("profile")] ReleaseProfileDto Profile,
    [property: JsonPropertyName("grab")] GrabAvailabilityDto Grab,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReleaseCandidateDto> Candidates,
    [property: JsonPropertyName("eligibleCount")] int EligibleCount,
    [property: JsonPropertyName("rejectedCount")] int RejectedCount,
    [property: JsonPropertyName("indexers")] IReadOnlyList<IndexerOutcomeDto> Indexers,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("truncated")] bool Truncated)
{
    /// <summary>Builds the public view of a snapshot.</summary>
    public static ReleaseSearchDto From(ReleaseSearchSnapshot snapshot, GrabAvailabilityDto grab)
    {
        var byHash = snapshot.Candidates.Where(candidate => candidate.InfoHash is not null)
            .GroupBy(candidate => candidate.InfoHash!).ToDictionary(group => group.Key, group => group.Select(c => c.ReleaseId).ToArray());
        var target = snapshot.Target;
        return new ReleaseSearchDto(snapshot.SearchId, Utc(snapshot.CreatedAt), Utc(snapshot.ExpiresAt),
            new ReleaseTargetDto(target.EntryId, target.EpisodeId, target.MediaType, target.Title, target.Year, target.SeasonNumber,
                target.EpisodeNumber),
            new ReleaseProfileDto(snapshot.Profile.Id, snapshot.Profile.Name, snapshot.Profile.Revision, snapshot.ProfileInherited),
            grab,
            snapshot.Candidates.Select(candidate => new ReleaseCandidateDto(candidate.ReleaseId, candidate.IndexerId,
                candidate.IndexerName, candidate.RawTitle, candidate.Parsed,
                new ReleaseMatchDto(candidate.Evaluation.Identity, candidate.SearchMethod), candidate.Size, candidate.Seeders,
                candidate.Peers, candidate.PublishedAt is { } published ? Utc(published) : null, candidate.Freeleech,
                candidate.Parsed.Proper, candidate.Parsed.Repack, candidate.InfoHash,
                candidate.InfoHash is { } hash ? byHash[hash].Where(id => id != candidate.ReleaseId).ToArray() : [],
                candidate.Evaluation.Score, candidate.Evaluation.Contributions, candidate.Evaluation.Eligible,
                candidate.Evaluation.Rejections, candidate.SeedRatio, candidate.SeedMinutes)).ToArray(),
            snapshot.Candidates.Count(candidate => candidate.Evaluation.Eligible),
            snapshot.Candidates.Count(candidate => !candidate.Evaluation.Eligible),
            snapshot.Indexers.Select(outcome => new IndexerOutcomeDto(outcome.IndexerId, outcome.Name, outcome.Status,
                outcome.Message, outcome.ResultCount, outcome.Truncated, outcome.RetryAfterSeconds)).ToArray(),
            snapshot.Indexers.Any(outcome => outcome.Status is not ("ok" or "no_results")),
            snapshot.Indexers.Any(outcome => outcome.Truncated));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>The only fields a grab request may carry; everything else is derived from server records.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GrabReleaseRequest
{
    /// <summary>Gets or sets the search the release came from.</summary>
    [JsonPropertyName("searchId")]
    public Guid SearchId { get; set; }

    /// <summary>Gets or sets the opaque release identity.</summary>
    [Required, MaxLength(64), JsonPropertyName("releaseId")]
    public string ReleaseId { get; set; } = string.Empty;

    /// <summary>Gets or sets the client-generated idempotency key.</summary>
    [Required, MinLength(8), MaxLength(128), RegularExpression("^[A-Za-z0-9._:-]+$"), JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;
}

/// <summary>The canonical grab operation.</summary>
public sealed record GrabOperationDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("cancellable")] bool Cancellable,
    [property: JsonPropertyName("entryId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EntryId,
    [property: JsonPropertyName("episodeId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? EpisodeId,
    [property: JsonPropertyName("releaseTitle")] string ReleaseTitle,
    [property: JsonPropertyName("indexerName")] string IndexerName,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quality,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Size,
    [property: JsonPropertyName("infoHash"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InfoHash,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("seedRatio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? SeedRatio,
    [property: JsonPropertyName("seedMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeedMinutes,
    [property: JsonPropertyName("holdUntil")] DateTime HoldUntil,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("submittedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? SubmittedAt,
    [property: JsonPropertyName("acceptedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? AcceptedAt,
    [property: JsonPropertyName("cancelledAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? CancelledAt,
    [property: JsonPropertyName("failureCode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FailureCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("openUrl"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OpenUrl)
{
    /// <summary>Builds the public view of an operation.</summary>
    public static GrabOperationDto From(GrabOperation operation, string? openUrl)
    {
        var parsed = JsonSerializer.Deserialize<ParsedRelease>(operation.ParsedJson);
        static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
        static DateTime? UtcOrNull(DateTime? value) => value is { } date ? Utc(date) : null;
        return new GrabOperationDto(operation.Id, operation.State, operation.ActiveTarget is not null,
            operation.State == GrabStates.Pending, operation.EntryId, operation.EpisodeId, operation.RawTitle, operation.IndexerName,
            parsed?.Quality, operation.Size, operation.InfoHash, operation.Score, operation.SeedRatio, operation.SeedMinutes,
            Utc(operation.HoldUntil), Utc(operation.CreatedAt), Utc(operation.UpdatedAt), UtcOrNull(operation.SubmittedAt),
            UtcOrNull(operation.AcceptedAt), UtcOrNull(operation.CancelledAt), operation.FailureCode, Describe(operation),
            operation.State == GrabStates.Accepted ? openUrl : null);
    }

    private static string Describe(GrabOperation operation) => operation.State switch
    {
        GrabStates.Pending => "Held before sending; it can still be cancelled.",
        GrabStates.Submitting => "Sending to the download client.",
        GrabStates.Accepted => "The download client accepted the torrent.",
        GrabStates.Cancelled => "Cancelled before anything was sent.",
        GrabStates.Unknown => operation.FailureCode == "client_settings_mismatch"
            ? "The download client holds the torrent with different settings. Check it in the client."
            : "The download client did not confirm the torrent. It is checked again before any retry.",
        _ => operation.FailureCode switch
        {
            "interrupted_before_submit" => "The server restarted during the hold; nothing was sent. Search again.",
            "client_torrent_exists" => "The download client already has this torrent outside JellyfinMod; it was left untouched.",
            "client_auth_failed" => "The download client rejected its credentials.",
            "client_unreachable" => "The download client could not be reached; nothing was sent.",
            "client_absent" => "The download client does not have the torrent.",
            "destination_not_same_filesystem" => "The download folder no longer shares a filesystem with the library.",
            "configuration_changed" => "The download client settings changed during the hold; nothing was sent.",
            "acquisition_disabled" => "Grabbing was turned off during the hold; nothing was sent.",
            _ => "The grab failed."
        }
    };
}

/// <summary>A privacy-safe acquisition summary for entry and episode reads (P4.A6).</summary>
public sealed record AcquisitionSummaryDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("operationId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? OperationId,
    [property: JsonPropertyName("releaseTitle"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReleaseTitle,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quality,
    [property: JsonPropertyName("failureCode"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FailureCode)
{
    /// <summary>Summarizes the newest operation; ordinary users see only its state and time.</summary>
    public static AcquisitionSummaryDto From(GrabOperation operation, bool administrator)
    {
        var updated = DateTime.SpecifyKind(operation.UpdatedAt, DateTimeKind.Utc);
        if (!administrator) return new AcquisitionSummaryDto(operation.State, updated, null, null, null, null);
        var parsed = JsonSerializer.Deserialize<ParsedRelease>(operation.ParsedJson);
        return new AcquisitionSummaryDto(operation.State, updated, operation.Id, operation.RawTitle, parsed?.Quality,
            operation.FailureCode);
    }
}
