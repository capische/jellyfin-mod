using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Owns the extracted web bundles under <c>&lt;plugin-data&gt;/web/</c> (P7.S3).
/// </summary>
/// <remarks>
/// <para>
/// The plugin package ships the built fork as <c>jellyfinmod-web.zip</c>. On startup that zip is extracted, once,
/// into a directory named after the bundle's own id, and verified against the manifest before anything is served
/// from it. A bundle that fails verification is not served: the interface stays at whatever was already there and
/// Health reports the reason, because serving half a bundle is worse than serving none.
/// </para>
/// <para>
/// Previous bundles are kept for a grace period. A television app that loaded the old page holds its asset URLs
/// until it is fully closed and reopened, so deleting the old bundle the moment a new one arrives would break a
/// running client for no gain.
/// </para>
/// </remarks>
public sealed class WebBundleStore
{
    private const string ZipName = "jellyfinmod-web.zip";
    private const string ManifestName = "jellyfinmod-web.json";
    private const string RetainedName = "retained.json";

    /// <summary>The most bundles kept on disk, the current one included.</summary>
    public const int MaxRetained = 3;

    /// <summary>The document the plugin serves; the stock entry's index.html is never used here.</summary>
    public const string DocumentName = "jellyfinmod.html";

    private readonly string _root;
    private readonly string _packageDirectory;
    private readonly ILogger<WebBundleStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="WebBundleStore"/> class.</summary>
    /// <param name="dataPath">The plugin's version-independent data directory.</param>
    /// <param name="packageDirectory">The directory the plugin assembly and its artifacts were installed into.</param>
    /// <param name="logger">The logger.</param>
    public WebBundleStore(string dataPath, string packageDirectory, ILogger<WebBundleStore> logger)
    {
        _root = Path.Combine(dataPath, "web");
        _packageDirectory = packageDirectory;
        _logger = logger;
    }

    /// <summary>Gets the bundle currently served, or null when none is installed.</summary>
    public InstalledBundle? Current { get; private set; }

    /// <summary>Gets why no bundle is being served, or null when one is.</summary>
    public string? Blocker { get; private set; }

    /// <summary>Gets the ids kept on disk, newest first.</summary>
    public IReadOnlyList<string> RetainedBundleIds { get; private set; } = [];

    /// <summary>Extracts and verifies the packaged bundle if it is not already installed, then prunes old ones.</summary>
    /// <param name="graceDays">How long a superseded bundle stays reachable for clients that already loaded it.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task InstallAsync(int graceDays, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var zipPath = Path.Combine(_packageDirectory, ZipName);
            if (!File.Exists(zipPath))
            {
                // A plugin build without a bundle is legitimate: the interface is then whatever the host serves.
                Blocker = "web_bundle_missing";
                Current = null;
                _logger.LogInformation("JellyfinMod ships no web bundle in this package; the stock interface is unchanged");
                return;
            }

            var installed = await InstallFromZipAsync(zipPath, cancellationToken).ConfigureAwait(false);
            if (installed is null) return;

            Current = installed;
            Blocker = null;
            Retain(installed.BundleId, graceDays);
            _logger.LogInformation(
                "JellyfinMod is serving web bundle {BundleId} built from {WebCommit}", installed.BundleId, installed.Manifest.WebCommit);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InstalledBundle?> InstallFromZipAsync(string zipPath, CancellationToken cancellationToken)
    {
        string bundleId;
        try
        {
            using var probe = ZipFile.OpenRead(zipPath);
            var manifestEntry = FindManifest(probe);
            if (manifestEntry is null)
            {
                Fail("web_bundle_manifest_missing", "the packaged bundle carries no " + ManifestName);
                return null;
            }

            await using var stream = manifestEntry.Open();
            var packaged = await JsonSerializer.DeserializeAsync<WebBundleManifest>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(packaged?.BundleId))
            {
                Fail("web_bundle_manifest_unreadable", "the packaged " + ManifestName + " has no bundleId");
                return null;
            }

            bundleId = packaged.BundleId;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            Fail("web_bundle_corrupt", "the packaged bundle could not be opened: " + error.Message);
            return null;
        }

        if (!IsSafeBundleId(bundleId))
        {
            Fail("web_bundle_corrupt", "the packaged bundle declares an unusable id");
            return null;
        }

        var target = Path.Combine(_root, bundleId);
        if (!Directory.Exists(target) && !await ExtractAsync(zipPath, target, bundleId, cancellationToken).ConfigureAwait(false))
            return null;

        var manifest = WebBundleManifest.Read(Path.Combine(target, ManifestName));
        if (manifest is null)
        {
            Fail("web_bundle_corrupt", "the extracted bundle has no readable manifest");
            return null;
        }

        var computed = ComputeBundleId(target);
        if (!string.Equals(computed, manifest.BundleId, StringComparison.Ordinal))
        {
            // The id is a hash of the bundle's own contents, so a mismatch means the files on disk are not the
            // files the build produced. Serving them would put an unknown page in front of everyone.
            Fail("web_bundle_corrupt", $"the extracted bundle hashes to {computed} but claims {manifest.BundleId}");
            return null;
        }

        if (!File.Exists(Path.Combine(target, DocumentName)))
        {
            Fail("web_bundle_corrupt", "the extracted bundle has no " + DocumentName);
            return null;
        }

