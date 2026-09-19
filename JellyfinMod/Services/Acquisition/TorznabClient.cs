using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>A stable, credential-free indexer failure.</summary>
public sealed class TorznabException(string code, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; } = code;

    /// <summary>Gets the indexer's rate-limit guidance, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The verified parts of a Torznab <c>t=caps</c> document.</summary>
public sealed record TorznabCapabilities(
    [property: JsonPropertyName("search")] IReadOnlyList<string> Search,
    [property: JsonPropertyName("movieSearch")] IReadOnlyList<string> MovieSearch,
    [property: JsonPropertyName("tvSearch")] IReadOnlyList<string> TvSearch,
    [property: JsonPropertyName("categories")] IReadOnlyList<int> Categories,
    [property: JsonPropertyName("limitMax")] int? LimitMax,
    [property: JsonPropertyName("limitDefault")] int? LimitDefault);

/// <summary>One feed row. Everything is untrusted text; unknown values stay null.</summary>
public sealed record TorznabItem(
    string Title,
    string Guid,
    string? DownloadUrl,
    string? MagnetUrl,
    long? Size,
    DateTime? PublishedAt,
    int? Seeders,
    int? Peers,
    string? InfoHash,
    double? DownloadVolumeFactor,
    double? MinimumRatio,
    long? MinimumSeedSeconds,
    string? ImdbId,
    int? TmdbId,
    int? TvdbId,
    int? Season,
    int? Episode);

/// <summary>One fetched feed page.</summary>
public sealed record TorznabPage(IReadOnlyList<TorznabItem> Items, int? Offset, int? Total);

/// <summary>The endpoint and credential of one indexer, resolved for a single request.</summary>
public sealed record TorznabEndpoint(Uri BaseUrl, string? ApiKey);

/// <summary>The torrent identity derived from fetched metadata or a magnet link.</summary>
public sealed record TorrentLocator(string InfoHash, byte[]? Metainfo, string? MagnetUri, long? Size, string? Name);

/// <summary>
/// Speaks Torznab to an administrator-configured endpoint (P4.A3). Redirects are never followed automatically:
/// every hop is checked against the indexer's allowed hosts, so a feed cannot make the plugin fetch an arbitrary
/// URL or carry a credential to another host.
/// </summary>
public sealed class TorznabClient(IHttpClientFactory clients, ILogger<TorznabClient> logger)
{
    /// <summary>The named client with automatic redirects disabled.</summary>
    public const string HttpClientName = "JellyfinMod.Acquisition";

    private const int MaxCapsBytes = 1 << 20;
    private const int MaxFeedBytes = 8 << 20;
    private const int MaxTorrentBytes = 4 << 20;
    private const int MaxRedirects = 3;

    /// <summary>Fetches and validates <c>t=caps</c>.</summary>
    public async Task<TorznabCapabilities> GetCapabilitiesAsync(TorznabEndpoint endpoint, CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync(endpoint, [("t", "caps")], MaxCapsBytes, cancellationToken).ConfigureAwait(false);
        var root = document.Root!;
        if (root.Name.LocalName != "caps") throw new TorznabException("malformed_response", "The indexer did not return a capability document.");
        var searching = root.Element("searching");
        IReadOnlyList<string> Modes(string name)
        {
            var element = searching?.Element(name);
            if (element is null || !string.Equals((string?)element.Attribute("available"), "yes", StringComparison.OrdinalIgnoreCase)) return [];
            return ((string?)element.Attribute("supportedParams") ?? "q").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant()).Distinct().ToArray();
        }

