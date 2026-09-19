using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>A self-created BitTorrent v1 fixture: legal payload description, exact info bytes and expected hash.</summary>
internal sealed record TorrentFixture(string Name, byte[] Bytes, string InfoHash)
{
    /// <summary>Builds a v1 torrent whose announce URL carries a passkey, as private trackers do.</summary>
    public static TorrentFixture Create(string name, long length, string announce, bool hybrid = false, bool v2Only = false)
    {
        var pieces = SHA1.HashData(Encoding.UTF8.GetBytes(name));
        using var info = new MemoryStream();
        void Write(string text) => info.Write(Encoding.ASCII.GetBytes(text));
        void WriteBytes(byte[] value) { Write(value.Length + ":"); info.Write(value); }
        Write("d");
        Write("6:lengthi" + length + "e");
        if (hybrid || v2Only) Write("12:meta versioni2e");
        WriteBytes(Encoding.UTF8.GetBytes("name"));
        WriteBytes(Encoding.UTF8.GetBytes(name));
        Write("12:piece lengthi262144e");
        if (!v2Only)
        {
            WriteBytes(Encoding.UTF8.GetBytes("pieces"));
            WriteBytes(pieces);
        }

        Write("e");
        var infoBytes = info.ToArray();
        using var torrent = new MemoryStream();
        var announceBytes = Encoding.UTF8.GetBytes(announce);
        torrent.Write(Encoding.ASCII.GetBytes("d8:announce" + announceBytes.Length + ":"));
        torrent.Write(announceBytes);
        torrent.Write(Encoding.ASCII.GetBytes("4:info"));
        torrent.Write(infoBytes);
        torrent.Write(Encoding.ASCII.GetBytes("e"));
        return new TorrentFixture(name, torrent.ToArray(), Convert.ToHexStringLower(SHA1.HashData(infoBytes)));
    }
}

/// <summary>One feed row served by the Torznab boundary.</summary>
internal sealed record FeedItem(string Title, string Guid, string? Link, long? Size, int? Seeders,
    Dictionary<string, string> Attributes);

/// <summary>A scripted Torznab indexer.</summary>
internal sealed class IndexerScript
{
    public string ApiKey { get; set; } = string.Empty;
    public string Caps { get; set; } = string.Empty;
    public List<FeedItem> MovieItems { get; } = [];
    public List<FeedItem> TvItems { get; } = [];
    public Func<HttpContext, Task<bool>>? Override { get; set; }
    public int PageSize { get; set; } = 100;
    public int? ReportedTotal { get; set; }
    public ConcurrentQueue<string> Queries { get; } = new();
}