        return new InstalledBundle(manifest.BundleId, target, manifest, DateTime.UtcNow);
    }

    private async Task<bool> ExtractAsync(string zipPath, string target, string bundleId, CancellationToken cancellationToken)
    {
        // Extract beside the target and rename, so a cancelled or failed extraction never leaves a directory that
        // looks installed.
        var staging = target + ".incoming-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(staging);
            await Task.Run(() => ExtractFlattened(zipPath, staging), cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, target);
            _logger.LogInformation("JellyfinMod extracted web bundle {BundleId}", bundleId);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail("web_bundle_corrupt", "the packaged bundle could not be extracted: " + error.Message);
            return false;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    /// <summary>
    /// Extracts the zip, dropping the single leading directory the packaging step adds, and refusing any entry
    /// that would land outside the destination.
    /// </summary>
    private static void ExtractFlattened(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var prefix = CommonPrefix(archive);
        var fullDestination = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0) continue;
            var path = Path.GetFullPath(Path.Combine(destination, relative));
            if (!path.StartsWith(fullDestination, StringComparison.Ordinal))
                throw new InvalidDataException("The bundle contains an entry outside its own directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    /// <summary>The directory prefix every entry shares, so a zip of <c>dist/</c> extracts as the bundle root.</summary>
    private static string CommonPrefix(ZipArchive archive)
    {
        var names = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).Select(entry => entry.FullName).ToList();
        if (names.Count == 0) return string.Empty;
        var first = names[0];
        var slash = first.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) return string.Empty;
        var candidate = first[..(slash + 1)];
        return names.TrueForAll(name => name.StartsWith(candidate, StringComparison.Ordinal)) ? candidate : string.Empty;
    }

    private static ZipArchiveEntry? FindManifest(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(entry =>
            Path.GetFileName(entry.FullName).Equals(ManifestName, StringComparison.Ordinal));

    /// <summary>
    /// Recomputes the bundle's declared identity from the files on disk, with the build's own rule: sha-256 over
    /// each file's relative name and content hash, sorted, excluding source maps, the manifest and the documents.
    /// </summary>
    internal static string ComputeBundleId(string directory)
    {
        var root = Path.GetFullPath(directory);
        var names = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(name => !name.EndsWith(".map", StringComparison.Ordinal)
                && name != ManifestName && name != DocumentName && name != "index.html")
            .Order(StringComparer.Ordinal)
            .ToList();

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in names)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(name));
            digest.AppendData([0]);
            using var file = File.OpenRead(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            digest.AppendData(SHA256.HashData(file));
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset())[..12];
    }

    /// <summary>A bundle id names a directory, so it may only be the characters the build actually produces.</summary>
    internal static bool IsSafeBundleId(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character));

    private void Fail(string blocker, string reason)
    {
        Blocker = blocker;
        Current = null;
        _logger.LogError("JellyfinMod will not serve its web bundle ({Blocker}): {Reason}", blocker, reason);
    }

    private void Retain(string currentId, int graceDays)
    {
        var record = ReadRetained();
        record[currentId] = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, graceDays));
        // At most three bundles (§4.5, PHASE7 default 6): the current one and the two most recent before it, so a
        // run of deployments cannot fill the disk while each one waits out its grace period.
        var newest = record.Where(pair => pair.Key != currentId).OrderByDescending(pair => pair.Value)
            .Take(MaxRetained - 1).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, installedAt) in record.ToList())
        {
            if (id == currentId || (installedAt >= cutoff && newest.Contains(id))) continue;
            record.Remove(id);
            var path = Path.Combine(_root, id);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                _logger.LogInformation("JellyfinMod pruned superseded web bundle {BundleId}", id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "JellyfinMod could not prune web bundle {BundleId}", id);
            }
        }

        // Directories nobody recorded (a manual copy, an interrupted upgrade) are left alone but not advertised.
        RetainedBundleIds = record.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
        WriteRetained(record);
    }

    private Dictionary<string, DateTime> ReadRetained()
    {
        try
        {
            var path = Path.Combine(_root, RetainedName);
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path))
                is { } values ? new(values, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void WriteRetained(Dictionary<string, DateTime> record)
    {
        try
        {
            File.WriteAllText(Path.Combine(_root, RetainedName), JsonSerializer.Serialize(record));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "JellyfinMod could not record which web bundles are retained");
        }
    }

    /// <summary>Resolves a file inside a bundle, or null when the id or path is not one this store serves.</summary>
    public string? ResolveFile(string bundleId, string relativePath)
    {
        if (!IsSafeBundleId(bundleId)) return null;
        var root = Path.GetFullPath(Path.Combine(_root, bundleId));
        if (!Directory.Exists(root)) return null;

        // Reject anything that could climb out before touching the filesystem, then prove it again afterwards.
        if (relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }
}

/// <summary>A bundle that is extracted, verified and ready to serve.</summary>
/// <param name="BundleId">Its identity.</param>
/// <param name="Directory">Where it was extracted.</param>
/// <param name="Manifest">What it says about itself.</param>
/// <param name="ServedAt">When this server started serving it.</param>
public sealed record InstalledBundle(string BundleId, string Directory, WebBundleManifest Manifest, DateTime ServedAt);
