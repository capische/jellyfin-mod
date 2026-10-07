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
            .Select(row => new DueEntry(row.Id, row.MediaType, row.TmdbId)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null) return RatingsOutcomes.NotFound;
        return await FetchUnderGateAsync(entry, key!, true, cancellationToken).ConfigureAwait(false) ?? RatingsOutcomes.NotFound;
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

    /// <summary>Claims, calls and applies one title. Returns null when the title vanished before its claim.</summary>
    private async Task<string?> FetchUnderGateAsync(DueEntry entry, string key, bool manual, CancellationToken cancellationToken)
    {
        var state = _state;
        // The claim and the budget in one commit, before the call.
        var fetch = await database.RatingsFetches.SingleOrDefaultAsync(row => row.EntryId == entry.Id, cancellationToken).ConfigureAwait(false);
        if (fetch is null)
        {
            fetch = new RatingsFetch { EntryId = entry.Id };
            database.RatingsFetches.Add(fetch);
        }

        fetch.AttemptedAt = Now;
        fetch.Outcome = RatingsOutcomes.Pending;
        fetch.RetryAfter = null;
        fetch.Error = null;
        fetch.Manual = manual;
        Spend(state);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The entry was removed meanwhile: nothing was called, and the budget increment is given back.
            database.ChangeTracker.Clear();
            _state = await RatingsStore.GetStateAsync(database, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        await PaceAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.FetchAsync(key, entry.MediaType, entry.TmdbId, cancellationToken).ConfigureAwait(false);
        fetch.Outcome = result.Outcome;
        fetch.RetryAfter = result.RetryAfter;
        fetch.Error = result.Outcome == RatingsOutcomes.Ok ? null : result.Status is { } status ? $"HTTP {status}" : result.Outcome;
        if (result.Outcome == RatingsOutcomes.Ok)
        {
            // What arrived replaces this title's MDBList values; a source missing from this answer is gone.
            var old = await database.TitleRatings.Where(row => row.EntryId == entry.Id && row.Provider == RatingSources.ProviderMdbList)
                .ToListAsync(CancellationToken.None).ConfigureAwait(false);
            database.TitleRatings.RemoveRange(old);
            var at = Now;
            database.TitleRatings.AddRange(result.Ratings.Select(rating => new TitleRating
            {
                EntryId = entry.Id, Source = rating.Source, Provider = RatingSources.ProviderMdbList, Value = rating.Value, Scale = rating.Scale,
                Votes = rating.Votes, FetchedAt = at, Url = rating.Url
            }));
        }

        Observe(state, result);
        try
        {
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The entry went while its call was out; its rows went with it. The call was made, so the state still counts it.
            database.ChangeTracker.Clear();
            _state = await RatingsStore.GetStateAsync(database, CancellationToken.None).ConfigureAwait(false);
            Observe(_state, result);
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        logger.LogDebug("Ratings fetch for entry {EntryId}: {Outcome}", entry.Id, result.Outcome);
        return result.Outcome;
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

    private sealed record DueEntry(Guid Id, string MediaType, int TmdbId);

    /// <summary>Never attempted, newest first; then attempted titles whose window passed, the oldest attempt first.</summary>
    private async Task<IReadOnlyList<DueEntry>> DueAsync(RatingsSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        var entries = await database.Entries.AsNoTracking().Where(entry => entry.TmdbId > 0)
            .Select(entry => new { entry.Id, entry.MediaType, entry.TmdbId, entry.AddedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var fetches = (await database.RatingsFetches.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.EntryId);
        var refresh = now - TimeSpan.FromDays(Math.Max(1, settings.RefreshDays));
        var retry = now - options.FailureRetry;
        var fresh = entries.Where(entry => !fetches.ContainsKey(entry.Id)).OrderByDescending(entry => entry.AddedAt).ThenBy(entry => entry.Id);
        var again = entries.Where(entry => fetches.TryGetValue(entry.Id, out var fetch) && fetch.Outcome != RatingsOutcomes.Pending &&
                fetch.AttemptedAt <= (fetch.Outcome is RatingsOutcomes.Ok or RatingsOutcomes.NotFound ? refresh : retry) &&
                (fetch.RetryAfter is not { } after || after <= now))
            .OrderBy(entry => fetches[entry.Id].AttemptedAt).ThenBy(entry => entry.Id);
        return fresh.Concat(again).Select(entry => new DueEntry(entry.Id, entry.MediaType, entry.TmdbId)).ToArray();
    }
}
