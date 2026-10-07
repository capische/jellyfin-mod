using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Ratings;

/// <summary>The fetcher's timing bounds; integration hosts shorten them.</summary>
/// <param name="MinInterval">The least time between two MDBList calls.</param>
/// <param name="FailureRetry">How long a title whose last attempt failed waits before it is due again.</param>
/// <param name="BreakerFailures">Consecutive transient failures that open the breaker.</param>
/// <param name="BreakerOpen">How long that breaker stays open.</param>
public sealed record RatingsOptions(TimeSpan MinInterval, TimeSpan FailureRetry, int BreakerFailures, TimeSpan BreakerOpen)
{
    /// <summary>The documented defaults (P9.R3): one call a second, a day before a failed title is retried, five failures, one hour.</summary>
    public static RatingsOptions Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), 5, TimeSpan.FromHours(1));
}

/// <summary>
/// One fetcher at a time in this process: the scheduled run, a manual refresh and Test take turns, so no title is claimed
/// twice and the interval between calls holds across all of them.
/// </summary>
public sealed class RatingsRunGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets or sets when the last MDBList call started.</summary>
    internal DateTime LastCallAt { get; set; } = DateTime.MinValue;

    /// <summary>Waits for the gate; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}

/// <summary>
/// Fetches ratings from MDBList inside the daily budget, the breaker and the refresh window (P9.R3). Every attempt is claimed
/// in SQLite — the claim and the budget increment commit before the call — so a run killed mid-call never fetches the
/// same title again that day: the claim it left is found at the next start and counted as a failed attempt.
/// </summary>
public sealed class RatingsRefreshRunner(
    ModDbContext database,
    MdbListClient client,
    AcquisitionSecretStore secrets,
    RatingsRunGate gate,
    RatingsOptions options,
    TimeProvider clock,
    ILogger<RatingsRefreshRunner> logger)
{
    private static readonly HashSet<string> Transient =
        [RatingsOutcomes.Failed, RatingsOutcomes.Unreachable, RatingsOutcomes.Timeout, RatingsOutcomes.Malformed];

    /// <summary>Why fetching may not start now, or null when it may (also the 409 codes of a manual refresh).</summary>
    public static string? Refusal(RatingsSettings settings, RatingsProviderState state, bool keyConfigured, DateTime now)
    {
        if (!settings.Enabled) return "ratings_disabled";
        if (!keyConfigured) return RatingsOutcomes.NotConfigured;
        if (state.Blocker is { } blocker) return blocker;
        if (state.BreakerUntil is { } until && until > now) return "breaker_open";
        if (BudgetUsed(state, now) >= settings.DailyBudget) return "budget_spent";
        return null;
    }

    /// <summary>The calls counted for today's UTC day.</summary>
    public static int BudgetUsed(RatingsProviderState state, DateTime now) => state.BudgetDay?.Date == now.Date ? state.BudgetUsed : 0;

    /// <summary>The scheduled run: titles never fetched (newest first), then titles due again, until the budget or a breaker stops it.</summary>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var now = Now;
        await RecoverClaimsAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RatingsStore.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        _state = await RatingsStore.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        var state = _state;
        state.LastRunStartedAt = now;
        state.LastRunFinishedAt = null;
        state.LastRunFetched = 0;
        state.LastRunFailed = 0;
        state.LastRunStopReason = null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var key = await secrets.GetAsync(settings.ApiKeyRef, cancellationToken).ConfigureAwait(false);
        var due = Refusal(settings, state, key is not null, now) is null ? await DueAsync(settings, now, cancellationToken).ConfigureAwait(false) : [];
        string? stop = Refusal(settings, state, key is not null, now);
        for (var index = 0; stop is null && index < due.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await FetchUnderGateAsync(due[index], key!, false, cancellationToken).ConfigureAwait(false);
            // A claim that failed reloads the state row; carry the run's counters onto it.
            if (!ReferenceEquals(state, _state))
            {
                (_state.LastRunStartedAt, _state.LastRunFetched, _state.LastRunFailed) = (state.LastRunStartedAt, state.LastRunFetched, state.LastRunFailed);
                state = _state;
            }

            if (outcome == RatingsOutcomes.Ok || outcome == RatingsOutcomes.NotFound) state.LastRunFetched++;
            else if (outcome is not null) state.LastRunFailed++;
            progress?.Report(100.0 * (index + 1) / due.Count);
            stop = Refusal(settings, state, true, Now);
        }

        state.LastRunStopReason = stop is "budget_spent" or "breaker_open" or "ratings_disabled" or RatingsOutcomes.NotConfigured or RatingsOutcomes.Unauthorized
            ? stop : null;
        state.LastRunFinishedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Ratings run: {Due} due, {Fetched} fetched, {Failed} failed, stopped by {Reason}", due.Count, state.LastRunFetched,
            state.LastRunFailed, state.LastRunStopReason ?? "nothing");
        progress?.Report(100);
    }

    /// <summary>A manual refresh of one title, whether or not it is due; refused like a run is.</summary>
    /// <returns>The outcome, or a refusal code when nothing was fetched.</returns>
    public async Task<string> FetchOneAsync(Guid entryId, CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await RecoverClaimsAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RatingsStore.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        _state = await RatingsStore.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        var key = await secrets.GetAsync(settings.ApiKeyRef, cancellationToken).ConfigureAwait(false);
        if (Refusal(settings, _state, key is not null, Now) is { } refusal) return refusal;
        var entry = await database.Entries.AsNoTracking().Where(row => row.Id == entryId)
            .Select(row => new { row.MediaType, row.TmdbId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null) return RatingsOutcomes.NotFound;
        // The same title in another library is the same MDBList answer: one call serves every entry of the identity.
        var siblings = await database.Entries.AsNoTracking().Where(row => row.MediaType == entry.MediaType && row.TmdbId == entry.TmdbId)
            .Select(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await FetchUnderGateAsync(new DueTitle(entry.MediaType, entry.TmdbId, siblings), key!, true, cancellationToken).ConfigureAwait(false)
            ?? RatingsOutcomes.NotFound;
    }

    /// <summary>The Test button: one call for a fixed well-known title, counted in the budget, not refused by it.</summary>
    public async Task<MdbListResult> TestAsync(CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RatingsStore.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var state = await RatingsStore.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        var key = await secrets.GetAsync(settings.ApiKeyRef, cancellationToken).ConfigureAwait(false);
        if (key is null) return new MdbListResult(RatingsOutcomes.NotConfigured, [], null, null);
        var revision = settings.Revision;
        Spend(state);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.FetchAsync(key, "movie", MdbListClient.TestTmdbId, cancellationToken).ConfigureAwait(false);
        Observe(state, result);
        if (result.Outcome == RatingsOutcomes.Ok)
        {
            state.Blocker = null;
            settings.VerifiedRevision = revision;
            settings.VerifiedAt = Now;
        }
        else
        {
            settings.VerifiedRevision = null;
        }

        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Ratings Test: {Outcome} (HTTP {Status})", result.Outcome, result.Status);
        return result;
    }

    private RatingsProviderState _state = new();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Claims, calls and applies one title identity for each of its entries (one call, however many libraries hold it). Returns
    /// null when every entry vanished before its claim.
    /// </summary>
    private async Task<string?> FetchUnderGateAsync(DueTitle title, string key, bool manual, CancellationToken cancellationToken)
    {
        // The claims and the budget in one commit, before the call.
        var ids = await ExistingAsync(title.EntryIds, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0) return null;
        var claimedAt = Now;
        var fetches = await database.RatingsFetches.Where(row => ids.Contains(row.EntryId)).ToDictionaryAsync(row => row.EntryId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in ids)
        {
            if (!fetches.TryGetValue(id, out var fetch))
            {
                fetch = new RatingsFetch { EntryId = id };
                database.RatingsFetches.Add(fetch);
            }

            (fetch.AttemptedAt, fetch.Outcome, fetch.RetryAfter, fetch.Error, fetch.Manual) = (claimedAt, RatingsOutcomes.Pending, null, null, manual);
        }

        Spend(_state);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // An entry was removed meanwhile: nothing was called and nothing was committed, the budget increment included.
            database.ChangeTracker.Clear();
            _state = await RatingsStore.GetStateAsync(database, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        await PaceAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.FetchAsync(key, title.MediaType, title.TmdbId, cancellationToken).ConfigureAwait(false);
        Observe(_state, result);
        await ApplyAsync(ids, result).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // An entry went while the call was out; its rows went with it. The call was made, so the state still counts it, and
            // the entries that remain get the answer.
            database.ChangeTracker.Clear();
            _state = await RatingsStore.GetStateAsync(database, CancellationToken.None).ConfigureAwait(false);
            Observe(_state, result);
            await ApplyAsync(await ExistingAsync(ids, CancellationToken.None).ConfigureAwait(false), result).ConfigureAwait(false);
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        logger.LogDebug("Ratings fetch for {MediaType} {TmdbId} ({Entries} entries): {Outcome}", title.MediaType, title.TmdbId, ids.Count, result.Outcome);
        return result.Outcome;
    }

    private async Task<List<Guid>> ExistingAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await database.Entries.AsNoTracking().Where(row => ids.Contains(row.Id)).Select(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Records the outcome on each entry's claim; an answer with ratings replaces that entry's MDBList values.</summary>
    private async Task ApplyAsync(IReadOnlyCollection<Guid> ids, MdbListResult result)
    {
        var fetches = await database.RatingsFetches.Where(row => ids.Contains(row.EntryId)).ToListAsync(CancellationToken.None).ConfigureAwait(false);
        var error = result.Outcome == RatingsOutcomes.Ok ? null : result.Status is { } status ? $"HTTP {status}" : result.Outcome;
        foreach (var fetch in fetches) (fetch.Outcome, fetch.RetryAfter, fetch.Error) = (result.Outcome, result.RetryAfter, error);
        if (result.Outcome != RatingsOutcomes.Ok) return;
        // What arrived replaces the title's MDBList values; a source missing from this answer is gone.
        var old = await database.TitleRatings.Where(row => ids.Contains(row.EntryId) && row.Provider == RatingSources.ProviderMdbList)
            .ToListAsync(CancellationToken.None).ConfigureAwait(false);
        database.TitleRatings.RemoveRange(old);
        var at = Now;
        foreach (var id in ids)
        {
            database.TitleRatings.AddRange(result.Ratings.Select(rating => new TitleRating
            {
                EntryId = id, Source = rating.Source, Provider = RatingSources.ProviderMdbList, Value = rating.Value, Scale = rating.Scale,
                Votes = rating.Votes, FetchedAt = at, Url = rating.Url
            }));
        }
    }

    /// <summary>What an answer means for the provider state: blocker, breaker and the failure streak.</summary>
    private void Observe(RatingsProviderState state, MdbListResult result)
    {
        var now = Now;
        switch (result.Outcome)
        {
            case RatingsOutcomes.Ok:
            case RatingsOutcomes.NotFound:
                state.ConsecutiveFailures = 0;
                if (state.BreakerReason == "failures") (state.BreakerUntil, state.BreakerReason) = (null, null);
                break;
            case RatingsOutcomes.Unauthorized:
                state.Blocker = RatingsOutcomes.Unauthorized;
                break;
            case RatingsOutcomes.RateLimited:
                // Retry-After is honoured, and the breaker stays open for the rest of the UTC day at least (PHASE9).
                var tomorrow = now.Date.AddDays(1);
                state.BreakerUntil = result.RetryAfter is { } after && after > tomorrow ? after : tomorrow;
                state.BreakerReason = RatingsOutcomes.RateLimited;
                break;
            default:
                if (!Transient.Contains(result.Outcome)) break;
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures >= options.BreakerFailures)
                {
                    state.BreakerUntil = now + options.BreakerOpen;
                    state.BreakerReason = "failures";
                    state.ConsecutiveFailures = 0;
                }

                break;
        }
    }

    private void Spend(RatingsProviderState state)
    {
        var today = Now.Date;
        if (state.BudgetDay?.Date != today)
        {
            state.BudgetDay = today;
            state.BudgetUsed = 0;
        }

        state.BudgetUsed++;
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        var wait = gate.LastCallAt + options.MinInterval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        gate.LastCallAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A claim still pending while this process holds the gate was left by a process that stopped mid-call. It counts as a
    /// failed attempt, so the title waits <see cref="RatingsOptions.FailureRetry"/> instead of being fetched again at once.
    /// </summary>
    private async Task RecoverClaimsAsync(CancellationToken cancellationToken)
    {
        var claims = await database.RatingsFetches.Where(row => row.Outcome == RatingsOutcomes.Pending).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (claims.Count == 0) return;
        foreach (var claim in claims)
        {
            claim.Outcome = RatingsOutcomes.Interrupted;
            claim.Error = "interrupted";
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Ratings: {Count} fetch(es) were interrupted and count as failed attempts", claims.Count);
    }

    /// <summary>One title identity and every entry that holds it (the same title can sit in more than one library).</summary>
    private sealed record DueTitle(string MediaType, int TmdbId, IReadOnlyList<Guid> EntryIds);

    /// <summary>
    /// Titles with an entry never attempted (newest first); then titles whose oldest attempt passed its window, the oldest attempt
    /// first. A title is one identity: one call serves every entry that holds it.
    /// </summary>
    private async Task<IReadOnlyList<DueTitle>> DueAsync(RatingsSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        var entries = await database.Entries.AsNoTracking().Where(entry => entry.TmdbId > 0)
            .Select(entry => new { entry.Id, entry.MediaType, entry.TmdbId, entry.AddedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var fetches = (await database.RatingsFetches.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.EntryId);
        var refresh = now - TimeSpan.FromDays(Math.Max(1, settings.RefreshDays));
        var retry = now - options.FailureRetry;
        bool Due(RatingsFetch fetch) => fetch.Outcome != RatingsOutcomes.Pending &&
            fetch.AttemptedAt <= (fetch.Outcome is RatingsOutcomes.Ok or RatingsOutcomes.NotFound ? refresh : retry) &&
            (fetch.RetryAfter is not { } after || after <= now);
        var titles = entries.GroupBy(entry => (entry.MediaType, entry.TmdbId)).Select(group => new
        {
            Title = new DueTitle(group.Key.MediaType, group.Key.TmdbId, group.Select(entry => entry.Id).Order().ToArray()),
            Fresh = group.Any(entry => !fetches.ContainsKey(entry.Id)),
            Again = group.Any(entry => fetches.TryGetValue(entry.Id, out var fetch) && Due(fetch)),
            Newest = group.Max(entry => entry.AddedAt),
            Oldest = group.Where(entry => fetches.ContainsKey(entry.Id)).Select(entry => fetches[entry.Id].AttemptedAt).DefaultIfEmpty(DateTime.MaxValue).Min()
        }).ToList();
        var fresh = titles.Where(title => title.Fresh).OrderByDescending(title => title.Newest).ThenBy(title => title.Title.TmdbId);
        var again = titles.Where(title => !title.Fresh && title.Again).OrderBy(title => title.Oldest).ThenBy(title => title.Title.TmdbId);
        return fresh.Concat(again).Select(title => title.Title).ToArray();
    }
}
