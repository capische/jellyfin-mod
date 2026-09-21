using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Publishes this plugin as a Jellyfin repository the server can read from itself (P7.S5).
/// </summary>
/// <remarks>
/// <para>
/// A plugin installed by copying files has no repository behind it, so the Dashboard's details panel asks every
/// configured repository for the package, finds nothing, and shows "An error occurred while getting the plugin
/// details from the repository". The panel is the cosmetic part; the real cost is that there is no version
/// history and no update path.
/// </para>
/// <para>
/// So the plugin serves its own. The manifest is derived from the <c>meta.json</c> JPRM generated at package
/// time, never hand-written, so the repository cannot drift from the plugin it describes. The package it points
/// at is built from the installed plugin directory and its checksum is computed from the file that is actually
/// served, which is what makes an install through the Dashboard genuinely work rather than merely render.
/// </para>
/// <para>
/// Self-hosted on purpose: it needs no internet access, which matters for a server that has none. Publishing a
/// manifest on the public internet is the right answer for distributing releases to other people, and is a
/// separate thing from this.
/// </para>
/// </remarks>
public sealed class PluginRepository
{
    private const string MetaName = "meta.json";

    private readonly string _packageDirectory;
    private readonly string _repositoryDirectory;
    private readonly ILogger<PluginRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PublishedPackage? _published;

    /// <summary>Initializes a new instance of the <see cref="PluginRepository"/> class.</summary>
    /// <param name="packageDirectory">The directory the plugin was installed into.</param>
    /// <param name="dataPath">The plugin's version-independent data directory.</param>
    /// <param name="logger">The logger.</param>
    public PluginRepository(string packageDirectory, string dataPath, ILogger<PluginRepository> logger)
    {
        _packageDirectory = packageDirectory;
        _repositoryDirectory = Path.Combine(dataPath, "repository");
        _logger = logger;
    }

    /// <summary>The package file name a client downloads, which is also the route's last segment.</summary>
    public string? PackageFileName => _published?.FileName;

    /// <summary>Gets the built package's path, or null when none could be built.</summary>
    public string? PackagePath => _published?.Path;

    /// <summary>
    /// Builds the downloadable package if it is missing or stale, and returns what the manifest should say.
    /// </summary>
    public async Task<PublishedPackage?> PublishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metaPath = Path.Combine(_packageDirectory, MetaName);
            var meta = ReadMeta(metaPath);
            if (meta is null)
            {
                _logger.LogWarning(
                    "JellyfinMod cannot publish its own repository: {Meta} is missing or unreadable. The plugin works; "
                    + "the Dashboard will keep reporting that it has no repository", MetaName);
                _published = null;
                return null;
            }

            Directory.CreateDirectory(_repositoryDirectory);
            var fileName = $"{meta.Name}_{meta.Version}.zip";
            var path = Path.Combine(_repositoryDirectory, fileName);

            if (IsStale(path))
            {
                Build(path);
                _logger.LogInformation("JellyfinMod built its installable package {FileName}", fileName);
            }

            _published = new PublishedPackage(meta, fileName, path, Checksum(path));
            return _published;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Never fail startup over the Dashboard's details panel.
            _logger.LogError(error, "JellyfinMod could not publish its own repository");
            _published = null;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The repository manifest, in the shape the server parses: an array of packages, each with its versions.
    /// </summary>
    /// <param name="sourceUrl">Absolute URL the package is downloadable from.</param>
    /// <param name="repositoryUrl">Absolute URL of this manifest.</param>
    public string? Manifest(string sourceUrl, string repositoryUrl)
    {
        var published = _published;
        if (published is null) return null;

        var meta = published.Meta;
        var package = new JsonObject
        {
            ["guid"] = meta.Guid,
            ["name"] = meta.Name,
            ["description"] = meta.Description,
            ["overview"] = meta.Overview,
            ["owner"] = meta.Owner,
            ["category"] = meta.Category,
            ["versions"] = new JsonArray(new JsonObject
            {
                ["version"] = meta.Version,
                ["changelog"] = meta.Changelog ?? string.Empty,
                ["targetAbi"] = meta.TargetAbi,
                ["sourceUrl"] = sourceUrl,
                // Hex MD5 of the very file `sourceUrl` serves; the server rejects the download otherwise.
                ["checksum"] = published.Checksum,
                ["timestamp"] = meta.Timestamp,
                ["repositoryName"] = "JellyfinMod",
                ["repositoryUrl"] = repositoryUrl
            })
        };

        return new JsonArray(package).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The package is rebuilt when it is absent or older than anything it is built from.</summary>
    private bool IsStale(string path)
    {
        if (!File.Exists(path)) return true;
        var built = File.GetLastWriteTimeUtc(path);
        return Directory.EnumerateFiles(_packageDirectory)
            .Any(source => File.GetLastWriteTimeUtc(source) > built);
    }

    /// <summary>
    /// Packages the installed plugin directory, flat, which is the layout the server expects to extract.
    /// </summary>
    private void Build(string path)
    {
        var temporary = path + ".building";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var source in Directory.EnumerateFiles(_packageDirectory).Order(StringComparer.Ordinal))
                {
                    archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Fastest);
                }
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Checksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    private static PluginMeta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var meta = JsonSerializer.Deserialize<PluginMeta>(File.ReadAllText(path));
            return string.IsNullOrEmpty(meta?.Guid) || string.IsNullOrEmpty(meta.Version) ? null : meta;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>What JPRM wrote about this build, and the package built from it.</summary>
/// <param name="Meta">The generated plugin metadata.</param>
/// <param name="FileName">The package's file name.</param>
/// <param name="Path">Where the package was built.</param>
/// <param name="Checksum">Hex MD5 of that file.</param>
public sealed record PublishedPackage(PluginMeta Meta, string FileName, string Path, string Checksum);

/// <summary>The fields of JPRM's <c>meta.json</c> that a repository manifest needs.</summary>
public sealed class PluginMeta
{
    /// <summary>Gets or sets the plugin's identity.</summary>
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    /// <summary>Gets or sets its name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the short description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the longer overview.</summary>
    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    /// <summary>Gets or sets the owner.</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the category.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets this build's version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the lowest server version it loads on.</summary>
    [JsonPropertyName("targetAbi")]
    public string TargetAbi { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was built.</summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the changelog for this version.</summary>
    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }
}
