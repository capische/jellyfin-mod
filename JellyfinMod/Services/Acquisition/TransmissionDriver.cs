using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>
/// The Phase 4 write driver (user decision 1): Transmission 4.x legacy RPC through the same transport the Phase 3
/// seed reader uses. Isolation is the configured label plus the configured download directory; a second,
/// per-operation label proves ownership during recovery.
/// </summary>
public sealed class TransmissionDriver(IHttpClientFactory clients, ILogger<TransmissionDriver> logger) : IDownloadClientDriver
{
    /// <summary>The configuration kind.</summary>
    public const string DriverKind = "transmission";

    /// <summary>Labels in <c>torrent-add</c> need RPC version 17 (Transmission 4.0).</summary>
    public const int MinimumRpcVersion = 17;

    /// <summary>Transmission's per-torrent mode that disables a ratio or idle stop condition.</summary>
    private const int Unlimited = 2;

    private static readonly string[] TorrentFields = ["hashString", "downloadDir", "labels", "seedRatioMode", "seedIdleMode"];

    /// <inheritdoc />
    public string Kind => DriverKind;

    /// <inheritdoc />
    public async Task<DownloadClientProbe> ProbeAsync(DownloadClientConnection connection, CancellationToken cancellationToken)
    {
        using var document = await CallAsync(connection, "session-get", new { fields = new[] { "version", "rpc-version" } },
            cancellationToken, definite: true).ConfigureAwait(false);
        TransmissionRpc.Success(document.RootElement, out var arguments);
        var version = arguments.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "unknown";
        var rpc = arguments.TryGetProperty("rpc-version", out var rpcValue) && rpcValue.TryGetInt32(out var number) ? number : 0;
        if (rpc < MinimumRpcVersion)
            throw new DownloadClientRejectedException("client_unsupported_version",
                $"Transmission RPC version {rpc} is too old; labels need version {MinimumRpcVersion} or later.");
        return new DownloadClientProbe(version, rpc.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task<DownloadClientTorrent?> FindAsync(DownloadClientConnection connection, string infoHash,
        CancellationToken cancellationToken)
    {
        using var document = await CallAsync(connection, "torrent-get", new { ids = new[] { infoHash }, fields = TorrentFields },
            cancellationToken, definite: false).ConfigureAwait(false);
        TransmissionRpc.Success(document.RootElement, out var arguments);
        if (!arguments.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
            throw new DownloadClientUnavailableException("client_invalid_response", "Transmission returned an invalid torrent list.");
        foreach (var torrent in torrents.EnumerateArray())
        {
            var hash = torrent.TryGetProperty("hashString", out var hashValue) ? hashValue.GetString() : null;
            if (!string.Equals(hash, infoHash, StringComparison.OrdinalIgnoreCase)) continue;
            var labels = torrent.TryGetProperty("labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Array
                ? labelValues.EnumerateArray().Select(label => label.GetString()).OfType<string>().ToArray()
                : [];
            var ratioMode = torrent.TryGetProperty("seedRatioMode", out var ratio) && ratio.TryGetInt32(out var r) ? r : -1;
            var idleMode = torrent.TryGetProperty("seedIdleMode", out var idle) && idle.TryGetInt32(out var i) ? i : -1;
            return new DownloadClientTorrent(infoHash,
                torrent.TryGetProperty("downloadDir", out var directory) ? directory.GetString() : null,
                labels, ratioMode == Unlimited && idleMode == Unlimited);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<SubmissionResult> SubmitAsync(DownloadClientConnection connection, DownloadSubmission submission,
        CancellationToken cancellationToken)
    {
        object arguments = submission.Metainfo is { } metainfo
            ? new Dictionary<string, object>
            {
                ["metainfo"] = Convert.ToBase64String(metainfo), ["download-dir"] = submission.DownloadDirectory,
                ["labels"] = submission.Labels, ["paused"] = false
            }
            : new Dictionary<string, object>
            {
                ["filename"] = submission.MagnetUri ?? throw new ArgumentException("A submission needs metainfo or a magnet.", nameof(submission)),
                ["download-dir"] = submission.DownloadDirectory, ["labels"] = submission.Labels, ["paused"] = false
            };
        using var document = await CallAsync(connection, "torrent-add", arguments, cancellationToken, definite: false,
            rejectOnFailureResult: true).ConfigureAwait(false);
        TransmissionRpc.Success(document.RootElement, out var result);
        if (result.TryGetProperty("torrent-added", out var added) && SameHash(added, submission.InfoHash)) return SubmissionResult.Added;
        if (result.TryGetProperty("torrent-duplicate", out var duplicate) && SameHash(duplicate, submission.InfoHash))
            return SubmissionResult.Duplicate;
        // A success envelope naming another hash is not proof of anything; recovery decides by lookup.
        throw new DownloadClientUnavailableException("client_unconfirmed", "Transmission did not confirm the torrent it added.");
    }

    /// <inheritdoc />
    public async Task ApplySeedSettingsAsync(DownloadClientConnection connection, string infoHash, CancellationToken cancellationToken)
    {
        using var _ = await CallAsync(connection, "torrent-set",
            new Dictionary<string, object> { ["ids"] = new[] { infoHash }, ["seedRatioMode"] = Unlimited, ["seedIdleMode"] = Unlimited },
            cancellationToken, definite: false).ConfigureAwait(false);
    }

    private static bool SameHash(JsonElement torrent, string infoHash) =>
        torrent.ValueKind == JsonValueKind.Object && torrent.TryGetProperty("hashString", out var hash) &&
        string.Equals(hash.GetString(), infoHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Calls one method. Refused connections and refused credentials prove nothing was processed; every other
    /// transport failure leaves the outcome unknown.
    /// </summary>
    private async Task<JsonDocument> CallAsync(DownloadClientConnection connection, string method, object arguments,
        CancellationToken cancellationToken, bool definite, bool rejectOnFailureResult = false)
    {
        using var client = clients.CreateClient(NamedClient.Default);
        HttpResponseMessage response;
        try
        {
            response = await TransmissionRpc.SendAsync(client, connection.Endpoint, method, arguments, connection.Username,
                connection.Password, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConnectionError)
        {
            logger.LogWarning("Transmission at {Host} refused the connection", connection.Endpoint.Host);
            throw new DownloadClientRejectedException("client_unreachable", "Transmission could not be reached.");
        }
        catch (HttpRequestException error) when (definite)
        {
            logger.LogWarning("Transmission at {Host} failed: {Error}", connection.Endpoint.Host, error.HttpRequestError);
            throw new DownloadClientRejectedException("client_unreachable", "Transmission could not be reached.");
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning("Transmission at {Host} failed mid-request: {Error}", connection.Endpoint.Host, error.HttpRequestError);
            throw new DownloadClientUnavailableException("client_unreachable", "Transmission stopped answering.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new DownloadClientRejectedException("client_auth_failed", "Transmission rejected its credentials.");
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new DownloadClientRejectedException("client_session_rejected", "Transmission did not accept its session id.");
            if (!response.IsSuccessStatusCode)
            {
                if (definite)
                    throw new DownloadClientRejectedException("client_error", $"Transmission answered HTTP {(int)response.StatusCode}.");
                throw new DownloadClientUnavailableException("client_error", $"Transmission answered HTTP {(int)response.StatusCode}.");
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new DownloadClientUnavailableException("client_invalid_response", "Transmission returned invalid JSON.");
            }

            if (TransmissionRpc.Success(document.RootElement, out _)) return document;
            document.Dispose();
            // A non-success result is Transmission's own answer that it did not do the work.
            if (definite || rejectOnFailureResult)
                throw new DownloadClientRejectedException("client_rejected", "Transmission refused the request.");
            throw new DownloadClientUnavailableException("client_invalid_response", "Transmission refused the request.");
        }
    }
}
