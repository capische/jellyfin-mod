using System.Net.Mime;
using JellyfinMod.Data;
using JellyfinMod.Services.Web;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new instance of the <see cref="HealthController"/> class.</summary>
    /// <param name="database">The plugin database, whose readiness is what liveness means.</param>
    /// <param name="services">
    /// The container, so the interface subsystem can be asked for rather than required.
    /// </param>
    /// <remarks>
    /// The web bundle store and the application host are resolved rather than injected on purpose. This endpoint
    /// exists to answer when things are wrong; a Health check that returns 500 because an optional subsystem was
    /// not registered tells an operator nothing and hides whatever they were actually diagnosing. Anything it
    /// cannot resolve is simply reported as absent.
    /// </remarks>
    public HealthController(DatabaseInitializer database, IServiceProvider services)
    {
        _database = database;
        _services = services;
    }

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
        Ok = _database.IsReady,
        Capabilities = Capabilities,
        Web = DescribeWeb()
    });

    /// <summary>
    /// What the plugin knows about the interface it serves (P7.S3), so the settings area and the acceptance
    /// runner can see which bundle is live without reading the filesystem.
    /// </summary>
    private object? DescribeWeb()
    {
        var web = _services.GetService<WebBundleStore>();
        if (web is null) return null;

        var current = web.Current;
        var hostVersion = _services.GetService<IServerApplicationHost>()?.ApplicationVersion.ToString();
        var supported = current?.Manifest.SupportedServer;
        // A host above the minimum that nobody has run yet is unknown, not known-bad: it runs and says so.
        var untested = supported is not null
            && hostVersion is not null
            && supported.TestedOn.Count > 0
            && !supported.IsTested(hostVersion);

        return new
        {
            BundleId = current?.BundleId,
            WebCommit = current?.Manifest.WebCommit,
            BuiltAt = current?.Manifest.BuiltAt,
            ServedAt = current?.ServedAt,
            RetainedBundleIds = web.RetainedBundleIds,
            HostVersion = hostVersion,
            SupportedServer = supported is null ? null : new { supported.Minimum, supported.TestedOn },
            Blocker = web.Blocker ?? (untested ? "server_version_untested" : null),
            Takeover = DescribeTakeover()
        };
    }

    /// <summary>What the plugin did to the host's web root, if anything (P7.S4).</summary>
    private object? DescribeTakeover()
    {
        var takeover = _services.GetService<WebRootTakeover>();
        if (takeover is null) return null;

        var state = takeover.State;
        return new
        {
            state.Status,
            state.WebRoot,
            state.BundleId,
            state.StockSha256,
            state.PatchedSha256,
            state.PatchedAt,
            state.PatchedBy,
            state.Blocker
        };
    }

    /// <summary>
    /// Request fields and endpoints this build understands, so a newer web client never sends a field an older
    /// plugin rejects with 400 (P1.W14). Names are only ever added.
    /// </summary>
    /// <remarks>
    /// <c>ui</c> is the switch the web fork reads to decide whether to show the JellyfinMod interface at all
    /// (P7.S2); <c>ui.web</c> says this build also serves that interface's bundle itself (P7.S3);
    /// <c>ui.takeover</c> says it can replace the host's own document at <c>/web</c> (P7.S4). The <c>settings.*</c>
    /// names and <c>setup</c> are the typed settings contract and first-run state of P7.S7.
    /// </remarks>
    public static readonly IReadOnlyList<string> Capabilities =
    [
        "browse.dueWithinDays",
        "browse.features",
        "retention.summary",
        "discover.skipped",
        "reconciliation.conflicts",
        "reconciliation.orphans",
        "entries.libraryBusy",
        "entries.qualityProfile",
        "acquisition.settings",
        "acquisition.releases",
        "acquisition.grabHold",
        "queue",
        "import",
        "seedRelease",
        "automation",
        "versions",
        "ui",
        "ui.web",
        "ui.takeover",
        "settings.overview",
        "settings.discovery",
        "settings.seedProtection",
        "settings.retention",
        "setup",
        "acquisition.prowlarr"
    ];
}
