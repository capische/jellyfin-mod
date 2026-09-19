using System.Globalization;
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
/// Owns the seeding copy of every completed import: waits for the effective goal, then removes the torrent and its data
/// through the client, and accounts honestly for the disk that removal frees (P5.I6).
/// </summary>
/// <remarks>
/// Serialized with retention through <see cref="RetentionExecutionGate"/>: a release never runs while a retention
/// operation on the same inode is prepared or unlinked, and it never deletes a library file. Keep and favourites protect
/// the library file only, so they never delay a release.
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
    /// <summary>How long a removed torrent's file may linger before the release reports it survived.</summary>
    public static readonly TimeSpan RemovalGrace = TimeSpan.FromMinutes(10);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Computes the effective goal: the strictest of the indexer snapshot on the grab, the client's own per-torrent limit
    /// (never lowered) and the global floor. Indexer and client components are all required; the floor is met by its ratio
    /// or its time, whichever comes first. Completion is always required.
    /// </summary>
    public static SeedGoal Evaluate(ClientTorrentStatus torrent, double? indexerRatio, long? indexerSeconds, AcquisitionSettings settings)
    {
        var floorRatio = settings.SeedFloorRatio is > 0 ? settings.SeedFloorRatio : null;
        long? floorSeconds = settings.SeedFloorHours is > 0 ? settings.SeedFloorHours * 3600L : null;
        var clientRatio = torrent.RatioLimit is > 0 ? torrent.RatioLimit : null;
        var ratio = torrent.UploadRatio;
        var seconds = torrent.SecondsSeeding;
        var waiting = new List<string>();
        if (!torrent.Complete) waiting.Add("complete");
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
            torrent.Complete && !ratioRequired && !timeRequired && floorMet, waiting.Distinct().ToArray());
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
            // Removed outside the plugin: the plugin deletes nothing and cannot prove what was freed.
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

    /// <summary>Checks every precondition, then asks the client to remove the torrent with its data.</summary>
    private async Task ReleaseAsync(SeedReleaseOperation seed, ClientTorrentStatus torrent, CancellationToken cancellationToken)
    {
        await using var lease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
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
            return;
        }

        if (IsInsideLibrary(seed.SeedingPath))
        {
            await BlockAsync(seed, SeedReleaseReasons.SeedingInsideLibrary,
                "The seeding copy lies inside a library folder; removing its data could delete library media.").ConfigureAwait(false);
            return;
        }

        if (!files.TryInspect(seed.SeedingPath, out var seeding) || seeding.PhysicalIdentity != seed.SeedingPhysicalIdentity)
        {
            await BlockAsync(seed, SeedReleaseReasons.SeedingUnavailable,
                "The seeding copy is not where the import found it; nothing was removed.").ConfigureAwait(false);
            return;
        }

        if (await database.RetentionOperations.AnyAsync(operation => operation.PhysicalIdentity == seed.SeedingPhysicalIdentity &&
                (operation.State == RetentionOperationStates.Prepared || operation.State == RetentionOperationStates.Unlinked),
                cancellationToken).ConfigureAwait(false))
        {
            await WaitAsync(seed, SeedReleaseReasons.RetentionOpen).ConfigureAwait(false);
            return;
        }

        var libraryLinkPresent = files.TryInspect(seed.LibraryPath, out var libraryFile) &&
            libraryFile.PhysicalIdentity == seed.SeedingPhysicalIdentity;
        if (!libraryLinkPresent && await ReclaimedByRetentionAsync(seed, cancellationToken).ConfigureAwait(false) is null)
        {
            await BlockAsync(seed, SeedReleaseReasons.LibraryLinkUnexpected,
                "The library file is gone but no retention operation removed it; nothing was removed.").ConfigureAwait(false);
            return;
        }

        seed.State = SeedReleaseStates.Removing;
        seed.Reason = null;
        seed.Error = null;
        seed.HardlinkCountBefore = seeding.HardlinkCount;
        seed.RemovingAt = seed.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} removes torrent {Hash} after its goal", seed.Id, seed.InfoHash);
        await RemoveFromClientAsync(seed, client, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveFromClientAsync(SeedReleaseOperation seed, AcquisitionDownloadClient client, CancellationToken cancellationToken)
    {
        if (drivers.Get(client.Kind) is not { } driver) return;
        try
        {
            var connection = await configuration.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            await driver.RemoveAsync(connection, seed.InfoHash, deleteData: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or TaskCanceledException)
        {
            // The removal outcome is unknown; the next tick reads the client and continues from `removing`.
            seed.Error = ImportService.Bound("The client did not confirm the removal: " + error.GetType().Name);
            seed.UpdatedAt = Now;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Finishes a removal found in progress, including after a restart between the request and the inspection.</summary>
    private async Task ContinueRemovalAsync(SeedReleaseOperation seed, ClientTorrentStatus? torrent, CancellationToken cancellationToken)
    {
        await using var lease = await retentionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (torrent is not null)
        {
            var client = await database.AcquisitionDownloadClients.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == seed.DownloadClientId, cancellationToken).ConfigureAwait(false);
            var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == seed.GrabId, cancellationToken)
                .ConfigureAwait(false);
            if (client is not null && grab is not null && torrent.Labels.Contains(GrabService.OwnerLabel(grab.Id), StringComparer.Ordinal))
                await RemoveFromClientAsync(seed, client, cancellationToken).ConfigureAwait(false);
            return;
        }

        var presence = files.Probe(seed.SeedingPath);
        if (presence == PathPresence.Unknown) return;
        if (presence == PathPresence.Present)
        {
            if (seed.RemovingAt is { } started && Now - started > RemovalGrace)
                await BlockAsync(seed, SeedReleaseReasons.SeedingSurvived,
                    "The client removed the torrent but its file is still on disk.").ConfigureAwait(false);
            return;
        }

        // The seeding path is gone. Hardlink counts decide what was actually freed (P3.T3).
        long? released;
        Guid? credited = null;
        string summary;
        var libraryLinkPresent = files.TryInspect(seed.LibraryPath, out var libraryFile) &&
            libraryFile.PhysicalIdentity == seed.SeedingPhysicalIdentity;
        if (libraryLinkPresent)
        {
            released = 0;
            summary = "Released the seeding copy after its goal; the library file keeps the data (0 B freed).";
        }
        else
        {
            var reclaim = await ReclaimedByRetentionAsync(seed, cancellationToken).ConfigureAwait(false);
            credited = reclaim?.Id;
            released = seed.HardlinkCountBefore == 1 ? seed.SourceLogicalBytes : seed.HardlinkCountBefore > 1 ? 0 : null;
            summary = released is > 0
                ? $"Released the seeding copy after its goal. Freed {FormatBytes(released.Value)} previously reported as 0 B."
                : "Released the seeding copy after its goal.";
        }

        seed.RemovedAt = Now;
        await FinishAsync(seed, SeedReleaseReasons.Released, released, credited, "seeding_released", summary).ConfigureAwait(false);
    }

    private async Task<RetentionOperation?> ReclaimedByRetentionAsync(SeedReleaseOperation seed, CancellationToken cancellationToken) =>
        await database.RetentionOperations.AsNoTracking()
            .Where(operation => operation.State == RetentionOperationStates.Completed &&
                (operation.PhysicalIdentity == seed.SeedingPhysicalIdentity || operation.MediaPath == seed.LibraryPath))
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

        if (seed.EntryId is { } entryId && await database.Entries.AnyAsync(entry => entry.Id == entryId).ConfigureAwait(false) &&
            !await database.History.AnyAsync(history => history.Id == seed.Id).ConfigureAwait(false))
            database.History.Add(new HistoryRecord
            {
                Id = seed.Id, EntryId = entryId, EventType = eventType, CreatedAt = now, Summary = ImportService.Bound(summary),
                Data = JsonSerializer.Serialize(new
                {
                    seedReleaseId = seed.Id, seed.ImportOperationId, seed.EpisodeId, logicalBytes = seed.SourceLogicalBytes,
                    physicalBytesReleased = released, creditedRetentionOperationId = credited, observedRatio = seed.ObservedRatio,
                    observedSeedingSeconds = seed.ObservedSeedingSeconds, goalRatio = seed.GoalRatio, goalSeconds = seed.GoalSeconds
                })
            });
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Seed release {Seed} finished: {Reason}", seed.Id, reason);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
