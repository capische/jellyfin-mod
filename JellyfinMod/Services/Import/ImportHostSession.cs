namespace JellyfinMod.Services.Import;

/// <summary>
/// When this host process began running imports. The host keeps its pending folder refreshes and queued library scans in
/// memory only, so a scan an import requested before this moment was lost with the previous process (live finding 12).
/// </summary>
/// <param name="time">The clock; read once, when the first import tick of this process resolves the session.</param>
public sealed class ImportHostSession(TimeProvider time)
{
    /// <summary>Gets the moment this process's first import tick began, in UTC.</summary>
    public DateTime StartedAt { get; } = time.GetUtcNow().UtcDateTime;
}
