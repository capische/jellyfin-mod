using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Data;

/// <summary>Durable completion-policy result for one movie or episode.</summary>
public sealed class RetentionEvaluation
{
    /// <summary>Gets or sets the evaluation identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the owning catalog entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the episode identity, or null for a movie.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Gets or sets the stable movie or episode identity.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Gets or sets the policy revision used for this result.</summary>
    public long PolicyVersion { get; set; }

    /// <summary>Gets or sets disabled, blocked, waiting or scheduled.</summary>
    [MaxLength(16)]
    public string State { get; set; } = RetentionEvaluationStates.Disabled;

    /// <summary>Gets or sets the stable explanation for the state.</summary>
    [MaxLength(48)]
    public string Reason { get; set; } = RetentionEvaluationReasons.RetentionDisabled;

    /// <summary>Gets or sets a non-identifying hash of the users with library access.</summary>
    [MaxLength(64)]
    public string AccessFingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets the completion instant which satisfied the watched-user mode.</summary>
    public DateTime? CompletionBasisAt { get; set; }

    /// <summary>Gets or sets when the current full grace period began.</summary>
    public DateTime? EligibleAt { get; set; }

    /// <summary>Gets or sets the resulting non-destructive retention deadline.</summary>
    public DateTime? Deadline { get; set; }

    /// <summary>Gets or sets when this result was computed.</summary>
    public DateTime EvaluatedAt { get; set; }

    /// <summary>Gets or sets the earliest instant the current representation's grace may start.</summary>
    /// <remarks>Set when the target is first evaluated and again when its last representation is reclaimed or unbound.</remarks>
    public DateTime BaselineAt { get; set; }

    /// <summary>Gets or sets a value indicating whether only completions after <see cref="BaselineAt"/> count.</summary>
    /// <remarks>Re-acquired media must be finished again; Jellyfin can reattach old user data to a re-added item.</remarks>
    public bool RequiresFreshCompletion { get; set; }
}

/// <summary>Internal T1 evaluation states.</summary>
internal static class RetentionEvaluationStates
{
    public const string Disabled = "disabled";
    public const string Blocked = "blocked";
    public const string Waiting = "waiting";
    public const string Scheduled = "scheduled";
}

/// <summary>Stable T1 completion-policy reasons.</summary>
internal static class RetentionEvaluationReasons
{
    public const string RetentionDisabled = "retention_disabled";
    public const string Kept = "kept";
    public const string AccessUnavailable = "access_unavailable";
    public const string NoAccessibleUsers = "no_accessible_users";
    public const string SelectedUserMissing = "selected_user_missing";
    public const string SelectedUserInaccessible = "selected_user_inaccessible";
    public const string CompletionEvidenceMissing = "completion_evidence_missing";
    public const string ActiveResume = "active_resume";
    public const string Favorite = "favorite";
    public const string FavoriteSeries = "favorite_series";
    public const string SeriesUnavailable = "series_unavailable";
    public const string WaitingForCompletion = "waiting_for_completion";
    public const string CompletionPolicySatisfied = "completion_policy_satisfied";
    public const string RepresentationReset = "representation_reset";
}

/// <summary>Resets a target's retention evidence when its last playable representation goes away.</summary>
internal static class RetentionTargetReset
{
    /// <summary>
    /// Clears any schedule and requires a completion after <paramref name="now"/>, so re-acquired or
    /// re-bound media never inherits a deadline or completion from the representation that was removed.
    /// The caller saves the context, inside its own transaction where one exists.
    /// </summary>
    public static async Task ResetAsync(ModDbContext database, Guid entryId, Guid? episodeId, DateTime now,
        CancellationToken cancellationToken)
    {
        var targetId = episodeId ?? entryId;
        var evaluation = await database.RetentionEvaluations.SingleOrDefaultAsync(
            candidate => candidate.TargetId == targetId, cancellationToken).ConfigureAwait(false);
        if (evaluation is null)
        {
            evaluation = new RetentionEvaluation { EntryId = entryId, EpisodeId = episodeId, TargetId = targetId };
            database.RetentionEvaluations.Add(evaluation);
        }

        evaluation.State = RetentionEvaluationStates.Waiting;
        evaluation.Reason = RetentionEvaluationReasons.RepresentationReset;
        evaluation.CompletionBasisAt = null;
        evaluation.EligibleAt = null;
        evaluation.Deadline = null;
        evaluation.EvaluatedAt = now;
        evaluation.BaselineAt = now;
        evaluation.RequiresFreshCompletion = true;
    }
}

/// <summary>Names files for people: the file name, lengthened by parent folders only where two files share one.</summary>
internal static class RetentionFileNames
{
    /// <summary>Returns the shortest trailing part of each path that tells the files apart, in order.</summary>
    public static string[] Distinct(IEnumerable<string> paths)
    {
        var parts = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var names = new string[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            for (var length = 1; length <= parts[index].Length; length++)
            {
                var suffix = string.Join('/', parts[index][^length..]);
                names[index] = suffix;
                if (parts.Where((_, other) => other != index)
                    .All(path => path.Length < length || string.Join('/', path[^length..]) != suffix)) break;
            }
        }

        return names;
    }
}

/// <summary>
/// How a movie's or an episode's own retention override combines with its entry's (P10.E2). Keep on the entry or on the
/// episode protects the episode; an episode's own window wins over its series' window, which wins over the global one.
/// </summary>
internal static class RetentionOverrides
{
    /// <summary>Whether the target is kept. <paramref name="episodePolicy"/> is null for a movie.</summary>
    public static bool IsKept(Entry entry, RetentionPolicy? episodePolicy) =>
        entry.RetentionPolicy == RetentionPolicy.Never || episodePolicy == RetentionPolicy.Never;

    /// <summary>Whether a tracked episode or its series is kept.</summary>
    public static bool IsKept(Entry entry, Episode? episode) => IsKept(entry, episode?.RetentionPolicy);

    /// <summary>The target's retention window in days.</summary>
    public static int WindowDays(Entry entry, RetentionPolicy? episodePolicy, int? episodeDays, int globalDays)
    {
        if (episodePolicy == RetentionPolicy.Days && episodeDays is > 0) return Math.Clamp(episodeDays.Value, 1, 3650);
        if (entry.RetentionPolicy == RetentionPolicy.Days && entry.ReclaimAfterDays is > 0)
            return Math.Clamp(entry.ReclaimAfterDays.Value, 1, 3650);
        return globalDays;
    }
}
