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

/// <summary>A self-created BitTorrent v1 fixture: legal generated payload, exact info bytes and expected hash.</summary>
internal sealed record TorrentFixture(string Name, byte[] Bytes, string InfoHash, IReadOnlyList<(string Path, long Length)> Files)
{
    /// <summary>A single-file torrent: the file's path is the torrent name.</summary>
    public static TorrentFixture Single(string name, long length) => Build(name, [(name, length)], single: true);

    /// <summary>A multi-file torrent: file paths are relative to a folder named after the torrent.</summary>
    public static TorrentFixture Multi(string name, params (string Path, long Length)[] files) => Build(name, files, single: false);

    private static TorrentFixture Build(string name, IReadOnlyList<(string Path, long Length)> files, bool single)
    {
        using var info = new MemoryStream();
        void Write(string text) => info.Write(Encoding.ASCII.GetBytes(text));
        void WriteBytes(byte[] value) { Write(value.Length + ":"); info.Write(value); }
        Write("d");
        if (single) Write("6:lengthi" + files[0].Length + "e");
        else
        {
            WriteBytes(Encoding.UTF8.GetBytes("files"));
            Write("l");
            foreach (var (path, length) in files)
            {
                Write("d6:lengthi" + length + "e4:pathl");
                foreach (var segment in path.Split('/')) WriteBytes(Encoding.UTF8.GetBytes(segment));
                Write("ee");
            }

            Write("e");
        }

        WriteBytes(Encoding.UTF8.GetBytes("name"));
        WriteBytes(Encoding.UTF8.GetBytes(name));
        Write("12:piece lengthi262144e");
        WriteBytes(Encoding.UTF8.GetBytes("pieces"));
        WriteBytes(SHA1.HashData(Encoding.UTF8.GetBytes(name)));
        Write("e");
        var infoBytes = info.ToArray();
        using var torrent = new MemoryStream();
        torrent.Write(Encoding.ASCII.GetBytes("d8:announce31:http://tracker.invalid/announce4:info"));
        torrent.Write(infoBytes);
        torrent.Write(Encoding.ASCII.GetBytes("e"));
        var clientFiles = single ? files : files.Select(file => (name + "/" + file.Path, file.Length)).ToArray();
        return new TorrentFixture(name, torrent.ToArray(), Convert.ToHexStringLower(SHA1.HashData(infoBytes)), clientFiles);
    }
}

/// <summary>One feed row served by the Torznab boundary.</summary>
internal sealed record FeedItem(string Title, string Guid, string? Link, long? Size, int? Seeders, Dictionary<string, string> Attributes);