/// <summary>
/// A real HTTP Torznab boundary: caps, paged feeds, torrent downloads, redirects and deliberately hostile answers.
/// Nothing here is a mock of the plugin's client; the plugin sends and parses real HTTP.
/// </summary>
internal sealed class TorznabBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    public Dictionary<string, IndexerScript> Indexers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, byte[]> Torrents { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Downloads { get; } = new();
    public Uri Address { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/{indexer}/api", async context =>
        {
            var name = (string)context.Request.RouteValues["indexer"]!;
            if (!Indexers.TryGetValue(name, out var script)) { context.Response.StatusCode = 404; return; }
            script.Queries.Enqueue(context.Request.QueryString.Value ?? string.Empty);
            if (script.Override is not null && await script.Override(context)) return;
            context.Response.ContentType = "application/xml";
            if (context.Request.Query["apikey"] != script.ApiKey)
            {
                // Torznab reports bad credentials with HTTP 200 and an error document.
                await context.Response.WriteAsync("<?xml version=\"1.0\"?><error code=\"100\" description=\"Incorrect user credentials\"/>");
                return;
            }

            var mode = context.Request.Query["t"].ToString();
            if (mode == "caps") { await context.Response.WriteAsync(script.Caps); return; }
            var items = mode == "tvsearch" ? script.TvItems : script.MovieItems;
            var offset = int.TryParse(context.Request.Query["offset"], out var o) ? o : 0;
            var limit = Math.Min(int.TryParse(context.Request.Query["limit"], out var l) ? l : 100, script.PageSize);
            var page = items.Skip(offset).Take(limit).ToArray();
            await context.Response.WriteAsync(Feed(page, offset, script.ReportedTotal ?? items.Count));
        });
        _app.MapGet("/dl/{id}", async context =>
        {
            var id = (string)context.Request.RouteValues["id"]!;
            Downloads.Enqueue(id + "?" + context.Request.QueryString.Value);
            if (Redirects.TryGetValue(id, out var location))
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = location;
                return;
            }

            if (!Torrents.TryGetValue(id, out var bytes)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "application/x-bittorrent";
            await context.Response.Body.WriteAsync(bytes);
        });
        await _app.StartAsync();
        Address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public static string Caps(string movieParams, string tvParams, string searchParams = "q", int max = 100) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <server title="JellyfinMod boundary"/>
          <limits max="{max}" default="{max}"/>
          <searching>
            <search available="{(searchParams.Length > 0 ? "yes" : "no")}" supportedParams="{searchParams}"/>
            <tv-search available="{(tvParams.Length > 0 ? "yes" : "no")}" supportedParams="{tvParams}"/>
            <movie-search available="{(movieParams.Length > 0 ? "yes" : "no")}" supportedParams="{movieParams}"/>
          </searching>
          <categories>
            <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD"/></category>
            <category id="5000" name="TV"><subcat id="5040" name="TV/HD"/></category>
          </categories>
        </caps>
        """;

    private static string Feed(IEnumerable<FeedItem> items, int offset, int total)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>""");
        builder.Append($"""<torznab:response offset="{offset}" total="{total}"/>""");
        foreach (var item in items)
        {
            builder.Append("<item>");
            builder.Append("<title>").Append(System.Security.SecurityElement.Escape(item.Title)).Append("</title>");
            builder.Append("<guid>").Append(System.Security.SecurityElement.Escape(item.Guid)).Append("</guid>");
            builder.Append("<pubDate>Fri, 18 Sep 2026 10:00:00 +0000</pubDate>");
            if (item.Link is not null)
                builder.Append($"""<enclosure url="{System.Security.SecurityElement.Escape(item.Link)}" length="{item.Size ?? 0}" type="application/x-bittorrent"/>""");
            if (item.Size is { } size) builder.Append($"""<torznab:attr name="size" value="{size}"/>""");
            if (item.Seeders is { } seeders) builder.Append($"""<torznab:attr name="seeders" value="{seeders}"/>""");
            foreach (var (key, value) in item.Attributes)
                builder.Append($"""<torznab:attr name="{key}" value="{System.Security.SecurityElement.Escape(value)}"/>""");
            builder.Append("</item>");
        }

        builder.Append("</channel></rss>");
        return builder.ToString();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>A host that must never be contacted: it counts every request it receives.</summary>
