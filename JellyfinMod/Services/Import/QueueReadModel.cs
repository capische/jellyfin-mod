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
        // A pack shows as grabbed on each episode it claimed, through its claims (season and series packs, 2026-10-08).
        var packIds = grabs.Where(grab => ReleaseScopes.IsPack(grab.Scope)).Select(grab => grab.Id).ToArray();
        if (packIds.Length > 0)
            foreach (var episodeId in await database.GrabClaims.AsNoTracking()
                         .Where(claim => packIds.Contains(claim.GrabId) && claim.ActiveKey != null).Select(claim => claim.EpisodeId)
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
                result[episodeId] = new("grabbed", null);
        foreach (var grab in grabs.Where(grab => !ReleaseScopes.IsPack(grab.Scope))) result[grab.EpisodeId ?? grab.EntryId!.Value] = new("grabbed", null);
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

    /// <summary>
    /// Returns the freshest known progress: the shared snapshot when it is newer than the stored observation. An import whose
    /// own file is known (a pack's episode) shows that file's progress, not the whole torrent's (next-up priority, P5.FU3).
    /// </summary>
    public static double? LiveProgress(ImportOperation operation, ClientSnapshotCache? snapshots)
    {
        if (operation.State != ImportStates.Waiting && operation.State != ImportStates.Blocked)
            return operation.CompletedDownloadAt is not null ? 1 : operation.Progress;
        var snapshot = snapshots?.Latest(operation.DownloadClientId);
        if (snapshot is { Reachable: true } && snapshot.Find(operation.InfoHash) is { } torrent &&
            (operation.ObservedAt is null || snapshot.CheckedAt >= operation.ObservedAt))
            return FileProgress(torrent, operation.SourceClientPath) ?? Math.Round(Math.Clamp(torrent.PercentDone, 0, 1), 4);
        return operation.Progress;
    }

    /// <summary>One file's own progress, 0–1, by its client path; null when the torrent does not list it.</summary>
    public static double? FileProgress(ClientTorrentStatus torrent, string? clientPath) =>
        ImportPaths.FileIndex(torrent, clientPath) is >= 0 and var index && torrent.Files[index].Length > 0
            ? Math.Round(Math.Clamp((double)torrent.Files[index].BytesCompleted / torrent.Files[index].Length, 0, 1), 4)
            : null;

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

    /// <summary>
    /// The pack detail of a pack's single queue row: its label and each claimed episode's newest import, with that episode's own
    /// file progress from the client snapshot.
    /// </summary>
    public static QueuePackDto Pack(GrabOperation grab, IReadOnlyList<Episode> claimed, IReadOnlyList<ImportOperation> children,
        ClientSnapshot? snapshot, IReadOnlySet<Guid>? held = null)
    {
        var torrent = snapshot is { Reachable: true } && grab.InfoHash is { } hash ? snapshot.Find(hash) : null;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<ParsedRelease>(grab.ParsedJson);
        var seasons = parsed?.PackSeasons() ?? [];
        var label = seasons.Count == 1
            ? $"Season {seasons[0]} pack · {claimed.Count} episode{(claimed.Count == 1 ? string.Empty : "s")}"
            : claimed.Count == 0 ? GrabService.PackLabel(parsed)
            : $"{GrabService.PackLabel(parsed)} · S{claimed.Min(value => value.SeasonNumber):00}–S{claimed.Max(value => value.SeasonNumber):00}";
        return new QueuePackDto(grab.Scope, label, grab.Mode, claimed.Select(episode =>
        {
            var import = children.Where(child => child.GrabId == grab.Id && child.EpisodeId == episode.Id)
                .OrderByDescending(child => child.CreatedAt).FirstOrDefault();
            var progress = import is null ? null
                : import.State is not (ImportStates.Waiting or ImportStates.Blocked) ? import.CompletedDownloadAt is not null ? 1 : null
                : torrent is null ? null : FileProgress(torrent, import.SourceClientPath);
            var file = torrent is null || import is null ? -1 : ImportPaths.FileIndex(torrent, import.SourceClientPath);
            return new QueuePackEpisodeDto(episode.Id, episode.SeasonNumber, episode.EpisodeNumber, import?.Id, import?.State, import?.Reason,
                progress, file >= 0 ? torrent!.Files[file].Length : null, held?.Contains(episode.Id) == true);
        }).ToArray());
    }

    /// <summary>
    /// A grab that is held, being sent or waiting for the client to confirm it, before its import exists (P4.FU10): a queued
    /// row from the moment the grab is made, so every page, tab and device shows it as queued, never as Not Downloaded. Its id
    /// is the grab's; <c>importState</c> is <c>held</c> while it can still be cancelled and <c>submitting</c> after. Once the
    /// client accepts it, the import row (created in the same commit) takes its place.
    /// </summary>
    public static QueueRowDto Held(GrabOperation grab, Entry? entry, Episode? episode, IReadOnlyList<Episode> claimed,
        AcquisitionDownloadClient? client, bool administrator, IReadOnlySet<Guid>? held = null) => new QueueRowDto(grab.Id, grab.Id,
            entry is null ? null : new QueueEntryDto(entry.Id, entry.MediaType, entry.Title, entry.Year, entry.PosterPath,
                entry.JellyfinItemId, entry.TargetLibraryId),
            episode is null ? null : new QueueEpisodeDto(episode.Id, episode.SeasonNumber, episode.EpisodeNumber, episode.Title),
            grab.RawTitle, "queued", grab.State == GrabStates.Pending ? "held" : "submitting", null, null, null, grab.Size, null, null,
            null, null, null, null, grab.Intent, Utc(grab.CreatedAt)!.Value, Utc(grab.UpdatedAt)!.Value,
            administrator && client is not null ? new QueueClientDto(client.Id, client.Name, client.OpenUrl) : null, null,
            administrator && grab.InfoHash is { } hash ? new QueueAdminDetailDto(null, null, null, null, null, null, null, hash) : null)
    {
        Pack = ReleaseScopes.IsPack(grab.Scope) ? Pack(grab, claimed, [], null, held) : null,
        Parsed = Parsed(grab),
        IndexerName = grab.IndexerName,
        Mode = grab.Mode,
        Replaces = Replaces(grab)
    };

    /// <summary>
    /// What a movie's or an episode's Replace removes, from the grab's fixed targets (the upgrade status, 2026-10-28): each file's
    /// binding and the resolution its name gives. Null for any other grab and for a pack, whose targets are fixed per episode later.
    /// </summary>
    public static IReadOnlyList<QueueReplaceTargetDto>? Replaces(GrabOperation grab) =>
        grab.Mode != GrabModes.Replace || ReleaseScopes.IsPack(grab.Scope) ? null
            : PackReplaceService.ReadTargets(grab.ReplaceTargets)
                .Select(target => new QueueReplaceTargetDto(target.BindingId, Automation.VersionQuality.Parse(target.Path).Resolution))
                .ToArray();

    /// <summary>The grab's release name as parsed when it was grabbed (P1.FU20); null for a grab that kept none.</summary>
    public static ParsedRelease? Parsed(GrabOperation grab)
    {
        if (string.IsNullOrEmpty(grab.ParsedJson) || grab.ParsedJson == "{}") return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ParsedRelease>(grab.ParsedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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
