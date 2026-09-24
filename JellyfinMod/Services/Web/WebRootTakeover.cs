using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Owns exactly one file in the host's web root: <c>index.html</c> (P7.S4).
/// </summary>
/// <remarks>
/// <para>
/// This is the most dangerous thing the plugin does, so it is built to be boring. The stock document is copied
/// and verified before anything is written; the patched document is only ever rendered from that copy, never from
/// a file that already carries the marker; every write is a temporary file renamed into place; and the patched
/// document carries a failsafe that loads the stock copy back if the bundle does not run.
/// </para>
/// <para>
/// Two files are written into the web root and no others: the patched <c>index.html</c> and
/// <c>index.jellyfinmod-stock.html</c> beside it. The pristine copy and the recorded hashes live under
/// <c>&lt;plugin-data&gt;/web-root/</c>, so losing the database never loses the way back.
/// </para>
/// </remarks>
public sealed class WebRootTakeover
{
    private const string IndexName = "index.html";
    private const string PristineName = "index.html.pristine";
    private const string PreviousPristineName = "index.html.pristine.prev";
    private const string StateName = "state.json";
    private const string WriteProbeName = ".jellyfinmod-write-probe";

    /// <summary>Printed at every patch and restore, and in the plugin README.</summary>
    public const string ManualRecovery =
        "To restore the original Jellyfin interface by hand, copy 'index.jellyfinmod-stock.html' over "
        + "'index.html' in the server's web directory, or copy 'index.html.pristine' from the JellyfinMod data "
        + "directory. In the JellyfinMod Docker image, recreating the container also restores it.";

    private readonly string _stateDirectory;
    private readonly WebBundleStore _bundles;
    private readonly ILogger<WebRootTakeover> _logger;
    private readonly string _baseUrl;
    private readonly Func<TakeoverEvent, CancellationToken, Task>? _record;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TakeoverEvent? _change;

    /// <summary>Initializes a new instance of the <see cref="WebRootTakeover"/> class.</summary>
    /// <param name="dataPath">The plugin's version-independent data directory.</param>
    /// <param name="bundles">The installed bundles.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="baseUrl">The host's configured URL prefix.</param>
    /// <param name="record">
    /// Called once after every patch and every restore, outside the file work, to put it on the durable record
    /// (the <c>interface_patched</c> / <c>interface_restored</c> history rows of §4.6). Its failure is logged and
    /// never undoes or blocks the file change.
    /// </param>
    public WebRootTakeover(string dataPath, WebBundleStore bundles, ILogger<WebRootTakeover> logger, string baseUrl = "",
        Func<TakeoverEvent, CancellationToken, Task>? record = null)
    {
        _record = record;
        _stateDirectory = Path.Combine(dataPath, "web-root");
        _bundles = bundles;
        _logger = logger;
        _baseUrl = string.IsNullOrEmpty(baseUrl.Trim('/')) ? string.Empty : "/" + baseUrl.Trim('/');
    }

    /// <summary>Gets the last observed state.</summary>
    public TakeoverState State { get; private set; } = new();

