using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>Bounds for one indexer during a search.</summary>
public sealed record TorznabOptions(TimeSpan Timeout, int MaxPages, int MaxParallel)
{
    /// <summary>The production bounds.</summary>
    public static TorznabOptions Default { get; } = new(TimeSpan.FromSeconds(20), 3, 4);
}

/// <summary>
/// Fans a target out to enabled indexers using only advertised capabilities, evaluates every row, and stores an
/// immutable snapshot (P4.A3/A4). A search never submits anything.
/// </summary>
public sealed class ReleaseSearchService(
    ModDbContext database,
    TorznabClient torznab,
    AcquisitionConfiguration configuration,
    ReleaseSearchCache cache,
    TorznabOptions options,
    ILogger<ReleaseSearchService> logger)
{
    private const int MaxLimit = 100;

    /// <summary>Verifies an indexer's capabilities and records them against its current revision.</summary>
    public async Task<TorznabCapabilities> VerifyAsync(AcquisitionIndexer indexer, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            var endpoint = await configuration.EndpointAsync(indexer, timeout.Token).ConfigureAwait(false);
            var capabilities = await torznab.GetCapabilitiesAsync(endpoint, timeout.Token).ConfigureAwait(false);
            indexer.CapabilitiesJson = JsonSerializer.Serialize(capabilities);
            indexer.CapabilitiesFetchedAt = DateTime.UtcNow;
            indexer.VerifiedRevision = indexer.Revision;
            indexer.LastError = null;
            return capabilities;
        }
        catch (TorznabException error)
        {
            indexer.VerifiedRevision = null;
            indexer.LastError = error.Code;
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            indexer.VerifiedRevision = null;
            indexer.LastError = "timeout";
            throw new TorznabException("timeout", "The indexer did not answer in time.");
        }
    }

    /// <summary>Indexer outcomes that count towards an indexer's circuit breaker (P6.M3).</summary>
    public static readonly IReadOnlySet<string> BreakerFailures =
        new HashSet<string>(["timeout", "rate_limited", "unavailable", "malformed_response", "indexer_error", "response_too_large"],
            StringComparer.Ordinal);

    /// <summary>Consecutive failures that open an indexer's breaker.</summary>
    public const int BreakerThreshold = 5;

    /// <summary>How long an open breaker keeps an indexer out of searches.</summary>
    public static readonly TimeSpan BreakerDuration = TimeSpan.FromHours(1);

    /// <summary>Searches every enabled indexer and stores the evaluated snapshot.</summary>
    public async Task<ReleaseSearchSnapshot> SearchAsync(Guid userId, ReleaseTarget target, EvaluationProfile profile,
        bool profileInherited, int settingsRevision, CancellationToken cancellationToken, ReleaseSearchOptions? searchOptions = null)
    {
        searchOptions ??= ReleaseSearchOptions.Default;
        var indexers = await database.AcquisitionIndexers.Where(indexer => indexer.Enabled)
            .OrderBy(indexer => indexer.Priority).ThenBy(indexer => indexer.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Per-indexer budgets and breakers apply to every search, manual ones included (P6.M3).
        var now = cache.UtcNow;
        var budgets = await IndexerBudgetsAsync(indexers, now, cancellationToken).ConfigureAwait(false);
        var blocked = new Dictionary<Guid, IndexerOutcome>();
        foreach (var indexer in indexers)
        {
            var budget = budgets[indexer.Id];
            IndexerQueryLedger.Seed(indexer.Id, budget, now);
            if (budget.BreakerOpenUntil is { } open && open > now)
                blocked[indexer.Id] = new(indexer.Id, indexer.Name, "breaker_open",
                    "The indexer failed repeatedly; it is paused until " + open.ToString("u", CultureInfo.InvariantCulture) + ".", 0, false,
                    (int)Math.Ceiling((open - now).TotalSeconds));
        }
        // Capabilities first: anything unverified at its current revision is checked before it is searched.
        var capabilities = new Dictionary<Guid, (TorznabCapabilities? Value, string? Error)>();
        foreach (var indexer in indexers)
        {
            if (indexer.VerifiedRevision == indexer.Revision && AcquisitionConfiguration.Capabilities(indexer) is { } known)
            {
                capabilities[indexer.Id] = (known, null);
                continue;
            }

            try
            {
                capabilities[indexer.Id] = (await VerifyAsync(indexer, cancellationToken).ConfigureAwait(false), null);
            }
            catch (TorznabException error)
            {
                capabilities[indexer.Id] = (null, error.Code);
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = new Dictionary<Guid, TorznabEndpoint?>();
        foreach (var indexer in indexers)
        {
            try
            {
                endpoints[indexer.Id] = await configuration.EndpointAsync(indexer, cancellationToken).ConfigureAwait(false);
            }
            catch (TorznabException)
            {
                endpoints[indexer.Id] = null;
            }
        }

        using var gate = new SemaphoreSlim(options.MaxParallel);
        (IReadOnlyList<ReleaseCandidate> Candidates, IndexerOutcome Outcome, int Queries)[] results;
        try
        {
            results = await Task.WhenAll(indexers.Select(async indexer =>
            {
                if (blocked.TryGetValue(indexer.Id, out var refused)) return (Candidates: (IReadOnlyList<ReleaseCandidate>)[], Outcome: refused, Queries: 0);
                if (!IndexerQueryLedger.HasBudget(indexer.Id, cache.UtcNow, indexer.DailyQueryBudget))
                    return (Candidates: (IReadOnlyList<ReleaseCandidate>)[], Outcome: new IndexerOutcome(indexer.Id, indexer.Name, "budget_exhausted",
                        "The indexer's daily query budget is used up.", 0, false, null), Queries: 0);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await SearchIndexerAsync(indexer, capabilities[indexer.Id], endpoints[indexer.Id], target, profile,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A search cancelled or failed after some queries went out still persists every query that was sent, so a restart
            // cannot spend them again (final review, finding 5; final Pi review, finding 2). The ledger counts a query as sent
            // only once it is about to go out and gives back reservations never sent, so nothing unsent is stored (final
            // review 2, finding 2).
            foreach (var indexer in indexers.Where(indexer => !blocked.ContainsKey(indexer.Id)))
                await PersistBudgetAsync(indexer.Id, false, false, cache.UtcNow).ConfigureAwait(false);
            throw;
        }

        var queries = new Dictionary<Guid, int>();
        var breakersOpened = new List<Guid>();
        var finished = cache.UtcNow;
        foreach (var (indexer, result) in indexers.Zip(results))
        {
            if (blocked.ContainsKey(indexer.Id)) continue;
            queries[indexer.Id] = result.Queries;
            // The ledger counted every query as it was sent; it holds this process's view, the row persists it.
            if (await PersistBudgetAsync(indexer.Id, BreakerFailures.Contains(result.Outcome.Status),
                    result.Outcome.Status is "ok" or "no_results", finished).ConfigureAwait(false))
            {
                breakersOpened.Add(indexer.Id);
                logger.LogWarning("Indexer {Indexer} failed {Count} times in a row; paused for an hour", indexer.Name, BreakerThreshold);
            }
        }

        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // A release an administrator blocklisted from the queue is rejected with a visible reason (P5.I7).
        var blocklist = await database.ReleaseBlocklist.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var blockedHashes = blocklist.Where(entry => entry.InfoHash is not null).Select(entry => entry.InfoHash!).ToHashSet(StringComparer.Ordinal);
        var blockedGuids = blocklist.Where(entry => entry.IndexerId is not null && entry.SourceGuid is not null)
            .Select(entry => (entry.IndexerId!.Value, entry.SourceGuid!)).ToHashSet();
        var candidates = results.SelectMany(result => result.Candidates)
            .Select(candidate => candidate.InfoHash is { } hash && blockedHashes.Contains(hash) ||
                blockedGuids.Contains((candidate.IndexerId, candidate.SourceGuid))
                    ? candidate with
                    {
                        Evaluation = candidate.Evaluation with
                        {
                            Eligible = false,
                            Rejections = candidate.Evaluation.Rejections
                                .Append(new ReleaseRejection("blocklisted", "An administrator removed this release from the queue and blocked it."))
                                .ToArray()
                        }
                    }
                    : candidate)
            .OrderByDescending(candidate => candidate.Evaluation.Eligible)
            .ThenByDescending(candidate => candidate.Evaluation.Score)
            .ThenBy(candidate => candidate.Seeders is null)
            .ThenByDescending(candidate => candidate.Seeders ?? 0)
            .ThenBy(candidate => candidate.IndexerPriority)
            .ThenByDescending(candidate => candidate.PublishedAt ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.ReleaseId, StringComparer.Ordinal)
            .ToArray();
        var created = cache.UtcNow;
        var snapshot = new ReleaseSearchSnapshot(Guid.NewGuid(), userId, target, profile, profileInherited, settingsRevision,
            created, created + ReleaseSearchCache.Lifetime, candidates, results.Select(result => result.Outcome).ToArray())
        {
            Intent = searchOptions.Intent,
            HeldQualities = searchOptions.HeldQualities ?? new HashSet<string>(StringComparer.Ordinal),
            QueriesByIndexer = queries,
            BreakersOpened = breakersOpened
        };
        cache.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Loads the budget rows, creating any missing one first. A row is created once: a concurrent search that created it
    /// first is not an error (Codex round 2 P2). The rows are read untracked; counters are written with conditional
    /// updates only, never by saving a stale row over a newer one.
    /// </summary>
    private async Task<Dictionary<Guid, IndexerBudgetState>> IndexerBudgetsAsync(IReadOnlyList<AcquisitionIndexer> indexers, DateTime now,
        CancellationToken cancellationToken)
    {
        var ids = indexers.Select(indexer => indexer.Id).ToArray();
        var existing = await database.IndexerBudgets.AsNoTracking().Where(budget => ids.Contains(budget.IndexerId))
            .Select(budget => budget.IndexerId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var missing in ids.Except(existing))
        {
            var created = new IndexerBudgetState { IndexerId = missing, Day = now.Date };
            database.IndexerBudgets.Add(created);
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // Another search created it meanwhile.
            }
            finally
            {
                database.Entry(created).State = EntityState.Detached;
            }
        }

        return await database.IndexerBudgets.AsNoTracking().Where(budget => ids.Contains(budget.IndexerId))
            .ToDictionaryAsync(budget => budget.IndexerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists what this process's ledger holds for an indexer, never moving the stored day, count or last request time
    /// backwards (Codex round 2 P2), and counts a breaker failure or success atomically. Returns whether this search opened
    /// the breaker.
    /// </summary>
    private async Task<bool> PersistBudgetAsync(Guid indexerId, bool failed, bool succeeded, DateTime finished)
    {
        if (IndexerQueryLedger.Snapshot(indexerId) is var (day, sent, lastSent))
        {
            await database.IndexerBudgets.Where(row => row.IndexerId == indexerId &&
                    (row.Day < day || row.Day == day && row.QueriesUsed < sent))
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Day, day).SetProperty(row => row.QueriesUsed, sent))
                .ConfigureAwait(false);
            if (lastSent is { } last)
                await database.IndexerBudgets.Where(row => row.IndexerId == indexerId && (row.LastQueryAt == null || row.LastQueryAt < last))
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastQueryAt, last)).ConfigureAwait(false);
        }

        if (succeeded)
        {
            await database.IndexerBudgets.Where(row => row.IndexerId == indexerId && row.ConsecutiveFailures != 0)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ConsecutiveFailures, 0)).ConfigureAwait(false);
            return false;
        }

        if (!failed) return false;
        await database.IndexerBudgets.Where(row => row.IndexerId == indexerId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ConsecutiveFailures, row => row.ConsecutiveFailures + 1))
            .ConfigureAwait(false);
        var until = finished + BreakerDuration;
        return await database.IndexerBudgets.Where(row => row.IndexerId == indexerId && row.ConsecutiveFailures >= BreakerThreshold)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.BreakerOpenUntil, until).SetProperty(row => row.ConsecutiveFailures, 0))
            .ConfigureAwait(false) > 0;
    }

    private async Task<(IReadOnlyList<ReleaseCandidate> Candidates, IndexerOutcome Outcome, int Queries)> SearchIndexerAsync(
        AcquisitionIndexer indexer, (TorznabCapabilities? Value, string? Error) capabilities, TorznabEndpoint? endpoint,
        ReleaseTarget target, EvaluationProfile profile, CancellationToken cancellationToken)
    {
        IndexerOutcome Outcome(string status, string? message, int count = 0, bool truncated = false, int? retry = null) =>
            new(indexer.Id, indexer.Name, status, message, count, truncated, retry);
        if (endpoint is null)
            return ([], Outcome("secret_unavailable", "The saved API key is no longer available; enter it again."), 0);
        if (capabilities.Value is not { } caps)
            return ([], Outcome(capabilities.Error ?? "capabilities_unavailable", "The indexer's capabilities could not be verified."), 0);
        if (cache.RemainingBackOff(indexer.Id) is { } backOff)
            return ([], Outcome("rate_limited", "The indexer asked to wait before searching again.", retry: (int)Math.Ceiling(backOff.TotalSeconds)), 0);

        var attempts = BuildQueries(caps, target);
        if (attempts.Count == 0)
            return ([], Outcome("unsupported_search", "The indexer does not advertise a search this title can use."), 0);
        var configured = indexer.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var categories = caps.Categories.Count == 0 ? configured : configured.Where(caps.Categories.Contains).ToArray();
        if (configured.Length > 0 && categories.Length == 0)
            return ([], Outcome("unsupported_search", "None of the configured categories is advertised by the indexer."), 0);
        var limit = Math.Min(caps.LimitMax ?? MaxLimit, MaxLimit);
        var allowedHosts = AcquisitionConfiguration.AllowedHosts(indexer);
        var items = new List<TorznabItem>();
        var truncated = false;
        var budgetStopped = false;
        var pages = 0;
        var identity = attempts[0].Identity;
        var method = attempts[0].Method;
        // The first query takes the indexer's next permitted time and a unit of its daily budget atomically, so concurrent
        // searches cannot read the same allowance; the minimum interval is waited out, never skipped. Every further page or
        // fallback query takes a unit of its own (whole-review chunk 2a, P2 4).
        var reservation = IndexerQueryLedger.ReserveSearch(indexer.Id, cache.UtcNow, indexer.DailyQueryBudget,
            TimeSpan.FromSeconds(Math.Max(0, indexer.MinIntervalSeconds)));
        if (reservation is null) return ([], Outcome("budget_exhausted", "The indexer's daily query budget is used up."), 0);
        // A search abandoned while it waits gives its unit and its slot back: nothing was sent, and the searches behind it
        // move up (Codex round 2 P2, delta review 2).
        try
        {
            await IndexerQueryLedger.WaitAsync(reservation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IndexerQueryLedger.Release(reservation);
            throw;
        }

        // Each query holds its unit until it is handed to HTTP, and counts as sent only then: the first query its reservation,
        // each further page or fallback query a unit of its own. A search cancelled, timed out or failed before that gives the
        // unit back, since nothing was sent (final review 2, finding 2; Pi review 1, finding 1).
        var first = reservation;
        var further = false;
        var sent = 0;
        // The indexer's answer deadline starts when its first query is handed to HTTP: a wait for the indexer's turn is
        // pacing, never the indexer being slow, so it cannot time the indexer out or count toward its breaker (final Pi
        // review 2, finding 1). Until then only the caller's cancellation applies.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The first query goes out only once a full interval has passed since the indexer's last request actually went out,
        // checked and committed together: a search whose wait ended late, or one ahead of it that went out late, waits again
        // instead of sending too soon (final Pi review, finding 1).
        async ValueTask SendingAsync(CancellationToken token)
        {
            if (first is not null)
            {
                while (!IndexerQueryLedger.TryCommit(first, cache.UtcNow))
                    await IndexerQueryLedger.WaitAsync(first, token).ConfigureAwait(false);
                timeout.CancelAfter(options.Timeout);
            }
            else if (further)
            {
                IndexerQueryLedger.DispatchQuery(indexer.Id, cache.UtcNow);
            }

            sent++;
            first = null;
            further = false;
        }

        void Unsent()
        {
            if (first is not null) IndexerQueryLedger.Release(first);
            else if (further) IndexerQueryLedger.ReleaseQuery(indexer.Id);
            first = null;
            further = false;
        }

        try
        {
            // Real indexers answer nothing for a query their own engine cannot match — 1337x returns zero
            // for "Night of the Living Dead" and eighty for "night living dead". Each attempt is tried until
            // one returns rows; the evaluator still verifies every row against the target, so a looser query
            // cannot grab the wrong title (P4.A3).
            foreach (var attempt in attempts)
            {
                identity = attempt.Identity;
                method = attempt.Method;
                var parameters = attempt.Parameters;
                if (categories.Length > 0) parameters.Add(("cat", string.Join(',', categories)));
                parameters.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));
                for (var page = 0; ; page++)
                {
                    // The first query was reserved with the search; each further one needs a unit of the budget of its own.
                    if (pages > 0)
                    {
                        if (!IndexerQueryLedger.ReserveQuery(indexer.Id, cache.UtcNow, indexer.DailyQueryBudget))
                        {
                            budgetStopped = true;
                            break;
                        }

                        further = true;
                    }

                    var offset = items.Count;
                    var pageParameters = parameters.Append(("offset", offset.ToString(CultureInfo.InvariantCulture))).ToArray();
                    pages++;
                    var result = await torznab.SearchAsync(endpoint, pageParameters, timeout.Token, SendingAsync).ConfigureAwait(false);
                    items.AddRange(result.Items);
                    // The feed's own offset/total decides; without a total, a full page means there may be more.
                    var more = result.Items.Count > 0 && (result.Total is { } total
                        ? offset + result.Items.Count < total
                        : result.Items.Count >= limit);
                    if (!more) break;
                    if (page + 1 >= options.MaxPages)
                    {
                        truncated = true;
                        break;
                    }
                }

                if (items.Count > 0 || budgetStopped) break;
            }
        }
        catch (TorznabException error)
        {
            if (error.Code == "rate_limited") cache.BackOff(indexer.Id, error.RetryAfter ?? TimeSpan.FromMinutes(1));
            logger.LogInformation("Torznab search on {Indexer} failed: {Code}", indexer.Name, error.Code);
            // Rows from pages already read are kept, but the source is reported as failed, never as complete.
            return (Evaluate(indexer, items, target, profile, identity, method, allowedHosts),
                Outcome(error.Code, error.Message, items.Count, items.Count > 0,
                    error.RetryAfter is { } retryAfter ? (int)Math.Ceiling(retryAfter.TotalSeconds) : null), sent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Evaluate(indexer, items, target, profile, identity, method, allowedHosts),
                Outcome("timeout", "The indexer did not answer in time.", items.Count, items.Count > 0), sent);
        }
        finally
        {
            Unsent();
        }

        var candidates = Evaluate(indexer, items, target, profile, identity, method, allowedHosts);
        if (budgetStopped && candidates.Count == 0)
            return (candidates, Outcome("budget_exhausted", "The indexer's daily query budget ran out during this search."), sent);
        return (candidates, Outcome(candidates.Count == 0 ? "no_results" : "ok", null, candidates.Count, truncated || budgetStopped), sent);
    }

    private readonly record struct SearchAttempt(
        List<(string Name, string Value)> Parameters, SearchIdentity Identity, string Method);

    /// <summary>
    /// The queries to try in order: the provider id an indexer advertises first, then the title with its year,
    /// the bare title, and finally the title without the small words that some indexer engines cannot match.
    /// </summary>
    private static List<SearchAttempt> BuildQueries(TorznabCapabilities caps, ReleaseTarget target)
    {
        var attempts = new List<SearchAttempt>();
        var isMovie = target.MediaType == "movie";
        var searchCaps = isMovie ? caps.MovieSearch : caps.TvSearch;
        var searchType = isMovie ? "movie" : "tvsearch";
        if (isMovie)
        {
            if (caps.MovieSearch.Contains("imdbid") && target.ImdbId is { Length: > 2 } imdb)
                attempts.Add(new([("t", "movie"), ("imdbid", imdb.TrimStart('t', 'T'))], SearchIdentity.ProviderId, "imdbid"));
            if (caps.MovieSearch.Contains("tmdbid"))
                attempts.Add(new([("t", "movie"), ("tmdbid", target.TmdbId.ToString(CultureInfo.InvariantCulture))],
                    SearchIdentity.ProviderId, "tmdbid"));
        }
        else if (caps.TvSearch.Contains("tvdbid") && caps.TvSearch.Contains("season") && caps.TvSearch.Contains("ep") &&
                 target.TvdbId is { } tvdb)
        {
            attempts.Add(new([("t", "tvsearch"), ("tvdbid", tvdb.ToString(CultureInfo.InvariantCulture)),
                ("season", target.SeasonNumber!.Value.ToString(CultureInfo.InvariantCulture)),
                ("ep", target.EpisodeNumber!.Value.ToString(CultureInfo.InvariantCulture))], SearchIdentity.ProviderId, "tvdbid"));
        }

        foreach (var (text, method) in TextQueries(target))
        {
            if (searchCaps.Contains("q")) attempts.Add(new([("t", searchType), ("q", text)], SearchIdentity.QueryOnly, method));
            else if (caps.Search.Contains("q")) attempts.Add(new([("t", "search"), ("q", text)], SearchIdentity.QueryOnly, method));
        }

        return attempts;
    }

    private static readonly string[] SmallWords =
        ["a", "an", "and", "at", "for", "from", "in", "of", "on", "or", "the", "to", "with"];

    private static List<(string Text, string Method)> TextQueries(ReleaseTarget target)
    {
        var suffix = target.MediaType == "movie"
            ? null
            : $"S{target.SeasonNumber:00}E{target.EpisodeNumber:00}";
        var queries = new List<(string, string)>();
        void Add(string text, string method)
        {
            var value = suffix is null ? text : text + " " + suffix;
            if (!string.IsNullOrWhiteSpace(text) && queries.TrueForAll(existing =>
                    !string.Equals(existing.Item1, value, StringComparison.OrdinalIgnoreCase)))
                queries.Add((value, method));
        }

        if (target.MediaType == "movie" && target.Year is { } year) Add($"{target.Title} {year}", "q");
        Add(target.Title, "q_title");
        var words = target.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => !SmallWords.Contains(word, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (words.Length > 0) Add(string.Join(' ', words), "q_simplified");
        return queries;
    }

    private static List<ReleaseCandidate> Evaluate(AcquisitionIndexer indexer, IEnumerable<TorznabItem> items, ReleaseTarget target,
        EvaluationProfile profile, SearchIdentity identity, string method, IReadOnlySet<string> allowedHosts)
    {
        var candidates = new List<ReleaseCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!seen.Add(item.Guid)) continue;
            string? unsupported = null;
            var hash = TorrentMetadata.NormalizeHash(item.InfoHash);
            if (item.InfoHash is not null && hash is null) unsupported = "The indexer reports an infohash format that is not supported.";
            if (item.MagnetUrl is not null)
            {
                try
                {
                    var magnetHash = TorrentMetadata.FromMagnet(item.MagnetUrl).InfoHash;
                    if (hash is not null && hash != magnetHash) unsupported = "The indexer reports two different infohashes.";
                    hash ??= magnetHash;
                }
                catch (TorznabException error) when (item.DownloadUrl is null)
                {
                    unsupported = error.Message;
                }
                catch (TorznabException)
                {
                    // A torrent link remains; its metadata decides the identity at grab time.
                }
            }

            var hostAllowed = item.DownloadUrl is null || Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var link) &&
                link.Scheme is "http" or "https" && allowedHosts.Contains(link.IdnHost.ToLowerInvariant());
            var parsed = ReleaseParser.Parse(item.Title);
            var freeleech = item.DownloadVolumeFactor is { } factor ? factor == 0 : (bool?)null;
            var facts = new ReleaseFacts(parsed, identity, item.Size, item.Seeders, freeleech, item.ImdbId, item.TmdbId,
                item.TvdbId, item.Season, item.Episode, item.DownloadUrl is not null || item.MagnetUrl is not null, hostAllowed,
                unsupported);
            var evaluation = ReleaseEvaluator.Evaluate(target, profile, facts);
            var ratio = Max(indexer.MinimumSeedRatio, item.MinimumRatio);
            int? itemMinutes = item.MinimumSeedSeconds is { } seconds ? (int)Math.Min(int.MaxValue, (seconds + 59) / 60) : null;
            var minutes = indexer.MinimumSeedMinutes is null && itemMinutes is null
                ? (int?)null
                : Math.Max(indexer.MinimumSeedMinutes ?? 0, itemMinutes ?? 0);
            candidates.Add(new ReleaseCandidate(ReleaseId(indexer.Id, item.Guid), indexer.Id, indexer.Name, indexer.Priority,
                indexer.Revision, item.Guid, item.Title, parsed, item.Size, item.Seeders, item.Peers, item.PublishedAt, freeleech,
                hash, evaluation, ratio, minutes, method, allowedHosts, item.DownloadUrl, item.MagnetUrl));
        }

        return candidates;
    }

    private static double? Max(double? left, double? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    /// <summary>An opaque, stable identity for a row: indexer-namespaced, never the GUID itself.</summary>
    private static string ReleaseId(Guid indexerId, string guid) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(indexerId.ToString("N") + "\n" + guid)).AsSpan(0, 16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// This process's per-indexer query ledger (whole-review chunk 2a, P2 4): the daily budget and the next permitted request
/// time are taken atomically before a query is sent, so concurrent searches of one indexer cannot spend the same
/// allowance or overwrite each other's counters. Seeded from the stored budget rows, which it writes back.
/// </summary>
internal static class IndexerQueryLedger
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, Entry> Entries = [];

    /// <summary>
    /// The clock the minimum interval is paced on: monotonic, so a wall clock set back or forward never moves a slot, and read
    /// under the ledger's lock, so no caller paces from a reading taken before it waited for the lock (Pi review 4, findings 1
    /// and 2). Days and stored request times stay on the wall clock.
    /// </summary>
    private static TimeSpan PaceNow() => Stopwatch.GetElapsedTime(0);

    /// <summary>
    /// One search's first query waiting for its turn: when it asked, the slot it holds now (both on the pacing clock), and a
    /// signal raised when an abandoned reservation ahead of it moves that slot earlier (Codex delta review 2).
    /// </summary>
    public sealed class Reservation(Guid indexerId, TimeSpan requested, TimeSpan interval)
    {
        public Guid IndexerId { get; } = indexerId;
        public TimeSpan Requested { get; } = requested;
        public TimeSpan Interval { get; } = interval;
        public TimeSpan Slot { get; set; }
        public TaskCompletionSource Moved { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// One indexer's day: the queries counted (sent and reserved), the last time one was sent (on the wall clock, as stored,
    /// and on the pacing clock, which the next one is paced from), the first queries of searches still waiting out the minimum
    /// interval, in slot order, and the further queries reserved but not yet sent. An abandoned wait or an unsent further
    /// query gives its unit back.
    /// </summary>
    private sealed class Entry(DateTime day, int used, DateTime? lastSent, TimeSpan? pacedFrom)
    {
        public DateTime Day { get; set; } = day;
        public int Used { get; set; } = used;
        public DateTime? LastSent { get; set; } = lastSent;
        public TimeSpan? PacedFrom { get; set; } = pacedFrom;
        public List<Reservation> Pending { get; } = [];
        public int Reserved { get; set; }
    }

    /// <summary>
    /// A stored request time on the pacing clock: as long ago as it was on the wall clock. One later than now (stored before
    /// the clock was set back) counts as now, so the next query still waits a full interval, never until that time.
    /// </summary>
    private static TimeSpan? Paced(DateTime? stored, DateTime now) =>
        stored is { } last ? PaceNow() - (last < now ? now - last : TimeSpan.Zero) : null;

    /// <summary>
    /// Takes the stored row's day, count and last request time unless this process already holds later ones. A later stored
    /// day moves the held entry forward in place, so the reservations of searches still under way stay with it and are
    /// counted on that day (Pi review 2, finding 1). Only a stored request newer than the one held moves the pacing: the one
    /// this process stored itself never does, so a time stored before the clock was set back cannot keep pushing it to now
    /// (Pi review 4, finding 2).
    /// </summary>
    public static void Seed(Guid indexerId, IndexerBudgetState row, DateTime now)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(indexerId, out var held))
            {
                Entries[indexerId] = new Entry(row.Day, row.QueriesUsed, row.LastQueryAt, Paced(row.LastQueryAt, now));
                return;
            }

            if (row.LastQueryAt is { } stored && (held.LastSent is not { } known || stored > known))
            {
                held.LastSent = stored;
                held.PacedFrom = LatestPace(held.PacedFrom, Paced(stored, now));
            }

            if (held.Day < row.Day)
            {
                held.Day = row.Day;
                held.Used = row.QueriesUsed + held.Pending.Count + held.Reserved;
                return;
            }

            if (held.Day != row.Day) return;
            held.Used = Math.Max(held.Used, row.QueriesUsed + held.Pending.Count + held.Reserved);
        }
    }

    /// <summary>Whether any of the indexer's daily budget is left now.</summary>
    public static bool HasBudget(Guid indexerId, DateTime now, int budget)
    {
        lock (Gate) return Current(indexerId, now).Used < Math.Max(0, budget);
    }

    /// <summary>
    /// Reserves a search's first query: a unit of the budget and the next permitted time. The search waits with
    /// <see cref="WaitAsync"/>, then <see cref="TryCommit"/>s the query as it goes out or <see cref="Release"/>s it when
    /// abandoned.
    /// </summary>
    public static Reservation? ReserveSearch(Guid indexerId, DateTime now, int budget, TimeSpan interval)
    {
        lock (Gate)
        {
            var entry = Current(indexerId, now);
            if (entry.Used >= Math.Max(0, budget)) return null;
            var reservation = new Reservation(indexerId, PaceNow(), interval);
            reservation.Slot = Next(entry.Pending.Count == 0 ? entry.PacedFrom : LatestPace(entry.PacedFrom, entry.Pending[^1].Slot),
                reservation);
            entry.Used++;
            entry.Pending.Add(reservation);
            return reservation;
        }
    }

    /// <summary>Waits until the reservation's slot, following it when an abandoned reservation ahead moves it earlier.</summary>
    public static async Task WaitAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task moved;
            TimeSpan wait;
            lock (Gate)
            {
                wait = reservation.Slot - PaceNow();
                moved = reservation.Moved.Task;
            }

            if (wait <= TimeSpan.Zero) return;
            var delay = Task.Delay(wait, cancellationToken);
            // Whether the delay ends or the slot moves, the slot is read again: one moved later meanwhile is waited for too.
            if (await Task.WhenAny(delay, moved).ConfigureAwait(false) == delay) await delay.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a reserved first query as sent, on the day and at the moment it goes out, if a full interval has passed since
    /// the indexer's last request went out; otherwise moves its slot to that time and refuses, and the search waits again.
    /// The searches waiting behind it are paced from when it really went out (final Pi review, finding 1).
    /// </summary>
    public static bool TryCommit(Reservation reservation, DateTime now)
    {
        lock (Gate)
        {
            if (!Entries.ContainsKey(reservation.IndexerId)) return true;
            var entry = Current(reservation.IndexerId, now);
            if (!entry.Pending.Contains(reservation)) return true;
            var at = PaceNow();
            if (entry.PacedFrom is { } last && at < last + reservation.Interval)
            {
                reservation.Slot = last + reservation.Interval;
                return false;
            }

            entry.Pending.Remove(reservation);
            entry.LastSent = Latest(entry.LastSent, now);
            entry.PacedFrom = LatestPace(entry.PacedFrom, at);
            PaceFollowers(entry);
            return true;
        }
    }

    /// <summary>Moves every waiting slot that is now too close to the last request, or to the slot ahead of it, later.</summary>
    private static void PaceFollowers(Entry entry)
    {
        var previous = entry.PacedFrom;
        foreach (var follower in entry.Pending)
        {
            var slot = Next(previous, follower);
            if (slot > follower.Slot)
            {
                follower.Slot = slot;
                var moved = follower.Moved;
                follower.Moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                moved.TrySetResult();
            }

            previous = follower.Slot;
        }
    }

    /// <summary>
    /// Gives back a reserved first query that was never sent: its unit of the budget and its slot. Every reservation behind it
    /// moves up, still the minimum interval apart, and its waiting search is told.
    /// </summary>
    public static void Release(Reservation reservation)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(reservation.IndexerId, out var entry)) return;
            var index = entry.Pending.IndexOf(reservation);
            if (index < 0) return;
            entry.Pending.RemoveAt(index);
            entry.Used = Math.Max(0, entry.Used - 1);
            var previous = index == 0 ? entry.PacedFrom : entry.Pending[index - 1].Slot;
            for (var next = index; next < entry.Pending.Count; next++)
            {
                var follower = entry.Pending[next];
                var slot = Next(previous, follower);
                if (slot < follower.Slot)
                {
                    follower.Slot = slot;
                    var moved = follower.Moved;
                    follower.Moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    moved.TrySetResult();
                }

                previous = follower.Slot;
            }
        }
    }

    /// <summary>The earliest slot a reservation may have after <paramref name="previous"/>: never before it asked.</summary>
    private static TimeSpan Next(TimeSpan? previous, Reservation reservation) =>
        previous is { } last && last + reservation.Interval > reservation.Requested ? last + reservation.Interval : reservation.Requested;

    /// <summary>
    /// Reserves one further query of a search already under way, or refuses when the budget is used up. The search then
    /// <see cref="DispatchQuery"/>s it as it goes out, or <see cref="ReleaseQuery"/>s it when it is never sent.
    /// </summary>
    public static bool ReserveQuery(Guid indexerId, DateTime now, int budget)
    {
        lock (Gate)
        {
            var entry = Current(indexerId, now);
            if (entry.Used >= Math.Max(0, budget)) return false;
            entry.Used++;
            entry.Reserved++;
            return true;
        }
    }

    /// <summary>Records a reserved further query as sent, on the day it goes out.</summary>
    public static void DispatchQuery(Guid indexerId, DateTime now)
    {
        lock (Gate)
        {
            if (!Entries.ContainsKey(indexerId)) return;
            var entry = Current(indexerId, now);
            if (entry.Reserved == 0) return;
            entry.Reserved--;
            entry.LastSent = Latest(entry.LastSent, now);
            entry.PacedFrom = LatestPace(entry.PacedFrom, PaceNow());
            PaceFollowers(entry);
        }
    }

    /// <summary>Gives back a reserved further query that was never sent.</summary>
    public static void ReleaseQuery(Guid indexerId)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(indexerId, out var entry) || entry.Reserved == 0) return;
            entry.Reserved--;
            entry.Used = Math.Max(0, entry.Used - 1);
        }
    }

    /// <summary>The ledger's day, the queries sent that day and the last request time, or null when it holds nothing.</summary>
    public static (DateTime Day, int Sent, DateTime? LastSent)? Snapshot(Guid indexerId)
    {
        lock (Gate)
            return Entries.TryGetValue(indexerId, out var entry)
                ? (entry.Day, Math.Max(0, entry.Used - entry.Pending.Count - entry.Reserved), entry.LastSent)
                : null;
    }

    /// <summary>
    /// The indexer's entry for now's day. The day only moves forward, as the stored row's does: a clock set back keeps
    /// counting against the later day until it reaches it again, so nothing sent on it is forgotten (Pi review 2, finding 1).
    /// </summary>
    private static Entry Current(Guid indexerId, DateTime now)
    {
        if (!Entries.TryGetValue(indexerId, out var entry))
            Entries[indexerId] = entry = new Entry(now.Date, 0, null, null);
        if (entry.Day < now.Date)
        {
            entry.Day = now.Date;
            entry.Used = entry.Pending.Count + entry.Reserved;
        }

        return entry;
    }

    private static DateTime? Latest(DateTime? left, DateTime? right) =>
        left is { } a && right is { } b ? (a > b ? a : b) : left ?? right;

    private static TimeSpan? LatestPace(TimeSpan? left, TimeSpan? right) =>
        left is { } a && right is { } b ? (a > b ? a : b) : left ?? right;
}
