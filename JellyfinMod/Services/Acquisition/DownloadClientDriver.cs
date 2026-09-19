namespace JellyfinMod.Services.Acquisition;

/// <summary>A configured client resolved for one call, including its secret. Never logged or serialized.</summary>
public sealed record DownloadClientConnection(
    Uri Endpoint,
    string Username,
    string? Password,
    string Label,
    string DownloadDirectory);

/// <summary>What the client reports about itself; no torrent is added to learn it.</summary>
public sealed record DownloadClientProbe(string Version, string ApiVersion);

/// <summary>One torrent add, owned by one grab operation.</summary>
public sealed record DownloadSubmission(
    Guid OperationId,
    string InfoHash,
    byte[]? Metainfo,
    string? MagnetUri,
    IReadOnlyList<string> Labels,
    string DownloadDirectory);

/// <summary>A torrent observed in the client by its infohash.</summary>
public sealed record DownloadClientTorrent(
    string InfoHash,
    string? DownloadDirectory,
    IReadOnlyList<string> Labels,
    bool SeedLimitsUnlimited);

/// <summary>One file inside a torrent as the client reports it (P5.I1).</summary>
/// <param name="Name">The path relative to the torrent's download directory, with forward slashes.</param>
/// <param name="Length">The file's full length.</param>
/// <param name="BytesCompleted">The bytes the client holds.</param>
/// <param name="Wanted">Whether the client downloads this file.</param>
public sealed record ClientTorrentFile(string Name, long Length, long BytesCompleted, bool Wanted);

/// <summary>A full status read of one torrent, used by the import monitor and the seed release (P5.I1/I3/I6).</summary>
/// <remarks>Built from one client read per monitor tick; it never carries credentials.</remarks>
public sealed record ClientTorrentStatus(
    string InfoHash,
    string Name,
    string? DownloadDirectory,
    IReadOnlyList<string> Labels,
    double PercentDone,
    long SizeWhenDone,
    long LeftUntilDone,
    long RateDownload,
    long? EtaSeconds,
    string Status,
    bool IsFinished,
    double UploadRatio,
    long SecondsSeeding,
    double? RatioLimit,
    long? IdleLimitSeconds,
    bool HasClientError,
    IReadOnlyList<ClientTorrentFile> Files)
{
    /// <summary>Gets a value indicating whether every wanted byte is downloaded.</summary>
    public bool Complete => LeftUntilDone == 0 && PercentDone >= 1 && Files.Where(file => file.Wanted)
        .All(file => file.BytesCompleted >= file.Length);
}

/// <summary>How a submission ended from the client's point of view.</summary>
public enum SubmissionResult
{
    /// <summary>The client created a new torrent for this submission.</summary>
    Added,

    /// <summary>The client already held a torrent with this hash; it is not this operation's.</summary>
    Duplicate
}

/// <summary>
/// A failure that proves the client did not take the torrent, such as refused authentication or a refused
/// connection. Anything else during submission is treated as uncertain.
/// </summary>
public sealed class DownloadClientRejectedException(string code, string message) : Exception(message)
{
    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; } = code;
}

/// <summary>A failure to read the client; the answer is unknown, never "absent".</summary>
public sealed class DownloadClientUnavailableException(string code, string message) : Exception(message)
{
    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The internal acquisition-engine boundary to one kind of download client (P4.A5). Phase 4 ships the
/// Transmission RPC driver (user decision 1); another client can be added by registering another driver.
/// </summary>
public interface IDownloadClientDriver
{
    /// <summary>Gets the stable kind stored on the configuration.</summary>
    string Kind { get; }

    /// <summary>Authenticates and reads version/capabilities without adding anything.</summary>
    Task<DownloadClientProbe> ProbeAsync(DownloadClientConnection connection, CancellationToken cancellationToken);

    /// <summary>Looks a torrent up by its normalized infohash; null means the client answered and does not hold it.</summary>
    Task<DownloadClientTorrent?> FindAsync(DownloadClientConnection connection, string infoHash, CancellationToken cancellationToken);

    /// <summary>Adds the torrent with the requested label and directory.</summary>
    /// <exception cref="DownloadClientRejectedException">The client definitely did not take the torrent.</exception>
    Task<SubmissionResult> SubmitAsync(DownloadClientConnection connection, DownloadSubmission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the seed settings to a torrent this operation owns, so no client stop condition can undercut a
    /// recorded seed requirement.
    /// </summary>
    Task ApplySeedSettingsAsync(DownloadClientConnection connection, string infoHash, CancellationToken cancellationToken);

    /// <summary>
    /// Reads progress, files, ratio and seeding time for the given torrents in one call. Torrents the client does not
    /// hold are absent from the result.
    /// </summary>
    /// <exception cref="DownloadClientUnavailableException">The client could not answer; absence is unknown.</exception>
    /// <exception cref="DownloadClientRejectedException">The client refused the credentials or the connection.</exception>
    Task<IReadOnlyList<ClientTorrentStatus>> GetStatusAsync(DownloadClientConnection connection, IReadOnlyCollection<string> infoHashes,
        CancellationToken cancellationToken);

    /// <summary>Removes one torrent, with its data when asked. Callers prove ownership first.</summary>
    Task RemoveAsync(DownloadClientConnection connection, string infoHash, bool deleteData, CancellationToken cancellationToken);
}

/// <summary>Resolves registered drivers by kind.</summary>
public sealed class DownloadClientDrivers(IEnumerable<IDownloadClientDriver> drivers)
{
    private readonly IReadOnlyDictionary<string, IDownloadClientDriver> _drivers =
        drivers.ToDictionary(driver => driver.Kind, StringComparer.Ordinal);

    /// <summary>Gets the registered kinds.</summary>
    public IReadOnlyCollection<string> Kinds => _drivers.Keys.ToArray();

    /// <summary>Returns the driver for a kind, or null.</summary>
    public IDownloadClientDriver? Get(string kind) => _drivers.GetValueOrDefault(kind);
}
