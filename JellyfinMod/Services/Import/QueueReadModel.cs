using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Import;

/// <summary>Acquisition state projected onto a file-less card or episode, without writing the entry (P5.I3).</summary>
/// <param name="State">The wire state: <c>grabbed</c> or <c>downloading</c>.</param>
/// <param name="Progress">Whole-number progress 0–100, or null before the client reported any.</param>
public sealed record ProjectedAcquisition(string State, int? Progress);

/// <summary>Builds queue rows and card projections from import operations and the shared client snapshot.</summary>
public static class QueueReadModel
{
    /// <summary>
    /// Projects open grabs and imports onto targets (entry id for movies, episode id for episodes). The projection uses
    /// the same cached client snapshot as the queue, so a card ring and its queue row agree within one poll.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ProjectedAcquisition>> ProjectAsync(ModDbContext database,
        ClientSnapshotCache? snapshots, IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken)
    {
        if (entryIds.Count == 0) return new Dictionary<Guid, ProjectedAcquisition>();
        var operations = await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId != null && entryIds.Contains(operation.EntryId.Value) &&
                ImportStates.Open.Contains(operation.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var grabs = await database.GrabOperations.AsNoTracking()
            .Where(grab => grab.EntryId != null && entryIds.Contains(grab.EntryId.Value) && grab.ActiveTarget != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, ProjectedAcquisition>();
        foreach (var grab in grabs) result[grab.EpisodeId ?? grab.EntryId!.Value] = new("grabbed", null);
        foreach (var operation in operations.OrderBy(operation => operation.CreatedAt))
        {
            var target = operation.EpisodeId ?? operation.EntryId!.Value;
            var progress = LiveProgress(operation, snapshots);
            result[target] = operation.State == ImportStates.Waiting && progress is null or <= 0
                ? new("grabbed", null)
                : new("downloading", progress is { } value ? (int)Math.Floor(value * 100) : 100);
        }

        return result;
    }

    /// <summary>Returns the freshest known progress: the shared snapshot when it is newer than the stored observation.</summary>
    public static double? LiveProgress(ImportOperation operation, ClientSnapshotCache? snapshots)
    {
        if (operation.State != ImportStates.Waiting && operation.State != ImportStates.Blocked)
            return operation.CompletedDownloadAt is not null ? 1 : operation.Progress;
        var snapshot = snapshots?.Latest(operation.DownloadClientId);
        if (snapshot is { Reachable: true } && snapshot.Find(operation.InfoHash) is { } torrent &&
            (operation.ObservedAt is null || snapshot.CheckedAt >= operation.ObservedAt))
            return Math.Round(Math.Clamp(torrent.PercentDone, 0, 1), 4);
        return operation.Progress;
    }

    /// <summary>Builds one queue row. Physical paths and client links appear only for administrators.</summary>
    public static QueueRowDto Row(ImportOperation operation, SeedReleaseOperation? seed, Entry? entry, Episode? episode,
        AcquisitionDownloadClient? client, ClientSnapshot? snapshot, AcquisitionSettings settings, bool administrator,
        bool libraryLinkPresent)
    {
        var torrent = snapshot is { Reachable: true } ? snapshot.Find(operation.InfoHash) : null;
        var fresh = torrent is not null && (operation.ObservedAt is null || snapshot!.CheckedAt >= operation.ObservedAt);
        var progress = fresh ? Math.Round(Math.Clamp(torrent!.PercentDone, 0, 1), 4) : operation.Progress;
        var state = RowState(operation, seed, snapshot, progress);
        var reason = state == "seeding" ? null : operation.Reason ?? (state == "unknown" ? ImportReasons.ClientUnreachable : null);
        return new QueueRowDto(operation.Id, operation.GrabId,
            entry is null ? null : new QueueEntryDto(entry.Id, entry.MediaType, entry.Title, entry.Year, entry.PosterPath,
                entry.JellyfinItemId, entry.TargetLibraryId),
            episode is null ? null : new QueueEpisodeDto(episode.Id, episode.SeasonNumber, episode.EpisodeNumber, episode.Title),
            operation.ReleaseTitle, state, operation.State, reason,
            reason is null ? null : ImportMessages.For(reason),
            progress,
            fresh ? torrent!.SizeWhenDone : operation.SizeBytes,
            fresh ? Math.Max(0, torrent!.SizeWhenDone - torrent.LeftUntilDone) : operation.DownloadedBytes,
            fresh ? torrent!.RateDownload : operation.DownloadRateBytes,
            fresh ? torrent!.EtaSeconds : operation.EtaSeconds,
            Utc(operation.StalledSince), fresh ? Utc(snapshot!.CheckedAt) : Utc(operation.ObservedAt), operation.VersionLabel,
            operation.Intent, Utc(operation.CreatedAt)!.Value, Utc(operation.UpdatedAt)!.Value,
            administrator && client is not null ? new QueueClientDto(client.Id, client.Name, client.OpenUrl) : null,
            seed is null ? null : Seeding(seed, torrent, settings, libraryLinkPresent),
            administrator ? Admin(operation) : null);
    }

    /// <summary>The row state shown to users (P5 API contract).</summary>
    public static string RowState(ImportOperation operation, SeedReleaseOperation? seed, ClientSnapshot? snapshot, double? progress)
    {
        if (operation.State == ImportStates.Completed) return "seeding";
        if (operation.State is ImportStates.Waiting)
        {
            if (snapshot is { Reachable: false }) return "unknown";
            // Nothing downloaded yet: queued, whether or not the client has reported 0 % already.
            if (progress is null or <= 0) return "queued";
            return operation.StalledSince is not null ? "stalled" : "downloading";
        }

        return operation.State switch
        {
            ImportStates.Identifying => "identifying",
            ImportStates.Linking or ImportStates.Linked => "linking",
            ImportStates.Scanning => "scanning",
            ImportStates.Blocked => "blocked",
            ImportStates.Failed => "failed",
            _ => operation.State
        };
    }

    /// <summary>The seeding summary; goals come from the release when it has observed them, else from the snapshot.</summary>
    public static QueueSeedingDto Seeding(SeedReleaseOperation seed, ClientTorrentStatus? torrent, AcquisitionSettings settings,
        bool libraryLinkPresent)
    {
        var goal = torrent is null ? null : SeedReleaseService.Evaluate(torrent, seed.IndexerRatio, seed.IndexerSeconds, settings);
        return new QueueSeedingDto(seed.Id, seed.State, seed.Reason, torrent?.UploadRatio ?? seed.ObservedRatio,
            goal?.Ratio ?? seed.GoalRatio, torrent?.SecondsSeeding ?? seed.ObservedSeedingSeconds, goal?.Seconds ?? seed.GoalSeconds,
            goal?.WaitingFor ?? [], Utc(seed.GoalMetAt), libraryLinkPresent, settings.SeedReleaseEnabled);
    }

    /// <summary>The administrator-only detail.</summary>
    public static QueueAdminDetailDto Admin(ImportOperation operation) => new(operation.SourceLocalPath, operation.SourceClientPath,
        operation.DestinationPath, operation.SourcePhysicalIdentity, operation.DestinationPhysicalIdentity, operation.HardlinkCountAfter,
        operation.Error, operation.InfoHash);

    /// <summary>The public view of an operation.</summary>
    public static ImportOperationDto Operation(ImportOperation operation, bool administrator) => new(operation.Id, operation.GrabId,
        operation.EntryId, operation.EpisodeId, operation.State, operation.Reason,
        operation.Reason is null ? null : ImportMessages.For(operation.Reason), operation.ReleaseTitle, operation.VersionLabel,
        operation.Progress, operation.NativeItemId, operation.ScanAttempts, Utc(operation.CreatedAt)!.Value,
        Utc(operation.UpdatedAt)!.Value, Utc(operation.CompletedDownloadAt), Utc(operation.LinkedAt), Utc(operation.ScanRequestedAt),
        Utc(operation.BoundAt), Utc(operation.CompletedAt), operation.RetryOfId, administrator ? Admin(operation) : null);

    /// <summary>Marks a stored time as UTC.</summary>
    public static DateTime? Utc(DateTime? value) => value is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;
}
