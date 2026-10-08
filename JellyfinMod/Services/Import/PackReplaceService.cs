using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Import;

/// <summary>
/// Finishes <c>replace</c> grabs (season and series packs, 2026-10-08): once the pack's file for an episode is imported, the
/// episode's other files are removed one by one through <see cref="RetentionExecutor.ReplaceVersionAsync"/>, which removes a
/// file only while the new one is, under its locks and right before the unlink, still bound to that episode, still the file
/// the import linked and the file Jellyfin plays, and only when nothing protects the old file (a per-file Keep, playback,
/// sharing, a download kept on disk, or a binding by episode number only). Keep on the title or the episode keeps every file.
/// A passing hold (a file playing, the new file not readable yet) is waited out for <see cref="WaitLimit"/>; anything else, or
/// a hold that outlasts the wait, keeps both files and records why. Nothing is removed for an episode whose pack file failed or
/// was skipped: only a completed import is ever marked for replacement. The files a replacement may remove are fixed when its
/// import completes (<see cref="ImportOperation.ReplaceTargets"/>), so a version added while it waits is never removed, and
/// Keep is read again under the removal's locks right before each unlink (Codex re-review of the pack plugin, findings 1-2).
/// </summary>
public sealed class PackReplaceService(
    ModDbContext database,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<PackReplaceService> logger)
{
    /// <summary>How long a replacement waits for a passing hold before it keeps both files.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromHours(24);

    /// <summary>The refusals that can pass by themselves: the replacement tries again on the next tick until the wait ends.</summary>
    private static readonly HashSet<string> Passing = new(StringComparer.Ordinal)
    {
        RetentionExecutionReasons.SuccessorUnavailable, RetentionExecutionReasons.OperationOpen,
        RetentionPreviewReasons.ActiveSession, RetentionPreviewReasons.ActiveSessionUnknown
    };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Advances every pending replacement once; returns how many are still pending.</summary>
    public async Task<int> AdvanceAllAsync(CancellationToken cancellationToken)
    {
        var pending = await database.ImportOperations.AsNoTracking().Where(value => value.ReplaceState == ReplaceStates.Pending)
            .Select(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var open = 0;
        foreach (var id in pending)
        {
            try
            {
                if (!await AdvanceAsync(id, cancellationToken).ConfigureAwait(false)) open++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Replacement after import {Operation} could not advance; it is retried on the next tick", id);
                open++;
            }
        }

        return open;
    }

    /// <summary>Whether any replacement is still pending, so the monitor keeps ticking for it.</summary>
    public static Task<bool> AnyPendingAsync(ModDbContext database, CancellationToken cancellationToken) =>
        database.ImportOperations.AnyAsync(value => value.ReplaceState == ReplaceStates.Pending, cancellationToken);

    private async Task<bool> AdvanceAsync(Guid importId, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var import = await database.ImportOperations.SingleAsync(value => value.Id == importId, cancellationToken).ConfigureAwait(false);
        if (import.ReplaceState != ReplaceStates.Pending) return true;
        if (import.State != ImportStates.Completed || import.EntryId is not { } entryId || import.EpisodeId is not { } episodeId ||
            import.BindingId is not { } newBinding)
            return await EndAsync(import, ReplaceStates.Refused, "The new file is no longer tracked; nothing was removed.", cancellationToken)
                .ConfigureAwait(false);
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
            .ConfigureAwait(false);
        var episode = await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || episode is null)
            return await EndAsync(import, ReplaceStates.Refused, "The episode was removed from the catalog; nothing was removed.",
                cancellationToken).ConfigureAwait(false);
        var label = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
        // Keep protects the whole title or episode: a kept episode only gains files, as an upgrade of it does.
        if (entry.RetentionPolicy == RetentionPolicy.Never || episode.RetentionPolicy == RetentionPolicy.Never)
            return await EndAsync(import, ReplaceStates.Refused, $"{label}: Keep protects this episode; its other files were kept.",
                cancellationToken).ConfigureAwait(false);

        var waiting = new List<string>();
        // The seeding copy of the new file is owned before anything is replaced, as for an upgrade.
        if (!await database.SeedReleaseOperations.AsNoTracking().AnyAsync(seed => seed.ImportOperationId == import.Id, cancellationToken)
                .ConfigureAwait(false))
            waiting.Add("the new file's seeding copy is not recorded yet");

        var removed = new List<string>();
        var refused = new List<string>();
        if (waiting.Count == 0)
        {
            // Only the files the episode held when the import completed (finding 2 of the re-review), each while it is still
            // bound to the episode at the same path; the new file's own binding, and anything at its path, is never one.
            var targets = ReadTargets(import.ReplaceTargets);
            var paths = targets.Select(target => target.Path).ToArray();
            var current = await database.EpisodeBindings.AsNoTracking()
                .Where(binding => binding.EpisodeId == episodeId && binding.Id != newBinding && binding.MediaPath != import.DestinationPath &&
                    binding.MediaPath != null && paths.Contains(binding.MediaPath))
                .Select(binding => new { binding.Id, binding.MediaPath }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var target in targets.OrderBy(target => target.Path, StringComparer.Ordinal))
            {
                var other = current.FirstOrDefault(binding => binding.MediaPath == target.Path);
                if (other is null) continue;
                var name = Path.GetFileName(target.Path);
                RetentionExecutionResult result;
                using (var scope = scopes.CreateScope())
                    result = await scope.ServiceProvider.GetRequiredService<RetentionExecutor>()
                        .ReplaceVersionAsync(entryId, other.Id, import.Id, target.Path, target.Fingerprint, cancellationToken)
                        .ConfigureAwait(false);
                if (result.State == RetentionOperationStates.Completed) removed.Add(name);
                else if (result.State is RetentionOperationStates.Prepared or RetentionOperationStates.Unlinked)
                    waiting.Add(name + " is being removed");
                else if (Passing.Contains(result.Reason)) waiting.Add(name + " (" + result.Reason + ")");
                else refused.Add(name + " (" + result.Reason + ")");
            }
        }

        database.ChangeTracker.Clear();
        import = await database.ImportOperations.SingleAsync(value => value.Id == importId, cancellationToken).ConfigureAwait(false);
        if (waiting.Count > 0)
        {
            if (Now - (import.CompletedAt ?? import.UpdatedAt) < WaitLimit)
            {
                var detail = ImportService.Bound($"{label}: waiting to replace: {string.Join(", ", waiting)}" +
                    (removed.Count > 0 ? "; removed " + string.Join(", ", removed) : string.Empty) + ".");
                if (import.ReplaceDetail != detail)
                {
                    import.ReplaceDetail = detail;
                    import.UpdatedAt = Now;
                    await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return false;
            }

            refused.AddRange(waiting);
        }

        if (refused.Count > 0)
            return await EndAsync(import, ReplaceStates.Refused,
                $"{label}: kept {string.Join(", ", refused)} beside the new file" +
                (removed.Count > 0 ? "; removed " + string.Join(", ", removed) : string.Empty) + ".", cancellationToken).ConfigureAwait(false);
        return await EndAsync(import, ReplaceStates.Done, removed.Count == 0
            ? $"{label}: no other file was left to replace."
            : $"{label}: replaced {string.Join(", ", removed)}.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One file a replacement may remove: its binding when the replacement was decided, its path and content.</summary>
    public sealed record ReplaceTarget(Guid BindingId, string Path, string? Fingerprint);

    /// <summary>
    /// The episode's other files when a replace import completes, as <see cref="ImportOperation.ReplaceTargets"/>: every file
    /// bound to the episode but the new one. Read by the caller under the library lock, in the import's completing transaction.
    /// </summary>
    public static async Task<string> SnapshotTargetsAsync(ModDbContext database, Guid episodeId, Guid newBindingId, string? destination,
        CancellationToken cancellationToken)
    {
        var targets = await database.EpisodeBindings.AsNoTracking()
            .Where(binding => binding.EpisodeId == episodeId && binding.Id != newBindingId && binding.MediaPath != null &&
                binding.MediaPath != destination)
            .OrderBy(binding => binding.MediaPath)
            .Select(binding => new ReplaceTarget(binding.Id, binding.MediaPath!, binding.FileFingerprint))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(targets);
    }

    private static List<ReplaceTarget> ReadTargets(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ReplaceTarget>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> EndAsync(ImportOperation import, string state, string detail, CancellationToken cancellationToken)
    {
        var now = Now;
        import.ReplaceState = state;
        import.ReplaceDetail = ImportService.Bound(detail);
        import.UpdatedAt = now;
        if (import.EntryId is { } entryId && await database.Entries.AnyAsync(entry => entry.Id == entryId, cancellationToken).ConfigureAwait(false))
            database.History.Add(new HistoryRecord
            {
                EntryId = entryId, EventType = state == ReplaceStates.Done ? "pack_replaced" : "pack_replace_refused", CreatedAt = now,
                BindingId = import.BindingId, Summary = ImportService.Bound(detail),
                Data = JsonSerializer.Serialize(new { operationId = import.Id, import.GrabId, import.EpisodeId, state })
            });
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Replacement after import {Operation}: {State}", import.Id, state);
        return true;
    }
}
