using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JellyfinMod.Data;
using JellyfinMod.Services.Import;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>
/// Downloads the first missing episode of a series and each viewer's next one first (next-up priority, P4.FU3; design
/// <c>design/next-up-priority-download.md</c>). On every import-monitor tick, per series with open downloads, the earliest
/// episode still downloading and, for every user who may see the series and has watched part of it, the first episode still
/// downloading after their furthest played one, are the targets: their files in a pack are set to high priority, a
/// single-episode torrent gets the torrent's own high priority and the top of the client's queue. Everything else the client
/// reports high goes back to normal. Only torrents this plugin added (the grab's owner label) are touched, only a difference
/// is written, and a client error is logged and tried again on the next tick; it never holds up an import.
/// </summary>
public sealed class DownloadOrderService(
    ModDbContext database,
    ImportService imports,
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    LibraryAccess access,
    IUserManager users,
    ILibraryManager library,
    ILogger<DownloadOrderService> logger)
{
    /// <summary>Applies the order to every open download once. Never throws for a client or library failure.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplyCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "The download order could not be applied; it is tried again on the next tick");
        }
    }

    /// <summary>One episode still downloading, or one torrent of a series whose priority the plugin may need to reset.</summary>
    private sealed record Download(Guid EntryId, Guid ClientId, string Hash, bool Pack, int Season, int Episode, int? FileIndex,
        bool Incomplete);

    private async Task ApplyCoreAsync(CancellationToken cancellationToken)
    {
        var waiting = await (from operation in database.ImportOperations.AsNoTracking()
                where operation.State == ImportStates.Waiting && operation.EntryId != null && operation.EpisodeId != null
                join episode in database.Episodes.AsNoTracking() on operation.EpisodeId equals episode.Id
                join grab in database.GrabOperations.AsNoTracking() on operation.GrabId equals grab.Id
                where episode.SeasonNumber > 0
                select new
                {
                    EntryId = operation.EntryId!.Value, operation.GrabId, operation.InfoHash, operation.DownloadClientId,
                    operation.SourceClientPath, episode.SeasonNumber, episode.EpisodeNumber, grab.Scope
                })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (waiting.Count == 0) return;

        var snapshots = new Dictionary<Guid, ClientSnapshot>();
        foreach (var clientId in waiting.Select(row => row.DownloadClientId).Distinct())
            snapshots[clientId] = await imports.SnapshotAsync(clientId, ClientSnapshotCache.QueueFreshness, cancellationToken)
                .ConfigureAwait(false);

        var downloads = new List<Download>();
        foreach (var row in waiting)
        {
            if (snapshots[row.DownloadClientId] is not { Reachable: true } snapshot || snapshot.Find(row.InfoHash) is not { } torrent ||
                torrent.Complete || !torrent.Labels.Contains(GrabService.OwnerLabel(row.GrabId), StringComparer.Ordinal))
                continue;
            var pack = ReleaseScopes.IsPack(row.Scope);
            if (!pack)
            {
                downloads.Add(new(row.EntryId, row.DownloadClientId, row.InfoHash, false, row.SeasonNumber, row.EpisodeNumber, null, true));
                continue;
            }

            // A pack episode counts once its file is known (P5.FU1 maps it while the torrent downloads).
            var index = ImportPaths.FileIndex(torrent, row.SourceClientPath);
            var file = index >= 0 ? torrent.Files[index] : null;
            downloads.Add(new(row.EntryId, row.DownloadClientId, row.InfoHash, true, row.SeasonNumber, row.EpisodeNumber,
                index >= 0 ? index : null, file is { Wanted: true } && file.BytesCompleted < file.Length));
        }

        foreach (var series in downloads.GroupBy(download => download.EntryId))
        {
            var candidates = series.Where(download => download.Incomplete && (!download.Pack || download.FileIndex is not null))
                .OrderBy(download => download.Season).ThenBy(download => download.Episode).ToList();
            var targets = new HashSet<(int Season, int Episode)>();
            // A single episode downloading has nothing to go ahead of.
            if (candidates.Select(download => (download.Season, download.Episode)).Distinct().Count() >= 2)
            {
                targets.Add((candidates[0].Season, candidates[0].Episode));
                foreach (var (season, episode) in await ViewerPositionsAsync(series.Key, cancellationToken).ConfigureAwait(false))
                {
                    var next = candidates.FirstOrDefault(download => download.Season > season ||
                        download.Season == season && download.Episode > episode);
                    if (next is not null) targets.Add((next.Season, next.Episode));
                }
            }

            foreach (var torrent in series.GroupBy(download => (download.ClientId, download.Hash)))
                await OrderAsync(snapshots[torrent.Key.ClientId].Find(torrent.Key.Hash)!, torrent.Key.ClientId, torrent.ToList(),
                    candidates, targets, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the difference between what the client reports and the wanted order for one torrent.</summary>
    private async Task OrderAsync(ClientTorrentStatus torrent, Guid clientId, IReadOnlyList<Download> downloads,
        IReadOnlyList<Download> candidates, IReadOnlySet<(int Season, int Episode)> targets, CancellationToken cancellationToken)
    {
        bool Target(Download download) => candidates.Contains(download) && targets.Contains((download.Season, download.Episode));
        IReadOnlyCollection<int> high = [];
        IReadOnlyCollection<int> normal = [];
        int? bandwidth = null;
        var moveTop = false;
        if (downloads[0].Pack)
        {
            var wanted = downloads.Where(Target).Select(download => download.FileIndex!.Value).ToHashSet();
            high = wanted.Where(index => torrent.Files[index].Priority != 1).ToArray();
            normal = Enumerable.Range(0, torrent.Files.Count).Where(index => torrent.Files[index].Priority == 1 && !wanted.Contains(index))
                .ToArray();
        }
        else
        {
            var target = downloads.Any(Target);
            var priority = target ? 1 : 0;
            if (torrent.BandwidthPriority != priority) bandwidth = priority;
            moveTop = target && torrent.Status == "download_wait";
        }

        if (high.Count == 0 && normal.Count == 0 && bandwidth is null && !moveTop) return;
        try
        {
            var client = await database.AcquisitionDownloadClients.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == clientId, cancellationToken).ConfigureAwait(false);
            if (client is null || drivers.Get(client.Kind) is not { } driver) return;
            var connection = await configuration.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            await driver.SetDownloadOrderAsync(connection, torrent.InfoHash, high, normal, bandwidth, moveTop, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Download order for torrent {Hash}: files {High} high, files {Normal} normal, torrent priority {Bandwidth}, moved to the top {MoveTop}",
                torrent.InfoHash, string.Join(',', high.Order()), string.Join(',', normal.Order()), bandwidth?.ToString() ?? "unchanged", moveTop);
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or HttpRequestException)
        {
            logger.LogInformation("The download order for torrent {Hash} was not applied: {Error}; it is tried again on the next tick",
                torrent.InfoHash, error.Message);
        }
    }

    /// <summary>
    /// The furthest episode each user who may see the series has played (the user's decision 1, 2026-10-12): everyone with
    /// access and history, read from Jellyfin's played state. A series not in the library yet has no history.
    /// </summary>
    private async Task<IReadOnlyList<(int Season, int Episode)>> ViewerPositionsAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry?.JellyfinItemId is not { } seriesId ||
            library.GetItemById(seriesId) is not MediaBrowser.Controller.Entities.TV.Series series)
            return [];
        var positions = new List<(int Season, int Episode)>();
        foreach (var user in users.GetUsers().Where(Active))
            if (access.CanRead(user, entry) && access.LastPlayedEpisode(user, series) is { } position)
                positions.Add(position);
        return positions;
    }

    private static bool Active(User user) =>
        !user.Permissions.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value);
}
