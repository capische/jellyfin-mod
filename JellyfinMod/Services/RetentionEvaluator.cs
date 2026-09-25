using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Computes access-aware completion deadlines without deleting media.</summary>
public sealed class RetentionEvaluator(
    ModDbContext database,
    IUserManager users,
    LibraryAccess access,
    TimeProvider clock,
    RetentionCompletionService? completion = null,
    ILibraryManager? library = null,
    IUserDataManager? userData = null)
{
    /// <summary>Re-evaluates every episode target that belongs to one native series (P3.T11).</summary>
    public async Task EvaluateSeriesAsync(Guid seriesItemId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var targets = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.SeriesItemId == seriesItemId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var target in targets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-evaluates every bound movie and episode, one full evaluation at a time in this process. A caller that finds a
    /// full evaluation already running waits for it and then runs its own only when none started after it asked: an
    /// evaluation that started later read everything this caller would read (every Keep, window, policy and user-data
    /// change saved before the request), so repeating it adds nothing but write load. Switching retention on used to run
    /// the listener's full evaluation and the administrator's preview side by side, each writing every target, and on a
    /// slow disk one of them waited past SQLite's busy timeout (`database is locked`, 18096, 2026-09-24).
    /// </summary>
    public async Task EvaluateAllAsync(CancellationToken cancellationToken)
    {
        var asked = Interlocked.Read(ref fullEvaluationsStarted);
        await FullEvaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref fullEvaluationsCompletedFrom) > asked) return;
            var started = Interlocked.Increment(ref fullEvaluationsStarted);
            await EvaluateAllCoreAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref fullEvaluationsCompletedFrom, started);
        }
        finally
        {
            FullEvaluationGate.Release();
        }
    }

    /// <summary>One full evaluation at a time (see <see cref="EvaluateAllAsync"/>).</summary>
    private static readonly SemaphoreSlim FullEvaluationGate = new(1, 1);

    /// <summary>How many full evaluations have started, and the start number of the last one that completed.</summary>
    private static long fullEvaluationsStarted;
    private static long fullEvaluationsCompletedFrom;

    /// <summary>
    /// Re-evaluates every target of the given entries: each movie, and each bound episode of each series. The executor
    /// uses it under its locks for the titles an action touches, instead of re-evaluating every title three times per
    /// action (RET3-R6).
    /// </summary>
    public async Task EvaluateEntriesAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0) return;
        database.ChangeTracker.Clear();
        var ids = entryIds.Distinct().ToArray();
        var movieTargets = await database.Entries.AsNoTracking()
            .Where(entry => ids.Contains(entry.Id) && entry.MediaType == "movie" &&
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var episodeTargets = await database.EpisodeBindings.AsNoTracking()
            .Join(database.Episodes.AsNoTracking().Where(episode => ids.Contains(episode.EntryId)),
                binding => binding.EpisodeId, episode => episode.Id, (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in movieTargets)
            await EvaluateAsync(new Target(entry, null), cancellationToken).ConfigureAwait(false);
        foreach (var target in episodeTargets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-evaluates the given targets (a movie's entry id, an episode's id) and, for an episode whose file holds several
    /// episodes, every episode that file covers, whose evaluations the multi-episode rule reads. The executor uses it under
    /// its locks for what an action touches, so an action's cost does not grow with the length of its series (RET4-R5).
    /// </summary>
    public async Task EvaluateTargetsAsync(IReadOnlyCollection<Guid> targetIds, CancellationToken cancellationToken)
    {
        if (targetIds.Count == 0) return;
        database.ChangeTracker.Clear();
        var ids = targetIds.ToHashSet();
        var bindings = await database.EpisodeBindings.AsNoTracking().Where(binding => ids.Contains(binding.EpisodeId))
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (binding, episode) => new { binding.JellyfinItemId, episode.EntryId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var binding in bindings)
        {
            if (Covered(binding.JellyfinItemId) is not var (season, first, last)) continue;
            var covered = await database.Episodes.AsNoTracking()
                .Where(episode => episode.EntryId == binding.EntryId && episode.SeasonNumber == season &&
                    episode.EpisodeNumber >= first && episode.EpisodeNumber <= last)
                .Select(episode => episode.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            ids.UnionWith(covered);
        }

        var list = ids.ToArray();
        var movieTargets = await database.Entries.AsNoTracking()
            .Where(entry => list.Contains(entry.Id) && entry.MediaType == "movie" &&
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var episodeTargets = await database.EpisodeBindings.AsNoTracking()
            .Join(database.Episodes.AsNoTracking().Where(episode => list.Contains(episode.Id)),
                binding => binding.EpisodeId, episode => episode.Id, (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in movieTargets)
            await EvaluateAsync(new Target(entry, null), cancellationToken).ConfigureAwait(false);
        foreach (var target in episodeTargets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The season and episode range a multi-episode file holds; null for any other item or one not readable.</summary>
    private (int Season, int First, int Last)? Covered(Guid itemId)
    {
        try
        {
            return library?.GetItemById(itemId) is MediaBrowser.Controller.Entities.TV.Episode
            {
                IndexNumber: { } first, IndexNumberEnd: { } last, ParentIndexNumber: { } season
            } && last > first ? (season, first, last) : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task EvaluateAllCoreAsync(CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var movieTargets = await database.EntryBindings.AsNoTracking()
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id, (_, entry) => entry)
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var episodeTargets = await database.EpisodeBindings.AsNoTracking()
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (_, episode) => episode)
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in movieTargets)
            await EvaluateAsync(new Target(entry, null), cancellationToken).ConfigureAwait(false);
        foreach (var target in episodeTargets)
            await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates one tracked episode, for example after its own Keep changed (P10.E2).</summary>
    public async Task EvaluateEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var target = await database.Episodes.AsNoTracking().Where(episode => episode.Id == episodeId &&
                database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id))
            .Join(database.Entries.AsNoTracking(), episode => episode.EntryId, entry => entry.Id,
                (episode, entry) => new Target(entry, episode.Id, episode.RetentionPolicy, episode.ReclaimAfterDays))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is not null) await EvaluateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates one bound movie, for example after a Keep on one of its files changed (RET2-R2).</summary>
    public async Task EvaluateMovieAsync(Guid entryId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var movie = await database.Entries.AsNoTracking().Where(entry => entry.Id == entryId && entry.MediaType == "movie" &&
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (movie is not null) await EvaluateAsync(new Target(movie, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-evaluates the stable target represented by a native movie or episode.</summary>
    public async Task EvaluateNativeItemAsync(Guid jellyfinItemId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var episode = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, item => item.Id,
                (_, item) => item)
            .Join(database.Entries.AsNoTracking(), item => item.EntryId, entry => entry.Id,
                (item, entry) => new Target(entry, item.Id, item.RetentionPolicy, item.ReclaimAfterDays))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (episode is not null)
        {
            await EvaluateAsync(episode, cancellationToken).ConfigureAwait(false);
            return;
        }

        var movie = await database.EntryBindings.AsNoTracking()
            .Where(binding => binding.JellyfinItemId == jellyfinItemId)
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id, (_, entry) => entry)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (movie is not null) await EvaluateAsync(new Target(movie, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates one target. Listener, repair and Keep can evaluate the same new target at once; the loser of
    /// the insert re-reads the winner's row instead of failing (P3.T11).
    /// </summary>
    private async Task EvaluateAsync(Target target, CancellationToken cancellationToken)
    {
        // One evaluation of a target at a time (RET2-R4): a batch reads a target's Keep and window, then saves its result;
        // an administrator's change that lands in between is followed by its own evaluation, which waits here and so
        // always writes last. Without this a batch could save "scheduled" for an episode kept a moment earlier.
        var gate = TargetGates.GetOrAdd(target.EpisodeId ?? target.Entry.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A busy database (another writer past the busy timeout) is retried rather than failing a preview or a run.
            await SqliteBusy.RetryAsync(async () =>
            {
                database.ChangeTracker.Clear();
                await EvaluateCoreAsync(target, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } ||
                                              error is DbUpdateConcurrencyException)
        {
            // Another writer created the row first (19), or reset its baseline or grace while this evaluation read it
            // (a concurrency token, RET3-R4): evaluate again from what is stored now.
            database.ChangeTracker.Clear();
            await SqliteBusy.RetryAsync(() => EvaluateCoreAsync(target, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Holds one target's evaluation gate, for a writer outside the evaluator that changes the target's evaluation row
    /// (an administrator's grace restart, RET3-R4). Take it before any database transaction, as the evaluator does, and
    /// release it before evaluating the target.
    /// </summary>
    internal static async Task<IDisposable> HoldTargetAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var gate = TargetGates.GetOrAdd(targetId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateRelease(gate);
    }

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }

    /// <summary>Per-target evaluation gates, shared by every scope of the plugin process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> TargetGates = new();

    private async Task EvaluateCoreAsync(Target target, CancellationToken cancellationToken)
    {
        // Keep and the window are read again for this target: a batch loads its targets once and can take minutes, so a
        // Keep or un-Keep made meanwhile would otherwise be evaluated against the settings from before it (found live).
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == target.Entry.Id, cancellationToken).ConfigureAwait(false);
        if (entry is null) return;
        target = target with { Entry = entry };
        if (target.EpisodeId is { } currentEpisodeId)
        {
            var settings = await database.Episodes.AsNoTracking().Where(episode => episode.Id == currentEpisodeId)
                .Select(episode => new { episode.RetentionPolicy, episode.ReclaimAfterDays })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (settings is null) return;
            target = target with { EpisodePolicy = settings.RetentionPolicy, EpisodeDays = settings.ReclaimAfterDays };
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var targetId = target.EpisodeId ?? target.Entry.Id;
        var result = await database.RetentionEvaluations.SingleOrDefaultAsync(
            candidate => candidate.TargetId == targetId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // A target's grace never starts before it was first evaluated, which is at or after its
            // binding. Historical native play dates must not make newly bound media due at once.
            // A movie or an episode additionally needs a completion after that first evaluation and after retention was
            // first enabled: only a new watch counts (PHASE10 Q1 for episodes; decision 12, 2026-09-24, for movies).
            result = new RetentionEvaluation
            {
                EntryId = target.Entry.Id,
                EpisodeId = target.EpisodeId,
                TargetId = targetId,
                BaselineAt = now,
                RequiresFreshCompletion = true
            };
            database.RetentionEvaluations.Add(result);
        }

        var priorPolicyVersion = result.PolicyVersion;
        var priorState = result.State;
        var hadPriorEvaluation = result.EvaluatedAt != default;
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        result.EvaluatedAt = now;
        result.PolicyVersion = policy?.Version ?? 0;
        if (policy is null || !policy.Enabled)
        {
            Set(result, RetentionEvaluationStates.Disabled, RetentionEvaluationReasons.RetentionDisabled);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Keep on the series or on the episode itself protects an episode (P10.E2).
        if (target.IsKept)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Kept);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        User[] accessibleUsers;
        try
        {
            accessibleUsers = users.GetUsers().Where(IsActive)
                .Where(user => access.CanUseLibrary(user, target.Entry.MediaType, target.Entry.TargetLibraryId))
                .OrderBy(user => user.Id)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.AccessUnavailable);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fingerprint = Fingerprint(accessibleUsers.Select(user => user.Id));
        var accessChanged = result.AccessFingerprint.Length > 0 && result.AccessFingerprint != fingerprint;
        result.AccessFingerprint = fingerprint;
        if (accessibleUsers.Length == 0)
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.NoAccessibleUsers);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.WatchedUserMode == WatchedUserMode.SelectedUser)
        {
            if (!policy.SelectedUserId.HasValue)
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserMissing);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (accessibleUsers.All(user => user.Id != policy.SelectedUserId.Value))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SelectedUserInaccessible);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var userIds = accessibleUsers.Select(user => user.Id).ToArray();
        var boundItemIds = target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.Entry.Id)
                .Select(binding => binding.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Reclaiming the file a movie folder resolved from leaves its other media sources without a native item
        // until Jellyfin re-resolves the folder and reconciliation observes it (P6.M6). Unreadable is not the
        // same as unwatched: reading evidence now would report none and restart the title's window, so the
        // previous evaluation stands until the bindings are current again.
        if (library is not null && hadPriorEvaluation && boundItemIds.Length > 0 &&
            boundItemIds.All(itemId => ResolvesNatively(itemId) == false))
        {
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        var observations = await LoadCurrentObservationsAsync(targetId, userIds, boundItemIds, cancellationToken)
            .ConfigureAwait(false);
        var missingUserIds = userIds.Where(userId => !observations.ContainsKey(userId)).ToArray();
        if (missingUserIds.Length > 0 && boundItemIds.Length > 0 && completion is not null)
        {
            // A user who never touched the item has no event to record; read their live state now
            // instead of blocking Any and Selected mode until a manual repair (prior-H1).
            try
            {
                foreach (var userId in missingUserIds)
                    await completion.RefreshAsync(userId, boundItemIds[0], "Evaluate", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not DbUpdateException &&
                                          !SqliteBusy.IsBusy(error))
            {
                // Jellyfin's item could not be read just now (review P3-1): this evaluation stays as it was, deadline
                // included, and the next evaluation reads the user again.
                database.ChangeTracker.Clear();
                return;
            }
            observations = await LoadCurrentObservationsAsync(targetId, userIds, boundItemIds, cancellationToken)
                .ConfigureAwait(false);
            // Evidence read just now may carry a last-played instant after the start of this evaluation; it is not
            // from the future and must not be discarded as such.
            now = clock.GetUtcNow().UtcDateTime;
        }
        if (userIds.Any(userId => !observations.TryGetValue(userId, out var observation) || !observation.EvidenceAvailable))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.CompletionEvidenceMissing);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (observations.Values.Any(observation => observation.PlaybackPositionTicks > 0))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.ActiveResume);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.ExemptFavourites && observations.Values.Any(observation => observation.IsFavorite))
        {
            Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.Favorite);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A favourite series protects its episodes. The state is persisted, so Details and "Due within 7 days"
        // agree with the executor instead of learning it only at unlink time (P3.T11).
        if (policy.ExemptFavourites && target.EpisodeId is { } favouriteEpisodeId && library is not null && userData is not null)
        {
            var seriesIds = await database.EpisodeBindings.AsNoTracking()
                .Where(binding => binding.EpisodeId == favouriteEpisodeId).Select(binding => binding.SeriesItemId)
                .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var seriesItems = seriesIds.Select(id => library.GetItemById(id)).ToArray();
            if (seriesItems.Any(series => series is null))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.SeriesUnavailable);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (seriesItems.Any(series => accessibleUsers.Any(user => userData.GetUserData(user, series!)?.IsFavorite == true)))
            {
                Set(result, RetentionEvaluationStates.Blocked, RetentionEvaluationReasons.FavoriteSeries);
                await SaveAsync(result, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Only a new watch counts (PHASE10 Q1 and Q9 for episodes; decision 12, 2026-09-24, the same rule for movies): a
        // completion must carry Jellyfin's own last-played instant at or after the later of when the title was first
        // tracked and when retention was first ever enabled, so no backlog watched before that becomes due by switching
        // retention on. A later switch-off and switch-on moves nothing.
        var freshFloor = FreshFloor(result, policy);
        const bool requiresFresh = true;
        var completed = observations.Values
            .Select(observation => (observation.UserId,
                CompletedAt: CompletionInstant(observation, requiresFresh, freshFloor, now)))
            .Where(item => item.CompletedAt.HasValue)
            .ToDictionary(item => item.UserId, item => item.CompletedAt!.Value);
        DateTime? completionBasis = policy.WatchedUserMode switch
        {
            WatchedUserMode.AllUsers when userIds.All(completed.ContainsKey) => completed.Values.Max(),
            WatchedUserMode.SelectedUser when completed.TryGetValue(policy.SelectedUserId!.Value, out var selected) => selected,
            WatchedUserMode.AnyUser when completed.Count > 0 => completed.Values.Min(),
            _ => null
        };
        if (!completionBasis.HasValue)
        {
            Set(result, RetentionEvaluationStates.Waiting, RetentionEvaluationReasons.WaitingForCompletion);
            await SaveAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        var priorDeadline = result.State == RetentionEvaluationStates.Scheduled ? result.Deadline : null;
        var priorBasis = result.State == RetentionEvaluationStates.Scheduled ? result.CompletionBasisAt : null;
        var policyChanged = priorPolicyVersion != 0 && priorPolicyVersion != policy.Version;
        // A target that stayed completed keeps the completion that scheduled it. Re-reading evidence from a remaining
        // version after a sibling version was reclaimed or removed can report a later instant, which must not restart the
        // title's window (P6.M7). A new completion always passes through waiting or blocked first; a policy or access
        // change recomputes as before and still never shortens the prior deadline.
        if (priorBasis is { } keptBasis && !policyChanged && !accessChanged && keptBasis < completionBasis.Value)
            completionBasis = keptBasis;
        // Grace starts no earlier than the first switch-on (PHASE10 Q9): switching retention off and on again does not
        // restart a countdown that was running, and neither does the policy revision that switch bumps.
        var eligibleAt = Latest(Latest(completionBasis.Value, policy.GraceStartAt ?? now), result.BaselineAt);
        if (priorState == RetentionEvaluationStates.Disabled) policyChanged = false;
        if (result.GraceNotBefore is { } restarted) eligibleAt = Latest(eligibleAt, restarted);
        if (!priorDeadline.HasValue && (accessChanged || policyChanged ||
            (hadPriorEvaluation && priorState != RetentionEvaluationStates.Disabled)))
        {
            eligibleAt = Latest(eligibleAt, now);
            // Remembered like an administrator's restart (RET3-N1): a window that starts now because access or the
            // policy changed, or because the completion was only now observed, must not start earlier after retention is
            // switched off and on (Q9), when this schedule is gone and the grace start is computed again.
            result.GraceNotBefore = result.GraceNotBefore is { } earlier ? Latest(earlier, eligibleAt) : eligibleAt;
        }

        var days = target.WindowDays(policy.ReclaimAfterDays);
        // An isolated test instance can shorten every window to minutes; production leaves this at zero.
        DateTime DeadlineFrom(DateTime start) =>
            policy.TestWindowMinutes > 0 ? start.AddMinutes(policy.TestWindowMinutes) : start.AddDays(days);
        var deadline = DeadlineFrom(eligibleAt);
        // Decision 13 (RET4-R1, 2026-09-25): at the switch-on, a title whose window was never announced (it was finished
        // while retention was off) or whose window has run out gets a full window from now, announced below: it is never due
        // at once and never deleted without a warning. A countdown announced before the switch-off that has not run out keeps
        // counting from the first switch-on with the current window (Q9). Remembered like RET3-N1, so a later off and on
        // keeps the date this window is announced with.
        if (priorState == RetentionEvaluationStates.Disabled && (result.AnnouncedDeadline is null || deadline <= now))
        {
            eligibleAt = Latest(eligibleAt, now);
            result.GraceNotBefore = result.GraceNotBefore is { } earlier ? Latest(earlier, eligibleAt) : eligibleAt;
            deadline = DeadlineFrom(eligibleAt);
        }
        if (priorDeadline > deadline)
        {
            deadline = priorDeadline.Value;
            // A schedule pushed out before its grace start was remembered (evaluations from before RET3-N1): remember it
            // now, while the window that produced it is still the one in force.
            if (policy.TestWindowMinutes == 0 && !policyChanged && !accessChanged)
            {
                var graceStart = deadline.AddDays(-days);
                result.GraceNotBefore = result.GraceNotBefore is { } earlier ? Latest(earlier, graceStart) : graceStart;
            }
        }

        // One event per window (RET2-R5): switching retention off and on keeps the countdown (Q9) and does not announce it
        // again; a restarted or new window has another deadline and is announced.
        if (priorState != RetentionEvaluationStates.Scheduled &&
            !(result.AnnouncedDeadline is { } announced && Math.Abs((announced - deadline).TotalSeconds) < 1))
        {
            await RecordWindowStartAsync(target, observations.Values, completionBasis.Value, deadline, cancellationToken)
                .ConfigureAwait(false);
            result.AnnouncedDeadline = deadline;
        }
        result.State = RetentionEvaluationStates.Scheduled;
        result.Reason = RetentionEvaluationReasons.CompletionPolicySatisfied;
        result.CompletionBasisAt = completionBasis;
        result.EligibleAt = eligibleAt;
        result.Deadline = deadline;
        await SaveAsync(result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that a target's retention window started, with its cause and its files (PHASE10 Q8, 2026-09-24), so the
    /// title's History says why and when its files will go. The cause is the save reason Jellyfin reported for the
    /// completing user's data: the Trakt plugin's sync saves with <c>Import</c>.
    /// </summary>
    private async Task RecordWindowStartAsync(Target target, IEnumerable<CompletionObservation> observations, DateTime basis,
        DateTime deadline, CancellationToken cancellationToken)
    {
        var source = observations.Where(observation => observation.Played)
            .OrderBy(observation => Math.Abs((observation.LastPlayedAt ?? observation.CompletedAt ?? basis).Ticks - basis.Ticks))
            .Select(observation => observation.SourceReason).FirstOrDefault() ?? string.Empty;
        var cause = WindowCause(source);
        var paths = target.EpisodeId is { } episodeId
            ? await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .Select(binding => binding.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false)
            : await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == target.Entry.Id)
                .Select(binding => binding.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Kept files are not counting down; they stay out of the event (PHASE10 Q3).
        var kept = (await database.VersionKeeps.AsNoTracking().Where(keep => keep.EntryId == target.Entry.Id)
            .Select(keep => keep.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var files = RetentionFileNames.Distinct(paths.Where(path => !string.IsNullOrEmpty(path) && !kept.Contains(path!)).Select(path => path!));
        var label = target.EpisodeId.HasValue
            ? await database.Episodes.AsNoTracking().Where(episode => episode.Id == target.EpisodeId)
                .Select(episode => "S" + episode.SeasonNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture) +
                    "E" + episode.EpisodeNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var what = files.Length == 1 ? "this file" : $"these {files.Length} files";
        database.History.Add(new HistoryRecord
        {
            EntryId = target.Entry.Id,
            EventType = "retention_started",
            Summary = $"{(label is null ? string.Empty : label + ": ")}Added to retention ({cause}): {what} will be deleted on " +
                $"{deadline.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} unless kept",
            Data = System.Text.Json.JsonSerializer.Serialize(new { episodeId = target.EpisodeId, cause, sourceReason = source,
                deadline = DateTime.SpecifyKind(deadline, DateTimeKind.Utc), files })
        });
    }

    private static string WindowCause(string sourceReason) => sourceReason switch
    {
        "Import" => "watched on another device (Trakt)",
        // A client that syncs watched state from elsewhere writes user data directly (POST /UserItems/{id}/UserData).
        "UpdateUserData" => "watched on another device (synced)",
        "TogglePlayed" => "marked played",
        "PlaybackFinished" or "PlaybackProgress" or "PlaybackStart" => "watched on this server",
        _ => "watched"
    };

    /// <summary>Writes an evaluation only when something other than its evaluation time changed (P3.T11).</summary>
    private async Task SaveAsync(RetentionEvaluation result, CancellationToken cancellationToken)
    {
        var tracked = database.Entry(result);
        if (tracked.State == EntityState.Modified && tracked.Properties
                .Where(property => property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                .All(property => property.Metadata.Name == nameof(RetentionEvaluation.EvaluatedAt)))
            tracked.State = EntityState.Unchanged;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether Jellyfin still has this native item, or null when the library cannot answer.</summary>
    private bool? ResolvesNatively(Guid itemId)
    {
        try
        {
            return library!.GetItemById(itemId) is not null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool IsActive(User user) =>
        !user.Permissions.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value);

    private static string Fingerprint(IEnumerable<Guid> userIds)
    {
        var value = string.Join(',', userIds.Order().Select(id => id.ToString("N")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static DateTime Latest(DateTime left, DateTime right) => left > right ? left : right;

    /// <summary>
    /// The instant a watch must be at or after to count for a target (PHASE10 Q1/Q9, decision 12): the later of its
    /// first evaluation (or its last reset) and the first time retention was ever enabled. The live check before an
    /// unlink applies the same floor (RET3-R4).
    /// </summary>
    internal static DateTime FreshFloor(RetentionEvaluation evaluation, RetentionPolicySnapshot policy) =>
        policy.GraceStartAt is { } enabledAt ? Latest(evaluation.BaselineAt, enabledAt) : evaluation.BaselineAt;

    /// <summary>Loads each user's observation, ignoring evidence read from a representation that is no longer bound.</summary>
    private async Task<Dictionary<Guid, CompletionObservation>> LoadCurrentObservationsAsync(
        Guid targetId, Guid[] userIds, Guid[] boundItemIds, CancellationToken cancellationToken) =>
        (await database.CompletionObservations.AsNoTracking()
            .Where(observation => observation.TargetId == targetId && userIds.Contains(observation.UserId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        .Where(observation => boundItemIds.Contains(observation.JellyfinItemId))
        .ToDictionary(observation => observation.UserId);

    /// <summary>
    /// Returns when this user's completion counts. After a reset only a completion at or after the
    /// baseline counts; the stored start of the completed state may predate it when Jellyfin
    /// reattached old user data, so a later native last-played value is accepted as the fresh one.
    /// </summary>
    private static DateTime? CompletionInstant(CompletionObservation observation, bool requiresFresh, DateTime floor, DateTime now)
    {
        if (!observation.Played || observation.PlaybackPositionTicks != 0 || !observation.CompletedAt.HasValue) return null;
        if (!requiresFresh) return observation.CompletedAt.Value;
        // A fresh completion must carry Jellyfin's own last-played instant at or after the baseline. A played state with
        // no last-played date (an imported or synced watched flag) has no evidence of when it happened; the stored start
        // of the completed state is then only the time the plugin first read it, which is not a new completion (P10.E1).
        // Jellyfin's MarkPlayed keeps an older LastPlayedDate when one exists and MarkUnplayed clears it, so "mark played"
        // on an item played before the floor stays old, while mark unplayed then played, or a real playback, is fresh.
        if (observation.LastPlayedAt is not { } lastPlayed || lastPlayed < floor || lastPlayed > now) return null;
        return observation.CompletedAt.Value >= floor ? observation.CompletedAt.Value : lastPlayed;
    }

    private static void Set(RetentionEvaluation result, string state, string reason)
    {
        result.State = state;
        result.Reason = reason;
        result.CompletionBasisAt = null;
        result.EligibleAt = null;
        result.Deadline = null;
    }

    private sealed record Target(Entry Entry, Guid? EpisodeId, RetentionPolicy EpisodePolicy = RetentionPolicy.Inherit,
        int? EpisodeDays = null)
    {
        public bool IsKept => RetentionOverrides.IsKept(Entry, EpisodeId.HasValue ? EpisodePolicy : null);

        public int WindowDays(int globalDays) =>
            RetentionOverrides.WindowDays(Entry, EpisodeId.HasValue ? EpisodePolicy : null, EpisodeDays, globalDays);
    }
}
