using JellyfinMod.Services.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace JellyfinMod.Api;

/// <summary>
/// Serves the JellyfinMod web bundle at <c>/web-mod</c> (P7.S3).
/// </summary>
/// <remarks>
/// <para>
/// The address deliberately mirrors the host's own <c>/web</c> rather than sitting under <c>/JellyfinMod</c>:
/// this is a static site a person types into a browser, not an API. Every actual API this plugin exposes stays
/// under <c>/JellyfinMod</c>.
/// </para>
/// <para>
/// Anonymous by necessity, not by oversight: these are the scripts and stylesheets of a page nobody has signed
/// into yet, exactly like the host's own <c>/web</c>. Only files inside an extracted, verified bundle are
/// reachable, and the plugin's database, secret store and configuration live outside that directory.
/// </para>
/// <para>
/// Everything under a bundle id is immutable — the id is a hash of the contents, so a changed file is a changed
/// id — which is what lets a client that loaded an older page keep working while a new bundle is installed.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("web-mod")]
public class WebController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private readonly WebBundleStore _store;

    /// <summary>Initializes a new instance of the <see cref="WebController"/> class.</summary>
    public WebController(WebBundleStore store) => _store = store;

    /// <summary>Serves the current bundle's document.</summary>
    /// <response code="200">The JellyfinMod interface.</response>
    /// <response code="404">No verified bundle is installed.</response>
    // One route only: the controller's own "JellyfinMod/Web" already answers with and without the trailing
    // slash, and declaring the absolute form as well makes every request to it an ambiguous match.
    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDocument()
    {
        var current = _store.Current;
        if (current is null) return NotFound();

        var path = Path.Combine(current.Directory, WebBundleStore.DocumentName);
        if (!System.IO.File.Exists(path)) return NotFound();

        var document = WebDocumentRenderer.Render(System.IO.File.ReadAllText(path), AssetRoot(current.BundleId));
        // Never cached: it names the bundle of the moment, and that changes with every plugin upgrade.
        Response.Headers.CacheControl = "no-store";
        return Content(document, "text/html; charset=utf-8");
    }

    /// <summary>Serves one file from an installed bundle.</summary>
    /// <param name="bundleId">The bundle's identity.</param>
    /// <param name="path">The file's path inside it.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such bundle or file.</response>
    [HttpGet("{bundleId}/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetFile([FromRoute] string bundleId, [FromRoute] string? path)
    {
        // An empty path is the bundle's own document, rendered for this id rather than for the current one, so a
        // client that already holds an old page keeps a working address.
        if (string.IsNullOrEmpty(path) || path == "/") return GetBundleDocument(bundleId);

        var resolved = _store.ResolveFile(bundleId, path);
        if (resolved is null) return NotFound();

        if (!ContentTypes.TryGetContentType(resolved, out var contentType)) contentType = "application/octet-stream";
        Response.Headers.CacheControl = ImmutableCache;
        // enableRangeProcessing: media and fonts are fetched by range on some clients.
        return PhysicalFile(resolved, contentType, enableRangeProcessing: true);
    }

    private ActionResult GetBundleDocument(string bundleId)
    {
        var resolved = _store.ResolveFile(bundleId, WebBundleStore.DocumentName);
        if (resolved is null) return NotFound();

        var document = WebDocumentRenderer.Render(System.IO.File.ReadAllText(resolved), AssetRoot(bundleId));
        Response.Headers.CacheControl = "no-store";
        return Content(document, "text/html; charset=utf-8");
    }

    /// <summary>
    /// The absolute path this bundle's files answer on. Taken from the request rather than from configuration:
    /// `PathBase` is what the host already stripped for a server published under a base URL or behind a reverse
    /// proxy, so it is right for this request by construction.
    /// </summary>
    private string AssetRoot(string bundleId) =>
        $"{Request.PathBase.Value?.TrimEnd('/') ?? string.Empty}/web-mod/{bundleId}/";
}
