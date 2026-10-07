using JellyfinMod.Data;
using Microsoft.Data.Sqlite;
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
/// The MDBList credential and its settings, held still: the ratings settings save takes it, and the fetcher holds it from its
/// last look at the settings, the key and the provider state, through the call, to recording what the call meant
/// (review 2026-10-07 round 2, P2 2-4). So once a save has returned, no call starts with what it replaced, and an answer to
/// a replaced key is always recorded before the replacement, which then clears it. A save waits at most for one call
/// already out (bounded by <see cref="MdbListClient.Timeout"/>).
/// </summary>
public sealed class RatingsCredentialGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

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
/// <remarks>
/// Each call is made under <see cref="RatingsCredentialGate"/>, which the settings save also takes: the last check of the
/// settings, the key and the provider state happens there, after the pause between calls, and the claim and the budget are
/// committed only once that check has passed, so a refused call leaves nothing to undo. Turning ratings off, lowering the
/// budget or replacing the key therefore takes effect at the very next call, and an answer can only ever be about the key
/// that is still saved when it is recorded (review 2026-10-07 round 2, P2 2-4).
/// </remarks>
public sealed class RatingsRefreshRunner(
    ModDbContext database,
    MdbListClient client,
    AcquisitionSecretStore secrets,
    RatingsRunGate gate,
    RatingsCredentialGate credential,
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

    /// <summary>Why Test may not call now: only the budget and an open breaker stop it (it exists to try a refused key).</summary>
    public static string? TestRefusal(RatingsSettings settings, RatingsProviderState state, DateTime now)
    {
        if (state.BreakerUntil is { } until && until > now) return "breaker_open";
        if (BudgetUsed(state, now) >= settings.DailyBudget) return "budget_spent";
        return null;
    }

    /// <summary>The calls counted for today's UTC day.</summary>
    public static int BudgetUsed(RatingsProviderState state, DateTime now) => state.BudgetDay?.Date == now.Date ? state.BudgetUsed : 0;

    /// <summary>The scheduled run: titles never fetched, then titles due again, newest titles first, until the budget or a breaker stops it.</summary>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = Now;
        await RecoverClaimsAsync(cancellationToken).ConfigureAwait(false);
        var state = await RatingsStore.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        (state.LastRunStartedAt, state.LastRunFinishedAt, state.LastRunFetched, state.LastRunFailed, state.LastRunStopReason) =
            (startedAt, null, 0, 0, null);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        await PruneAsync(current.Settings, cancellationToken).ConfigureAwait(false);
        await AdoptSiblingsAsync(cancellationToken).ConfigureAwait(false);
        var stop = current.Refusal;
        var due = stop is null ? await DueAsync(current.Settings, Now, cancellationToken).ConfigureAwait(false) : [];
        int fetched = 0, failed = 0;
        for (var index = 0; stop is null && index < due.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, refusal) = await FetchAsync(due[index], false, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                stop = refusal;
                break;
            }

            if (outcome is RatingsOutcomes.Ok or RatingsOutcomes.NotFound) fetched++;
            else if (outcome is not null) failed++;
            progress?.Report(100.0 * (index + 1) / due.Count);
            stop = (await CurrentAsync(cancellationToken).ConfigureAwait(false)).Refusal;
        }

        database.ChangeTracker.Clear();
        state = await RatingsStore.GetStateAsync(database, CancellationToken.None).ConfigureAwait(false);
        (state.LastRunStartedAt, state.LastRunFetched, state.LastRunFailed, state.LastRunFinishedAt) = (startedAt, fetched, failed, Now);
        state.LastRunStopReason = stop is "budget_spent" or "breaker_open" or "ratings_disabled" or RatingsOutcomes.NotConfigured
            or RatingsOutcomes.Unauthorized or RatingsOutcomes.DatabaseBusy ? stop : null;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Ratings run: {Due} due, {Fetched} fetched, {Failed} failed, stopped by {Reason}", due.Count, fetched, failed,
            state.LastRunStopReason ?? "nothing");
        progress?.Report(100);
    }

    /// <summary>A manual refresh of one title, whether or not it is due; refused like a run is.</summary>
    /// <returns>The outcome, or a refusal code when nothing was fetched.</returns>
    public async Task<string> FetchOneAsync(Guid entryId, CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await RecoverClaimsAsync(cancellationToken).ConfigureAwait(false);
        var entry = await database.Entries.AsNoTracking().Where(row => row.Id == entryId)
            .Select(row => new { row.MediaType, row.TmdbId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null) return RatingsOutcomes.NotFound;
        var (outcome, refusal) = await FetchAsync(new DueTitle(entry.MediaType, entry.TmdbId), true, cancellationToken).ConfigureAwait(false);
        return refusal ?? outcome ?? RatingsOutcomes.NotFound;
    }

    /// <summary>
    /// The Test button: one call for a fixed well-known title, counted in the budget and refused by an open breaker or a spent
    /// budget like any other call (review 2026-10-07, P2 3). A refused key may be tried: that is what Test is for. It is checked,
    /// called and recorded under the credential gate, so the key it tested is still the saved one when it is marked verified.
    /// </summary>
    public async Task<MdbListResult> TestAsync(CancellationToken cancellationToken)
    {
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var held = await credential.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = BoundDatabase();
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.Key is null) return new MdbListResult(RatingsOutcomes.NotConfigured, [], null, null);
        if (TestRefusal(current.Settings, current.State, Now) is { } refusal) return new MdbListResult(refusal, [], null, null);
        Spend(current.State);
        if (!await SaveBoundedAsync(cancellationToken).ConfigureAwait(false)) return new MdbListResult(RatingsOutcomes.DatabaseBusy, [], null, null);
        gate.LastCallAt = DateTime.UtcNow;
        var result = await client.FetchAsync(current.Key, "movie", MdbListClient.TestTmdbId, cancellationToken).ConfigureAwait(false);
        Observe(current.State, result);
        if (result.Outcome == RatingsOutcomes.Ok)
        {
            current.State.Blocker = null;
            (current.Settings.VerifiedRevision, current.Settings.VerifiedAt) = (current.Settings.Revision, Now);
        }
        else
        {
            current.Settings.VerifiedRevision = null;
        }

        // The state and the verification in one bounded save: all of it or, when the database stays busy, none of it.
        if (!await SaveBoundedAsync(CancellationToken.None).ConfigureAwait(false))
        {
            logger.LogWarning("Ratings Test: the answer ({Outcome}) could not be recorded, the database stayed busy", result.Outcome);
            return new MdbListResult(RatingsOutcomes.DatabaseBusy, [], null, result.Status);
        }

        logger.LogInformation("Ratings Test: {Outcome} (HTTP {Status})", result.Outcome, result.Status);
        return result;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// How long the database work done under the credential gate may take in all, per step: the claim before the call, and the
    /// recording after it. With each SQLite command limited to <see cref="CommandSeconds"/>, a busy database holds the gate —
    /// and with it a waiting settings save — for at most about this long (review round 3, P2 1).
    /// </summary>
    public static TimeSpan DatabaseLimit { get; } = TimeSpan.FromSeconds(8);

    private const int CommandSeconds = 4;

    /// <summary>
    /// Limits every SQLite busy wait while the credential gate is held: EF's commands, and the connection's own statements —
    /// the BEGIN IMMEDIATE and COMMIT of a save's transaction run on the connection with its default timeout (30 s), which a
    /// cancellation token does not interrupt (review round 4, P2 1). Dispose to restore both values as they were.
    /// </summary>
    private IDisposable BoundDatabase()
    {
        var command = database.Database.GetCommandTimeout();
        var connection = database.Database.GetDbConnection() as SqliteConnection;
        var defaultTimeout = connection?.DefaultTimeout;
        database.Database.SetCommandTimeout(CommandSeconds);
        if (connection is not null) connection.DefaultTimeout = CommandSeconds;
        return new Restore(database, command, connection, defaultTimeout);
    }

    private sealed class Restore(ModDbContext database, int? command, SqliteConnection? connection, int? defaultTimeout) : IDisposable
    {
        public void Dispose()
        {
            database.Database.SetCommandTimeout(command);
            if (connection is not null && defaultTimeout is { } previous) connection.DefaultTimeout = previous;
        }
    }

    /// <summary>Saves within <see cref="DatabaseLimit"/>; false when the database stayed busy (nothing was written).</summary>
    private async Task<bool> SaveBoundedAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(DatabaseLimit);
        try
        {
            await database.SaveChangesAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && (failure is OperationCanceledException || SqliteBusy.IsBusy(failure)))
        {
            database.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>The settings, key and provider state as they are now, read afresh, and whether a call may be made.</summary>
    private sealed record Current(RatingsSettings Settings, RatingsProviderState State, string? Key, string? Refusal);

    private async Task<Current> CurrentAsync(CancellationToken cancellationToken)
    {
        // Nothing is pending here: every step saves before it reads again, so the tracker can start empty and read the database.
        database.ChangeTracker.Clear();
        var settings = await RatingsStore.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var state = await RatingsStore.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        var key = await secrets.GetAsync(settings.ApiKeyRef, cancellationToken).ConfigureAwait(false);
        return new Current(settings, state, key, Refusal(settings, state, key is not null, Now));
    }

    /// <summary>
    /// Checks, claims, calls and records one title identity, for every entry that holds it (one call, however many libraries).
    /// Returns the outcome, or the refusal that stopped it before any call, or neither when no entry holds the title any more.
    /// </summary>
    private async Task<(string? Outcome, string? Refusal)> FetchAsync(DueTitle title, bool manual, CancellationToken cancellationToken)
    {
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        using var held = await credential.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = BoundDatabase();
        // The last look, after the pause and under the gate the settings save takes: what it sees is what the call uses.
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.Refusal is { } refusal) return (null, refusal);
        if ((await HoldersAsync(title, cancellationToken).ConfigureAwait(false)).Count == 0) return (null, null);
        // The claim and the budget in one commit, before the call.
        var attempt = await database.RatingsFetches.SingleOrDefaultAsync(row => row.MediaType == title.MediaType && row.TmdbId == title.TmdbId,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            attempt = new RatingsFetch { MediaType = title.MediaType, TmdbId = title.TmdbId };
            database.RatingsFetches.Add(attempt);
        }

        (attempt.AttemptedAt, attempt.Outcome, attempt.Error, attempt.Manual) = (Now, RatingsOutcomes.Pending, null, manual);
        Spend(current.State);
        if (!await SaveBoundedAsync(cancellationToken).ConfigureAwait(false)) return (null, RatingsOutcomes.DatabaseBusy);

        gate.LastCallAt = DateTime.UtcNow;
        var result = await client.FetchAsync(current.Key!, title.MediaType, title.TmdbId, cancellationToken).ConfigureAwait(false);
        var error = result.Outcome == RatingsOutcomes.Ok ? null : result.Status is { } status ? $"HTTP {status}" : result.Outcome;
        // The recording is bounded too, so the gate a settings save waits on is held for the call's limit plus at most
        // DatabaseLimit (review round 3, P2 1). It is one save: all of it or nothing. When the database stays busy past the
        // bound, nothing is recorded, the claim stays pending (the next run counts it as interrupted) and this run stops.
        using var limit = new CancellationTokenSource(DatabaseLimit);
        try
        {
            for (var tries = 0; ; tries++)
            {
                // Recorded from what is in the database now (the claim and the budget are already there): the state, the
                // title's attempt and, for an answer with ratings, the values of every entry that still holds the title.
                database.ChangeTracker.Clear();
                var state = await RatingsStore.GetStateAsync(database, limit.Token).ConfigureAwait(false);
                Observe(state, result);
                var recorded = await database.RatingsFetches.SingleAsync(row => row.MediaType == title.MediaType && row.TmdbId == title.TmdbId,
                    limit.Token).ConfigureAwait(false);
                (recorded.Outcome, recorded.Error) = (result.Outcome, error);
                if (result.Outcome == RatingsOutcomes.Ok) await StoreAsync(title, result.Ratings, limit.Token).ConfigureAwait(false);
                try
                {
                    await database.SaveChangesAsync(limit.Token).ConfigureAwait(false);
                    break;
                }
                catch (DbUpdateException update) when (tries < 2 && !SqliteBusy.IsBusy(update))
                {
                    // An entry went while its values were being written; the next pass writes them for the entries that remain.
                }
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException || SqliteBusy.IsBusy(failure))
        {
            database.ChangeTracker.Clear();
            logger.LogWarning("Ratings fetch for {MediaType} {TmdbId}: the answer ({Outcome}) could not be recorded, the database stayed busy",
                title.MediaType, title.TmdbId, result.Outcome);
            return (RatingsOutcomes.DatabaseBusy, RatingsOutcomes.DatabaseBusy);
        }

        logger.LogDebug("Ratings fetch for {MediaType} {TmdbId}: {Outcome}", title.MediaType, title.TmdbId, result.Outcome);
        return (result.Outcome, null);
    }

    /// <summary>The entries that hold a title identity now.</summary>
    private async Task<List<Guid>> HoldersAsync(DueTitle title, CancellationToken cancellationToken) =>
        await database.Entries.AsNoTracking().Where(row => row.MediaType == title.MediaType && row.TmdbId == title.TmdbId)
            .Select(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>What arrived replaces the title's MDBList values on every entry that holds it; a source missing from this answer is gone.</summary>
    private async Task StoreAsync(DueTitle title, IReadOnlyList<FetchedRating> ratings, CancellationToken cancellationToken)
    {
        var ids = await HoldersAsync(title, cancellationToken).ConfigureAwait(false);
        var old = await database.TitleRatings.Where(row => ids.Contains(row.EntryId) && row.Provider == RatingSources.ProviderMdbList)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        database.TitleRatings.RemoveRange(old);
        var at = Now;
        foreach (var id in ids)
        {
            database.TitleRatings.AddRange(ratings.Select(rating => new TitleRating
            {
                EntryId = id, Source = rating.Source, Provider = RatingSources.ProviderMdbList, Value = rating.Value, Scale = rating.Scale,
                Votes = rating.Votes, FetchedAt = at
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
                // Retry-After and X-RateLimit-Reset are honoured, and the breaker stays open for the rest of the UTC day at
                // least (PHASE9). The quota is the key's, so the breaker is the only place this deadline lives: replacing the
                // key closes it, and no title keeps a deadline of its own (review 2026-10-07 round 2, P2 4).
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

    /// <summary>Waits out the least interval since the last call. The call itself records when it started.</summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        var wait = gate.LastCallAt + options.MinInterval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Forgets the attempts of titles no entry holds any more, once nothing they say still matters: past the refresh window and
    /// the failure wait, so a title removed and added again inside either is still not fetched twice.
    /// </summary>
    private async Task PruneAsync(RatingsSettings settings, CancellationToken cancellationToken)
    {
        var keep = TimeSpan.FromDays(Math.Max(1, settings.RefreshDays));
        if (options.FailureRetry > keep) keep = options.FailureRetry;
        var before = Now - keep;
        var removed = await database.RatingsFetches
            .Where(row => row.AttemptedAt < before && row.Outcome != RatingsOutcomes.Pending &&
                !database.Entries.Any(entry => entry.MediaType == row.MediaType && entry.TmdbId == row.TmdbId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0) logger.LogDebug("Ratings: forgot {Count} attempt(s) of titles no library holds", removed);
    }

    /// <summary>
    /// An entry that joins a title already fetched (the same title added to another library) takes the title's stored MDBList
    /// values without a call. The title's attempt is its identity's, so it is shared already (review 2026-10-07, P2 4).
    /// </summary>
    private async Task AdoptSiblingsAsync(CancellationToken cancellationToken)
    {
        // One read of every entry and every stored MDBList value, so the decisions below rest on one snapshot: an entry or its
        // values removed meanwhile cannot leave a title half-read (review round 3, P2 2).
        var entries = await database.Entries.AsNoTracking().Where(entry => entry.TmdbId > 0)
            .Select(entry => new { entry.Id, entry.MediaType, entry.TmdbId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stored = (await database.TitleRatings.AsNoTracking().Where(row => row.Provider == RatingSources.ProviderMdbList)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(row => row.EntryId);
        var adopted = 0;
        foreach (var group in entries.GroupBy(entry => (entry.MediaType, entry.TmdbId)))
        {
            var ids = group.Select(entry => entry.Id).ToArray();
            var missing = ids.Where(id => !stored.Contains(id)).ToArray();
            var source = ids.Where(stored.Contains).Select(id => stored[id].ToArray()).OrderByDescending(rows => rows.Max(row => row.FetchedAt)).FirstOrDefault();
            if (missing.Length == 0 || source is null) continue;
            foreach (var id in missing)
            {
                database.TitleRatings.AddRange(source.Select(row => new TitleRating
                {
                    EntryId = id, Source = row.Source, Provider = row.Provider, Value = row.Value, Scale = row.Scale, Votes = row.Votes,
                    FetchedAt = row.FetchedAt
                }));
            }

            // Each title on its own: an entry removed meanwhile fails only its own title, which the next run adopts again.
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                adopted += missing.Length;
            }
            catch (DbUpdateException failure)
            {
                database.ChangeTracker.Clear();
                logger.LogInformation("Ratings: {MediaType} {TmdbId} could not take its stored values now ({ErrorType}); the next run tries again",
                    group.Key.MediaType, group.Key.TmdbId, failure.GetType().Name);
            }
        }

        if (adopted > 0) logger.LogInformation("Ratings: {Count} entr(y/ies) took the ratings their title already has", adopted);
    }

    /// <summary>One title identity (the same title can sit in more than one library; one call serves them all).</summary>
    private sealed record DueTitle(string MediaType, int TmdbId);

    /// <summary>
    /// Titles never attempted, then titles whose latest attempt passed its window; each group newest title first (user decision
    /// 5, review 2026-10-07 P2 7). An answer about the key rather than the title — refused, or the quota spent — leaves the title
    /// due as soon as fetching may resume; the blocker and the breaker hold everything until then.
    /// </summary>
    private async Task<IReadOnlyList<DueTitle>> DueAsync(RatingsSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        var entries = await database.Entries.AsNoTracking().Where(entry => entry.TmdbId > 0)
            .Select(entry => new { entry.MediaType, entry.TmdbId, entry.AddedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var attempts = (await database.RatingsFetches.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.MediaType, row.TmdbId));
        var refresh = now - TimeSpan.FromDays(Math.Max(1, settings.RefreshDays));
        var retry = now - options.FailureRetry;
        bool Due(RatingsFetch attempt) => attempt.Outcome switch
        {
            RatingsOutcomes.Pending => false,
            RatingsOutcomes.Ok or RatingsOutcomes.NotFound => attempt.AttemptedAt <= refresh,
            RatingsOutcomes.Unauthorized or RatingsOutcomes.RateLimited => true,
            _ => attempt.AttemptedAt <= retry
        };
        var titles = entries.GroupBy(entry => (entry.MediaType, entry.TmdbId)).Select(group =>
        {
            var attempt = attempts.GetValueOrDefault(group.Key);
            return new
            {
                Title = new DueTitle(group.Key.MediaType, group.Key.TmdbId),
                Fresh = attempt is null,
                Again = attempt is not null && Due(attempt),
                Newest = group.Max(entry => entry.AddedAt)
            };
        }).ToList();
        var fresh = titles.Where(title => title.Fresh).OrderByDescending(title => title.Newest).ThenBy(title => title.Title.TmdbId);
        var again = titles.Where(title => title.Again).OrderByDescending(title => title.Newest).ThenBy(title => title.Title.TmdbId);
        return fresh.Concat(again).Select(title => title.Title).ToArray();
    }
}
