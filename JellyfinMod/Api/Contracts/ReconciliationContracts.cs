using System.Text.Json.Serialization;
using JellyfinMod.Data;
using JellyfinMod.Services;

namespace JellyfinMod.Api.Contracts;

/// <summary>Administrator-facing summary of one full reconciliation run.</summary>
public sealed record ReconciliationRunDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("scannedItems")] int ScannedItems,
    [property: JsonPropertyName("createdEntries")] int CreatedEntries,
    [property: JsonPropertyName("updatedBindings")] int UpdatedBindings,
    [property: JsonPropertyName("unchangedItems")] int UnchangedItems,
    [property: JsonPropertyName("unmatchedItems")] int UnmatchedItems,
    [property: JsonPropertyName("conflictedItems")] int ConflictedItems,
    [property: JsonPropertyName("failedItems")] int FailedItems,
    [property: JsonPropertyName("missingItems")] int MissingItems,
    [property: JsonPropertyName("incompleteLibraries")] int IncompleteLibraries,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ReconciliationDiagnostic> Diagnostics)
{
    /// <summary>Creates a wire-safe summary from its durable row.</summary>
    public ReconciliationRunDto(ReconciliationRun run, IReadOnlyList<ReconciliationDiagnostic> diagnostics)
        : this(run.Id, DateTime.SpecifyKind(run.StartedAt, DateTimeKind.Utc),
            run.CompletedAt.HasValue ? DateTime.SpecifyKind(run.CompletedAt.Value, DateTimeKind.Utc) : null,
            run.Status, run.TotalItems, run.ScannedItems, run.CreatedEntries, run.UpdatedBindings,
            run.UnchangedItems, run.UnmatchedItems, run.ConflictedItems, run.FailedItems, run.MissingItems,
            run.IncompleteLibraries, diagnostics)
    {
    }
}

/// <summary>An entry whose target library is no longer configured, listed for administrators (P2.R7).</summary>
public sealed record OrphanedEntryDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("tmdbId")] int TmdbId,
    [property: JsonPropertyName("targetLibraryId")] Guid? TargetLibraryId,
    [property: JsonPropertyName("state")] string State);
