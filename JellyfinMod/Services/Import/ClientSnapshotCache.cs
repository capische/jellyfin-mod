using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Import;

/// <summary>One read of the download client for every torrent the plugin tracks (P5.I3).</summary>
/// <param name="ClientId">The client that was read.</param>
/// <param name="Reachable">Whether the client answered.</param>
/// <param name="CheckedAt">When the read finished.</param>
/// <param name="Reason">A stable reason when the client did not answer.</param>
/// <param name="Torrents">The torrents the client holds, keyed by infohash. Absent means the client answered without it.</param>
public sealed record ClientSnapshot(
    Guid ClientId,
    bool Reachable,
    DateTime CheckedAt,
    string? Reason,
    IReadOnlyDictionary<string, ClientTorrentStatus> Torrents)
{
    /// <summary>Returns the torrent, or null when the client answered without it or did not answer.</summary>
    public ClientTorrentStatus? Find(string infoHash) => Torrents.GetValueOrDefault(infoHash);
}

/// <summary>
/// Shares one client read between the import monitor, the queue API and the card projection, so polling the queue from
/// several sessions costs one client call per freshness window, not one per request (P5.I3/I7).
/// </summary>
public sealed class ClientSnapshotCache(TimeProvider time, ILogger<ClientSnapshotCache> logger)
{
    /// <summary>How fresh a snapshot must be for the queue and the projection.</summary>
    public static readonly TimeSpan QueueFreshness = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ClientSnapshot> _latest = [];
    private long _reads;

    /// <summary>Gets how many times the client was actually read since start, for log evidence.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>Gets the latest snapshot for a client without reading it.</summary>
    public ClientSnapshot? Latest(Guid clientId)
    {
        lock (_latest) return _latest.GetValueOrDefault(clientId);
    }

    /// <summary>
    /// Returns a snapshot no older than <paramref name="maxAge"/> that covers every requested hash, reading the client at
    /// most once for concurrent callers.
    /// </summary>
    public async Task<ClientSnapshot> GetAsync(Guid clientId, IReadOnlyCollection<string> hashes, TimeSpan maxAge,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<ClientSnapshot>> read, CancellationToken cancellationToken)
    {
        if (Fresh(clientId, hashes, maxAge) is { } cached) return cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(clientId, hashes, maxAge) is { } concurrent) return concurrent;
            var snapshot = await read(hashes, cancellationToken).ConfigureAwait(false);
            var reads = Interlocked.Increment(ref _reads);
            logger.LogDebug("Read download client {Client}: {Count} torrents, reachable {Reachable} (read {Reads})",
                clientId, snapshot.Torrents.Count, snapshot.Reachable, reads);
            lock (_latest)
            {
                // An unreachable client keeps the last known torrents so rows show "stale since", never 0 %.
                if (!snapshot.Reachable && _latest.TryGetValue(clientId, out var previous))
                    snapshot = snapshot with { Torrents = previous.Torrents };
                _latest[clientId] = snapshot with { Torrents = new Dictionary<string, ClientTorrentStatus>(snapshot.Torrents) };
                Covered[clientId] = hashes.ToHashSet(StringComparer.Ordinal);
            }

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<Guid, HashSet<string>> Covered { get; } = [];

    private ClientSnapshot? Fresh(Guid clientId, IReadOnlyCollection<string> hashes, TimeSpan maxAge)
    {
        lock (_latest)
        {
            if (!_latest.TryGetValue(clientId, out var snapshot) || !Covered.TryGetValue(clientId, out var covered)) return null;
            if (time.GetUtcNow().UtcDateTime - snapshot.CheckedAt > maxAge) return null;
            return hashes.All(covered.Contains) ? snapshot : null;
        }
    }
}

/// <summary>Reads the configured client once for a set of hashes, turning failures into an unreachable snapshot.</summary>
public sealed class ClientSnapshotReader(
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    TimeProvider time,
    ILogger<ClientSnapshotReader> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Reads one client.</summary>
    public async Task<ClientSnapshot> ReadAsync(AcquisitionDownloadClient? client, Guid clientId, IReadOnlyCollection<string> hashes,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (client is null) return new(clientId, false, now, ImportReasons.ClientMissing, new Dictionary<string, ClientTorrentStatus>());
        if (drivers.Get(client.Kind) is not { } driver)
            return new(clientId, false, now, "download_client_driver_missing", new Dictionary<string, ClientTorrentStatus>());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            var connection = await configuration.ConnectAsync(client, timeout.Token).ConfigureAwait(false);
            var torrents = await driver.GetStatusAsync(connection, hashes, timeout.Token).ConfigureAwait(false);
            return new(clientId, true, time.GetUtcNow().UtcDateTime, null,
                torrents.ToDictionary(torrent => torrent.InfoHash, StringComparer.Ordinal));
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var code = error switch
            {
                DownloadClientRejectedException rejected => rejected.Code,
                DownloadClientUnavailableException unavailable => unavailable.Code,
                _ => ImportReasons.ClientUnreachable
            };
            logger.LogInformation("Download client {Client} could not be read: {Code}", client.Name, code);
            return new(clientId, false, time.GetUtcNow().UtcDateTime, ImportReasons.ClientUnreachable,
                new Dictionary<string, ClientTorrentStatus>());
        }
    }

    /// <summary>Returns the infohashes of every open import and seed release of one client.</summary>
    public static async Task<IReadOnlyCollection<string>> TrackedHashesAsync(ModDbContext database, Guid clientId,
        CancellationToken cancellationToken)
    {
        var imports = await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.DownloadClientId == clientId && ImportStates.Open.Contains(operation.State))
            .Select(operation => operation.InfoHash).ToListAsync(cancellationToken).ConfigureAwait(false);
        var seeds = await database.SeedReleaseOperations.AsNoTracking()
            .Where(operation => operation.DownloadClientId == clientId && SeedReleaseStates.Open.Contains(operation.State))
            .Select(operation => operation.InfoHash).ToListAsync(cancellationToken).ConfigureAwait(false);
        return imports.Concat(seeds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
