using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Import;

/// <summary>The effective seed goal of one torrent and whether it is met (P5.I6).</summary>
/// <param name="Ratio">The strictest ratio goal, or null.</param>
/// <param name="RatioSource">Where the ratio goal came from.</param>
/// <param name="Seconds">The strictest seeding-time goal in seconds, or null.</param>
/// <param name="SecondsSource">Where the seeding-time goal came from.</param>
/// <param name="Met">Whether the torrent is complete and every required component is met.</param>
/// <param name="WaitingFor">The unmet components: <c>complete</c>, <c>ratio</c> or <c>time</c>.</param>
public sealed record SeedGoal(double? Ratio, string? RatioSource, long? Seconds, string? SecondsSource, bool Met,
    IReadOnlyList<string> WaitingFor);

/// <summary>
/// Owns the seeding copy of every completed import: waits for the effective goal, then has the client forget the torrent
/// (P5.I6). Nothing is ever deleted here: the torrent's checked files are recorded on the release before the client is
/// asked, and they stay on disk for the administrator to remove (Codex delta review 5; user decisions 2026-10-02:
/// 0.1.0.0 never deletes a download; a cleanup tool is planned for a later version).
/// </summary>
/// <remarks>
/// Serialized with retention through <see cref="RetentionExecutionGate"/>: a release never runs while a retention
/// operation on the same inode is prepared or unlinked. Keep and favourites protect the library file only, so they never
/// delay a release.
/// </remarks>
public sealed class SeedReleaseService(
    ModDbContext database,
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    UnixFileInspector files,
    ILibraryManager library,
    RetentionExecutionGate retentionGate,
    TimeProvider time,
    ILogger<SeedReleaseService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Computes the effective goal: the strictest of the indexer snapshot on the grab, the client's own per-torrent limit
    /// (never lowered) and the global floor. Indexer and client components are all required; the floor is met by its ratio
    /// or its time, whichever comes first. Completion is always required.
    /// </summary>
    public static SeedGoal Evaluate(ClientTorrentStatus torrent, double? indexerRatio, long? indexerSeconds, AcquisitionSettings settings) =>
        Evaluate(torrent.Complete, torrent.UploadRatio, torrent.SecondsSeeding, torrent.RatioLimit, indexerRatio, indexerSeconds,
            settings.SeedFloorRatio, settings.SeedFloorHours);

    /// <summary>
    /// Computes the effective goal from its parts; the Phase 3 seed reader uses the same rule for torrents this plugin
    /// added, so retention and the seed release agree on when a plugin torrent has seeded enough.
    /// </summary>
    /// <param name="complete">Whether every wanted byte is downloaded.</param>
    /// <param name="ratio">The current upload ratio.</param>
    /// <param name="seconds">The cumulative seeding time.</param>
    /// <param name="clientRatioLimit">A finite ratio limit the client applies to the torrent, if any; never lowered.</param>
    /// <param name="indexerRatio">The indexer's ratio requirement snapshot on the grab.</param>
    /// <param name="indexerSeconds">The indexer's seeding-time requirement snapshot on the grab.</param>
    /// <param name="seedFloorRatio">The global floor ratio.</param>
    /// <param name="seedFloorHours">The global floor seeding time in hours.</param>
    public static SeedGoal Evaluate(bool complete, double ratio, long seconds, double? clientRatioLimit, double? indexerRatio,
        long? indexerSeconds, double? seedFloorRatio, int? seedFloorHours)
    {
        var floorRatio = seedFloorRatio is > 0 ? seedFloorRatio : null;
        long? floorSeconds = seedFloorHours is > 0 ? seedFloorHours * 3600L : null;
        var clientRatio = clientRatioLimit is > 0 ? clientRatioLimit : null;
        var waiting = new List<string>();
        if (!complete) waiting.Add("complete");
        var ratioRequired = indexerRatio is > 0 && ratio < indexerRatio || clientRatio is { } limit && ratio < limit;
        var timeRequired = indexerSeconds is > 0 && seconds < indexerSeconds;
        var floorMet = floorRatio is null && floorSeconds is null || floorRatio is { } fr && ratio >= fr ||
            floorSeconds is { } fs && seconds >= fs;
        if (ratioRequired || !floorMet && floorRatio is not null) waiting.Add("ratio");
        if (timeRequired || !floorMet && floorSeconds is not null) waiting.Add("time");
        var (goalRatio, ratioSource) = Strictest((indexerRatio is > 0 ? indexerRatio : null, "indexer"), (clientRatio, "client"),
            (floorRatio, "floor"));
        var (goalSeconds, secondsSource) = Strictest((indexerSeconds is > 0 ? indexerSeconds : null, "indexer"), (floorSeconds, "floor"));
        return new SeedGoal(goalRatio, ratioSource, goalSeconds, secondsSource,
            complete && !ratioRequired && !timeRequired && floorMet, waiting.Distinct().ToArray());
    }

    private static (T? Value, string? Source) Strictest<T>(params (T? Value, string Source)[] candidates) where T : struct, IComparable<T>
    {
        (T? Value, string? Source) best = (null, null);
        foreach (var candidate in candidates)
            if (candidate.Value is { } value && (best.Value is not { } current || value.CompareTo(current) > 0))
                best = (value, candidate.Source);
        return best;
    }

    /// <summary>Advances one seed release against a client snapshot.</summary>
    public async Task EvaluateAsync(Guid seedId, ClientSnapshot snapshot, AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var seed = await database.SeedReleaseOperations.SingleAsync(value => value.Id == seedId, cancellationToken).ConfigureAwait(false);
        if (!SeedReleaseStates.Open.Contains(seed.State) || !snapshot.Reachable) return;
        var torrent = snapshot.Find(seed.InfoHash);
        if (seed.State == SeedReleaseStates.Removing)
        {
            await ContinueRemovalAsync(seed, torrent, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (torrent is null)
        {
            // A release that recorded its files for a detach that did not finish (a retry sent it back to waiting or blocked)
            // keeps them: the torrent leaving the client does not resolve them (Codex delta review 7, P2 5).
            if (seed.CleanupManifest is not null)
            {
                await DetachAsync(seed, "The torrent left the download client outside JellyfinMod. Nothing was deleted; its recorded "
                    + "files stay on disk.").ConfigureAwait(false);
                return;
            }

            // A torrent gone from the client says nothing about its data (final review, finding 1). Only a seeding file
            // confirmed gone completes the release; one still there, or one that cannot be read, is kept: the release is
            // detached for a manual cleanup with its seeding file, which retention never unlinks.
            if (files.Probe(seed.SeedingPath) != PathPresence.Absent)
            {
                await DetachAsync(seed, "The torrent left the download client outside JellyfinMod. Nothing was deleted; its "
                    + "downloaded files stay on disk and need a manual cleanup.").ConfigureAwait(false);
                return;
            }

            // Removed outside the plugin, data included: the plugin deletes nothing and cannot prove what was freed.
            await FinishAsync(seed, SeedReleaseReasons.CopyMissing, null, null, "seeding_copy_missing",
                "The seeding copy was removed outside JellyfinMod; the library file is unaffected.").ConfigureAwait(false);
            return;
        }

        var goal = Evaluate(torrent, seed.IndexerRatio, seed.IndexerSeconds, settings);
        var changed = seed.ObservedRatio != torrent.UploadRatio || seed.ObservedSeedingSeconds != torrent.SecondsSeeding ||
            seed.GoalRatio != goal.Ratio || seed.GoalSeconds != goal.Seconds;
        seed.ObservedRatio = torrent.UploadRatio;
        seed.ObservedSeedingSeconds = torrent.SecondsSeeding;
        seed.GoalRatio = goal.Ratio;
        seed.GoalRatioSource = goal.RatioSource;
        seed.GoalSeconds = goal.Seconds;
        seed.GoalSecondsSource = goal.SecondsSource;
        if (goal.Met && seed.GoalMetAt is null)
        {
            seed.GoalMetAt = Now;
            changed = true;
        }
        else if (!goal.Met && seed.GoalMetAt is not null)
        {
            // A goal that is no longer met (a raised floor, a recheck that found missing pieces) protects the library file
            // again; a historical completion must not stand in for the current goal (whole-review chunk 1, P2 4).
            seed.GoalMetAt = null;
            changed = true;
        }

        var reason = !goal.Met
            ? torrent.Complete ? SeedReleaseReasons.GoalUnmet : SeedReleaseReasons.Incomplete
            : !settings.SeedReleaseEnabled ? SeedReleaseReasons.Disabled : null;
        if (reason is not null)
        {
            changed |= seed.State != SeedReleaseStates.Waiting || seed.Reason != reason;
            seed.State = SeedReleaseStates.Waiting;
            seed.Reason = reason;
            if (changed)
            {
                seed.UpdatedAt = Now;
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        await ReleaseAsync(seed, torrent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks every precondition, records the torrent's files, then has the client forget the torrent, deleting nothing.</summary>
    private async Task ReleaseAsync(SeedReleaseOperation seed, ClientTorrentStatus snapshot, CancellationToken cancellationToken)
    {
        await using var lease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        // The tick's snapshot predates the wait for the gate; the torrent may have moved since (Codex re-review P1-a).
        var (read, torrent) = await ReadNowAsync(seed, cancellationToken).ConfigureAwait(false);
        if (!read || torrent is null) return;
        var (client, seeding, verified) = await CheckReleasableAsync(seed, torrent, cancellationToken).ConfigureAwait(false);
        if (client is null) return;

        seed.State = SeedReleaseStates.Removing;
        seed.Reason = null;
        seed.Error = null;
        seed.HardlinkCountBefore = seeding.HardlinkCount;
        seed.RemovingAt = seed.UpdatedAt = Now;
        // The checked files are stored before the client forgets the torrent, so a restart in between still knows them.
        seed.CleanupManifest = TorrentDataRemoval.Serialize(verified);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} detaches torrent {Hash} after its goal; its files are kept", seed.Id, seed.InfoHash);
        if (await RemoveFromClientAsync(seed, client, cancellationToken).ConfigureAwait(false))
            await DetachAsync(seed).ConfigureAwait(false);
    }

    /// <summary>
    /// Every safeguard a removal with data needs, read now: before the first request and again before every retry, so a
    /// check made before an interrupted request never authorizes a later deletion (whole-review P1 7). Returns the client
    /// and the seeding file, or a null client after blocking or waiting the release.
    /// </summary>
    private async Task<(AcquisitionDownloadClient? Client, UnixFileSnapshot Seeding, IReadOnlyList<VerifiedTorrentFile> Verified)>
        CheckReleasableAsync(SeedReleaseOperation seed, ClientTorrentStatus torrent, CancellationToken cancellationToken)
    {
        var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == seed.GrabId, cancellationToken)
            .ConfigureAwait(false);
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == seed.DownloadClientId, cancellationToken).ConfigureAwait(false);
        // Only a torrent this plugin added, recognized by both labels it set, is ever removed.
        if (grab is null || client is null || !torrent.Labels.Contains(GrabService.OwnerLabel(grab.Id), StringComparer.Ordinal) ||
            !torrent.Labels.Contains(grab.Label, StringComparer.Ordinal))
        {
            await BlockAsync(seed, SeedReleaseReasons.NotOwned, "The torrent does not carry JellyfinMod's ownership labels.")
                .ConfigureAwait(false);
            return (null, default, []);
        }

        // The goal is decided on the torrent as it is now, under the current settings: a torrent turned incomplete, or a goal
        // raised, while the release waited goes back to waiting, before the first removal and before every retry (Codex
        // delta review 1, P2).
        var settings = await database.AcquisitionSettings.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == AcquisitionSettings.SingletonId, cancellationToken).ConfigureAwait(false) ??
            new AcquisitionSettings();
        var goal = Evaluate(torrent, seed.IndexerRatio, seed.IndexerSeconds, settings);
        if (!goal.Met || !settings.SeedReleaseEnabled)
        {
            if (!goal.Met) seed.GoalMetAt = null;
            seed.State = SeedReleaseStates.Waiting;
            seed.Reason = !goal.Met
                ? torrent.Complete ? SeedReleaseReasons.GoalUnmet : SeedReleaseReasons.Incomplete
                : SeedReleaseReasons.Disabled;
            seed.UpdatedAt = Now;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return (null, default, []);
        }

        // The client deletes the files where the torrent is now, which a relocation can have moved into a library. Every
        // current file must map to this server, resolve outside every library, and still include the recorded seeding copy
        // (whole-review P1 5).
        var mappings = await database.DownloadClientPathMappings.AsNoTracking()
            .Where(mapping => mapping.DownloadClientId == client.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = ImportPaths.ResolveTorrentData(torrent, mappings, client, files);
        if (IsInsideLibrary(seed.SeedingPath) || current is null || current.Any(IsInsideLibrary))
        {
            await BlockAsync(seed, SeedReleaseReasons.SeedingInsideLibrary,
                "The torrent's data is not in a mapped download folder outside every library; removing it could delete library media.")
                .ConfigureAwait(false);
            return (null, default, []);
        }

        var verified = TorrentDataRemoval.Inspect(torrent, current, files);
        if (!files.TryInspect(seed.SeedingPath, out var seeding) || seeding.PhysicalIdentity != seed.SeedingPhysicalIdentity ||
            !current.Contains(seeding.CanonicalPath, StringComparer.Ordinal) || verified is null ||
            !verified.Any(file => file.Path == seeding.CanonicalPath && file.PhysicalIdentity == seed.SeedingPhysicalIdentity))
        {
            await BlockAsync(seed, SeedReleaseReasons.SeedingUnavailable,
                "The seeding copy is not where the import found it, or the torrent no longer holds it; nothing was removed.")
                .ConfigureAwait(false);
            return (null, default, []);
        }

        if (await database.RetentionOperations.AnyAsync(operation => operation.PhysicalIdentity == seed.SeedingPhysicalIdentity &&
                (operation.State == RetentionOperationStates.Prepared || operation.State == RetentionOperationStates.Unlinked),
                cancellationToken).ConfigureAwait(false))
        {
            await WaitAsync(seed, SeedReleaseReasons.RetentionOpen).ConfigureAwait(false);
            return (null, default, []);
        }

        var libraryLinkPresent = files.TryInspect(seed.LibraryPath, out var libraryFile) &&
            libraryFile.PhysicalIdentity == seed.SeedingPhysicalIdentity;
        if (!libraryLinkPresent && await ReclaimedByRetentionAsync(seed, cancellationToken).ConfigureAwait(false) is null)
        {
            await BlockAsync(seed, SeedReleaseReasons.LibraryLinkUnexpected,
                "The library file is gone but no retention operation removed it; nothing was removed.").ConfigureAwait(false);
            return (null, default, []);
        }

        return (client, seeding, verified);
    }

    /// <summary>
    /// Has the client forget the torrent without deleting anything (Codex delta reviews 1 and 5). Returns whether the client
    /// confirmed; otherwise the next tick retries.
    /// </summary>
    private async Task<bool> RemoveFromClientAsync(SeedReleaseOperation seed, AcquisitionDownloadClient client,
        CancellationToken cancellationToken)
    {
        if (drivers.Get(client.Kind) is not { } driver) return false;
        try
        {
            var connection = await configuration.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            await driver.RemoveAsync(connection, seed.InfoHash, deleteData: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or TaskCanceledException)
        {
            // The removal outcome is unknown; the next tick reads the client and continues from `removing`.
            seed.Error = ImportService.Bound("The client did not confirm the removal: " + error.GetType().Name);
            seed.UpdatedAt = Now;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Reads the torrent from its client now, under the retention gate, for the checks right before a removal with data:
    /// never the tick's snapshot, which is older than the wait for the gate (Codex re-review P1-a). Not read when the client
    /// cannot be asked; a null torrent when the client no longer holds it.
    /// </summary>
    private async Task<(bool Read, ClientTorrentStatus? Torrent)> ReadNowAsync(SeedReleaseOperation seed, CancellationToken cancellationToken)
    {
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == seed.DownloadClientId, cancellationToken).ConfigureAwait(false);
        if (client is null || drivers.Get(client.Kind) is not { } driver) return (false, null);
        try
        {
            var connection = await configuration.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            var torrents = await driver.GetStatusAsync(connection, [seed.InfoHash], cancellationToken).ConfigureAwait(false);
            return (true, torrents.FirstOrDefault(value => string.Equals(value.InfoHash, seed.InfoHash, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or TaskCanceledException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Whether a seed's goal is met by its torrent as the client shows it now: complete, and seeded enough under the client's
    /// current requirements and the current floor. Retention asks before it reclaims a library file this seed shares, since the
    /// last observation may predate a torrent turning incomplete or its goal being raised (Codex round 2 P2). Null when the
    /// client cannot be asked or no longer holds the torrent.
    /// </summary>
    public async Task<SeedGoal?> CurrentGoalAsync(SeedReleaseOperation seed, AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        var (read, torrent) = await ReadNowAsync(seed, cancellationToken).ConfigureAwait(false);
        return read && torrent is not null ? Evaluate(torrent, seed.IndexerRatio, seed.IndexerSeconds, settings) : null;
    }

    /// <summary>Finishes a detach found in progress, including after a restart between recording the files and the request.</summary>
    private async Task ContinueRemovalAsync(SeedReleaseOperation seed, ClientTorrentStatus? snapshot, CancellationToken cancellationToken)
    {
        await using var lease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        // Read again under the gate, as before the first request (Codex re-review P1-a); the snapshot only says a detach is due.
        var (read, torrent) = snapshot is null ? (true, null) : await ReadNowAsync(seed, cancellationToken).ConfigureAwait(false);
        if (!read) return;
        if (torrent is not null)
        {
            // A retry is a new detach: every safeguard and the goal are read again first, and the files recorded afresh
            // (whole-review P1 7, Codex delta review 1).
            var (client, _, verified) = await CheckReleasableAsync(seed, torrent, cancellationToken).ConfigureAwait(false);
            if (client is null) return;
            seed.CleanupManifest = TorrentDataRemoval.Serialize(verified);
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            if (await RemoveFromClientAsync(seed, client, cancellationToken).ConfigureAwait(false))
                await DetachAsync(seed).ConfigureAwait(false);
            return;
        }

        // The client forgot the torrent (a restart came between the request and its confirmation). Its files stay where they
        // are. A release from an older build, which recorded no list of them, is left for manual cleanup with its seeding
        // path rather than trusted to that one path (Codex delta review 5, P2).
        await DetachAsync(seed).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the client forgot the torrent and nothing was deleted. The release leaves the open states, so no tick
    /// and no missing torrent completes it; its recorded files wait for the administrator's manual cleanup, and a release
    /// without a trustworthy list of them is marked for manual cleanup (Codex delta review 5, P2).
    /// </summary>
    private async Task DetachAsync(SeedReleaseOperation seed, string? summary = null)
    {
        var listed = TorrentDataRemoval.Deserialize(seed.CleanupManifest);
        if (listed is null) seed.CleanupManifest = null;
        var now = Now;
        seed.State = SeedReleaseStates.Detached;
        seed.Reason = listed is null ? SeedReleaseReasons.ManualCleanup : SeedReleaseReasons.CleanupPending;
        seed.Error = null;
        seed.RemovedAt = seed.UpdatedAt = now;
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == seed.GrabId).ConfigureAwait(false);
        if (grab is not null)
        {
            grab.ActiveHash = null;
            grab.ActiveTarget = null;
            grab.UpdatedAt = now;
        }

        AddHistory(seed, "seeding_released", summary ?? (listed is null
            ? "Stopped seeding after its goal. Nothing was deleted; its downloaded files need a manual cleanup."
            : "Stopped seeding after its goal. Nothing was deleted; its downloaded files stay on disk."),
            released: null, credited: null, now);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} detached; {Count} file(s) kept on disk", seed.Id, listed?.Count ?? 0);
    }

    /// <summary>
    /// The retention operation that reclaimed this import's own library link: the same physical file, reclaimed after this
    /// release was prepared. A reclamation of an earlier file at the same pathname is not evidence about this one
    /// (whole-review P1 6).
    /// </summary>
    private async Task<RetentionOperation?> ReclaimedByRetentionAsync(SeedReleaseOperation seed, CancellationToken cancellationToken) =>
        await database.RetentionOperations.AsNoTracking()
            .Where(operation => operation.State == RetentionOperationStates.Completed &&
                operation.PhysicalIdentity == seed.SeedingPhysicalIdentity && operation.PreparedAt >= seed.PreparedAt)
            .OrderByDescending(operation => operation.CompletedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private bool IsInsideLibrary(string path)
    {
        try
        {
            return library.GetVirtualFolders().SelectMany(folder => folder.Locations ?? [])
                .Select(location => files.TryCanonicalize(location, out var canonical) ? canonical : location)
                .Any(root => MediaStorageIdentity.Contains(root, path));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Unknown libraries: assume the worst and never remove data.
            return true;
        }
    }

    private async Task WaitAsync(SeedReleaseOperation seed, string reason)
    {
        if (seed.State == SeedReleaseStates.Waiting && seed.Reason == reason) return;
        seed.State = SeedReleaseStates.Waiting;
        seed.Reason = reason;
        seed.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task BlockAsync(SeedReleaseOperation seed, string reason, string detail)
    {
        if (seed.State == SeedReleaseStates.Blocked && seed.Reason == reason) return;
        seed.State = SeedReleaseStates.Blocked;
        seed.Reason = reason;
        seed.Error = ImportService.Bound(detail);
        seed.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} blocked: {Reason}", seed.Id, reason);
    }

    /// <summary>Ends a release with one history event keyed by the release, and gives the grab's hash back.</summary>
    private async Task FinishAsync(SeedReleaseOperation seed, string reason, long? released, Guid? credited, string eventType,
        string summary)
    {
        var now = Now;
        seed.State = SeedReleaseStates.Completed;
        seed.Reason = reason;
        seed.PhysicalBytesReleased = released;
        seed.CreditedRetentionOperationId = credited;
        seed.CompletedAt = seed.UpdatedAt = now;
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == seed.GrabId).ConfigureAwait(false);
        if (grab is not null)
        {
            grab.ActiveHash = null;
            grab.ActiveTarget = null;
            grab.UpdatedAt = now;
        }

        AddHistory(seed, eventType, summary, released, credited, now);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} finished: {Reason}", seed.Id, reason);
    }

    /// <summary>Adds the release's one history event, keyed by the release, unless its entry is gone or it was written already.</summary>
    private void AddHistory(SeedReleaseOperation seed, string eventType, string summary, long? released, Guid? credited, DateTime now)
    {
        if (seed.EntryId is not { } entryId || !database.Entries.Any(entry => entry.Id == entryId) ||
            database.History.Any(history => history.Id == seed.Id) ||
            database.History.Local.Any(history => history.Id == seed.Id))
            return;
        database.History.Add(new HistoryRecord
        {
            Id = seed.Id, EntryId = entryId, EventType = eventType, CreatedAt = now, Summary = ImportService.Bound(summary),
            Data = JsonSerializer.Serialize(new
            {
                seedReleaseId = seed.Id, seed.ImportOperationId, seed.EpisodeId, logicalBytes = seed.SourceLogicalBytes,
                physicalBytesReleased = released, creditedRetentionOperationId = credited, observedRatio = seed.ObservedRatio,
                observedSeedingSeconds = seed.ObservedSeedingSeconds, goalRatio = seed.GoalRatio, goalSeconds = seed.GoalSeconds,
                filesKept = TorrentDataRemoval.Deserialize(seed.CleanupManifest)?.Count
            })
        });
    }
}
