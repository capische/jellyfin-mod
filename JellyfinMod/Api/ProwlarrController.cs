using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Administrator-only Prowlarr sources (P7.S9, PHASE7 §6). One source in the first slice; the key is write-only and
/// stored once, and the indexers a sync creates hold no key of their own.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod/Settings/Prowlarr")]
public sealed class ProwlarrController(
    ModDbContext database,
    DatabaseInitializer readiness,
    AcquisitionSecretStore secrets,
    ProwlarrSync sync,
    ReleaseSearchCache cache) : ControllerBase
{
    /// <summary>Lists the sources.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProwlarrSourceDto>>> List(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var sources = await database.ProwlarrSources.AsNoTracking().OrderBy(source => source.Name).ToListAsync(cancellationToken);
        var result = new List<ProwlarrSourceDto>();
        foreach (var source in sources) result.Add(await ToDtoAsync(source, cancellationToken));
        return result;
    }

    /// <summary>Adds the source. The first slice allows one.</summary>
    [HttpPost]
    public async Task<ActionResult<ProwlarrSourceDto>> Create(ProwlarrSourceRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (Validate(request, creating: true) is { } error) return Invalid(error);
        if (await database.ProwlarrSources.AnyAsync(cancellationToken))
            return Conflict(Problem("prowlarr_source_exists", "One Prowlarr source is supported; edit the existing one."));
        var source = new ProwlarrSource
        {
            Name = request.Name.Trim(), BaseUrl = request.BaseUrl.Trim().TrimEnd('/'), Enabled = request.Enabled,
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            ApiKeySecretRef = await secrets.AddAsync(request.ApiKey.Value!, cancellationToken)
        };
        database.ProwlarrSources.Add(source);
        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            await secrets.RemoveAsync(source.ApiKeySecretRef, CancellationToken.None);
            return conflict;
        }

        return StatusCode(201, await ToDtoAsync(source, cancellationToken));
    }

    /// <summary>Changes the source; replacing the key rotates it for every synced indexer at once.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ProwlarrSourceDto>> Patch(Guid id, ProwlarrSourceRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        // Revision check and save under one gate (whole-review chunk 3a P2 3, 3b P2 3).
        await using var settingsMutation = await SettingsMutationGate.AcquireAsync(cancellationToken);
        if (Validate(request, creating: false) is { } error) return Invalid(error);
        var source = await database.ProwlarrSources.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (source is null) return NotFound();
        if (request.Revision != source.Revision)
            return Conflict(Problem("revision_conflict", "This configuration changed since it was loaded. Reload and try again."));
        var previous = source.ApiKeySecretRef;
        var previousBase = source.BaseUrl;
        source.Name = request.Name.Trim();
        source.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
        source.Enabled = request.Enabled;
        source.SyncIntervalMinutes = request.SyncIntervalMinutes;
        if (request.ApiKey.Action == "replace") source.ApiKeySecretRef = await secrets.AddAsync(request.ApiKey.Value!, cancellationToken);
        source.Revision++;
        var moved = !string.Equals(previousBase, source.BaseUrl, StringComparison.Ordinal);
        if (moved) await MoveSyncedIndexersAsync(source, previousBase, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        if (previous != source.ApiKeySecretRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        cache.Invalidate();
        // Re-verify at the new address now rather than at the next scheduled sync; its outcome is on the source.
        if (moved && source.Enabled) await sync.SyncByIdAsync(source.Id, cancellationToken);
        database.ChangeTracker.Clear();
        source = await database.ProwlarrSources.SingleAsync(value => value.Id == id, cancellationToken);
        return await ToDtoAsync(source, cancellationToken);
    }

    /// <summary>
    /// Points every synced indexer at the source's new address in the same save that moves the source, so no search,
    /// <c>t=caps</c> or download ever goes to the old host with the source's key (REVIEW-2026-09-24 S9-R3). The
    /// feeds must prove themselves again at the new address.
    /// </summary>
    private async Task MoveSyncedIndexersAsync(ProwlarrSource source, string previousBase, CancellationToken cancellationToken)
    {
        var oldHost = new Uri(previousBase).Host;
        var newBase = new Uri(source.BaseUrl.TrimEnd('/') + "/");
        var rows = await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == source.Id).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (row.ProwlarrIndexerId is { } prowlarrId) row.BaseUrl = new Uri(newBase, $"{prowlarrId}/api").ToString();
            var hosts = row.DownloadHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(host => string.Equals(host, oldHost, StringComparison.OrdinalIgnoreCase) ? newBase.Host : host)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            row.DownloadHosts = string.Join(',', hosts);
            row.Revision++;
            row.VerifiedRevision = null;
            row.CapabilitiesJson = null;
            row.CapabilitiesFetchedAt = null;
        }
    }

    /// <summary>Removes the source and its synced indexers, unless a grab through one is still active.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var source = await database.ProwlarrSources.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (source is null) return NotFound();
        var synced = await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == id).Select(indexer => indexer.Id)
            .ToListAsync(cancellationToken);
        if (await database.GrabOperations.AnyAsync(grab => synced.Contains(grab.IndexerId) && grab.ActiveHash != null, cancellationToken))
            return Conflict(Problem("prowlarr_indexer_in_use", "Grabs through a synced indexer are still active; resolve them first."));
        database.AcquisitionIndexers.RemoveRange(await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == id)
            .ToListAsync(cancellationToken));
        database.ProwlarrSources.Remove(source);
        await database.SaveChangesAsync(cancellationToken);
        await secrets.RemoveAsync(source.ApiKeySecretRef, CancellationToken.None);
        cache.Invalidate();
        return NoContent();
    }

    /// <summary>Reads system status, health and the indexer list without changing anything.</summary>
    [HttpPost("{id:guid}/Test")]
    public async Task<ActionResult<ConnectionTestDto>> Test(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var source = await database.ProwlarrSources.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (source is null) return NotFound();
        var (code, message, version) = await sync.TestAsync(source, cancellationToken);
        return new ConnectionTestDto(code == "ok", code, message, version, null);
    }

    /// <summary>Syncs now and answers what changed.</summary>
    [HttpPost("{id:guid}/Sync")]
    public async Task<ActionResult<object>> Sync(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var outcome = await sync.SyncByIdAsync(id, cancellationToken);
        if (outcome is null) return NotFound();
        return new
        {
            code = outcome.Code, seen = outcome.Seen, created = outcome.Created, updated = outcome.Updated, disabled = outcome.Disabled,
            removed = outcome.Removed, verified = outcome.Verified, failed = outcome.Failed
        };
    }

    /// <summary>
    /// The download clients the source's Prowlarr knows, for the Download Client step to fill its form (P6.FU1). Read here
    /// with the saved key, which never reaches the browser; passwords are neither read nor returned. A Prowlarr that fails
    /// answers its code and no client. Changes nothing.
    /// </summary>
    [HttpGet("{id:guid}/DownloadClients")]
    public async Task<ActionResult<ProwlarrDownloadClientsDto>> DownloadClients(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var source = await database.ProwlarrSources.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (source is null) return NotFound();
        var (code, clients) = await sync.ReadDownloadClientsAsync(source, cancellationToken);
        return new ProwlarrDownloadClientsDto(code, clients.Select(client =>
        {
            var transmission = string.Equals(client.Implementation, "Transmission", StringComparison.OrdinalIgnoreCase);
            var baseUrl = transmission ? TransmissionRpcUrl(client) : null;
            return new ProwlarrDownloadClientDto(client.Implementation, client.Name, client.Enabled, client.Host, client.Port, client.UseSsl, client.UrlBase,
                client.Username, baseUrl is null ? null : TransmissionDriver.DriverKind, baseUrl);
        }).ToList(), DefaultGateway());
    }

    /// <summary>
    /// Transmission's RPC address as Prowlarr builds it: scheme, host and port, then the URL base (Prowlarr's default
    /// <c>/transmission/</c>) and <c>rpc</c>. Null when the host or port would not make an http(s) address without credentials.
    /// </summary>
    private static string? TransmissionRpcUrl(ProwlarrDownloadClient client)
    {
        var host = client.Host?.Trim();
        if (string.IsNullOrEmpty(host) || client.Port is not { } port || Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        var urlBase = (client.UrlBase ?? string.Empty).Trim().Trim('/');
        try
        {
            var uri = new UriBuilder(client.UseSsl ? "https" : "http", host, port, (urlBase.Length == 0 ? string.Empty : "/" + urlBase) + "/rpc").Uri;
            return string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? uri.ToString() : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// This server's IPv4 default gateway from <c>/proc/net/route</c>, when it is in a private range (10/8, 172.16/12,
    /// 192.168/16): the address a service published on the Docker host is often reached at. Null anywhere else.
    /// </summary>
    private static string? DefaultGateway()
    {
        try
        {
            foreach (var line in System.IO.File.ReadLines("/proc/net/route").Skip(1))
            {
                var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Iface, Destination, Gateway, Flags, RefCnt, Use, Metric, Mask: the default route has destination and mask 0.
                if (columns.Length < 8 || columns[1] != "00000000" || columns[7] != "00000000" ||
                    !uint.TryParse(columns[2], System.Globalization.NumberStyles.HexNumber, null, out var value) || value == 0) continue;
                // The kernel writes the address in host byte order of its network-order bytes: little-endian on x86 and ARM.
                var bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
                var isPrivate = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
                return isPrivate ? new System.Net.IPAddress(bytes).ToString() : null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private async Task<ProwlarrSourceDto> ToDtoAsync(ProwlarrSource source, CancellationToken cancellationToken) => new(
        source.Id, source.Name, source.BaseUrl, await secrets.HasAsync(source.ApiKeySecretRef, cancellationToken), source.Enabled,
        source.SyncIntervalMinutes, source.LastSyncAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, source.LastSyncOutcome,
        source.LastError, source.ConsecutiveEmptySyncs,
        await database.AcquisitionIndexers.CountAsync(indexer => indexer.ProwlarrSourceId == source.Id, cancellationToken), source.Revision);

    private static string? Validate(ProwlarrSourceRequest request, bool creating)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "invalid_name";
        if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "invalid_prowlarr_url";
        if (!creating && request.Revision is null) return "revision_required";
        return request.ApiKey.Action switch
        {
            "replace" when !string.IsNullOrWhiteSpace(request.ApiKey.Value) && request.ApiKey.Value.Length <= 256 => null,
            "unchanged" when !creating && request.ApiKey.Value is null => null,
            _ => "invalid_secret_change"
        };
    }

    private async Task<ActionResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            database.ChangeTracker.Clear();
            return Conflict(Problem("name_in_use", "Another source already uses this name."));
        }
    }

    private BadRequestObjectResult Invalid(string code) =>
        BadRequest(new ProblemDetails { Status = 400, Type = code, Title = code == "invalid_prowlarr_url"
            ? "Enter the Prowlarr address, for example http://host:9696, without credentials."
            : "The configuration is not valid." });

    private static ProblemDetails Problem(string code, string title) => new() { Status = 409, Type = code, Title = title };
}