    /// <summary>
    /// Brings the web root into the state the configuration asks for, whatever it is in now.
    /// </summary>
    /// <param name="enabled">Whether the takeover is wanted.</param>
    /// <param name="webRoot">The host's web directory.</param>
    /// <param name="reason">
    /// What prompted this run: <c>startup</c> (the plugin itself), <c>setting</c> (an administrator changed the switch)
    /// or <c>restoreStock</c> (an administrator asked for the stock page without changing the switch).
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<TakeoverState> ReconcileAsync(
        bool enabled, string? webRoot, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _change = null;
            try
            {
                State = Reconcile(enabled, webRoot, reason);
            }
            finally
            {
                // Recorded even when a later step failed: the file change it describes did happen.
                if (_change is { } change) await RecordAsync(change).ConfigureAwait(false);
            }

            return State;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never fail startup over the interface. A server whose page could not be patched still runs.
            _logger.LogError(error, "JellyfinMod could not reconcile the web root; the interface is unchanged");
            State = State with { Status = "inconsistent", Blocker = "web_root_error" };
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    private TakeoverState Reconcile(bool enabled, string? webRoot, string reason)
    {
        if (string.IsNullOrEmpty(webRoot) || !Directory.Exists(webRoot))
            return Blocked("readOnly", "web_root_missing", webRoot);

        if (!CanWrite(webRoot))
            return Blocked("readOnly", "web_root_read_only", webRoot);

        Directory.CreateDirectory(_stateDirectory);
        var indexPath = Path.Combine(webRoot, IndexName);
        if (!File.Exists(indexPath))
            return Blocked("readOnly", "web_root_no_index", webRoot);

        // The archive shape: the host is already serving this fork's own build, which carries the mod document
        // beside its index. Nothing to take over, and patching would fight whoever deployed it.
        if (File.Exists(Path.Combine(webRoot, WebBundleStore.DocumentName)))
        {
            return new TakeoverState
            {
                Status = "forkServedByHost",
                WebRoot = "writable"
            };
        }

        var record = ReadState();
        var observed = File.ReadAllText(indexPath);
        var observedHash = Hash(observed);
        var marked = WebDocumentRenderer.IsRendered(observed);

        if (!enabled)
        {
            return marked ? Restore(webRoot, indexPath, record, reason == "restoreStock" ? "restoreStock" : "setting") : new TakeoverState
            {
                Status = "stock",
                WebRoot = "writable",
                StockSha256 = record?.StockSha256 ?? observedHash
            };
        }

        var bundle = _bundles.Current;
        if (bundle is null)
        {
            // Nothing to patch with. If a previous patch is still in place it must come out, or the page points
            // at a bundle that is no longer served.
            return marked
                ? Restore(webRoot, indexPath, record, "noBundle")
                : new TakeoverState { Status = "stock", WebRoot = "writable", Blocker = _bundles.Blocker ?? "web_bundle_missing" };
        }

        if (marked)
        {
            if (record is null || !File.Exists(Path.Combine(_stateDirectory, PristineName)))
            {
                // A patched file with no way back. Touching it further could destroy the only stock copy left.
                _logger.LogError(
                    "JellyfinMod found a patched web root with no pristine copy and will not write to it. {Recovery}",
                    ManualRecovery);
                return new TakeoverState { Status = "inconsistent", WebRoot = "writable", Blocker = "pristine_missing" };
            }

            if (observedHash == record.PatchedSha256
                && record.BundleId == bundle.BundleId
                && record.RendererVersion == WebDocumentRenderer.Version
                && record.BaseUrl == _baseUrl)
            {
                return Patched(record, "writable");
            }

            if (record.RendererVersion != WebDocumentRenderer.Version)
            {
                _logger.LogInformation(
                    "JellyfinMod is re-rendering its patched index.html: it was written by renderer version {Old}, this build is {New}",
                    record.RendererVersion, WebDocumentRenderer.Version);
            }

            if (observedHash != record.PatchedSha256)
            {
                _logger.LogWarning(
                    "JellyfinMod found its patched index.html modified since it was written; re-rendering it from the pristine copy");
            }

            // A new bundle or renderer is "bundle"; a hand edit or a changed prefix is the plugin repairing itself.
            var rerender = record.BundleId != bundle.BundleId || record.RendererVersion != WebDocumentRenderer.Version
                ? "bundle" : "startup";
            return Patch(webRoot, indexPath, record.StockSha256, bundle, rerender, record);
        }

        // Unmarked. Either the first patch, or the host replaced its own index.html.
        if (record is not null && observedHash != record.StockSha256)
        {
            _logger.LogWarning(
                "JellyfinMod found a different stock index.html ({Observed} instead of {Recorded}); the host was "
                + "probably upgraded. Recording the new one and re-applying the interface.",
                observedHash, record.StockSha256);
            ArchivePreviousPristine();
        }

        return Patch(webRoot, indexPath, observedHash, bundle, reason, record, stockDocument: observed);
    }

    /// <summary>
    /// Writes the pristine copy, the stock copy, the recovery record and then the patched document.
    /// </summary>
    /// <remarks>
    /// Order is the safety property. Nothing replaces <c>index.html</c> until a verified copy of the original
    /// exists in two places, so there is no window in which the only stock document is the one being overwritten.
    /// </remarks>
    private TakeoverState Patch(
        string webRoot,
        string indexPath,
        string stockHash,
        InstalledBundle bundle,
        string reason,
        TakeoverRecord? record,
        string? stockDocument = null)
    {
        var pristinePath = Path.Combine(_stateDirectory, PristineName);

        if (stockDocument is not null)
        {
            WriteAtomic(pristinePath, stockDocument);
            if (Hash(File.ReadAllText(pristinePath)) != stockHash)
                throw new IOException("The pristine copy did not verify after it was written.");
        }

        var pristine = File.ReadAllText(pristinePath);
        var pristineHash = Hash(pristine);
        if (pristineHash != stockHash)
        {
            _logger.LogError(
                "JellyfinMod will not patch: the pristine copy hashes to {Actual} but the recorded stock is {Expected}. {Recovery}",
                pristineHash, stockHash, ManualRecovery);
            return new TakeoverState { Status = "inconsistent", WebRoot = "writable", Blocker = "patched_file_modified" };
        }

        WriteAtomic(Path.Combine(webRoot, WebDocumentRenderer.StockCopyName), pristine);

        // Rendered from the BUNDLE's own document, never from the host's. The host's index.html lists the host's
        // own scripts; rewriting those URLs to point into our bundle would load the bundle's *stock* entry, which
        // is a working Jellyfin but not this interface. The bundle document is also never a patched file, so
        // patches still cannot stack — the pristine copy's job is to be the way back, not the input.
        var source = Path.Combine(bundle.Directory, WebBundleStore.DocumentName);
        if (!File.Exists(source))
        {
            _logger.LogError("JellyfinMod will not patch: bundle {BundleId} has no {Document}", bundle.BundleId, WebBundleStore.DocumentName);
            return new TakeoverState { Status = "stock", WebRoot = "writable", Blocker = "web_bundle_corrupt" };
        }

        var patched = WebDocumentRenderer.Render(
            File.ReadAllText(source), $"{_baseUrl}/web-mod/{bundle.BundleId}/", withFailsafe: true);
        var patchedHash = Hash(patched);

        var updated = new TakeoverRecord
        {
            WebRoot = webRoot,
            StockSha256 = stockHash,
            PatchedSha256 = patchedHash,
            BundleId = bundle.BundleId,
            RendererVersion = WebDocumentRenderer.Version,
            BaseUrl = _baseUrl,
            PatchedAt = DateTime.UtcNow,
            PatchedBy = PatchedBy(reason, record)
        };
        // The record is a write-ahead recovery record: even if the process stops at the next rename,
        // restart can verify the pristine copy and reconcile either the old or the new live document.
        // A failed metadata write must never leave a newly patched index without recovery information.
        WriteState(updated);
        WriteAtomic(indexPath, patched);

        _change = new TakeoverEvent("interface_patched", updated.PatchedBy!, bundle.BundleId, record?.BundleId, stockHash, patchedHash);
        _logger.LogInformation(
            "JellyfinMod replaced the Jellyfin web interface at /web with bundle {BundleId} ({Reason}). "
            + "Original sha256 {StockHash}, patched sha256 {PatchedHash}. {Recovery}",
            bundle.BundleId, updated.PatchedBy, stockHash, patchedHash, ManualRecovery);

        return Patched(updated, "writable");
    }

    private TakeoverState Restore(string webRoot, string indexPath, TakeoverRecord? record, string reason)
    {
        var pristinePath = Path.Combine(_stateDirectory, PristineName);
        if (record is null || !File.Exists(pristinePath))
        {
            _logger.LogError("JellyfinMod cannot restore the original interface: no pristine copy. {Recovery}", ManualRecovery);
            return new TakeoverState { Status = "inconsistent", WebRoot = "writable", Blocker = "pristine_missing" };
        }

        var pristine = File.ReadAllText(pristinePath);
        if (Hash(pristine) != record.StockSha256)
        {
            _logger.LogError("JellyfinMod cannot restore: the pristine copy does not match its recorded hash. {Recovery}", ManualRecovery);
            return new TakeoverState { Status = "inconsistent", WebRoot = "writable", Blocker = "pristine_modified" };
        }

        WriteAtomic(indexPath, pristine);
        var restored = Hash(File.ReadAllText(indexPath));
        if (restored != record.StockSha256)
            throw new IOException("The restored index.html did not verify.");

        var stockCopy = Path.Combine(webRoot, WebDocumentRenderer.StockCopyName);
        if (File.Exists(stockCopy)) File.Delete(stockCopy);

        _change = new TakeoverEvent("interface_restored", reason, null, record.BundleId, record.StockSha256, record.PatchedSha256);
        WriteState(record with { PatchedSha256 = null, BundleId = null, PatchedAt = null, PatchedBy = null });
        _logger.LogInformation(
            "JellyfinMod restored the original Jellyfin web interface at /web ({Reason}); sha256 {Hash} matches the original",
            reason, restored);

        return new TakeoverState { Status = "stock", WebRoot = "writable", StockSha256 = record.StockSha256 };
    }

    /// <summary>
    /// The contract's three values (§4.6): <c>setting</c> when an administrator turned the switch on, <c>bundle</c>
    /// when a new bundle or renderer re-rendered an existing patch, and <c>automatic</c> for everything the plugin
    /// did by itself — the first patch after an install, a re-patch after a host upgrade or a container recreate.
    /// </summary>
    private static string PatchedBy(string reason, TakeoverRecord? record) => reason switch
    {
        "bundle" => "bundle",
        "setting" => "setting",
        _ => "automatic"
    };

    private async Task RecordAsync(TakeoverEvent change)
    {
        if (_record is null) return;
        try
        {
            await _record(change, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The page already changed; the log line above is the record of last resort.
            _logger.LogWarning("JellyfinMod could not write the {Event} history row ({Error})", change.EventType, error.GetType().Name);
        }
    }

    private TakeoverState Patched(TakeoverRecord record, string webRoot) => new()
    {
        Status = "patched",
        WebRoot = webRoot,
        StockSha256 = record.StockSha256,
        PatchedSha256 = record.PatchedSha256,
        BundleId = record.BundleId,
        PatchedAt = record.PatchedAt,
        PatchedBy = record.PatchedBy
    };

    private TakeoverState Blocked(string status, string blocker, string? webRoot)
    {
        _logger.LogWarning(
            "JellyfinMod is not replacing the web interface ({Blocker}); it stays reachable at /web-mod", blocker);
        return new TakeoverState { Status = status, WebRoot = webRoot is null ? "unknown" : "readOnly", Blocker = blocker };
    }

    /// <summary>Proves the directory is writable by writing, rather than by reading permission bits.</summary>
    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, WriteProbeName);
        try
        {
            File.WriteAllText(probe, string.Empty);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A probe we cannot remove is untidy, not dangerous.
            }
        }
    }

    private void ArchivePreviousPristine()
    {
        var pristine = Path.Combine(_stateDirectory, PristineName);
        if (!File.Exists(pristine)) return;
        File.Move(pristine, Path.Combine(_stateDirectory, PreviousPristineName), true);
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".jellyfinmod-tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }

    private static string Hash(string contents) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));

    private TakeoverRecord? ReadState()
    {
        try
        {
            var path = Path.Combine(_stateDirectory, StateName);
            return File.Exists(path) ? JsonSerializer.Deserialize<TakeoverRecord>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteState(TakeoverRecord record) =>
        WriteAtomic(Path.Combine(_stateDirectory, StateName), JsonSerializer.Serialize(record));
}

/// <summary>One patch or restore, as the history row records it: ids and hashes, never a path.</summary>
/// <param name="EventType"><c>interface_patched</c> or <c>interface_restored</c>.</param>
/// <param name="By">
/// For a patch, <c>automatic</c>, <c>setting</c> or <c>bundle</c>; for a restore, <c>setting</c> (the switch is off),
/// <c>restoreStock</c> (an administrator asked, the switch unchanged) or <c>noBundle</c> (nothing left to serve).
/// </param>
/// <param name="BundleId">The bundle now in place, for a patch.</param>
/// <param name="PreviousBundleId">The bundle the replaced document pointed at, if any.</param>
/// <param name="StockSha256">The host's own <c>index.html</c> hash.</param>
/// <param name="PatchedSha256">The patched document's hash: the new one for a patch, the removed one for a restore.</param>
public sealed record TakeoverEvent(string EventType, string By, string? BundleId, string? PreviousBundleId, string StockSha256,
    string? PatchedSha256);

/// <summary>What the engine recorded about the web root, kept outside the database.</summary>
public sealed record TakeoverRecord
{
    /// <summary>Gets the web directory this record describes.</summary>
    [JsonPropertyName("webRoot")]
    public string WebRoot { get; init; } = string.Empty;

    /// <summary>Gets the hash of the host's own index.html.</summary>
    [JsonPropertyName("stockSha256")]
    public string StockSha256 { get; init; } = string.Empty;

    /// <summary>Gets the hash of the document this plugin wrote, when one is in place.</summary>
    [JsonPropertyName("patchedSha256")]
    public string? PatchedSha256 { get; init; }

    /// <summary>Gets the bundle the patched document points at.</summary>
    [JsonPropertyName("bundleId")]
    public string? BundleId { get; init; }

    /// <summary>Gets the renderer version that produced the patched document.</summary>
    [JsonPropertyName("rendererVersion")]
    public int RendererVersion { get; init; }

    /// <summary>Gets the host URL prefix used to render the document.</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Gets when the patch was applied.</summary>
    [JsonPropertyName("patchedAt")]
    public DateTime? PatchedAt { get; init; }

    /// <summary>Gets what applied it: automatic, setting or bundle.</summary>
    [JsonPropertyName("patchedBy")]
    public string? PatchedBy { get; init; }
}

/// <summary>What Health and the settings area report about the web root.</summary>
public sealed record TakeoverState
{
    /// <summary>Gets the state: stock, patched, readOnly, forkServedByHost or inconsistent.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Gets whether the web root is writable, read-only or unknown.</summary>
    public string WebRoot { get; init; } = "unknown";

    /// <summary>Gets the hash of the host's own index.html, once observed.</summary>
    public string? StockSha256 { get; init; }

    /// <summary>Gets the hash of the document in place, when this plugin wrote it.</summary>
    public string? PatchedSha256 { get; init; }

    /// <summary>Gets the bundle the patched document points at.</summary>
    public string? BundleId { get; init; }

    /// <summary>Gets when the patch was applied.</summary>
    public DateTime? PatchedAt { get; init; }

    /// <summary>Gets what applied it, so an administrator can see why the page changed.</summary>
    public string? PatchedBy { get; init; }

    /// <summary>Gets why the takeover is not in place, when it is not.</summary>
    public string? Blocker { get; init; }

    /// <summary>Gets the sentence an administrator needs if everything else fails.</summary>
    public string Recovery => WebRootTakeover.ManualRecovery;
}
