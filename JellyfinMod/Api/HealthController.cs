using System.Net.Mime;
using JellyfinMod.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMod.Api;

/// <summary>
/// Phase 0 smoke test: proves the assembly was scanned, the controller was registered and
/// auth is wired. If this answers, the plugin is alive.
/// </summary>
[ApiController]
[Authorize]
[Route("JellyfinMod")]
[Produces(MediaTypeNames.Application.Json)]
public class HealthController : ControllerBase
{
    private readonly DatabaseInitializer _database;

    /// <summary>Initializes a new instance of the <see cref="HealthController"/> class.</summary>
    public HealthController(DatabaseInitializer database) => _database = database;

    /// <summary>Reports plugin liveness.</summary>
    /// <response code="200">The plugin is loaded.</response>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<object> GetHealth() => StatusCode(
        _database.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, new
    {
        Name = Plugin.Instance?.Name,
        Version = Plugin.Instance?.Version.ToString(),
        Ok = _database.IsReady
    });
}
