using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Administrator-only import configuration (P5.I2): switches, the seed floor, the monitor cadence and the download
/// client's path mappings with a hardlink probe that never adds a torrent.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod/Settings")]
public sealed class ImportSettingsController(
    ModDbContext database,
    DatabaseInitializer readiness,
    ImportPathProbe probe,
    ImportMonitor? monitor = null) : ControllerBase
{
    /// <summary>Gets the import settings.</summary>
    [HttpGet("Import")]
    public async Task<ActionResult<ImportSettingsDto>> Get(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return ToDto(await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken));
    }

    /// <summary>Replaces the import settings; the request must echo the current revision.</summary>
    [HttpPatch("Import")]
    public async Task<ActionResult<ImportSettingsDto>> Patch(ImportSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.ImportRevision)
            return Conflict(new ProblemDetails
            {
                Status = 409, Type = "revision_conflict", Title = "These settings changed since they were loaded. Reload and try again."
            });
        var extensions = request.VideoExtensions.Select(value => value.Trim().TrimStart('.').ToLowerInvariant())
            .Where(value => value.Length > 0).Distinct().ToArray();
        if (extensions.Length == 0 || extensions.Length > 32 ||
            extensions.Any(value => value.Length > 8 || !value.All(char.IsAsciiLetterOrDigit)) ||
            extensions.Any(ImportDefaults.ArchiveExtensions.Contains))
            return BadRequest(new ProblemDetails
            {
                Status = 400, Type = "invalid_video_extensions",
                Title = "List between 1 and 32 short video extensions; archive extensions are never imported."
            });
        settings.ImportEnabled = request.ImportEnabled;
        settings.SeedReleaseEnabled = request.SeedReleaseEnabled;
        settings.SeedFloorRatio = request.SeedFloorRatio is > 0 ? request.SeedFloorRatio : null;
        settings.SeedFloorHours = request.SeedFloorHours is > 0 ? request.SeedFloorHours : null;
        settings.ImportPollSeconds = request.ImportPollSeconds;
        settings.VideoExtensions = string.Join(',', extensions);
        settings.StalledAfterHours = request.StalledAfterHours;
        settings.ScanTimeoutMinutes = request.ScanTimeoutMinutes;
        settings.QueueVisibleToUsers = request.QueueVisibleToUsers;
        settings.ImportRevision++;
        await database.SaveChangesAsync(cancellationToken);
        monitor?.Wake();
        return ToDto(settings);
    }

    /// <summary>Lists a download client's path mappings.</summary>
    [HttpGet("DownloadClients/{id:guid}/PathMappings")]
    public async Task<ActionResult<IReadOnlyList<PathMappingDto>>> Mappings(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (!await database.AcquisitionDownloadClients.AnyAsync(client => client.Id == id, cancellationToken)) return NotFound();
        return (await database.DownloadClientPathMappings.AsNoTracking().Where(mapping => mapping.DownloadClientId == id)
            .OrderBy(mapping => mapping.Order).ToListAsync(cancellationToken)).Select(ToDto).ToArray();
    }

    /// <summary>
    /// Replaces a client's ordered mappings without changing its connection revision. Each mapping is probed; an
    /// unverified one is saved but blocks imports through it.
    /// </summary>
    [HttpPut("DownloadClients/{id:guid}/PathMappings")]
    public async Task<ActionResult<IReadOnlyList<PathMappingDto>>> ReplaceMappings(Guid id, PathMappingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var client = await database.AcquisitionDownloadClients.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (client is null) return NotFound();
        if (request.Revision is { } revision && revision != client.Revision)
            return Conflict(new ProblemDetails
            {
                Status = 409, Type = "revision_conflict", Title = "This client changed since it was loaded. Reload and try again."
            });
        if (probe.Validate(request.PathMappings) is { } error)
            return BadRequest(new ProblemDetails { Status = 400, Type = error.Code, Title = error.Message });
        database.DownloadClientPathMappings.RemoveRange(await database.DownloadClientPathMappings
            .Where(mapping => mapping.DownloadClientId == id).ToListAsync(cancellationToken));
        await database.SaveChangesAsync(cancellationToken);
        var now = DateTime.UtcNow;
        for (var index = 0; index < request.PathMappings.Length; index++)
        {
            var local = ImportPaths.Normalize(request.PathMappings[index].LocalPathPrefix)!;
            var result = probe.Probe(local);
            database.DownloadClientPathMappings.Add(new DownloadClientPathMapping
            {
                DownloadClientId = id, Order = index, ClientPathPrefix = ImportPaths.Normalize(request.PathMappings[index].ClientPathPrefix)!,
                LocalPathPrefix = local, VerifiedAt = result.Ok ? now : null, VerificationReason = result.Ok ? null : result.Code
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        monitor?.Wake();
        return await Mappings(id, cancellationToken);
    }

    /// <summary>
    /// Maps a client path, reports whether it exists and which library roots share its mount, and probes a real hardlink
    /// with a dot-prefixed temporary file that is removed immediately. No torrent is added.
    /// </summary>
    [HttpPost("DownloadClients/{id:guid}/TestImportPath")]
    public async Task<ActionResult<ImportPathTestDto>> TestImportPath(Guid id, ImportPathTestRequest request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var client = await database.AcquisitionDownloadClients.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (client is null) return NotFound();
        var clientPath = ImportPaths.Normalize(request.ClientPath);
        if (clientPath is null)
            return BadRequest(new ProblemDetails { Status = 400, Type = "invalid_client_path", Title = "Enter an absolute client path." });
        var mappings = await database.DownloadClientPathMappings.AsNoTracking().Where(mapping => mapping.DownloadClientId == id)
            .ToListAsync(cancellationToken);
        return probe.Probe(ImportPaths.Map(clientPath, mappings, client));
    }

    private static ImportSettingsDto ToDto(AcquisitionSettings settings) => new(settings.ImportEnabled, settings.SeedReleaseEnabled,
        settings.SeedFloorRatio, settings.SeedFloorHours, settings.ImportPollSeconds,
        settings.VideoExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries), settings.StalledAfterHours,
        settings.ScanTimeoutMinutes, settings.QueueVisibleToUsers, settings.ImportRevision);

    private static PathMappingDto ToDto(DownloadClientPathMapping mapping) => new(mapping.Id, mapping.Order, mapping.ClientPathPrefix,
        mapping.LocalPathPrefix, mapping.VerifiedAt is { } verified ? DateTime.SpecifyKind(verified, DateTimeKind.Utc) : null,
        mapping.VerificationReason);
}
