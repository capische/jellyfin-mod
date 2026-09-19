using System.Collections.Concurrent;

namespace JellyfinMod.Services.Acquisition;

/// <summary>One evaluated feed row with its server-only download locator.</summary>
public sealed record ReleaseCandidate(
    string ReleaseId,
    Guid IndexerId,
    string IndexerName,
    int IndexerPriority,
    int IndexerRevision,
    string SourceGuid,
    string RawTitle,
    ParsedRelease Parsed,
    long? Size,
    int? Seeders,
    int? Peers,
    DateTime? PublishedAt,
    bool? Freeleech,
    string? InfoHash,
    ReleaseEvaluation Evaluation,
    double? SeedRatio,
    int? SeedMinutes,
    string SearchMethod,
    IReadOnlySet<string> AllowedHosts,
    // Server-only; never serialized. May embed a passkey.
    string? DownloadUrl,
    string? MagnetUrl);

/// <summary>The per-indexer outcome of one search.</summary>
public sealed record IndexerOutcome(
    Guid IndexerId,
    string Name,
    string Status,
    string? Message,
    int ResultCount,
    bool Truncated,
    int? RetryAfterSeconds);

/// <summary>An immutable evaluated search, valid for one user and one target until it expires.</summary>
public sealed record ReleaseSearchSnapshot(
    Guid SearchId,
    Guid UserId,
    ReleaseTarget Target,
    EvaluationProfile Profile,
    bool ProfileInherited,
    int SettingsRevision,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<ReleaseCandidate> Candidates,
    IReadOnlyList<IndexerOutcome> Indexers);

/// <summary>
/// A bounded in-memory store of search snapshots (P4.A1). Expiry or restart requires a new search; configuration
/// changes clear it, so nothing is ever submitted under rules that changed after it was evaluated.
/// </summary>
public sealed class ReleaseSearchCache(TimeProvider time)
{
    /// <summary>How long a search can be grabbed from.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private const int Capacity = 64;
    private readonly ConcurrentDictionary<Guid, ReleaseSearchSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backoff = new();

    /// <summary>Gets the current time used for expiry.</summary>
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>Stores a snapshot, evicting the oldest beyond capacity.</summary>
    public void Add(ReleaseSearchSnapshot snapshot)
    {
        _snapshots[snapshot.SearchId] = snapshot;
        foreach (var stale in _snapshots.Values.OrderByDescending(value => value.CreatedAt).Skip(Capacity))
            _snapshots.TryRemove(stale.SearchId, out _);
    }

    /// <summary>Finds a snapshot owned by the user; expired snapshots are still reported as expired.</summary>
    public (ReleaseSearchSnapshot? Snapshot, bool Expired) Find(Guid searchId, Guid userId)
    {
        if (!_snapshots.TryGetValue(searchId, out var snapshot) || snapshot.UserId != userId) return (null, false);
        return (snapshot, snapshot.ExpiresAt <= UtcNow);
    }

    /// <summary>Forgets every snapshot after a configuration change.</summary>
    public void Invalidate() => _snapshots.Clear();

    /// <summary>Records an indexer's rate-limit guidance.</summary>
    public void BackOff(Guid indexerId, TimeSpan delay) =>
        _backoff[indexerId.ToString("N")] = time.GetUtcNow() + (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay);

    /// <summary>Returns the remaining back-off for an indexer, if any.</summary>
    public TimeSpan? RemainingBackOff(Guid indexerId) =>
        _backoff.TryGetValue(indexerId.ToString("N"), out var until) && until > time.GetUtcNow() ? until - time.GetUtcNow() : null;
}
