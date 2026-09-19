using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Import;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// The download queue (P5.I7): a monitor of downloads, imports and seeding copies, not a torrent client. Remove and Open
/// in client are its only actions. Rows are filtered by library access; ordinary users see them only when the
/// administrator allows it, and every write requires elevation.
/// </summary>
[ApiController, Authorize, Route("JellyfinMod")]
public sealed class QueueController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    ImportService imports,
    ImportMonitor monitor,
    ImportTickGate gate,
    ClientSnapshotCache snapshots,
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    UnixFileInspector files,
    ILibraryManager library,
    TimeProvider time,
    IAuthorizationService authorization,
    JellyfinMod.Services.Automation.AutomationStatusService? automation = null) : ControllerBase
{
    /// <summary>Lists open downloads, imports and seeding copies the caller may see.</summary>
    [HttpGet("Queue")]
    public async Task<ActionResult<QueueResultDto>> Queue([FromQuery(Name = "state")] string[]? states, [FromQuery] Guid? entryId,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var administrator = await IsAdministratorAsync();
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (!administrator && !settings.QueueVisibleToUsers)
            return Refuse(403, "queue_admin_only", "The download queue is available to administrators.");

        var seeds = await database.SeedReleaseOperations.AsNoTracking()
            .Where(seed => SeedReleaseStates.Open.Contains(seed.State)).ToListAsync(cancellationToken);
        var seedImportIds = seeds.Select(seed => seed.ImportOperationId).ToHashSet();
        // Failed operations stay listed until retried or removed, so an administrator can act on them.
        var operations = await database.ImportOperations.AsNoTracking()
            .Where(operation => ImportStates.Open.Contains(operation.State) || seedImportIds.Contains(operation.Id) ||
                operation.State == ImportStates.Failed && !database.ImportOperations.Any(retry => retry.RetryOfId == operation.Id))
            .OrderBy(operation => operation.CreatedAt).ToListAsync(cancellationToken);
        if (entryId is { } onlyEntry) operations = operations.Where(operation => operation.EntryId == onlyEntry).ToList();

        var clientStatus = new QueueClientStatusDto(true, null, null);
        var clientSnapshots = new Dictionary<Guid, ClientSnapshot>();
        foreach (var clientId in operations.Select(operation => operation.DownloadClientId).Distinct())
        {
            // One shared read per freshness window, whoever asks (P5.I7 polling cost).
            var snapshot = await imports.SnapshotAsync(clientId, ClientSnapshotCache.QueueFreshness, cancellationToken);
            clientSnapshots[clientId] = snapshot;
            if (!snapshot.Reachable || clientStatus.CheckedAt is null)
                clientStatus = new QueueClientStatusDto(snapshot.Reachable, QueueReadModel.Utc(snapshot.CheckedAt),
                    snapshot.Reachable ? null : snapshot.Reason ?? ImportReasons.ClientUnreachable);
        }

        var entries = await LoadEntriesAsync(operations, cancellationToken);
        var episodes = await LoadEpisodesAsync(operations, cancellationToken);
        var clients = await database.AcquisitionDownloadClients.AsNoTracking().ToDictionaryAsync(client => client.Id, cancellationToken);
        var rows = new List<QueueRowDto>();
        foreach (var operation in operations)
        {
            var entry = operation.EntryId is { } id ? entries.GetValueOrDefault(id) : null;
            var episode = operation.EpisodeId is { } episodeId ? episodes.GetValueOrDefault(episodeId) : null;
            if (!CanSee(user, administrator, entry, episode)) continue;
            var seed = seeds.FirstOrDefault(value => value.ImportOperationId == operation.Id);
            var row = QueueReadModel.Row(operation, seed, entry, episode, clients.GetValueOrDefault(operation.DownloadClientId),
                clientSnapshots.GetValueOrDefault(operation.DownloadClientId), settings, administrator,
                seed is not null && LibraryLinkPresent(seed));
            if (states is { Length: > 0 } && !states.Contains(row.State, StringComparer.Ordinal)) continue;
            rows.Add(row);
        }

        return new QueueResultDto(rows, rows.Count, time.GetUtcNow().UtcDateTime, clientStatus, settings.ImportEnabled,
            settings.SeedReleaseEnabled)
        {
            Automation = automation is null ? null : await automation.BannerAsync(cancellationToken)
        };
    }

    /// <summary>Gets one import operation; inaccessible targets answer with the concealed 404.</summary>
    [HttpGet("Imports/{id:guid}")]
    public async Task<ActionResult<ImportOperationDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var administrator = await IsAdministratorAsync();
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var operation = await database.ImportOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (operation is null || !await CanSeeAsync(user, administrator, operation, cancellationToken)) return NotFound();
        if (!administrator && !settings.QueueVisibleToUsers)
            return Refuse(403, "queue_admin_only", "The download queue is available to administrators.");
        return QueueReadModel.Operation(operation, administrator);
    }

    /// <summary>
    /// Retries an import. A blocked operation resumes itself after its cause was fixed; a failed one is replaced by a new
    /// operation for the same grab, refused while another operation for that grab is open.
    /// </summary>
    [HttpPost("Imports/{id:guid}/Retry"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<ImportOperationDto>> Retry(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        ImportOperation result;
        await using (await gate.AcquireAsync(cancellationToken))
        {
            database.ChangeTracker.Clear();
            var operation = await database.ImportOperations.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (operation is null || !await CanSeeAsync(user, true, operation, cancellationToken)) return NotFound();
            var now = time.GetUtcNow().UtcDateTime;
            if (operation.State == ImportStates.Blocked)
            {
                // Everything is re-inspected from the start; nothing recorded is trusted blindly.
                operation.State = operation.LinkedAt is not null && operation.DestinationPath is not null
                    ? ImportStates.Linked
                    : ImportStates.Waiting;
                if (operation.State == ImportStates.Waiting) operation.DestinationPath = null;
                operation.Reason = null;
                operation.Error = null;
                operation.HistoryReason = null;
                operation.ScanAttempts = 0;
                operation.UpdatedAt = now;
                await database.SaveChangesAsync(cancellationToken);
                result = operation;
            }
            else if (operation.State == ImportStates.Failed)
            {
                if (await database.ImportOperations.AnyAsync(value => value.GrabId == operation.GrabId &&
                        ImportStates.Open.Contains(value.State), cancellationToken))
                    return Refuse(409, "import_open", "Another import of this grab is still open.");
                var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken);
                if (grab is null) return Refuse(409, "grab_missing", "The grab of this import no longer exists.");
                result = await ImportService.CreateForGrabAsync(database, grab, now, cancellationToken) ??
                    throw new InvalidOperationException("The grab has no target.");
                result.RetryOfId = operation.Id;
                result.Intent = operation.Intent;
                await database.SaveChangesAsync(cancellationToken);
            }
            else
            {
                return Refuse(409, "import_not_retryable", "Only blocked or failed imports can be retried.");
            }
        }

        monitor.Wake();
        return StatusCode(202, QueueReadModel.Operation(result, true));
    }

    /// <summary>
    /// Removes a row. Never touches a library file. With <c>removeFromClient</c>, removes the torrent and its data only
    /// when this plugin added it; with <c>blocklist</c>, Phase 4 scoring rejects the release afterwards.
    /// </summary>
    [HttpDelete("Queue/{id:guid}"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<ImportOperationDto>> Remove(Guid id, [FromBody] QueueRemoveRequest? request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        request ??= new QueueRemoveRequest();
        await using var lease = await gate.AcquireAsync(cancellationToken);
        database.ChangeTracker.Clear();
        var operation = await database.ImportOperations.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (operation is null || !await CanSeeAsync(user, true, operation, cancellationToken)) return NotFound();
        var seed = await database.SeedReleaseOperations.SingleOrDefaultAsync(value => value.ImportOperationId == id, cancellationToken);
        var importOpen = ImportStates.Open.Contains(operation.State) || operation.State == ImportStates.Failed;
        var seedOpen = seed is not null && SeedReleaseStates.Open.Contains(seed.State);
        if (!importOpen && !seedOpen) return Refuse(409, "not_in_queue", "This import is no longer in the queue.");
        if (seed?.State == SeedReleaseStates.Removing)
            return Refuse(409, "seed_release_in_progress", "The seeding copy is being released; try again shortly.");
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken);

        var removedFromClient = false;
        if (request.RemoveFromClient)
        {
            var client = await database.AcquisitionDownloadClients.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == operation.DownloadClientId, cancellationToken);
            if (client is null || drivers.Get(client.Kind) is not { } driver)
                return Refuse(409, ImportReasons.ClientMissing, "The download client is no longer configured.");
            var snapshot = await imports.SnapshotAsync(client.Id, TimeSpan.Zero, cancellationToken);
            if (!snapshot.Reachable)
                return Refuse(503, ImportReasons.ClientUnreachable, "The download client cannot be reached; nothing was changed.");
            if (snapshot.Find(operation.InfoHash) is { } torrent)
            {
                // Only a torrent this plugin added, and never one whose data lies in a library folder.
                if (grab is null || !torrent.Labels.Contains(GrabService.OwnerLabel(grab.Id), StringComparer.Ordinal))
                    return Refuse(409, SeedReleaseReasons.NotOwned, "JellyfinMod did not add this torrent; remove it in the client.");
                var mappings = await database.DownloadClientPathMappings.AsNoTracking()
                    .Where(mapping => mapping.DownloadClientId == client.Id).ToListAsync(cancellationToken);
                var dataPaths = torrent.Files.Select(file => ImportPaths.Normalize((torrent.DownloadDirectory ?? string.Empty) + "/" + file.Name))
                    .Select(path => path is null ? null : ImportPaths.Map(path, mappings, client)).ToArray();
                if (dataPaths.Any(path => path is null || InsideLibrary(path)))
                    return Refuse(409, SeedReleaseReasons.SeedingInsideLibrary,
                        "The torrent's data is not in a mapped download folder outside every library; remove it in the client.");
                try
                {
                    var connection = await configuration.ConnectAsync(client, cancellationToken);
                    await driver.RemoveAsync(connection, operation.InfoHash, deleteData: true, cancellationToken);
                    removedFromClient = true;
                }
                catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
                    HttpRequestException)
                {
                    return Refuse(503, ImportReasons.ClientUnreachable, "The download client did not confirm the removal; check it in the client.");
                }
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (importOpen)
        {
            operation.State = ImportStates.Cancelled;
            operation.Reason = ImportReasons.Cancelled;
            operation.OpenGrabKey = null;
            operation.CancelledBy = user.Id;
            operation.CompletedAt = operation.UpdatedAt = now;
        }

        if (seedOpen)
        {
            seed!.State = SeedReleaseStates.Cancelled;
            seed.Reason = SeedReleaseReasons.Cancelled;
            seed.CompletedAt = seed.UpdatedAt = now;
        }

        if (grab is not null)
        {
            grab.ActiveTarget = null;
            grab.ActiveHash = null;
            grab.UpdatedAt = now;
        }

        if (operation.EntryId is { } entryId && await database.Entries.AnyAsync(entry => entry.Id == entryId, cancellationToken))
        {
            database.History.Add(new HistoryRecord
            {
                EntryId = entryId, EventType = "queue_removed", CreatedAt = now,
                Summary = ImportService.Bound("Removed from the queue" + (removedFromClient ? " and from the download client: " : ": ") +
                    operation.ReleaseTitle),
                Data = JsonSerializer.Serialize(new
                {
                    operationId = operation.Id, operation.EpisodeId, removeFromClient = removedFromClient, request.Blocklist
                })
            });
            if (request.Blocklist)
            {
                database.ReleaseBlocklist.Add(new ReleaseBlocklistEntry
                {
                    InfoHash = operation.InfoHash, IndexerId = grab?.IndexerId, SourceGuid = grab?.SourceGuid,
                    RawTitle = operation.ReleaseTitle, EntryId = entryId, EpisodeId = operation.EpisodeId, Reason = "queue_removed",
                    CreatedAt = now, CreatedByUserId = user.Id
                });
                database.History.Add(new HistoryRecord
                {
                    EntryId = entryId, EventType = "blocklisted", CreatedAt = now,
                    Summary = ImportService.Bound("Blocklisted " + operation.ReleaseTitle),
                    Data = JsonSerializer.Serialize(new { operationId = operation.Id, operation.InfoHash })
                });
            }
        }

        await database.SaveChangesAsync(CancellationToken.None);
        return QueueReadModel.Operation(operation, true);
    }

    /// <summary>Lists open seed releases with their goals: where disk will be freed and why it is not yet.</summary>
    [HttpGet("Seeding"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<SeedingDto>>> Seeding(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(seed => SeedReleaseStates.Open.Contains(seed.State))
            .OrderBy(seed => seed.PreparedAt).ToListAsync(cancellationToken);
        var result = new List<SeedingDto>();
        foreach (var seed in seeds)
        {
            var torrent = snapshots.Latest(seed.DownloadClientId)?.Find(seed.InfoHash);
            var goal = torrent is null ? null : SeedReleaseService.Evaluate(torrent, seed.IndexerRatio, seed.IndexerSeconds, settings);
            result.Add(new SeedingDto(seed.Id, seed.ImportOperationId, seed.EntryId, seed.EpisodeId, seed.InfoHash, seed.State, seed.Reason,
                goal?.Ratio ?? seed.GoalRatio, goal?.RatioSource ?? seed.GoalRatioSource, goal?.Seconds ?? seed.GoalSeconds,
                goal?.SecondsSource ?? seed.GoalSecondsSource, torrent?.UploadRatio ?? seed.ObservedRatio,
                torrent?.SecondsSeeding ?? seed.ObservedSeedingSeconds, QueueReadModel.Utc(seed.GoalMetAt), goal?.WaitingFor ?? [],
                seed.SourceLogicalBytes, LibraryLinkPresent(seed), seed.SeedingPath, seed.LibraryPath));
        }

        return result;
    }

    private bool LibraryLinkPresent(SeedReleaseOperation seed) =>
        files.TryInspect(seed.LibraryPath, out var file) && file.PhysicalIdentity == seed.SeedingPhysicalIdentity;

    private bool InsideLibrary(string path)
    {
        try
        {
            return library.GetVirtualFolders().SelectMany(folder => folder.Locations ?? [])
                .Select(location => files.TryCanonicalize(location, out var canonical) ? canonical : location)
                .Any(root => ImportPaths.Normalize(root) is { } normalized && ImportPaths.Within(normalized, path));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return true;
        }
    }

    private bool CanSee(User user, bool administrator, Entry? entry, Episode? episode)
    {
        // A detached row (its entry was removed) is visible to administrators only.
        if (entry is null) return administrator;
        return access.CanRead(user, entry) && (episode is null || access.CanReadEpisode(user, episode));
    }

    private async Task<bool> CanSeeAsync(User user, bool administrator, ImportOperation operation, CancellationToken cancellationToken)
    {
        var entry = operation.EntryId is { } entryId
            ? await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
            : null;
        var episode = operation.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
            : null;
        return CanSee(user, administrator, entry, episode);
    }

    private async Task<Dictionary<Guid, Entry>> LoadEntriesAsync(IEnumerable<ImportOperation> operations, CancellationToken cancellationToken)
    {
        var ids = operations.Where(operation => operation.EntryId.HasValue).Select(operation => operation.EntryId!.Value).Distinct().ToArray();
        return await database.Entries.AsNoTracking().Where(entry => ids.Contains(entry.Id)).ToDictionaryAsync(entry => entry.Id, cancellationToken);
    }

    private async Task<Dictionary<Guid, Episode>> LoadEpisodesAsync(IEnumerable<ImportOperation> operations, CancellationToken cancellationToken)
    {
        var ids = operations.Where(operation => operation.EpisodeId.HasValue).Select(operation => operation.EpisodeId!.Value).Distinct().ToArray();
        return await database.Episodes.AsNoTracking().Where(episode => ids.Contains(episode.Id)).ToDictionaryAsync(episode => episode.Id, cancellationToken);
    }

    private async Task<bool> IsAdministratorAsync() => (await authorization.AuthorizeAsync(User, Policies.RequiresElevation)).Succeeded;

    private ObjectResult Refuse(int status, string code, string title) =>
        StatusCode(status, new ProblemDetails { Status = status, Type = code, Title = title });
}
