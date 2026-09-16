using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Reads Transmission 4.x torrent and seed-goal state without issuing mutations.</summary>
public sealed class TransmissionSeedClient(
    IHttpClientFactory httpClientFactory,
    Func<PluginConfiguration> configuration,
    UnixFileInspector files,
    ILogger<TransmissionSeedClient> logger)
{
    private const string SessionHeader = "X-Transmission-Session-Id";
    private static readonly string[] SessionFields =
        ["version", "seedRatioLimited", "seedRatioLimit", "idle-seeding-limit-enabled", "idle-seeding-limit"];
    private static readonly string[] TorrentFields =
    [
        "id", "hashString", "downloadDir", "files", "leftUntilDone", "percentDone", "status", "uploadRatio",
        "secondsSeeding", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit", "etaIdle", "isFinished"
    ];

    /// <summary>Gets one complete, read-only snapshot or an unavailable result that blocks deletion.</summary>
    public async Task<TransmissionSeedSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var settings = configuration();
        if (!Uri.TryCreate(settings.TransmissionRpcUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
            return TransmissionSeedSnapshot.Unavailable("transmission_unconfigured");

        try
        {
            using var client = httpClientFactory.CreateClient(NamedClient.Default);
            using var sessionResponse = await SendAsync(client, endpoint, "session-get",
                new { fields = SessionFields }, settings, cancellationToken).ConfigureAwait(false);
            if (!sessionResponse.IsSuccessStatusCode)
                return TransmissionSeedSnapshot.Unavailable("transmission_unreachable");
            using var sessionDocument = await JsonDocument.ParseAsync(
                await sessionResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!Success(sessionDocument.RootElement, out var sessionArguments))
                return TransmissionSeedSnapshot.Unavailable("transmission_invalid_response");

            using var torrentResponse = await SendAsync(client, endpoint, "torrent-get",
                new { fields = TorrentFields }, settings, cancellationToken).ConfigureAwait(false);
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
            foreach (var torrent in torrents.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseTorrent(torrent, session, out var protection, out var downloadDirectory,
                        out var torrentFiles))
                {
                    completeIndex = false;
                    continue;
                }

                foreach (var file in torrentFiles)
                {
                    var path = Path.GetFullPath(file.Name, downloadDirectory);
                    if (!files.TryInspect(path, out var observed))
                    {
                        completeIndex = false;
                        continue;
                    }

                    var state = protection with { FileComplete = file.BytesCompleted >= file.Length };
                    if (!indexedFiles.TryGetValue(observed.PhysicalIdentity, out var matches))
                        indexedFiles[observed.PhysicalIdentity] = matches = [];
                    matches.Add(state);
                }
            }

            return new TransmissionSeedSnapshot(true, completeIndex, null, indexedFiles);
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

    private static HttpRequestMessage Request(Uri endpoint, string method, object arguments, PluginConfiguration settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { method, arguments })
        };
        if (!string.IsNullOrEmpty(settings.TransmissionUsername) || !string.IsNullOrEmpty(settings.TransmissionPassword))
        {
            var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                settings.TransmissionUsername + ":" + settings.TransmissionPassword));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", value);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri endpoint,
        string method,
        object arguments,
        PluginConfiguration settings,
        CancellationToken cancellationToken)
    {
        using var request = Request(endpoint, method, arguments, settings);
        var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict ||
            !response.Headers.TryGetValues(SessionHeader, out var values) || values.FirstOrDefault() is not { } sessionId)
            return response;

        response.Dispose();
        using var retry = Request(endpoint, method, arguments, settings);
        retry.Headers.TryAddWithoutValidation(SessionHeader, sessionId);
        return await client.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static bool Success(JsonElement root, out JsonElement arguments)
    {
        arguments = default;
        return root.TryGetProperty("result", out var result) && result.GetString() == "success" &&
            root.TryGetProperty("arguments", out arguments) && arguments.ValueKind == JsonValueKind.Object;
    }

    private static TransmissionSession ParseSession(JsonElement arguments) => new(
        arguments.TryGetProperty("seedRatioLimited", out var ratioEnabled) && ratioEnabled.GetBoolean(),
        arguments.TryGetProperty("seedRatioLimit", out var ratioLimit) ? ratioLimit.GetDouble() : 0,
        arguments.TryGetProperty("idle-seeding-limit-enabled", out var idleEnabled) && idleEnabled.GetBoolean(),
        arguments.TryGetProperty("idle-seeding-limit", out var idleLimit) ? idleLimit.GetInt32() : 0);

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
        torrentFiles = files.EnumerateArray().Select(file => new TransmissionFile(
            file.GetProperty("name").GetString()!, file.GetProperty("length").GetUInt64(),
            file.GetProperty("bytesCompleted").GetUInt64())).ToArray();

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

    private sealed record TransmissionSession(bool RatioLimited, double RatioLimit, bool IdleLimited, int IdleLimitMinutes);
    private sealed record TransmissionFile(string Name, ulong Length, ulong BytesCompleted);
}

/// <summary>A complete Transmission read or a conservative unavailable result.</summary>
/// <param name="Available">Whether RPC returned valid session and torrent data.</param>
/// <param name="CompleteFileIndex">Whether every reported torrent file could be identified on disk.</param>
/// <param name="UnavailableReason">A stable reason when the snapshot is unavailable.</param>
/// <param name="FilesByPhysicalIdentity">Torrent protection states keyed by device and inode.</param>
public sealed record TransmissionSeedSnapshot(
    bool Available,
    bool CompleteFileIndex,
    string? UnavailableReason,
    IReadOnlyDictionary<string, List<TransmissionFileProtection>> FilesByPhysicalIdentity)
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
