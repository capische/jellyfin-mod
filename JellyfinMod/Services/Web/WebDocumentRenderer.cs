using System.Text.RegularExpressions;

namespace JellyfinMod.Services.Web;

/// <summary>
/// Turns a bundle's own <c>jellyfinmod.html</c> into a document that works from somewhere else (P7.S3).
/// </summary>
/// <remarks>
/// <para>
/// The built document references its assets relatively, because in the fork's own <c>dist/</c> they sit beside it.
/// The plugin serves them from a per-bundle immutable path instead, and in S4 the document itself moves to the
/// host's web root. Both cases need the same thing: every relative reference made absolute against the bundle's
/// real location, and the bundle told where that is.
/// </para>
/// <para>
/// Only the entry scripts and stylesheets are rewritten here. Chunks, dictionaries and lazily imported assets
/// root themselves at the runtime script's own URL, because the fork builds with webpack's
/// <c>publicPath: 'auto'</c>; and <c>config.json</c> follows <c>window.__jfmodAssetRoot</c>.
/// </para>
/// </remarks>
public static partial class WebDocumentRenderer
{
    /// <summary>Marks a document this plugin produced, so it is never mistaken for a stock one.</summary>
    public const string Marker = "<!-- jellyfinmod:takeover -->";

    /// <summary>The stock copy the failsafe falls back to, written beside the patched document.</summary>
    public const string StockCopyName = "index.jellyfinmod-stock.html";

    /// <summary>
    /// How this renderer builds a document. Bumped whenever the output changes for the same inputs.
    /// </summary>
    /// <remarks>
    /// The takeover re-renders when the bundle changes, which is almost always the right trigger — but a plugin
    /// upgrade can change the rendering itself while the bundle stays put, and without this the engine would look
    /// at a correctly-recorded patched file and decide there was nothing to do. That failure is silent and
    /// survives restarts, which is the worst combination, so the renderer states its own version and the engine
    /// treats a change in it exactly like a change of bundle.
    /// </remarks>
    public const int Version = 2;

    /// <summary>Renders the served document with its assets rooted at <paramref name="assetRoot"/>.</summary>
    /// <param name="document">The bundle's own <c>jellyfinmod.html</c>.</param>
    /// <param name="assetRoot">Absolute path the bundle's files are served from, with a trailing slash.</param>
    /// <param name="withFailsafe">
    /// Whether to include the recovery script. Needed when this document replaces the host's own
    /// <c>index.html</c>, where a bundle that fails to load would otherwise leave a blank page and no way back.
    /// </param>
    public static string Render(string document, string assetRoot, bool withFailsafe = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetRoot);
        var root = assetRoot.EndsWith('/') ? assetRoot : assetRoot + '/';

        var rewritten = AssetReference().Replace(document, match =>
        {
            var url = match.Groups["url"].Value;
            return IsRelative(url)
                ? $"{match.Groups["attribute"].Value}=\"{root}{url}\""
                : match.Value;
        });

        if (withFailsafe)
        {
            // Only the files the interface cannot run without: entry scripts and stylesheets. A manifest or a
            // touch icon that fails to load is cosmetic, and tearing the page down to stock over one would be a
            // far worse failure than the one it was reacting to.
            rewritten = ScriptOrStylesheet().Replace(rewritten, match =>
                match.Value.Contains("onerror", StringComparison.OrdinalIgnoreCase)
                    ? match.Value
                    : match.Value[..^1] + " onerror=\"__jfmodStock()\">");
        }

        // Placed first in <head> so both run before the deferred entry scripts.
        var preamble = Marker
            + "<script>window.__jfmodAssetRoot=" + JsonString(root) + ";</script>"
            + (withFailsafe ? "<script>" + Failsafe + "</script>" : string.Empty);
        var head = HeadTag().Match(rewritten);
        return head.Success
            ? rewritten[..(head.Index + head.Length)] + preamble + rewritten[(head.Index + head.Length)..]
            : preamble + rewritten;
    }

    /// <summary>
    /// The recovery script, inlined into a patched document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the answer to the one genuinely dangerous thing this plugin does. The patched document is the
    /// login page, and it is also the Dashboard that an administrator would use to undo a bad patch — so if the
    /// bundle it points at is missing, the usual way out is inside the page that is broken.
    /// </para>
    /// <para>
    /// So the stock page is kept beside the patched one and loaded in its place, at the same address, whenever
    /// the bundle does not run: a script or stylesheet failed, or the bundle never set its marker by the time the
    /// document finished parsing. The plugin being uninstalled, disabled, or serving a bundle that was pruned all
    /// look the same from here, and all recover the same way.
    /// </para>
    /// <para>
    /// <c>XMLHttpRequest</c> and <c>document.write</c> are deliberate rather than dated: they work on the oldest
    /// webOS engines this fork supports, where <c>fetch</c> and module scripts do not. A <c>sessionStorage</c>
    /// latch stops a failing stock page from reloading itself for ever.
    /// </para>
    /// </remarks>
    private const string Failsafe = """
        (function () {
          var KEY = 'jfmod-stock-fallback';
          var STOCK = 'index.jellyfinmod-stock.html';
          var done = false;
          window.__jfmodStock = function () {
            if (done) { return; }
            done = true;
            try { if (sessionStorage.getItem(KEY)) { return; } sessionStorage.setItem(KEY, '1'); } catch (e) {}
            var request = new XMLHttpRequest();
            request.open('GET', STOCK, true);
            request.onload = function () {
              if (request.status >= 200 && request.status < 300 && request.responseText) {
                document.open(); document.write(request.responseText); document.close();
              } else { location.replace(STOCK + location.search + location.hash); }
            };
            request.onerror = function () { location.replace(STOCK + location.search + location.hash); };
            request.send(null);
          };
          window.addEventListener('DOMContentLoaded', function () {
            // Deferred scripts have run by now, so the bundle has had its chance to say it executed.
            if (!window.__jfmodBundle) { window.__jfmodStock(); }
            else { try { sessionStorage.removeItem(KEY); } catch (e) {} }
          });
        })();
        """;

    /// <summary>Whether this document was produced by <see cref="Render"/> rather than shipped by the host.</summary>
    public static bool IsRendered(string document) => document.Contains(Marker, StringComparison.Ordinal);

    /// <summary>
    /// A URL is ours to rewrite only when it is relative to the document: absolute URLs, protocol-relative URLs,
    /// root-relative paths, fragments and inline data all already resolve correctly wherever the document lives.
    /// </summary>
    private static bool IsRelative(string url) =>
        url.Length > 0
        && !url.StartsWith('/')
        && !url.StartsWith('#')
        && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !AbsoluteScheme().IsMatch(url);

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    [GeneratedRegex("(?<attribute>\\bsrc|\\bhref)\\s*=\\s*\"(?<url>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AssetReference();

    [GeneratedRegex("<head(?:\\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTag();

    [GeneratedRegex("<script\\s[^>]*\\bsrc\\s*=[^>]*>|<link\\s[^>]*rel\\s*=\\s*\"stylesheet\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStylesheet();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteScheme();
}
