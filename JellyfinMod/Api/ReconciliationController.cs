using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>Exposes bounded reconciliation diagnostics to administrators.</summary>
[ApiController, Route("JellyfinMod/Reconciliation"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class ReconciliationController(ModDbContext database, DatabaseInitializer readiness) : ControllerBase
{
    /// <summary>Gets the latest running or completed reconciliation summary.</summary>
    [HttpGet("Latest")]
    public async Task<ActionResult<ReconciliationRunDto>> Latest(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var run = await database.ReconciliationRuns.AsNoTracking().OrderByDescending(candidate => candidate.StartedAt)
            .ThenByDescending(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null) return NotFound();
        IReadOnlyList<ReconciliationDiagnostic> diagnostics = run.DiagnosticsJson is null
            ? []
            : JsonSerializer.Deserialize<ReconciliationDiagnostic[]>(run.DiagnosticsJson) ?? [];
        return new ReconciliationRunDto(run, diagnostics);
    }
}
