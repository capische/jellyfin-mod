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
    /// <summary>The name of a source created without one; the settings form has no Name field (the one source is Prowlarr).</summary>
    private const string DefaultName = "Prowlarr";

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
            Name = string.IsNullOrWhiteSpace(request.Name) ? DefaultName : request.Name.Trim(), BaseUrl = request.BaseUrl.Trim().TrimEnd('/'), Enabled = request.Enabled,
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

    /// <summary>
    /// Changes the source; replacing the key rotates it for every synced indexer at once. An address that names another
    /// Prowlarr (<see cref="SameProwlarr"/>) is a new source (the user, 2026-11-07): the indexers synced from the old one go in
    /// the same save, and the new one is synced at once.
    /// </summary>
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
        var newBase = request.BaseUrl.Trim().TrimEnd('/');
        var another = !SameProwlarr(source.BaseUrl, newBase);
        if (another && await SyncedIndexersInUseAsync(id, cancellationToken)) return InUse();
        var previous = source.ApiKeySecretRef;
        if (!string.IsNullOrWhiteSpace(request.Name)) source.Name = request.Name.Trim();
        source.BaseUrl = newBase;
        source.Enabled = request.Enabled;
        source.SyncIntervalMinutes = request.SyncIntervalMinutes;
        if (request.ApiKey.Action == "replace") source.ApiKeySecretRef = await secrets.AddAsync(request.ApiKey.Value!, cancellationToken);
        source.Revision++;
        if (another)
        {
            // Nothing of the old Prowlarr is carried over: its indexers, its sync state and its empty-answer count. A search or
            // sync that read the old rows meanwhile is refused (source_changed), so no request reaches the old host again.
            await RemoveSyncedIndexersAsync(id, cancellationToken);
            (source.LastSyncAt, source.LastSyncOutcome, source.LastError) = (null, null, null);
            (source.ConsecutiveEmptySyncs, source.LastEmptySyncAt) = (0, null);
        }

        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        if (previous != source.ApiKeySecretRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        cache.Invalidate();
        // Sync the new Prowlarr now, whether or not the schedule is on; a failed sync is fail-closed and leaves its code on the source.
        if (another) await sync.SyncByIdAsync(source.Id, cancellationToken);
        database.ChangeTracker.Clear();
        source = await database.ProwlarrSources.SingleAsync(value => value.Id == id, cancellationToken);
        return await ToDtoAsync(source, cancellationToken);
    }

    /// <summary>
    /// Whether two addresses name the same Prowlarr: the same scheme, host (any case), port (the default one written or not)
    /// and path, a trailing slash ignored. Anything else is another Prowlarr, even the same one moved to a new host.
    /// </summary>
    private static bool SameProwlarr(string stored, string requested)
    {
        if (!Uri.TryCreate(stored.TrimEnd('/'), UriKind.Absolute, out var before) || !Uri.TryCreate(requested.TrimEnd('/'), UriKind.Absolute, out var after))
            return string.Equals(stored.TrimEnd('/'), requested.TrimEnd('/'), StringComparison.Ordinal);
        return string.Equals(before.Scheme, after.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(before.IdnHost, after.IdnHost, StringComparison.OrdinalIgnoreCase) && before.Port == after.Port &&
            string.Equals(before.AbsolutePath.TrimEnd('/'), after.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private async Task<bool> SyncedIndexersInUseAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var synced = await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == sourceId).Select(indexer => indexer.Id)
            .ToListAsync(cancellationToken);
        return await database.GrabOperations.AnyAsync(grab => synced.Contains(grab.IndexerId) && grab.ActiveHash != null, cancellationToken);
    }

    /// <summary>Marks the source's synced indexers for removal; grabs through them keep their recorded indexer name.</summary>
    private async Task RemoveSyncedIndexersAsync(Guid sourceId, CancellationToken cancellationToken) =>
        database.AcquisitionIndexers.RemoveRange(await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == sourceId)
            .ToListAsync(cancellationToken));

    private ConflictObjectResult InUse() =>
        Conflict(Problem("prowlarr_indexer_in_use", "Grabs through a synced indexer are still active; resolve them first."));

    /// <summary>
    /// Disconnects: removes the source, its synced indexers and its key, unless a grab through one is still active. Indexers
    /// added by hand stay; the settings form calls it when the address and the key are both cleared (the user, 2026-11-07).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var source = await database.ProwlarrSources.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (source is null) return NotFound();
        if (await SyncedIndexersInUseAsync(id, cancellationToken)) return InUse();
        await RemoveSyncedIndexersAsync(id, cancellationToken);
        database.ProwlarrSources.Remove(source);
        await database.SaveChangesAsync(cancellationToken);
        await secrets.RemoveAsync(source.ApiKeySecretRef, CancellationToken.None);
        cache.Invalidate();
        return NoContent();
    }

    /// <summary>
    /// Reads system status, health and the indexer list without changing anything. A body may carry the address and key typed
    /// in the form but not saved (ds.t9): each is used in place of the saved one, and nothing is stored. Without a source yet
    /// (<c>POST Test</c>), both must be typed.
    /// </summary>
    [HttpPost("{id:guid}/Test")]
    [HttpPost("Test")]
    public async Task<ActionResult<ConnectionTestDto>> Test(Guid? id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] KeyTestRequest? request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var source = id is { } sourceId
            ? await database.ProwlarrSources.AsNoTracking().SingleOrDefaultAsync(value => value.Id == sourceId, cancellationToken)
            : new ProwlarrSource { Name = DefaultName, BaseUrl = string.Empty };
        if (source is null) return NotFound();
        var typedKey = request?.TypedKey;
        if (typedKey is { Length: > 256 }) return Invalid("invalid_secret_change");
        var typedAddress = string.IsNullOrWhiteSpace(request?.BaseUrl) ? null : request.BaseUrl.Trim().TrimEnd('/');
        if (typedAddress is not null && !string.Equals(typedAddress, source.BaseUrl, StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(typedAddress, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return Invalid("invalid_prowlarr_url");
            source.BaseUrl = typedAddress;
        }
        else
        {
            typedAddress = null;
        }

        if (id is null && (typedAddress is null || typedKey is null))
            return Invalid("prowlarr_test_incomplete");
        var (code, message, version) = await sync.TestAsync(source, cancellationToken, typedKey);
        // Typed values get words that say what to do next; the saved ones keep the sync's own sentence.
        if (typedKey is not null || typedAddress is not null)
        {
            message = code switch
            {
                "ok" => message.Replace("Prowlarr answered and has", typedKey is null ? "Prowlarr answered at this address and has" : "Prowlarr accepted this key and has",
                    StringComparison.Ordinal) + " Save it to sync with it.",
                "unauthorized" when typedKey is not null => "Prowlarr refused this key. Copy it again from its General settings.",
                _ => message
            };
        }

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
            : code == "prowlarr_test_incomplete" ? "Type the Prowlarr address and its API key to test them."
            : "The configuration is not valid." });

    private static ProblemDetails Problem(string code, string title) => new() { Status = 409, Type = code, Title = title };
}
