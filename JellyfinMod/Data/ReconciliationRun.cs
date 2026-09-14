namespace JellyfinMod.Data;

/// <summary>Durable summary of one full catalog reconciliation run.</summary>
public sealed class ReconciliationRun
{
    /// <summary>Gets or sets the run identity.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets when the run started.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Gets or sets when the run completed or was interrupted.</summary>
    public DateTime? CompletedAt { get; set; }
    /// <summary>Gets or sets the running, completed, cancelled, or failed state.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Gets or sets the number of title observations expected.</summary>
    public int TotalItems { get; set; }
    /// <summary>Gets or sets the number of title observations attempted.</summary>
    public int ScannedItems { get; set; }
    /// <summary>Gets or sets the number of newly created entries.</summary>
    public int CreatedEntries { get; set; }
    /// <summary>Gets or sets the number of existing entries or episode sets updated.</summary>
    public int UpdatedBindings { get; set; }
    /// <summary>Gets or sets the number of observations already converged.</summary>
    public int UnchangedItems { get; set; }
    /// <summary>Gets or sets the number of observations without usable provider identity.</summary>
    public int UnmatchedItems { get; set; }
    /// <summary>Gets or sets the number of observations that conflicted with durable identity.</summary>
    public int ConflictedItems { get; set; }
    /// <summary>Gets or sets the number of observations that failed independently.</summary>
    public int FailedItems { get; set; }
    /// <summary>Gets or sets the number of catalog titles or episodes confirmed to have lost playable media.</summary>
    public int MissingItems { get; set; }
    /// <summary>Gets or sets the number of libraries whose absence check remained uncertain.</summary>
    public int IncompleteLibraries { get; set; }
    /// <summary>Gets or sets bounded administrator diagnostics as JSON.</summary>
    public string? DiagnosticsJson { get; set; }
}
