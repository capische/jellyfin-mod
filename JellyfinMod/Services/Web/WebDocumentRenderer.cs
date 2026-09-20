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

    /// <summary>Renders the served document with its assets rooted at <paramref name="assetRoot"/>.</summary>
    /// <param name="document">The bundle's own <c>jellyfinmod.html</c>.</param>
    /// <param name="assetRoot">Absolute path the bundle's files are served from, with a trailing slash.</param>
    public static string Render(string document, string assetRoot)
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

        // Placed first in <head> so it is set before the deferred entry scripts run.
        var preamble = Marker + "<script>window.__jfmodAssetRoot=" + JsonString(root) + ";</script>";
        var head = HeadTag().Match(rewritten);
        return head.Success
            ? rewritten[..(head.Index + head.Length)] + preamble + rewritten[(head.Index + head.Length)..]
            : preamble + rewritten;
    }

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

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteScheme();
}
