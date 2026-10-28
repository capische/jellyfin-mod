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

        // Grabs not handed to the client yet (held, being sent, unconfirmed) are queued rows too (P4.FU10). They are read before
        // the imports: a grab accepted between the two reads then shows as both (its held row is dropped below), never as neither.
        var held = await database.GrabOperations.AsNoTracking()
            .Where(grab => grab.ActiveTarget != null && GrabStates.Unresolved.Contains(grab.State) &&
                !database.ImportOperations.Any(operation => operation.GrabId == grab.Id))
            .OrderBy(grab => grab.CreatedAt).ToListAsync(cancellationToken);
        if (entryId is { } heldEntry) held = held.Where(grab => grab.EntryId == heldEntry).ToList();

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

        // A download this read finds complete is imported now, not at the monitor's next poll (P4.FU11); the read is the one the
        // pages poll anyway, so this costs the client nothing more than the monitor's own tick.
        if (settings.ImportEnabled && operations.Any(operation => operation.State == ImportStates.Waiting &&
                clientSnapshots[operation.DownloadClientId].Find(operation.InfoHash) is { Complete: true }))
            monitor.Wake();

        var entries = await LoadEntriesAsync(operations, cancellationToken);
        var episodes = await LoadEpisodesAsync(operations, cancellationToken);
        var clients = await database.AcquisitionDownloadClients.AsNoTracking().ToDictionaryAsync(client => client.Id, cancellationToken);
        var rows = new List<QueueRowDto>();
        // A pack is one row (season and series packs, 2026-10-08): its first listed import stands for the torrent, and the row
        // lists every claimed episode's own state. It is shown only to a user who may see every one of them.
        var grabIds = operations.Select(operation => operation.GrabId).Distinct().ToArray();
        // Every row's grab: a pack's for its one row, and each row's parsed release and indexer (P1.FU20).
        var grabs = await database.GrabOperations.AsNoTracking().Where(grab => grabIds.Contains(grab.Id))
            .ToDictionaryAsync(grab => grab.Id, cancellationToken);
        var packs = grabs.Values.Where(grab => grab.Scope == "season" || grab.Scope == "series").ToDictionary(grab => grab.Id);
        var packChildren = packs.Count == 0 ? [] : await database.ImportOperations.AsNoTracking()
            .Where(operation => packs.Keys.Contains(operation.GrabId)).ToListAsync(cancellationToken);
        var packClaims = packs.Count == 0 ? [] : await database.GrabClaims.AsNoTracking().Where(claim => packs.Keys.Contains(claim.GrabId))
            .Join(database.Episodes.AsNoTracking(), claim => claim.EpisodeId, value => value.Id, (claim, value) => new { claim.GrabId, claim.Held, Episode = value })
            .ToListAsync(cancellationToken);
        var packsShown = new HashSet<Guid>();
        foreach (var operation in operations)
        {
            var entry = operation.EntryId is { } id ? entries.GetValueOrDefault(id) : null;
            var episode = operation.EpisodeId is { } episodeId ? episodes.GetValueOrDefault(episodeId) : null;
            if (packs.TryGetValue(operation.GrabId, out var pack))
            {
                if (!packsShown.Add(pack.Id)) continue;
                var claimed = packClaims.Where(claim => claim.GrabId == pack.Id).Select(claim => claim.Episode)
                    .OrderBy(value => value.SeasonNumber).ThenBy(value => value.EpisodeNumber).ToArray();
                if (!CanSee(user, administrator, entry, null) || claimed.Any(value => !CanSee(user, administrator, entry, value))) continue;
                // The row stands for the pack as a whole (Codex review of the pack plugin, finding 9): an episode still
                // downloading first, then one importing, then a failed one, and only then a seeding one, so the row's state and
                // its Retry match what the pack still needs. Downloading comes before importing (P4.FU12): a pack episode imports
                // as soon as its own file is in, so the row would otherwise say importing and then downloading again.
                var representative = operations.Where(value => value.GrabId == pack.Id)
                    .OrderBy(value => value.State == ImportStates.Waiting ? 0 : value.State == ImportStates.Blocked ? 2
                        : ImportStates.Open.Contains(value.State) ? 1 : value.State == ImportStates.Failed ? 3 : 4)
                    .ThenBy(value => value.CreatedAt).ThenBy(value => value.Id).First();
                var packSeed = representative.State == ImportStates.Completed
                    ? seeds.FirstOrDefault(value => value.ImportOperationId == representative.Id) ?? seeds.FirstOrDefault(value => value.GrabId == pack.Id)
                    : null;
                var packRow = QueueReadModel.Row(representative, packSeed, entry, null,
                    clients.GetValueOrDefault(representative.DownloadClientId), clientSnapshots.GetValueOrDefault(representative.DownloadClientId),
                    settings, administrator, packSeed is not null && LibraryLinkPresent(packSeed)) with { Episode = null };
                if (states is { Length: > 0 } && !states.Contains(packRow.State, StringComparer.Ordinal)) continue;
                rows.Add(packRow with
                {
                    Pack = QueueReadModel.Pack(pack, claimed, packChildren, clientSnapshots.GetValueOrDefault(representative.DownloadClientId),
                        packClaims.Where(claim => claim.GrabId == pack.Id && claim.Held).Select(claim => claim.Episode.Id).ToHashSet()),
                    Parsed = QueueReadModel.Parsed(pack),
                    IndexerName = pack.IndexerName,
                    Mode = pack.Mode
                });
                continue;
            }

            if (!CanSee(user, administrator, entry, episode)) continue;
            var seed = seeds.FirstOrDefault(value => value.ImportOperationId == operation.Id);
            var row = QueueReadModel.Row(operation, seed, entry, episode, clients.GetValueOrDefault(operation.DownloadClientId),
                clientSnapshots.GetValueOrDefault(operation.DownloadClientId), settings, administrator,
                seed is not null && LibraryLinkPresent(seed));
            if (states is { Length: > 0 } && !states.Contains(row.State, StringComparer.Ordinal)) continue;
            var grab = grabs.GetValueOrDefault(operation.GrabId);
            rows.Add(grab is null ? row : row with
            {
                Parsed = QueueReadModel.Parsed(grab), IndexerName = grab.IndexerName, Mode = grab.Mode, Replaces = QueueReadModel.Replaces(grab)
            });
        }

        held = held.Where(grab => !grabs.ContainsKey(grab.Id)).ToList();
        if (held.Count > 0 && (states is not { Length: > 0 } || states.Contains("queued", StringComparer.Ordinal)))
            rows.AddRange(await HeldRowsAsync(held, user, administrator, clients, cancellationToken));

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
            var packGrab = await PackOfAsync(operation, cancellationToken);
            // A pack is acted on only by someone who may read every episode it claimed (Codex review of the pack plugin,
            // finding 10).
            if (packGrab is not null && !await CanSeePackAsync(user, true, packGrab, cancellationToken)) return NotFound();
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
            else if (packGrab is not null)
            {
                // A failed episode of a pack is retried alone, with its own intent and ancestry; a retry on the pack's row
                // (any other episode of it) retries every failed episode of the pack (Codex review of the pack plugin,
                // finding 5).
                var failed = operation.State == ImportStates.Failed
                    ? [operation]
                    : await database.ImportOperations.Where(value => value.GrabId == packGrab.Id && value.State == ImportStates.Failed &&
                            !database.ImportOperations.Any(retry => retry.RetryOfId == value.Id))
                        .OrderBy(value => value.CreatedAt).ToListAsync(cancellationToken);
                if (failed.Count == 0) return Refuse(409, "import_not_retryable", "Only blocked or failed imports can be retried.");
                ImportOperation? first = null;
                foreach (var child in failed)
                {
                    var (retried, refusal) = await ImportService.RetryPackEpisodeAsync(database, packGrab, child, now, cancellationToken);
                    if (refusal is not null)
                        return refusal == "import_open"
                            ? Refuse(409, "import_open", "Another import of this episode is still open.")
                            : Refuse(409, "grab_missing", "The grab of this import no longer exists.");
                    first ??= retried;
                }

                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
                {
                    return Refuse(409, "grab_active", "Another grab now holds this episode or this pack's season; the pack is not imported again.");
                }

                result = first!;
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
    /// Removes a row. Never touches a library file. With <c>removeFromClient</c>, has the client forget the torrent, only when
    /// this plugin added it, and keeps its files for the manual cleanup; with <c>deleteFiles</c> as well, the client deletes
    /// what the torrent downloaded instead, only while no file of it was ever imported (user, 2026-10-26), and otherwise the
    /// removal is refused with nothing changed; with <c>blocklist</c>, Phase 4 scoring rejects the release afterwards.
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
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken);
        var pack = grab is not null && ReleaseScopes.IsPack(grab.Scope);
        // A pack is removed whole, so only by someone who may read every episode it claimed (Codex review of the pack plugin,
        // finding 10); every import of it still in the queue, open or failed, and every seed release of it go with it
        // (finding 9).
        if (pack && !await CanSeePackAsync(user, true, grab!, cancellationToken)) return NotFound();
        var removed = pack
            ? await database.ImportOperations.Where(value => value.GrabId == grab!.Id && (ImportStates.Open.Contains(value.State) ||
                value.State == ImportStates.Failed && !database.ImportOperations.Any(retry => retry.RetryOfId == value.Id)))
                .ToListAsync(cancellationToken)
            : ImportStates.Open.Contains(operation.State) || operation.State == ImportStates.Failed ? [operation] : [];
        var removedSeeds = pack
            ? await database.SeedReleaseOperations.Where(value => value.GrabId == grab!.Id && SeedReleaseStates.Open.Contains(value.State))
                .ToListAsync(cancellationToken)
            : await database.SeedReleaseOperations.Where(value => value.ImportOperationId == id && SeedReleaseStates.Open.Contains(value.State))
                .ToListAsync(cancellationToken);
        if (removed.Count == 0 && removedSeeds.Count == 0) return Refuse(409, "not_in_queue", "This import is no longer in the queue.");
        // A seeding copy being released waits for that release; a deleting removal asks the client first, so a release that
        // removed the torrent and then timed out does not hold it up, and one whose torrent is still there is refused below as
        // delete_not_allowed (Codex review of the P2 fixes, 2026-10-27).
        if (!request.DeleteFiles && removedSeeds.Any(value => value.State == SeedReleaseStates.Removing))
            return Refuse(409, "seed_release_in_progress", "The seeding copy is being released; try again shortly.");
        // Deleting the downloaded files (user, 2026-10-26; Transmission's own delete-local-data, user, 2026-10-27) is the client's
        // delete-with-data, so it needs the client removal.
        if (request.DeleteFiles && !request.RemoveFromClient)
            return Refuse(409, DeleteNotAllowed, "Deleting the downloaded files also removes the torrent from the client.");

        var removedFromClient = false;
        var deletedFiles = false;
        if (request.RemoveFromClient)
        {
            var client = await database.AcquisitionDownloadClients.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == operation.DownloadClientId, cancellationToken);
            if (client is null || drivers.Get(client.Kind) is not { } driver)
                return Refuse(409, ImportReasons.ClientMissing, "The download client is no longer configured.");
            // The removed row's own torrent is always asked for: a failed row is not among the tracked hashes, and leaving it
            // out would read as the client no longer having it (Codex review 2026-10-26, P2 6).
            var snapshot = await imports.SnapshotAsync(client.Id, TimeSpan.Zero, cancellationToken, operation.InfoHash);
            if (!snapshot.Reachable)
                return Refuse(503, ImportReasons.ClientUnreachable, "The download client cannot be reached; nothing was changed.");
            // A torrent the client no longer has is not deleted, so the row goes with nothing refused (Codex review 2026-10-26,
            // P2 7); every check below applies only to a torrent the client still holds.
            if (snapshot.Find(operation.InfoHash) is { } torrent)
            {
                // Its files are deleted only while none of them reached the library: no import of this grab or of this torrent
                // ever recorded a destination, linked or bound, and no seed release exists for either (for a pack, none of its
                // episodes). Refused, never quietly downgraded to keeping the files.
                if (request.DeleteFiles && (removedSeeds.Count > 0 || await AnyImportedAsync(operation, cancellationToken)))
                    return Refuse(409, DeleteNotAllowed,
                        "Part of this download is already in your library, so its files cannot be deleted; remove it without deleting them.");
                // Only a torrent this plugin added, and never one whose data lies in a library folder. A removal that would
                // delete files is refused as delete_not_allowed (review P3 9).
                if (grab is null || !torrent.Labels.Contains(GrabService.OwnerLabel(grab.Id), StringComparer.Ordinal))
                    return Refuse(409, request.DeleteFiles ? DeleteNotAllowed : SeedReleaseReasons.NotOwned,
                        "JellyfinMod did not add this torrent; remove it in the client.");
                var mappings = await database.DownloadClientPathMappings.AsNoTracking()
                    .Where(mapping => mapping.DownloadClientId == client.Id).ToListAsync(cancellationToken);
                // Every path the removal deletes, resolved through every symbolic link: an alias in the download folder that
                // points into a library must not let the client delete library media (whole-review P1 9).
                var dataPaths = ImportPaths.ResolveTorrentData(torrent, mappings, client, files);
                var verified = dataPaths is null ? null : TorrentDataRemoval.Inspect(torrent, dataPaths, files);
                if (dataPaths is null || verified is null || dataPaths.Any(InsideLibrary) || verified.Any(file => InsideLibrary(file.Path)))
                    return Refuse(409, request.DeleteFiles ? DeleteNotAllowed : SeedReleaseReasons.SeedingInsideLibrary,
                        "The torrent's data is not in a mapped download folder outside every library; remove it in the client.");
                // A downloaded file with a second name somewhere (a hardlink the records do not know of) is never deleted.
                if (request.DeleteFiles && verified.Any(file => !files.TryInspect(file.Path, out var current) || current.HardlinkCount > 1))
                    return Refuse(409, DeleteNotAllowed,
                        "A downloaded file is also linked elsewhere, so its files cannot be deleted; remove it without deleting them.");
                // Without deleteFiles the client only forgets the torrent and nothing is deleted: the checked files are recorded
                // on the operation first, so a restart keeps them, and stay on disk for the administrator to remove (Codex delta
                // reviews 1 and 5; user decisions 2026-10-02). With it, the same record names what the client was asked to delete.
                operation.CleanupManifest = TorrentDataRemoval.Serialize(verified);
                await database.SaveChangesAsync(CancellationToken.None);
                try
                {
                    var connection = await configuration.ConnectAsync(client, cancellationToken);
                    await driver.RemoveAsync(connection, operation.InfoHash, deleteData: request.DeleteFiles, cancellationToken);
                    removedFromClient = true;
                    deletedFiles = request.DeleteFiles;
                }
                catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
                    HttpRequestException)
                {
                    return Refuse(503, ImportReasons.ClientUnreachable, "The download client did not confirm the removal; check it in the client.");
                }
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var child in removed)
        {
            child.State = ImportStates.Cancelled;
            child.Reason = ImportReasons.Cancelled;
            child.OpenGrabKey = null;
            child.CancelledBy = user.Id;
            child.CompletedAt = child.UpdatedAt = now;
        }

        foreach (var removedSeed in removedSeeds)
        {
            removedSeed.State = SeedReleaseStates.Cancelled;
            removedSeed.Reason = SeedReleaseReasons.Cancelled;
            removedSeed.CompletedAt = removedSeed.UpdatedAt = now;
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
                Summary = ImportService.Bound("Removed from the queue" + (deletedFiles
                        ? " and from the download client, and its downloaded files were deleted: "
                        : removedFromClient ? " and from the download client; its files are kept on disk: "
                        : request.DeleteFiles ? "; the download client no longer had the torrent, so nothing was deleted: " : ": ") +
                    operation.ReleaseTitle),
                Data = JsonSerializer.Serialize(new
                {
                    operationId = operation.Id, operation.EpisodeId, removeFromClient = removedFromClient, deleteFiles = deletedFiles, request.Blocklist,
                    grabId = pack ? grab!.Id : (Guid?)null, removedImports = removed.Select(child => child.Id).ToArray()
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
                    Data = JsonSerializer.Serialize(new { operationId = operation.Id, operation.EpisodeId, operation.InfoHash })
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
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var seeds = await database.SeedReleaseOperations.AsNoTracking().Where(seed => SeedReleaseStates.Open.Contains(seed.State))
            .OrderBy(seed => seed.PreparedAt).ToListAsync(cancellationToken);
        // The same target visibility as the queue: an administrator limited to some libraries sees no seed, hash or path of
        // another library's download (whole-review chunk 3a, P2 5).
        var entryIds = seeds.Where(seed => seed.EntryId.HasValue).Select(seed => seed.EntryId!.Value).Distinct().ToArray();
        var episodeIds = seeds.Where(seed => seed.EpisodeId.HasValue).Select(seed => seed.EpisodeId!.Value).Distinct().ToArray();
        var entries = await database.Entries.AsNoTracking().Where(entry => entryIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, cancellationToken);
        var episodes = await database.Episodes.AsNoTracking().Where(episode => episodeIds.Contains(episode.Id))
            .ToDictionaryAsync(episode => episode.Id, cancellationToken);
        var result = new List<SeedingDto>();
        foreach (var seed in seeds)
        {
            var entry = seed.EntryId is { } entryId ? entries.GetValueOrDefault(entryId) : null;
            var episode = seed.EpisodeId is { } episodeId ? episodes.GetValueOrDefault(episodeId) : null;
            if (!CanSee(user, true, entry, episode)) continue;
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

    /// <summary>The pack grab an import belongs to, or null for a single-episode or movie import.</summary>
    private async Task<GrabOperation?> PackOfAsync(ImportOperation operation, CancellationToken cancellationToken)
    {
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken);
        return grab is not null && ReleaseScopes.IsPack(grab.Scope) ? grab : null;
    }

    /// <summary>Whether the user may see the pack's title and every episode it claimed, as the queue lists it.</summary>
    private async Task<bool> CanSeePackAsync(User user, bool administrator, GrabOperation grab, CancellationToken cancellationToken)
    {
        var entry = grab.EntryId is { } entryId
            ? await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
            : null;
        if (!CanSee(user, administrator, entry, null)) return false;
        var claimed = await database.GrabClaims.AsNoTracking().Where(claim => claim.GrabId == grab.Id)
            .Join(database.Episodes.AsNoTracking(), claim => claim.EpisodeId, value => value.Id, (_, value) => value)
            .ToListAsync(cancellationToken);
        return claimed.All(value => CanSee(user, administrator, entry, value));
    }

    /// <summary>
    /// The queued rows of grabs the client has not accepted yet (P4.FU10), with the visibility of the rows they become: a title
    /// and episode the user may read, and for a pack every episode it claimed.
    /// </summary>
    private async Task<List<QueueRowDto>> HeldRowsAsync(IReadOnlyList<GrabOperation> held, User user, bool administrator,
        IReadOnlyDictionary<Guid, AcquisitionDownloadClient> clients, CancellationToken cancellationToken)
    {
        var entryIds = held.Where(grab => grab.EntryId.HasValue).Select(grab => grab.EntryId!.Value).Distinct().ToArray();
        var entries = await database.Entries.AsNoTracking().Where(entry => entryIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, cancellationToken);
        var heldIds = held.Select(grab => grab.Id).ToArray();
        var claims = await database.GrabClaims.AsNoTracking().Where(claim => heldIds.Contains(claim.GrabId))
            .Join(database.Episodes.AsNoTracking(), claim => claim.EpisodeId, value => value.Id, (claim, value) => new { claim.GrabId, claim.Held, Episode = value })
            .ToListAsync(cancellationToken);
        var rows = new List<QueueRowDto>();
        foreach (var grab in held)
        {
            var entry = grab.EntryId is { } id ? entries.GetValueOrDefault(id) : null;
            var claimed = claims.Where(claim => claim.GrabId == grab.Id).Select(claim => claim.Episode)
                .OrderBy(value => value.SeasonNumber).ThenBy(value => value.EpisodeNumber).ToArray();
            var episode = grab.EpisodeId is { } episodeId ? claimed.FirstOrDefault(value => value.Id == episodeId) : null;
            if (!CanSee(user, administrator, entry, episode) || claimed.Any(value => !CanSee(user, administrator, entry, value))) continue;
            rows.Add(QueueReadModel.Held(grab, entry, episode, claimed, clients.GetValueOrDefault(grab.DownloadClientId), administrator,
                claims.Where(claim => claim.GrabId == grab.Id && claim.Held).Select(claim => claim.Episode.Id).ToHashSet()));
        }

        return rows;
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

    /// <summary>Why a removal that would delete the downloaded files was refused, with nothing changed.</summary>
    private const string DeleteNotAllowed = "delete_not_allowed";

    /// <summary>
    /// Whether any file of this download may be in a library: an import of its grab or of its torrent that recorded a
    /// destination (recorded before the link is made), linked, bound or reached a linking state, or a seed release of either.
    /// </summary>
    private async Task<bool> AnyImportedAsync(ImportOperation operation, CancellationToken cancellationToken)
    {
        string[] linkedStates = [ImportStates.Linking, ImportStates.Linked, ImportStates.Scanning, ImportStates.Completed];
        return await database.ImportOperations.AnyAsync(value => (value.GrabId == operation.GrabId || value.InfoHash == operation.InfoHash) &&
                (value.DestinationPath != null || value.LinkedAt != null || value.BindingId != null || linkedStates.Contains(value.State)),
                cancellationToken) ||
            await database.SeedReleaseOperations.AnyAsync(value => value.GrabId == operation.GrabId || value.InfoHash == operation.InfoHash,
                cancellationToken);
    }

    private ObjectResult Refuse(int status, string code, string title) =>
        StatusCode(status, new ProblemDetails { Status = status, Type = code, Title = title });
}
