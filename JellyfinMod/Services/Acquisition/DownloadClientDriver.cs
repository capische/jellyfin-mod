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
