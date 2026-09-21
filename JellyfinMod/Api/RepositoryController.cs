using JellyfinMod.Services.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>
/// Serves this plugin's own repository manifest and installable package (P7.S5).
/// </summary>
/// <remarks>
/// Anonymous because the server fetches a repository as an ordinary outbound HTTP request, with no session and
/// no token — the same way it reads any other plugin repository. Nothing here exposes anything the plugin package
/// does not already contain, and the manifest describes only this plugin.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("JellyfinMod/Repository")]
public class RepositoryController : ControllerBase
{
    private readonly PluginRepository _repository;

    /// <summary>Initializes a new instance of the <see cref="RepositoryController"/> class.</summary>
    public RepositoryController(PluginRepository repository) => _repository = repository;

    /// <summary>The repository manifest, for the server's plugin repository list.</summary>
    /// <response code="200">The manifest.</response>
    /// <response code="503">No package could be published; the plugin still works.</response>
    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetManifest()
    {
        var published = await _repository.PublishAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (published is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var manifest = _repository.Manifest(Absolute(published.FileName), Absolute(null));
        if (manifest is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        // The server re-reads this whenever it refreshes its repositories, and the package is rebuilt on upgrade.
        Response.Headers.CacheControl = "no-store";
        return Content(manifest, "application/json; charset=utf-8");
    }

    /// <summary>The installable package the manifest points at.</summary>
    /// <param name="fileName">The package file name from the manifest.</param>
    /// <response code="200">The package.</response>
    /// <response code="404">No such package.</response>
    [HttpGet("{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPackage([FromRoute] string fileName)
    {
        var published = await _repository.PublishAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        // Compared by name rather than resolved as a path: the only file this route serves is the one the
        // manifest just named, so there is no path to traverse.
        if (published is null || !string.Equals(fileName, published.FileName, StringComparison.Ordinal))
            return NotFound();

        return PhysicalFile(published.Path, "application/zip", published.FileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// An absolute URL back to this controller.
    /// </summary>
    /// <remarks>
    /// Built from the request that arrived, so it is correct for however the administrator registered the
    /// repository — by IP, by hostname, through a reverse proxy or under a base URL — without the plugin having
    /// to be told what the server is called.
    /// </remarks>
    private string Absolute(string? fileName)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/JellyfinMod/Repository";
        return fileName is null ? baseUrl : $"{baseUrl}/{Uri.EscapeDataString(fileName)}";
    }
}