        var categories = root.Element("categories")?.Elements("category")
            .SelectMany(category => category.Elements("subcat").Prepend(category))
            .Select(category => Int((string?)category.Attribute("id"))).OfType<int>().Distinct().Order().ToArray() ?? [];
        var limits = root.Element("limits");
        var capabilities = new TorznabCapabilities(Modes("search"), Modes("movie-search"), Modes("tv-search"), categories,
            Int((string?)limits?.Attribute("max")), Int((string?)limits?.Attribute("default")));
        if (capabilities.Search.Count == 0 && capabilities.MovieSearch.Count == 0 && capabilities.TvSearch.Count == 0)
            throw new TorznabException("capabilities_unavailable", "The indexer advertises no search mode.");
        return capabilities;
    }

    /// <summary>Fetches one search page with the given, already capability-checked parameters.</summary>
    public async Task<TorznabPage> SearchAsync(TorznabEndpoint endpoint, IReadOnlyList<(string Key, string Value)> parameters,
        CancellationToken cancellationToken)
    {
        var document = await GetXmlAsync(endpoint, parameters, MaxFeedBytes, cancellationToken).ConfigureAwait(false);
        var channel = document.Root?.Name.LocalName == "rss" ? document.Root.Element("channel") : null;
        if (channel is null) throw new TorznabException("malformed_response", "The indexer did not return a feed.");
        var response = channel.Elements().FirstOrDefault(element => element.Name.LocalName == "response");
        var items = new List<TorznabItem>();
        foreach (var item in channel.Elements("item"))
        {
            var title = ((string?)item.Element("title"))?.Trim();
            var guid = ((string?)item.Element("guid"))?.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(guid)) continue;
            var attributes = item.Elements().Where(element => element.Name.LocalName == "attr")
                .GroupBy(element => ((string?)element.Attribute("name"))?.ToLowerInvariant() ?? string.Empty)
                .ToDictionary(group => group.Key, group => (string?)group.First().Attribute("value"));
            string? Attribute(string name) => attributes.GetValueOrDefault(name);
            var enclosure = item.Element("enclosure");
            var link = ((string?)enclosure?.Attribute("url"))?.Trim() ?? ((string?)item.Element("link"))?.Trim();
            var magnet = Attribute("magneturl");
            if (link is not null && link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                magnet ??= link;
                link = null;
            }

            items.Add(new TorznabItem(
                title.Length > 1024 ? title[..1024] : title,
                guid.Length > 1024 ? guid[..1024] : guid,
                link,
                magnet,
                Long(Attribute("size")) ?? Long((string?)item.Element("size")) ?? Long((string?)enclosure?.Attribute("length")),
                DateTime.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var published) ? published : null,
                Int(Attribute("seeders")),
                Int(Attribute("peers")),
                Attribute("infohash")?.Trim().ToLowerInvariant(),
                Double(Attribute("downloadvolumefactor")),
                Double(Attribute("minimumratio")),
                Long(Attribute("minimumseedtime")),
                Attribute("imdbid") ?? Attribute("imdb"),
                Int(Attribute("tmdbid")),
                Int(Attribute("tvdbid")),
                Int(Attribute("season")),
                Int(Attribute("episode"))));
        }

        return new TorznabPage(items, Int((string?)response?.Attribute("offset")), Int((string?)response?.Attribute("total")));
    }

    /// <summary>
    /// Resolves the torrent identity: a v1 magnet directly, or by fetching the torrent from an allowed host.
    /// </summary>
    public async Task<TorrentLocator> ResolveAsync(string? downloadUrl, string? magnetUri, IReadOnlySet<string> allowedHosts,
        CancellationToken cancellationToken)
    {
        if (downloadUrl is null && magnetUri is not null) return TorrentMetadata.FromMagnet(magnetUri);
        if (downloadUrl is null) throw new TorznabException("no_download_locator", "The release has no download link.");
        var current = new Uri(downloadUrl, UriKind.Absolute);
        using var client = clients.CreateClient(HttpClientName);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (current.Scheme is not ("http" or "https") || !allowedHosts.Contains(current.IdnHost.ToLowerInvariant()))
                throw new TorznabException(hop == 0 ? "download_host_not_allowed" : "redirect_rejected",
                    "The torrent link leads to a host this indexer is not allowed to use.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null) throw new TorznabException("redirect_rejected", "The indexer sent an empty redirect.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)) return TorrentMetadata.FromMagnet(next.OriginalString);
                if (current.Scheme == "https" && next.Scheme == "http")
                    throw new TorznabException("redirect_rejected", "The indexer redirected to an insecure link.");
                current = next;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TorznabException("auth_failed", "The indexer refused the torrent download.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new TorznabException("rate_limited", "The indexer is rate limiting downloads.", RetryAfter(response));
            if (!response.IsSuccessStatusCode)
                throw new TorznabException("download_failed", $"The indexer answered HTTP {(int)response.StatusCode} for the torrent.");
            var bytes = await ReadBoundedAsync(response, MaxTorrentBytes, cancellationToken).ConfigureAwait(false);
            return TorrentMetadata.FromTorrent(bytes);
        }

        throw new TorznabException("redirect_rejected", "The torrent link redirected too many times.");
    }

    private async Task<XDocument> GetXmlAsync(TorznabEndpoint endpoint, IReadOnlyList<(string Key, string Value)> parameters,
        int maxBytes, CancellationToken cancellationToken)
    {
        var query = parameters.Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}").ToList();
        if (!string.IsNullOrEmpty(endpoint.ApiKey)) query.Add("apikey=" + Uri.EscapeDataString(endpoint.ApiKey));
        var uri = new UriBuilder(endpoint.BaseUrl) { Query = string.Join('&', query) }.Uri;
        using var client = clients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            // The request URI carries the API key, so only the endpoint's host is ever logged.
            logger.LogWarning("Torznab indexer {Host} is unreachable: {Error}", endpoint.BaseUrl.Host, error.HttpRequestError);
            throw new TorznabException("unavailable", "The indexer could not be reached.");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new TorznabException("redirect_rejected", "The indexer redirected its API request.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TorznabException("auth_failed", "The indexer rejected its credentials.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new TorznabException("rate_limited", "The indexer is rate limiting searches.", RetryAfter(response));
            if (!response.IsSuccessStatusCode)
                throw new TorznabException("unavailable", $"The indexer answered HTTP {(int)response.StatusCode}.");
            var bytes = await ReadBoundedAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxBytes,
                    MaxCharactersFromEntities = 0, IgnoreComments = true, Async = false
                };
                using var stream = new MemoryStream(bytes);
                using var reader = XmlReader.Create(stream, settings);
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (XmlException)
            {
                throw new TorznabException("malformed_response", "The indexer returned malformed XML.");
            }

            if (document.Root?.Name.LocalName == "error")
            {
                // Newznab/Torznab errors arrive with HTTP 200; they are never an empty result.
                var code = Int((string?)document.Root.Attribute("code"));
                throw code switch
                {
                    100 or 101 or 102 => new TorznabException("auth_failed", "The indexer rejected its credentials."),
                    429 or 500 or 501 => new TorznabException("rate_limited", "The indexer reports its request limit was reached."),
                    200 or 201 or 202 or 203 => new TorznabException("unsupported_request", "The indexer does not support this search."),
                    _ => new TorznabException("indexer_error", "The indexer reported an error.")
                };
            }

            return document;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new TorznabException("response_too_large", "The indexer response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new TorznabException("response_too_large", "The indexer response is too large.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow,
        _ => null
    };

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long? Long(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : null;

    private static double? Double(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : null;
}