internal sealed class CanaryBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    public int Hits;
    public Uri Address { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Run(context =>
        {
            Interlocked.Increment(ref Hits);
            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("d8:announce0:4:infod4:name1:x6:pieces20:aaaaaaaaaaaaaaaaaaaaee");
        });
        await _app.StartAsync();
        var bound = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        // "localhost" is a different host name from the indexer's 127.0.0.1, so it is outside the allowed set.
        Address = new UriBuilder(bound) { Host = "localhost" }.Uri;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>One torrent held by the Transmission boundary.</summary>
internal sealed class HeldTorrent
{
    public required string Hash { get; init; }
    public required string DownloadDir { get; init; }
    public required List<string> Labels { get; init; }
    public int SeedRatioMode { get; set; }
    public int SeedIdleMode { get; set; }
}

/// <summary>
/// A real HTTP Transmission RPC boundary speaking the legacy 4.x protocol: Basic authentication, the 409 session-id
/// handshake, session-get, torrent-get, torrent-add and torrent-set, with injectable faults.
/// </summary>
internal sealed class TransmissionBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    public string Username { get; } = "jellyfinmod";
    public string Password { get; } = "tx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    public ConcurrentDictionary<string, HeldTorrent> Torrents { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> MetainfoHashes { get; } = new(StringComparer.Ordinal);
    public int RpcVersion { get; set; } = 17;
    public int AddCalls;
    public int SetCalls;
    public int HandshakeRejections;
    public int UnauthenticatedCalls;
    /// <summary>Records the add, then drops the connection so the response is lost.</summary>
    public bool DropAfterAdd { get; set; }
    /// <summary>Drops the connection without recording the add.</summary>
    public bool DropBeforeAdd { get; set; }
    public bool RefuseAdd { get; set; }
    public Uri Endpoint { get; private set; } = null!;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/transmission/rpc", HandleAsync);
        await _app.StartAsync();
        Endpoint = new Uri(new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            "/transmission/rpc");
    }

    private async Task HandleAsync(HttpContext context)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password));
        if (context.Request.Headers.Authorization != expected)
        {
            Interlocked.Increment(ref UnauthenticatedCalls);
            context.Response.StatusCode = 401;
            return;
        }

        if (context.Request.Headers["X-Transmission-Session-Id"] != _sessionId)
        {
            Interlocked.Increment(ref HandshakeRejections);
            context.Response.StatusCode = 409;
            context.Response.Headers["X-Transmission-Session-Id"] = _sessionId;
            return;
        }

        var request = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
        var method = request["method"]!.GetValue<string>();
        var arguments = request["arguments"]?.AsObject() ?? [];
        JsonObject result;
        switch (method)
        {
            case "session-get":
                result = new JsonObject { ["version"] = "4.0.6 (boundary)", ["rpc-version"] = RpcVersion };
                break;
            case "torrent-get":
                var ids = arguments["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var list = new JsonArray();
                foreach (var torrent in Torrents.Values.Where(value => ids.Contains(value.Hash)))
                    list.Add(new JsonObject
                    {
                        ["hashString"] = torrent.Hash, ["downloadDir"] = torrent.DownloadDir,
                        ["labels"] = new JsonArray(torrent.Labels.Select(label => (JsonNode)label).ToArray()),
                        ["seedRatioMode"] = torrent.SeedRatioMode, ["seedIdleMode"] = torrent.SeedIdleMode
                    });
                result = new JsonObject { ["torrents"] = list };
                break;
            case "torrent-add":
                Interlocked.Increment(ref AddCalls);
                if (DropBeforeAdd) { context.Abort(); return; }
                if (RefuseAdd)
                {
                    await context.Response.WriteAsJsonAsync(new { result = "invalid or corrupt torrent file", arguments = new { } });
                    return;
                }

                string hash;
                if (arguments["metainfo"]?.GetValue<string>() is { } metainfo)
                    hash = MetainfoHashes.TryGetValue(metainfo, out var known) ? known : throw new InvalidOperationException("Unknown metainfo");
                else
                    hash = arguments["filename"]!.GetValue<string>().Split("urn:btih:")[1][..40].ToLowerInvariant();
                if (Torrents.TryGetValue(hash, out var existing))
                {
                    result = new JsonObject { ["torrent-duplicate"] = new JsonObject { ["hashString"] = existing.Hash, ["id"] = 1 } };
                    break;
                }

                Torrents[hash] = new HeldTorrent
                {
                    Hash = hash, DownloadDir = arguments["download-dir"]!.GetValue<string>(),
                    Labels = arguments["labels"]!.AsArray().Select(label => label!.GetValue<string>()).ToList()
                };
                if (DropAfterAdd) { context.Abort(); return; }
                result = new JsonObject { ["torrent-added"] = new JsonObject { ["hashString"] = hash, ["id"] = Torrents.Count } };
                break;
            case "torrent-set":
                Interlocked.Increment(ref SetCalls);
                foreach (var id in arguments["ids"]!.AsArray().Select(value => value!.GetValue<string>()))
                    if (Torrents.TryGetValue(id.ToLowerInvariant(), out var torrent))
                    {
                        if (arguments["seedRatioMode"] is { } ratio) torrent.SeedRatioMode = ratio.GetValue<int>();
                        if (arguments["seedIdleMode"] is { } idle) torrent.SeedIdleMode = idle.GetValue<int>();
                    }

                result = [];
                break;
            default:
                await context.Response.WriteAsJsonAsync(new { result = "method name not recognized", arguments = new { } });
                return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(new JsonObject { ["result"] = "success", ["arguments"] = result }.ToJsonString());
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal static class Json
{
    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>The host serializes Guids in Jellyfin's compact form, so parse any format.</summary>
    public static Guid AsGuid(this JsonElement value) => Guid.Parse(value.GetString()!);
}
