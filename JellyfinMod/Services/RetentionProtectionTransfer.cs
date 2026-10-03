using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>
/// Carries a duplicate's settings and retention protections onto the row that survives it, before the duplicate is deleted
/// and its evaluation goes with it by cascade. Used wherever two rows prove to be one title or one episode: re-homing merges a
/// file-less wanted entry into the re-homed one (P2.R7) and the series refresh folds a TMDB episode into a position row
/// (P3.T10). Conservative throughout, so a merge can never make the surviving file reclaimable sooner (Codex re-review P1-b
/// and round 2 P1): a Keep always wins; a window of the duplicate's own is taken only where it is longer than the one the
/// survivor actually has (its own, else its series', else the global one), with a fresh grace; and the latest grace or
/// baseline the duplicate's evaluation holds becomes the earliest instant the survivor's grace may start.
/// </summary>
internal static class RetentionProtectionTransfer
{
    /// <summary>Carries a duplicate episode onto the surviving episode of <paramref name="series"/>. Saves nothing.</summary>
    public static async Task CarryEpisodeAsync(
        ModDbContext database,
        Entry series,
        Episode survivor,
        Episode duplicate,
        DateTime now,
        CancellationToken cancellationToken)
    {
        survivor.Monitored |= duplicate.Monitored;
        var restart = false;
        if (duplicate.RetentionPolicy == RetentionPolicy.Never)
        {
            survivor.RetentionPolicy = RetentionPolicy.Never;
        }
        else if (duplicate.RetentionPolicy == RetentionPolicy.Days && duplicate.ReclaimAfterDays is > 0 and var days &&
                 survivor.RetentionPolicy != RetentionPolicy.Never &&
                 RetentionOverrides.WindowDays(series, survivor.RetentionPolicy, survivor.ReclaimAfterDays,
                     await GlobalDaysAsync(database, cancellationToken).ConfigureAwait(false)) < days)
        {
            (survivor.RetentionPolicy, survivor.ReclaimAfterDays) = (RetentionPolicy.Days, days);
            restart = true;
        }

        await CarryGraceAsync(database, series.Id, survivor.Id, survivor.Id, duplicate.Id, restart, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Carries a duplicate entry's own settings onto the surviving entry, and for a movie its retention protections. A series'
    /// episodes are carried one by one with <see cref="CarryEpisodeAsync"/>; when the series' own window grows, every episode
    /// that follows it needs a fresh grace too, which <see cref="RestartFollowingEpisodesAsync"/> gives once the episodes are
    /// merged. Returns whether a series' window grew. Saves nothing.
    /// </summary>
    public static async Task<bool> CarryEntryAsync(
        ModDbContext database,
        Entry survivor,
        Entry duplicate,
        DateTime now,
        CancellationToken cancellationToken)
    {
        survivor.Monitored |= duplicate.Monitored;
        var restart = false;
        if (duplicate.RetentionPolicy == RetentionPolicy.Never)
        {
            survivor.RetentionPolicy = RetentionPolicy.Never;
        }
        else if (duplicate.RetentionPolicy == RetentionPolicy.Days && duplicate.ReclaimAfterDays is > 0 and var days &&
                 survivor.RetentionPolicy != RetentionPolicy.Never &&
                 RetentionOverrides.WindowDays(survivor, null, null,
                     await GlobalDaysAsync(database, cancellationToken).ConfigureAwait(false)) < days)
        {
            (survivor.RetentionPolicy, survivor.ReclaimAfterDays) = (RetentionPolicy.Days, days);
            restart = true;
        }

        if (survivor.MediaType == "movie")
        {
            await CarryGraceAsync(database, survivor.Id, null, survivor.Id, duplicate.Id, restart, now, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        return restart;
    }

    /// <summary>
    /// Gives every episode of a series whose window is the series' own a fresh grace from <paramref name="now"/>: bound or not,
    /// since a multi-episode file reads the grace of every episode it covers (Codex delta review 1, P1). Saves nothing.
    /// </summary>
    public static async Task RestartFollowingEpisodesAsync(ModDbContext database, Entry series, DateTime now,
        CancellationToken cancellationToken)
    {
        var episodes = await database.Episodes.Where(episode => episode.EntryId == series.Id).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var episode in database.Episodes.Local.Where(episode => episode.EntryId == series.Id).Union(episodes).Distinct())
        {
            if (episode.RetentionPolicy == RetentionPolicy.Never ||
                episode.RetentionPolicy == RetentionPolicy.Days && episode.ReclaimAfterDays is > 0)
                continue;
            await RaiseGraceAsync(database, series.Id, episode.Id, episode.Id, now, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> GlobalDaysAsync(ModDbContext database, CancellationToken cancellationToken) =>
        (await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            policy => policy.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false))?.ReclaimAfterDays ?? 14;

    private static async Task CarryGraceAsync(
        ModDbContext database,
        Guid entryId,
        Guid? episodeId,
        Guid survivorTargetId,
        Guid duplicateTargetId,
        bool restart,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var duplicate = await FindAsync(database, duplicateTargetId, cancellationToken).ConfigureAwait(false);
        DateTime? floor = restart ? now : null;
        if (duplicate is not null)
        {
            floor = Latest(floor, duplicate.BaselineAt);
            floor = Latest(floor, duplicate.GraceNotBefore);
        }

        if (floor is not { } graceFloor) return;
        await RaiseGraceAsync(database, entryId, episodeId, survivorTargetId, graceFloor, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes <paramref name="graceFloor"/> the earliest instant a target's grace may start, creating its evaluation.</summary>
    private static async Task RaiseGraceAsync(
        ModDbContext database,
        Guid entryId,
        Guid? episodeId,
        Guid survivorTargetId,
        DateTime graceFloor,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var survivor = await FindAsync(database, survivorTargetId, cancellationToken).ConfigureAwait(false);
        if (survivor is null)
        {
            // A survivor never evaluated gets the evaluation its first evaluation would create, waiting out the carried grace.
            database.RetentionEvaluations.Add(new RetentionEvaluation
            {
                EntryId = entryId, EpisodeId = episodeId, TargetId = survivorTargetId, State = RetentionEvaluationStates.Waiting,
                Reason = RetentionEvaluationReasons.RepresentationReset, EvaluatedAt = now, BaselineAt = now,
                GraceNotBefore = graceFloor, RequiresFreshCompletion = true
            });
            return;
        }

        if (survivor.GraceNotBefore is { } current && current >= graceFloor) return;
        survivor.GraceNotBefore = graceFloor;
        // A schedule computed before the carried grace no longer holds: it waits to be evaluated again rather than act on a
        // deadline the grace has moved.
        if (survivor.State != RetentionEvaluationStates.Scheduled) return;
        survivor.State = RetentionEvaluationStates.Waiting;
        survivor.Reason = RetentionEvaluationReasons.WaitingForCompletion;
        survivor.Deadline = null;
        survivor.EligibleAt = null;
        survivor.CompletionBasisAt = null;
    }

    private static async Task<RetentionEvaluation?> FindAsync(ModDbContext database, Guid targetId, CancellationToken cancellationToken) =>
        database.RetentionEvaluations.Local.FirstOrDefault(evaluation => evaluation.TargetId == targetId &&
            database.Entry(evaluation).State != EntityState.Deleted) ??
        await database.RetentionEvaluations.SingleOrDefaultAsync(evaluation => evaluation.TargetId == targetId, cancellationToken)
            .ConfigureAwait(false);

    private static DateTime? Latest(DateTime? left, DateTime? right) =>
        left is { } a && right is { } b ? (a > b ? a : b) : left ?? right;
}
