using System.ComponentModel.DataAnnotations;

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
    public const string WaitingForCompletion = "waiting_for_completion";
    public const string CompletionPolicySatisfied = "completion_policy_satisfied";
}
