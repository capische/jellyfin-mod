using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Common.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Reads Transmission 4.x torrent and seed-goal state without issuing mutations.</summary>
public sealed class TransmissionSeedClient(
    IHttpClientFactory httpClientFactory,
    Func<PluginConfiguration> configuration,
    UnixFileInspector files,
    ILogger<TransmissionSeedClient> logger,
    AcquisitionSecretStore? secrets = null)
{
    private static readonly string[] SessionFields =
        ["version", "seedRatioLimited", "seedRatioLimit", "idle-seeding-limit-enabled", "idle-seeding-limit",
            "incomplete-dir", "incomplete-dir-enabled", "rename-partial-files"];
    private static readonly string[] TorrentFields =
    [
        "id", "hashString", "downloadDir", "files", "fileStats", "leftUntilDone", "percentDone", "status", "uploadRatio",
        "secondsSeeding", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "etaIdle", "isFinished", "labels"
    ];

    /// <summary>Gets one complete, read-only snapshot or an unavailable result that blocks deletion.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="database">
    /// The plugin database. When given, the path mappings of the acquisition client that uses this same RPC endpoint
    /// translate the daemon's paths to this server's (P4.A6 (b)); without it, paths are read as the daemon reports them.
    /// </param>
    public async Task<TransmissionSeedSnapshot> GetSnapshotAsync(CancellationToken cancellationToken, ModDbContext? database = null)
    {
        var settings = configuration();
        if (!Uri.TryCreate(settings.TransmissionRpcUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
            return TransmissionSeedSnapshot.Unavailable("transmission_unconfigured");

        try
        {
            var context = database is null
                ? SeedContext.Empty
                : await SeedContext.LoadAsync(database, settings.TransmissionRpcUrl, cancellationToken).ConfigureAwait(false);
            // The saved password lives in the secret store (user decision 3); a value on the object is used as given.
            var password = settings.TransmissionPassword.Length > 0 || secrets is null
                ? settings.TransmissionPassword
                : await secrets.GetAsync(settings.TransmissionPasswordRef, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            using var client = httpClientFactory.CreateClient(NamedClient.Default);
            using var sessionResponse = await SendAsync(client, endpoint, "session-get",
                new { fields = SessionFields }, settings, password, cancellationToken).ConfigureAwait(false);
            if (!sessionResponse.IsSuccessStatusCode)
                return TransmissionSeedSnapshot.Unavailable("transmission_unreachable");
            using var sessionDocument = await JsonDocument.ParseAsync(
                await sessionResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!Success(sessionDocument.RootElement, out var sessionArguments))
                return TransmissionSeedSnapshot.Unavailable("transmission_invalid_response");

            using var torrentResponse = await SendAsync(client, endpoint, "torrent-get",
                new { fields = TorrentFields }, settings, password, cancellationToken).ConfigureAwait(false);
            if (!torrentResponse.IsSuccessStatusCode)
                return TransmissionSeedSnapshot.Unavailable("transmission_unreachable");
            using var torrentDocument = await JsonDocument.ParseAsync(
                await torrentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!Success(torrentDocument.RootElement, out var torrentArguments) ||
                !torrentArguments.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
                return TransmissionSeedSnapshot.Unavailable("transmission_invalid_response");

            var session = ParseSession(sessionArguments);
            var indexedFiles = new Dictionary<string, List<TransmissionFileProtection>>(StringComparer.Ordinal);
            var completeIndex = true;
            var unresolvedFiles = 0;
            foreach (var torrent in torrents.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseTorrent(torrent, session, out var protection, out var downloadDirectory,
                        out var torrentFiles))
                {
                    completeIndex = false;
                    continue;
                }

                var paths = context.PathsFor(torrent);
                foreach (var file in torrentFiles)
                {
                    if (!TryInspectTorrentFile(file, downloadDirectory, session, paths, out var observed))
                    {
                        // Unwanted or not-yet-started files have no data on disk to protect. Only a wanted
                        // file with downloaded data that cannot be found leaves the index incomplete (P3.T17).
                        if (!file.Wanted || file.BytesCompleted == 0) continue;
                        completeIndex = false;
                        unresolvedFiles++;
                        continue;
                    }

                    // A file only counts as complete when its whole torrent is complete as well.
                    var state = protection with { FileComplete = protection.FileComplete && file.BytesCompleted >= file.Length };
                    if (!indexedFiles.TryGetValue(observed.PhysicalIdentity, out var matches))
                        indexedFiles[observed.PhysicalIdentity] = matches = [];
                    matches.Add(state);
                }
            }

            return new TransmissionSeedSnapshot(true, completeIndex, null, indexedFiles, unresolvedFiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        {
            logger.LogWarning("Transmission seed snapshot is unavailable: {ErrorType}", error.GetType().Name);
            return TransmissionSeedSnapshot.Unavailable("transmission_unreachable");
        }
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri endpoint,
        string method,
        object arguments,
        PluginConfiguration settings,
        string password,
        CancellationToken cancellationToken) => TransmissionRpc.SendAsync(client, endpoint, method, arguments,
        settings.TransmissionUsername, password, cancellationToken);

    private static bool Success(JsonElement root, out JsonElement arguments) => TransmissionRpc.Success(root, out arguments);

    private static TransmissionSession ParseSession(JsonElement arguments) => new(
        arguments.TryGetProperty("seedRatioLimited", out var ratioEnabled) && ratioEnabled.GetBoolean(),
        arguments.TryGetProperty("seedRatioLimit", out var ratioLimit) ? ratioLimit.GetDouble() : 0,
        arguments.TryGetProperty("idle-seeding-limit-enabled", out var idleEnabled) && idleEnabled.GetBoolean(),
        arguments.TryGetProperty("idle-seeding-limit", out var idleLimit) ? idleLimit.GetInt32() : 0,
        arguments.TryGetProperty("incomplete-dir-enabled", out var incompleteEnabled) && incompleteEnabled.GetBoolean() &&
            arguments.TryGetProperty("incomplete-dir", out var incompleteDir) && !string.IsNullOrWhiteSpace(incompleteDir.GetString())
            ? Path.GetFullPath(incompleteDir.GetString()!) : null,
        arguments.TryGetProperty("rename-partial-files", out var renamePartial) && renamePartial.GetBoolean());

    /// <summary>
    /// Finds a torrent file where Transmission keeps it: the download directory or, while downloading,
    /// the incomplete directory, under its own name or with the partial-file suffix. Daemon paths pass through the
    /// client's path mappings; a path whose best mapping is unverified is never read, so the file stays unresolved.
    /// </summary>
    private bool TryInspectTorrentFile(TransmissionFile file, string downloadDirectory, TransmissionSession session,
        PathTranslator paths, out UnixFileSnapshot observed)
    {
        foreach (var directory in session.IncompleteDirectory is { } incomplete ? [downloadDirectory, incomplete] : new[] { downloadDirectory })
        {
            if (paths.Translate(Path.GetFullPath(file.Name, directory)) is not { } path) continue;
            if (files.TryInspect(path, out observed)) return true;
            if (session.RenamePartialFiles && files.TryInspect(path + ".part", out observed)) return true;
        }

        observed = default;
        return false;
    }

    /// <summary>Translates daemon paths to local ones for one torrent.</summary>
    private sealed class PathTranslator(AcquisitionDownloadClient? client, IReadOnlyList<DownloadClientPathMapping> mappings)
    {
        public static PathTranslator Identity { get; } = new(null, []);

        /// <summary>
        /// Returns the local path: mapped through a verified prefix, unchanged when no prefix covers it (the daemon shares
        /// this server's paths), or null when the best matching prefix is unverified.
        /// </summary>
        public string? Translate(string clientPath)
        {
            if (client is null) return clientPath;
            return ImportPaths.Resolve(clientPath, mappings, client, out var local) switch
            {
                PathMapOutcome.Mapped => local,
                PathMapOutcome.Unmatched => clientPath,
                _ => null
            };
        }
    }

    /// <summary>The plugin's own knowledge of the daemon this reader protects: its clients and their path mappings.</summary>
    private sealed class SeedContext
    {
        public static SeedContext Empty { get; } = new([], null);

        private readonly IReadOnlyList<(AcquisitionDownloadClient Client, PathTranslator Paths)> clients;
        private readonly PathTranslator primary;

        private SeedContext(IReadOnlyList<(AcquisitionDownloadClient Client, PathTranslator Paths)> clients, Guid? primaryId)
        {
            this.clients = clients;
            primary = clients.OrderBy(entry => entry.Client.Id == primaryId ? 0 : 1).ThenBy(entry => entry.Client.Id)
                .Select(entry => entry.Paths).FirstOrDefault() ?? PathTranslator.Identity;
        }

        /// <summary>Loads the Transmission acquisition clients configured for the same RPC endpoint.</summary>
        public static async Task<SeedContext> LoadAsync(ModDbContext database, string rpcUrl, CancellationToken cancellationToken)
        {
            var endpoint = AcquisitionConfiguration.NormalizeEndpoint(rpcUrl);
            var matching = (await database.AcquisitionDownloadClients.AsNoTracking()
                    .Where(client => client.Kind == TransmissionDriver.DriverKind).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(client => AcquisitionConfiguration.NormalizeEndpoint(client.BaseUrl) == endpoint).ToList();
            if (matching.Count == 0) return Empty;
            var ids = matching.Select(client => client.Id).ToArray();
            var mappings = await database.DownloadClientPathMappings.AsNoTracking().Where(mapping => ids.Contains(mapping.DownloadClientId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var selected = await database.AcquisitionSettings.AsNoTracking().Where(value => value.Id == AcquisitionSettings.SingletonId)
                .Select(value => value.DownloadClientId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return new SeedContext(matching.Select(client => (client, new PathTranslator(client,
                mappings.Where(mapping => mapping.DownloadClientId == client.Id).ToList()))).ToList(), selected);
        }

        /// <summary>
        /// Chooses the client whose mappings apply to a torrent: the one whose label the torrent carries, else the client
        /// acquisition uses, else the first configured for this endpoint.
        /// </summary>
        public PathTranslator PathsFor(JsonElement torrent)
        {
            if (clients.Count == 0) return PathTranslator.Identity;
            var labels = Labels(torrent);
            return clients.Where(entry => entry.Client.Label.Length > 0 && labels.Contains(entry.Client.Label))
                .OrderBy(entry => entry.Client.Id).Select(entry => entry.Paths).FirstOrDefault() ?? primary;
        }
    }

    private static HashSet<string> Labels(JsonElement torrent) =>
        torrent.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().Where(label => label.ValueKind == JsonValueKind.String).Select(label => label.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : [];

    private static bool TryParseTorrent(
        JsonElement torrent,
        TransmissionSession session,
        out TransmissionFileProtection protection,
        out string downloadDirectory,
        out TransmissionFile[] torrentFiles)
    {
        protection = null!;
        downloadDirectory = string.Empty;
        torrentFiles = [];
        if (!torrent.TryGetProperty("downloadDir", out var download) || string.IsNullOrWhiteSpace(download.GetString()) ||
            !torrent.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return false;
        downloadDirectory = Path.GetFullPath(download.GetString()!);
        // fileStats carries each file's wanted flag in the same order as files; absent means wanted.
        var wanted = torrent.TryGetProperty("fileStats", out var stats) && stats.ValueKind == JsonValueKind.Array
            ? stats.EnumerateArray().Select(stat => !stat.TryGetProperty("wanted", out var flag) || flag.ValueKind != JsonValueKind.False)
                .ToArray()
            : [];
        torrentFiles = files.EnumerateArray().Select((file, index) => new TransmissionFile(
            file.GetProperty("name").GetString()!, file.GetProperty("length").GetUInt64(),
            file.GetProperty("bytesCompleted").GetUInt64(), index >= wanted.Length || wanted[index])).ToArray();

        var ratioMode = torrent.GetProperty("seedRatioMode").GetInt32();
        var ratioGoal = ratioMode switch
        {
            0 when session.RatioLimited => session.RatioLimit,
            1 => torrent.GetProperty("seedRatioLimit").GetDouble(),
            _ => (double?)null
        };
        var idleMode = torrent.GetProperty("seedIdleMode").GetInt32();
        var idleGoal = idleMode switch
        {
            0 when session.IdleLimited => session.IdleLimitMinutes,
            1 => torrent.GetProperty("seedIdleLimit").GetInt32(),
            _ => (int?)null
        };
        var uploadRatio = torrent.GetProperty("uploadRatio").GetDouble();
        var etaIdle = torrent.GetProperty("etaIdle").GetInt64();
        var ratioSatisfied = ratioGoal.HasValue && uploadRatio >= ratioGoal.Value;
        var idleSatisfied = idleGoal.HasValue && etaIdle == 0;
        var hasFiniteGoal = ratioGoal.HasValue || idleGoal.HasValue;
        protection = new TransmissionFileProtection(
            FileComplete: torrent.GetProperty("leftUntilDone").GetUInt64() == 0 &&
                torrent.GetProperty("percentDone").GetDouble() >= 1,
            HasFiniteSeedGoal: hasFiniteGoal,
            SeedGoalSatisfied: torrent.GetProperty("isFinished").GetBoolean() || ratioSatisfied || idleSatisfied,
            RatioGoal: ratioGoal,
            UploadRatio: uploadRatio,
            IdleGoalMinutes: idleGoal,
            SecondsSeeding: torrent.GetProperty("secondsSeeding").GetInt64(),
            Status: torrent.GetProperty("status").GetInt32());
        return true;
    }

    private sealed record TransmissionSession(bool RatioLimited, double RatioLimit, bool IdleLimited, int IdleLimitMinutes,
        string? IncompleteDirectory, bool RenamePartialFiles);
    private sealed record TransmissionFile(string Name, ulong Length, ulong BytesCompleted, bool Wanted);
}

/// <summary>A complete Transmission read or a conservative unavailable result.</summary>
/// <param name="Available">Whether RPC returned valid session and torrent data.</param>
/// <param name="CompleteFileIndex">Whether every reported torrent file could be identified on disk.</param>
/// <param name="UnavailableReason">A stable reason when the snapshot is unavailable.</param>
/// <param name="FilesByPhysicalIdentity">Torrent protection states keyed by device and inode.</param>
/// <param name="UnresolvedFiles">Wanted files with downloaded data that could not be found on disk.</param>
public sealed record TransmissionSeedSnapshot(
    bool Available,
    bool CompleteFileIndex,
    string? UnavailableReason,
    IReadOnlyDictionary<string, List<TransmissionFileProtection>> FilesByPhysicalIdentity,
    int UnresolvedFiles = 0)
{
    /// <summary>Creates a deletion-blocking unavailable snapshot.</summary>
    public static TransmissionSeedSnapshot Unavailable(string reason) =>
        new(false, false, reason, new Dictionary<string, List<TransmissionFileProtection>>());
}

/// <summary>Read-only seed protection for one torrent file.</summary>
/// <param name="FileComplete">Whether the torrent and this file are fully downloaded.</param>
/// <param name="HasFiniteSeedGoal">Whether Transmission defines at least one finite seed goal.</param>
/// <param name="SeedGoalSatisfied">Whether every configured finite seed goal is satisfied.</param>
/// <param name="RatioGoal">The effective ratio goal, if configured.</param>
/// <param name="UploadRatio">The current uploaded-to-wanted-size ratio.</param>
/// <param name="IdleGoalMinutes">The effective idle goal, if configured.</param>
/// <param name="SecondsSeeding">The torrent's cumulative seeding time.</param>
/// <param name="Status">Transmission's current activity code.</param>
public sealed record TransmissionFileProtection(
    bool FileComplete,
    bool HasFiniteSeedGoal,
    bool SeedGoalSatisfied,
    double? RatioGoal,
    double UploadRatio,
    int? IdleGoalMinutes,
    long SecondsSeeding,
    int Status);