/// <summary>A real HTTP Torznab indexer: caps, feeds and torrent downloads.</summary>
internal sealed class TorznabBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    public string ApiKey { get; } = "idx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
    public List<FeedItem> MovieItems { get; } = [];
    public List<FeedItem> TvItems { get; } = [];
    public ConcurrentDictionary<string, byte[]> Torrents { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Queries { get; } = new();
    public Uri Address { get; private set; } = null!;

    public string Download(string id) => new Uri(Address, $"/dl/{id}").ToString();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/api", async context =>
        {
            Queries.Enqueue(context.Request.QueryString.Value ?? string.Empty);
            context.Response.ContentType = "application/xml";
            if (context.Request.Query["apikey"] != ApiKey)
            {
                await context.Response.WriteAsync("<?xml version=\"1.0\"?><error code=\"100\" description=\"Incorrect user credentials\"/>");
                return;
            }

            var mode = context.Request.Query["t"].ToString();
            if (mode == "caps") { await context.Response.WriteAsync(Caps); return; }
            List<FeedItem> items;
            lock (MovieItems) items = (mode == "tvsearch" ? TvItems : MovieItems).ToList();
            if (mode == "tvsearch" && int.TryParse(context.Request.Query["ep"], out var episode))
                items = items.Where(item => item.Title.Contains($"E{episode:00}", StringComparison.OrdinalIgnoreCase)).ToList();
            await context.Response.WriteAsync(Feed(items));
        });
        _app.MapGet("/dl/{id}", async context =>
        {
            var id = (string)context.Request.RouteValues["id"]!;
            if (!Torrents.TryGetValue(id, out var bytes)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "application/x-bittorrent";
            await context.Response.Body.WriteAsync(bytes);
        });
        await _app.StartAsync();
        Address = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private const string Caps = """
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <server title="JellyfinMod phase 5 boundary"/>
          <limits max="100" default="100"/>
          <searching>
            <search available="yes" supportedParams="q"/>
            <tv-search available="yes" supportedParams="q,tvdbid,season,ep"/>
            <movie-search available="yes" supportedParams="q,imdbid,tmdbid"/>
          </searching>
          <categories>
            <category id="2000" name="Movies"/>
            <category id="5000" name="TV"/>
          </categories>
        </caps>
        """;

    private static string Feed(IEnumerable<FeedItem> items)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>""");
        var list = items.ToArray();
        builder.Append($"""<torznab:response offset="0" total="{list.Length}"/>""");
        foreach (var item in list)
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

/// <summary>One file of a torrent the Transmission boundary holds.</summary>
internal sealed class HeldFile
{
    public required string Name { get; init; }
    public required long Length { get; init; }
    public long Completed { get; set; }
    public bool Wanted { get; set; } = true;
}

/// <summary>One torrent the Transmission boundary holds.</summary>
internal sealed class HeldTorrent
{
    public required string Hash { get; init; }
    public required string Name { get; init; }
    public required string DownloadDir { get; set; }
    public required List<string> Labels { get; init; }
    public required List<HeldFile> Files { get; init; }
    public int SeedRatioMode { get; set; }
    public int SeedIdleMode { get; set; }
    public double SeedRatioLimit { get; set; } = 2;
    public double UploadRatio { get; set; }
    public long SecondsSeeding { get; set; }
    public long RateDownload { get; set; }
    public long Size => Files.Where(file => file.Wanted).Sum(file => file.Length);
    public long Left => Files.Where(file => file.Wanted).Sum(file => file.Length - file.Completed);
}

/// <summary>
/// A real HTTP Transmission RPC boundary speaking the legacy 4.x protocol (user decision 1): Basic authentication, the
/// 409 session-id handshake, session-get, torrent-add, torrent-get, torrent-set and torrent-remove. It "downloads" by
/// writing real bytes into the local folder its download directory maps to, and "delete-local-data" removes exactly the
/// torrent's own files, as Transmission does.
/// </summary>
internal sealed class TransmissionBoundary : IAsyncDisposable
{
    private WebApplication _app = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    public string Username { get; } = "jellyfinmod";
    public string Password { get; } = "tx-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    public ConcurrentDictionary<string, HeldTorrent> Torrents { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, TorrentFixture> Fixtures { get; } = new(StringComparer.Ordinal);
    /// <summary>Client path prefix to local folder, for writing and deleting data.</summary>
    public ConcurrentDictionary<string, string> Roots { get; } = new(StringComparer.Ordinal);
    public int GetCalls;
    public int RemoveCalls;
    public bool Offline { get; set; }
    public Uri Endpoint { get; private set; } = null!;

    public void Register(TorrentFixture fixture) => Fixtures[Convert.ToBase64String(fixture.Bytes)] = fixture;

    public string Local(string clientPath)
    {
        foreach (var (prefix, local) in Roots.OrderByDescending(pair => pair.Key.Length))
            if (clientPath == prefix || clientPath.StartsWith(prefix + "/", StringComparison.Ordinal))
                return local + clientPath[prefix.Length..];
        throw new InvalidOperationException("The boundary has no local folder for " + clientPath);
    }

    /// <summary>Downloads to a fraction of every wanted file, writing real bytes.</summary>
    public void Progress(string hash, double fraction, long rate = 1_000_000)
    {
        var torrent = Torrents[hash];
        foreach (var file in torrent.Files.Where(file => file.Wanted))
        {
            var target = (long)Math.Floor(file.Length * Math.Clamp(fraction, 0, 1));
            var path = Local(torrent.DownloadDir + "/" + file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write))
            {
                if (stream.Length < target)
                {
                    stream.Seek(0, SeekOrigin.End);
                    var chunk = new byte[64 * 1024];
                    RandomNumberGenerator.Fill(chunk);
                    for (var written = stream.Length; written < target;)
                    {
                        var count = (int)Math.Min(chunk.Length, target - written);
                        stream.Write(chunk, 0, count);
                        written += count;
                    }
                }
            }

            file.Completed = target;
        }

        torrent.RateDownload = fraction >= 1 ? 0 : rate;
    }

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
        if (Offline)
        {
            context.Abort();
            return;
        }

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password));
        if (context.Request.Headers.Authorization != expected)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (context.Request.Headers["X-Transmission-Session-Id"] != _sessionId)
        {
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
                result = new JsonObject
                {
                    ["version"] = "4.0.6 (phase 5 boundary)", ["rpc-version"] = 17, ["seedRatioLimited"] = false, ["seedRatioLimit"] = 2.0,
                    ["idle-seeding-limit-enabled"] = false, ["idle-seeding-limit"] = 30, ["incomplete-dir-enabled"] = false,
                    ["rename-partial-files"] = false
                };
                break;
            case "torrent-get":
                Interlocked.Increment(ref GetCalls);
                var ids = arguments["ids"] is JsonArray idArray
                    ? idArray.Select(id => id!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
                var list = new JsonArray();
                var index = 0;
                foreach (var torrent in Torrents.Values.Where(value => ids is null || ids.Contains(value.Hash)).ToArray())
                    list.Add(Describe(torrent, ++index));
                result = new JsonObject { ["torrents"] = list };
                break;
            case "torrent-add":
                var metainfo = arguments["metainfo"]!.GetValue<string>();
                var fixture = Fixtures[metainfo];
                if (Torrents.TryGetValue(fixture.InfoHash, out var existing))
                {
                    result = new JsonObject { ["torrent-duplicate"] = new JsonObject { ["hashString"] = existing.Hash, ["id"] = 1 } };
                    break;
                }

                Torrents[fixture.InfoHash] = new HeldTorrent
                {
                    Hash = fixture.InfoHash, Name = fixture.Name, DownloadDir = arguments["download-dir"]!.GetValue<string>(),
                    Labels = arguments["labels"]!.AsArray().Select(label => label!.GetValue<string>()).ToList(),
                    Files = fixture.Files.Select(file => new HeldFile { Name = file.Path, Length = file.Length }).ToList()
                };
                result = new JsonObject { ["torrent-added"] = new JsonObject { ["hashString"] = fixture.InfoHash, ["id"] = Torrents.Count } };
                break;
            case "torrent-set":
                foreach (var id in arguments["ids"]!.AsArray().Select(value => value!.GetValue<string>()))
                    if (Torrents.TryGetValue(id.ToLowerInvariant(), out var torrent))
                    {
                        if (arguments["seedRatioMode"] is { } ratio) torrent.SeedRatioMode = ratio.GetValue<int>();
                        if (arguments["seedIdleMode"] is { } idle) torrent.SeedIdleMode = idle.GetValue<int>();
                    }

                result = [];
                break;
            case "torrent-remove":
                Interlocked.Increment(ref RemoveCalls);
                var deleteData = arguments["delete-local-data"]?.GetValue<bool>() == true;
                foreach (var id in arguments["ids"]!.AsArray().Select(value => value!.GetValue<string>()))
                    if (Torrents.TryRemove(id.ToLowerInvariant(), out var removed) && deleteData)
                        DeleteData(removed);
                result = [];
                break;
            default:
                await context.Response.WriteAsJsonAsync(new { result = "method name not recognized", arguments = new { } });
                return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(new JsonObject { ["result"] = "success", ["arguments"] = result }.ToJsonString());
    }

    private void DeleteData(HeldTorrent torrent)
    {
        foreach (var file in torrent.Files)
        {
            var path = Local(torrent.DownloadDir + "/" + file.Name);
            if (File.Exists(path)) File.Delete(path);
        }

        // Transmission removes the torrent's own folder when it is left empty.
        var folder = Local(torrent.DownloadDir + "/" + torrent.Name);
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any(File.Exists))
            Directory.Delete(folder, true);
    }

    private static JsonObject Describe(HeldTorrent torrent, int id)
    {
        var size = torrent.Size;
        var left = torrent.Left;
        var complete = left == 0;
        return new JsonObject
        {
            ["id"] = id, ["hashString"] = torrent.Hash, ["name"] = torrent.Name, ["downloadDir"] = torrent.DownloadDir,
            ["labels"] = new JsonArray(torrent.Labels.Select(label => (JsonNode)label).ToArray()),
            ["percentDone"] = size == 0 ? 0 : (double)(size - left) / size, ["sizeWhenDone"] = size, ["leftUntilDone"] = left,
            ["rateDownload"] = torrent.RateDownload, ["eta"] = complete ? -1 : 600, ["status"] = complete ? 6 : 4,
            ["isFinished"] = false, ["uploadRatio"] = torrent.UploadRatio, ["secondsSeeding"] = torrent.SecondsSeeding,
            ["seedRatioMode"] = torrent.SeedRatioMode, ["seedRatioLimit"] = torrent.SeedRatioLimit, ["seedIdleMode"] = torrent.SeedIdleMode,
            ["seedIdleLimit"] = 30, ["etaIdle"] = -1, ["error"] = 0,
            ["files"] = new JsonArray(torrent.Files.Select(file => (JsonNode)new JsonObject
            {
                ["name"] = file.Name, ["length"] = file.Length, ["bytesCompleted"] = file.Completed
            }).ToArray()),
            ["fileStats"] = new JsonArray(torrent.Files.Select(file => (JsonNode)new JsonObject
            {
                ["wanted"] = file.Wanted, ["bytesCompleted"] = file.Completed, ["priority"] = 0
            }).ToArray())
        };
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal static class Json
{
    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>The host serializes Guids in Jellyfin's compact form, so parse any format.</summary>
    public static Guid AsGuid(this JsonElement value) => Guid.Parse(value.GetString()!);
}
