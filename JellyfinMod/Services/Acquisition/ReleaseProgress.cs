using System.Collections.Concurrent;

namespace JellyfinMod.Services.Acquisition;

/// <summary>One indexer's line in a search's progress: waiting, being asked, or its final outcome.</summary>
/// <param name="IndexerId">The indexer.</param>
/// <param name="Name">Its display name.</param>
/// <param name="State"><c>queued</c>, <c>verifying</c>, <c>asking</c>, or the final status of the search (<c>ok</c>, <c>no_results</c>, a failure code).</param>
/// <param name="StartedAt">When its first query went out, while <c>asking</c>.</param>
/// <param name="ResultCount">The rows it returned, once final.</param>
/// <param name="RetryAfterSeconds">When to ask again, for a paused or rate-limited indexer.</param>
/// <param name="ColorIndex">The indexer's colour in the release picker (P4.FU25).</param>
public sealed record IndexerProgress(Guid IndexerId, string Name, string State, DateTime? StartedAt, int ResultCount, int? RetryAfterSeconds,
    int ColorIndex = 0);

/// <summary>What a person's running search has done so far (the release picker's live progress, 2026-10-10).</summary>
/// <param name="UserId">Who started it.</param>
/// <param name="SearchId">
/// The id its snapshot is stored under: the rows of the indexers that have answered while it runs, then the final one (P4.FU24).
/// </param>
/// <param name="Indexers">One line per enabled indexer.</param>
/// <param name="Finished">Whether the search is over.</param>
/// <param name="FinishedAt">When it ended.</param>
public sealed record SearchProgress(Guid UserId, Guid SearchId, IReadOnlyList<IndexerProgress> Indexers, bool Finished, DateTime? FinishedAt);

/// <summary>
/// A small in-memory side channel for the release picker: while <c>GET /Releases</c> runs, the page polls the snapshot a search
/// writes under a key the page chose. Nothing is stored in the database, and a search without a key writes nothing. A finished
/// entry is kept for a minute so the last poll still sees it, and the table is bounded.
/// </summary>
public sealed class ReleaseProgressTable(TimeProvider time)
{
    /// <summary>How long a finished search stays readable.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);

    private const int Capacity = 128;
    private readonly ConcurrentDictionary<string, SearchProgress> _entries = new(StringComparer.Ordinal);

    /// <summary>Whether a key is one the page could have made: short and made of letters, digits, dashes and underscores.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= 64 && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>Starts a search's entry with every indexer waiting; blocked ones are final at once.</summary>
    public void Start(string key, Guid userId, Guid searchId, IEnumerable<IndexerProgress> indexers)
    {
        Prune();
        _entries[key] = new SearchProgress(userId, searchId, indexers.ToArray(), false, null);
    }

    /// <summary>Sets one indexer's state.</summary>
    public void Set(string key, IndexerProgress line) =>
        Update(key, current => current with
        {
            Indexers = current.Indexers.Select(existing => existing.IndexerId == line.IndexerId ? line : existing).ToArray()
        });

    /// <summary>Marks the search over.</summary>
    public void Finish(string key) =>
        Update(key, current => current with { Finished = true, FinishedAt = time.GetUtcNow().UtcDateTime });

    /// <summary>Reads a search's progress; only the user who started it can.</summary>
    public SearchProgress? Find(string key, Guid userId)
    {
        Prune();
        return _entries.TryGetValue(key, out var progress) && progress.UserId == userId ? progress : null;
    }

    private void Update(string key, Func<SearchProgress, SearchProgress> change)
    {
        while (_entries.TryGetValue(key, out var current))
            if (_entries.TryUpdate(key, change(current), current)) return;
    }

    private void Prune()
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (key, progress) in _entries)
            if (progress.FinishedAt is { } finished && finished + Retention <= now) _entries.TryRemove(key, out _);
        if (_entries.Count <= Capacity) return;
        foreach (var key in _entries.OrderBy(pair => pair.Value.FinishedAt is null ? 1 : 0).ThenBy(pair => pair.Value.FinishedAt)
                     .Take(_entries.Count - Capacity).Select(pair => pair.Key).ToArray())
            _entries.TryRemove(key, out _);
    }
}
