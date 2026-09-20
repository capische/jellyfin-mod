using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Import;

/// <summary>Serializes import ticks, recovery and queue actions within the plugin process.</summary>
public sealed class ImportTickGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires exclusive import access.</summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>What one tick did, for the repair task and the logs.</summary>
public sealed record ImportTickResult(int OpenImports, int OpenSeedReleases, int ClientReads, bool ClientReachable);

/// <summary>
/// The import pipeline (P5.I3–I5): watches accepted grabs in the client, identifies the file, hardlinks it into the
/// library without ever copying, requests a targeted scan and attributes reconciliation's binding to the operation.
/// </summary>
/// <remarks>
/// Every physical fact is recorded before it is acted on, and no database transaction is held across the hardlink or
/// an HTTP call. The only binding writer stays the Phase 2 reconciliation service; this service reads bindings.
/// </remarks>
public sealed class ImportService(
    ModDbContext database,
    ClientSnapshotCache snapshots,
    ClientSnapshotReader reader,
    UnixFileInspector files,
    MediaStorageIdentity mounts,
    ILibraryManager library,
    ILibraryMonitor libraryMonitor,
    ReconciliationLibraryLock libraryLock,
    SeedReleaseService seedReleases,
    TimeProvider time,
    ILogger<ImportService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Advances every open import and seed release once, reading each client at most once.</summary>
    public async Task<ImportTickResult> TickAsync(CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        await EnsureOperationsAsync(cancellationToken).ConfigureAwait(false);
        var operations = await database.ImportOperations.Where(operation => ImportStates.Open.Contains(operation.State))
            .OrderBy(operation => operation.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var seeds = await database.SeedReleaseOperations.Where(operation => SeedReleaseStates.Open.Contains(operation.State))
            .OrderBy(operation => operation.PreparedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (operations.Count == 0 && seeds.Count == 0) return new(0, 0, 0, true);

        var reads = snapshots.Reads;
        var clientIds = operations.Select(operation => operation.DownloadClientId)
            .Concat(seeds.Select(operation => operation.DownloadClientId)).Distinct().ToArray();
        var clientSnapshots = new Dictionary<Guid, ClientSnapshot>();
        // Every tick reads the client once; queue polls between ticks share that read for the freshness window.
        foreach (var clientId in clientIds)
            clientSnapshots[clientId] = await SnapshotAsync(clientId, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await AdvanceAsync(operation.Id, clientSnapshots[operation.DownloadClientId], settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Import {Operation} could not advance; it is retried on the next tick", operation.Id);
            }
        }

        // Seed releases created by imports completed in this tick are evaluated on the next one.
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await seedReleases.EvaluateAsync(seed.Id, clientSnapshots[seed.DownloadClientId], settings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError(error, "Seed release {Operation} could not advance; it is retried on the next tick", seed.Id);
            }
        }

        return new(operations.Count, seeds.Count, (int)(snapshots.Reads - reads), clientSnapshots.Values.All(value => value.Reachable));
    }

    /// <summary>Returns whether any import or seed release is open, so an idle monitor never reads the client.</summary>
    public async Task<bool> HasOpenWorkAsync(CancellationToken cancellationToken) =>
        await database.ImportOperations.AnyAsync(operation => ImportStates.Open.Contains(operation.State), cancellationToken)
            .ConfigureAwait(false) ||
        await database.SeedReleaseOperations.AnyAsync(operation => SeedReleaseStates.Open.Contains(operation.State), cancellationToken)
            .ConfigureAwait(false) ||
        await database.GrabOperations.AnyAsync(grab => grab.State == GrabStates.Accepted && grab.ActiveTarget != null &&
            !database.ImportOperations.Any(operation => operation.GrabId == grab.Id), cancellationToken).ConfigureAwait(false) ||
        await database.UpgradeOperations.AnyAsync(upgrade => UpgradeStates.Open.Contains(upgrade.State), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Reads one client for every hash the plugin tracks there, through the shared cache.</summary>
    public async Task<ClientSnapshot> SnapshotAsync(Guid clientId, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var hashes = await ClientSnapshotReader.TrackedHashesAsync(database, clientId, cancellationToken).ConfigureAwait(false);
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == clientId, cancellationToken).ConfigureAwait(false);
        return await snapshots.GetAsync(clientId, hashes, maxAge,
            (wanted, token) => reader.ReadAsync(client, clientId, wanted, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the import operation for an accepted grab that has none, so every accepted grab is tracked.</summary>
    public static async Task<ImportOperation?> CreateForGrabAsync(ModDbContext database, GrabOperation grab, DateTime now,
        CancellationToken cancellationToken)
    {
        if (grab.EntryId is not { } entryId || grab.InfoHash is null) return null;
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
            .ConfigureAwait(false);
        if (entry?.TargetLibraryId is not { } libraryId) return null;
        var operation = new ImportOperation
        {
            GrabId = grab.Id, OpenGrabKey = grab.Id.ToString("N"), EntryId = entryId, EpisodeId = grab.EpisodeId,
            TargetLibraryId = libraryId, DownloadClientId = grab.DownloadClientId, InfoHash = grab.InfoHash,
            ReleaseTitle = grab.RawTitle, Intent = grab.Intent, State = ImportStates.Waiting, CreatedAt = now, UpdatedAt = now
        };
        database.ImportOperations.Add(operation);
        return operation;
    }

    private async Task EnsureOperationsAsync(CancellationToken cancellationToken)
    {
        var orphans = await database.GrabOperations.Where(grab => grab.State == GrabStates.Accepted && grab.ActiveTarget != null &&
                !database.ImportOperations.Any(operation => operation.GrabId == grab.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (orphans.Count == 0) return;
        foreach (var grab in orphans) await CreateForGrabAsync(database, grab, Now, cancellationToken).ConfigureAwait(false);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent acceptance created it first.
            database.ChangeTracker.Clear();
        }
    }

    private async Task AdvanceAsync(Guid operationId, ClientSnapshot snapshot, AcquisitionSettings settings,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var operation = await database.ImportOperations.SingleAsync(value => value.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (!ImportStates.Open.Contains(operation.State)) return;
        var entry = operation.EntryId is { } entryId
            ? await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var episode = operation.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (entry is null || operation.EpisodeId.HasValue && episode is null)
        {
            await FailAsync(operation, ImportReasons.TargetMissing, "The catalog title of this grab no longer exists.")
                .ConfigureAwait(false);
            return;
        }

        switch (operation.State)
        {
            case ImportStates.Waiting:
            case ImportStates.Blocked when operation.Reason is ImportReasons.TorrentMissing or ImportReasons.ImportDisabled:
                await WatchAsync(operation, snapshot, settings, entry, episode, cancellationToken).ConfigureAwait(false);
                return;
            case ImportStates.Identifying:
                await IdentifyAsync(operation, snapshot, settings, entry, episode, cancellationToken).ConfigureAwait(false);
                return;
            case ImportStates.Linking:
                await LinkAsync(operation.Id, operation.TargetLibraryId, entry, episode, cancellationToken).ConfigureAwait(false);
                return;
            case ImportStates.Linked:
                await RecoverLinkedAsync(operation, entry, episode, cancellationToken).ConfigureAwait(false);
                return;
            case ImportStates.Scanning:
            case ImportStates.Blocked when operation.Reason is ImportReasons.BindingNotObserved or ImportReasons.ScanTimeout:
                await ObserveBindingAsync(operation, settings, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Tracks a download, projecting its progress without writing the entry, until the client reports it complete.</summary>
    private async Task WatchAsync(ImportOperation operation, ClientSnapshot snapshot, AcquisitionSettings settings, Entry entry,
        Episode? episode, CancellationToken cancellationToken)
    {
        // An unreachable client leaves the operation and its last observation untouched (P5.I3).
        if (!snapshot.Reachable) return;
        var torrent = snapshot.Find(operation.InfoHash);
        if (torrent is null)
        {
            // Removed in the client, perhaps by an administrator: blocked, never failed, so a re-add resumes it.
            if (operation.State != ImportStates.Blocked || operation.Reason != ImportReasons.TorrentMissing)
                await BlockAsync(operation, ImportReasons.TorrentMissing, "The download client no longer holds this torrent.")
                    .ConfigureAwait(false);
            return;
        }

        var changed = Observe(operation, torrent, settings);
        if (operation.State == ImportStates.Blocked)
        {
            operation.State = ImportStates.Waiting;
            operation.Reason = null;
            operation.Error = null;
            operation.UpdatedAt = Now;
            changed = true;
        }

        if (!torrent.Complete)
        {
            if (changed) await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        operation.CompletedDownloadAt ??= Now;
        if (!settings.ImportEnabled)
        {
            await BlockAsync(operation, ImportReasons.ImportDisabled, "Importing is turned off in the plugin settings.")
                .ConfigureAwait(false);
            return;
        }

        operation.State = ImportStates.Identifying;
        operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await IdentifyAsync(operation, snapshot, settings, entry, episode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the client columns only when something a user can see changed (P5.I3 write-volume rule).</summary>
    private bool Observe(ImportOperation operation, ClientTorrentStatus torrent, AcquisitionSettings settings)
    {
        var now = Now;
        var progress = Math.Round(Math.Clamp(torrent.PercentDone, 0, 1), 4);
        var downloaded = Math.Max(0, torrent.SizeWhenDone - torrent.LeftUntilDone);
        var changed = operation.Progress != progress || operation.DownloadRateBytes != torrent.RateDownload ||
            operation.ClientStatus != torrent.Status || operation.SizeBytes != torrent.SizeWhenDone ||
            operation.EtaSeconds != torrent.EtaSeconds;
        if (operation.Progress is null || progress > operation.Progress) operation.LastProgressAt = now;
        var stalled = progress < 1 && operation.LastProgressAt is { } last &&
            now - last >= TimeSpan.FromHours(Math.Max(1, settings.StalledAfterHours));
        if (stalled && operation.StalledSince is null)
        {
            operation.StalledSince = now;
            changed = true;
        }
        else if (!stalled && operation.StalledSince is not null)
        {
            operation.StalledSince = null;
            changed = true;
        }

        if (!changed) return false;
        operation.Progress = progress;
        operation.SizeBytes = torrent.SizeWhenDone;
        operation.DownloadedBytes = downloaded;
        operation.DownloadRateBytes = torrent.RateDownload;
        operation.EtaSeconds = torrent.EtaSeconds;
        operation.ClientStatus = torrent.Status;
        operation.ObservedAt = now;
        return true;
    }

    /// <summary>Identifies the one file that belongs to the grab and records its physical identity (P5.I3).</summary>
    private async Task IdentifyAsync(ImportOperation operation, ClientSnapshot snapshot, AcquisitionSettings settings, Entry entry,
        Episode? episode, CancellationToken cancellationToken)
    {
        if (!snapshot.Reachable) return;
        var torrent = snapshot.Find(operation.InfoHash);
        if (torrent is null)
        {
            await BlockAsync(operation, ImportReasons.TorrentMissing, "The download client no longer holds this torrent.")
                .ConfigureAwait(false);
            return;
        }

        var choice = ImportFileSelector.Choose(torrent, settings.VideoExtensions, episode);
        if (choice.File is not { } file)
        {
            await BlockAsync(operation, choice.Reason!, choice.Detail).ConfigureAwait(false);
            return;
        }

        var clientPath = ImportPaths.Normalize((torrent.DownloadDirectory ?? string.Empty).TrimEnd('/') + "/" + file.Name);
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == operation.DownloadClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            await BlockAsync(operation, ImportReasons.ClientMissing, "The download client is no longer configured.").ConfigureAwait(false);
            return;
        }

        var mappings = await database.DownloadClientPathMappings.AsNoTracking()
            .Where(mapping => mapping.DownloadClientId == client.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var localPath = clientPath is null ? null : ImportPaths.Map(clientPath, mappings, client);
        operation.SourceClientPath = clientPath;
        if (localPath is null)
        {
            await BlockAsync(operation, ImportReasons.PathUnmapped,
                "No verified path mapping turns the client's download path into a path Jellyfin can open.").ConfigureAwait(false);
            return;
        }

        if (!files.TryInspect(localPath, out var source))
        {
            await BlockAsync(operation, ImportReasons.SourceMissing, "The downloaded file cannot be found at its mapped path.")
                .ConfigureAwait(false);
            return;
        }

        if ((long)source.LogicalBytes != file.Length)
        {
            await BlockAsync(operation, ImportReasons.SourceSizeMismatch,
                $"The file has {source.LogicalBytes} bytes but the client reports {file.Length}.").ConfigureAwait(false);
            return;
        }

        operation.SourceLocalPath = source.CanonicalPath;
        operation.SourcePhysicalIdentity = source.PhysicalIdentity;
        operation.SourceLogicalBytes = (long)source.LogicalBytes;
        operation.SourceMountIdentity = mounts.Capture(source.CanonicalPath);
        operation.State = ImportStates.Linking;
        operation.Reason = null;
        operation.Error = null;
        operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await LinkAsync(operation.Id, operation.TargetLibraryId, entry, episode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hardlinks the source into the library under the target library's reconciliation lock (P5.I4). Never copies and
    /// never moves: any failure blocks with a stable reason and leaves the library untouched.
    /// </summary>
    private async Task LinkAsync(Guid operationId, Guid libraryId, Entry entry, Episode? episode, CancellationToken cancellationToken)
    {
        ImportOperation operation;
        await using (await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false))
        {
            database.ChangeTracker.Clear();
            operation = await database.ImportOperations.SingleAsync(value => value.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation.State != ImportStates.Linking) return;
            if (operation.SourceLocalPath is null || !files.TryInspect(operation.SourceLocalPath, out var source) ||
                source.PhysicalIdentity != operation.SourcePhysicalIdentity)
            {
                // A crash between identifying and linking, or a source that changed underneath: identify again.
                if (operation.DestinationPath is { } recorded && files.TryInspect(recorded, out var existing) &&
                    existing.PhysicalIdentity == operation.SourcePhysicalIdentity)
                {
                    await MarkLinkedAsync(operation, existing).ConfigureAwait(false);
                }
                else
                {
                    operation.State = ImportStates.Identifying;
                    operation.DestinationPath = null;
                    operation.UpdatedAt = Now;
                    await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            // Recovery: a destination recorded by an interrupted attempt that already shares the source inode is done.
            if (operation.DestinationPath is { } previous && files.TryInspect(previous, out var linkedBefore) &&
                linkedBefore.PhysicalIdentity == source.PhysicalIdentity)
            {
                await MarkLinkedAsync(operation, linkedBefore).ConfigureAwait(false);
            }
            else
            {
                var plan = await PlanDestinationAsync(operation, entry, episode, source, cancellationToken).ConfigureAwait(false);
                if (plan.Reason is { } reason)
                {
                    await BlockAsync(operation, reason, plan.Detail).ConfigureAwait(false);
                    return;
                }

                // The destination is durable before the filesystem changes, so recovery knows where to look.
                operation.DestinationPath = plan.Path;
                operation.DestinationRoot = plan.Root;
                operation.VersionLabel = plan.Label;
                operation.UpdatedAt = Now;
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

                var created = new List<string>();
                try
                {
                    foreach (var directory in plan.MissingDirectories)
                    {
                        Directory.CreateDirectory(directory);
                        created.Add(directory);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    RemoveCreated(created);
                    await BlockAsync(operation, ImportReasons.DestinationNotWritable, "The library folder cannot be written.")
                        .ConfigureAwait(false);
                    return;
                }

                var result = files.Link(source.CanonicalPath, plan.Path!);
                if (!result.Linked)
                {
                    RemoveCreated(created);
                    var (linkReason, linkDetail) = result.CrossDevice
                        ? (ImportReasons.CrossFilesystem, "The download and the library are on different mounts; nothing was copied.")
                        : result.Exists
                            ? (ImportReasons.DestinationCollision, "A different file appeared at the destination; it was left untouched.")
                            : (ImportReasons.DestinationNotWritable, $"The hardlink could not be created (errno {result.Errno}).");
                    await BlockAsync(operation, linkReason, linkDetail).ConfigureAwait(false);
                    return;
                }

                if (!files.TryInspect(plan.Path!, out var linked) || linked.PhysicalIdentity != source.PhysicalIdentity ||
                    linked.HardlinkCount < 2 || linked.LogicalBytes != source.LogicalBytes)
                {
                    await BlockAsync(operation, ImportReasons.DestinationCollision,
                        "The destination did not verify as a hardlink of the download.").ConfigureAwait(false);
                    return;
                }

                await MarkLinkedAsync(operation, linked).ConfigureAwait(false);
                logger.LogInformation("Import {Operation} hardlinked the download into library {Library}", operation.Id,
                    operation.TargetLibraryId);
            }
        }

        await RequestScanAsync(operation).ConfigureAwait(false);
    }

    private async Task MarkLinkedAsync(ImportOperation operation, UnixFileSnapshot linked)
    {
        operation.State = ImportStates.Linked;
        operation.DestinationPath = linked.CanonicalPath;
        operation.DestinationPhysicalIdentity = linked.PhysicalIdentity;
        operation.HardlinkCountAfter = linked.HardlinkCount;
        operation.LinkedAt ??= Now;
        operation.Reason = null;
        operation.Error = null;
        operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void RemoveCreated(List<string> created)
    {
        // Only folders this attempt created, deepest first, and only while they are still empty.
        for (var index = created.Count - 1; index >= 0; index--) files.RemoveEmptyDirectory(created[index]);
    }

    /// <summary>
    /// A <c>linked</c> operation found at the start of a tick was interrupted before its scan: re-verify the link, then scan.
    /// </summary>
    private async Task RecoverLinkedAsync(ImportOperation operation, Entry entry, Episode? episode,
        CancellationToken cancellationToken)
    {
        if (operation.DestinationPath is { } destination && files.TryInspect(destination, out var linked) &&
            linked.PhysicalIdentity == operation.DestinationPhysicalIdentity)
        {
            await RequestScanAsync(operation).ConfigureAwait(false);
            return;
        }

        if (operation.SourceLocalPath is { } sourcePath && files.TryInspect(sourcePath, out var source) &&
            source.PhysicalIdentity == operation.SourcePhysicalIdentity)
        {
            // The library link vanished before binding but the download survives: link again.
            operation.State = ImportStates.Linking;
            operation.DestinationPath = null;
            operation.UpdatedAt = Now;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await LinkAsync(operation.Id, operation.TargetLibraryId, entry, episode, cancellationToken).ConfigureAwait(false);
            return;
        }

        await BlockAsync(operation, ImportReasons.SourceMissing, "Both the library link and the download are gone.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks Jellyfin to refresh the folder that received the file (P5.I5). A reported change is enough when the
    /// host acts on it, but on the deployed host a brand-new folder stayed unindexed until a library scan ran,
    /// so the second attempt escalates to one rather than repeating a call that already did nothing (P5.I5).
    /// </summary>
    private async Task RequestScanAsync(ImportOperation operation)
    {
        try
        {
            libraryMonitor.ReportFileSystemChanged(operation.DestinationPath!);
            if (operation.ScanAttempts >= 1) library.QueueLibraryScan();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The link stays; the next tick asks again.
            operation.Error = Bound("The targeted scan could not be requested: " + error.Message);
            operation.UpdatedAt = Now;
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        operation.State = ImportStates.Scanning;
        operation.ScanAttempts++;
        operation.ScanRequestedAt = Now;
        operation.Reason = null;
        operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Attributes reconciliation's binding of the destination to this operation and completes it (P5.I5). The binding is
    /// found by its media path; this service never creates one.
    /// </summary>
    private async Task ObserveBindingAsync(ImportOperation operation, AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        var binding = await FindBindingAsync(operation, cancellationToken).ConfigureAwait(false);
        if (binding is not null)
        {
            await CompleteAsync(operation.Id, binding.Value.BindingId, binding.Value.NativeItemId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation.State != ImportStates.Scanning) return;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, settings.ScanTimeoutMinutes));
        // The reported change is cheap and usually enough; a minute later the escalation to a library scan is
        // worth its cost, and only after the full window does the operation give up (P5.I5).
        var wait = operation.ScanAttempts < 2 ? TimeSpan.FromSeconds(Math.Min(60, timeout.TotalSeconds)) : timeout;
        if (operation.ScanRequestedAt is not { } requested || Now - requested < wait) return;
        if (operation.ScanAttempts < 2)
        {
            await RequestScanAsync(operation).ConfigureAwait(false);
            return;
        }

        // The hardlink stays; a later full scan still binds it and completes the operation.
        await BlockAsync(operation, ImportReasons.BindingNotObserved,
            "Jellyfin did not bind the imported file after two targeted scans.").ConfigureAwait(false);
    }

    private async Task<(Guid BindingId, Guid NativeItemId)?> FindBindingAsync(ImportOperation operation, CancellationToken cancellationToken)
    {
        if (operation.DestinationPath is not { } destination) return null;
        if (operation.EpisodeId is { } episodeId)
        {
            var bindings = await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == episodeId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return bindings.Where(binding => SamePath(binding.MediaPath, destination))
                .Select(binding => ((Guid, Guid)?)(binding.Id, binding.JellyfinItemId)).FirstOrDefault();
        }

        var movieBindings = await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == operation.EntryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return movieBindings.Where(binding => SamePath(binding.MediaPath, destination))
            .Select(binding => ((Guid, Guid)?)(binding.Id, binding.JellyfinItemId)).FirstOrDefault();
    }

    private bool SamePath(string? mediaPath, string destination) =>
        mediaPath is not null && (string.Equals(mediaPath, destination, StringComparison.Ordinal) ||
            files.TryCanonicalize(mediaPath, out var canonical) && string.Equals(canonical, destination, StringComparison.Ordinal));

    /// <summary>
    /// Completes the import in one transaction: one <c>imported</c> event keyed by the operation, a fresh retention
    /// baseline (P3.T7) and a waiting seed release. The grab's target is released; its hash stays owned until the seed
    /// release ends.
    /// </summary>
    private async Task CompleteAsync(Guid operationId, Guid bindingId, Guid nativeItemId, CancellationToken cancellationToken)
    {
        var entryLibrary = await database.ImportOperations.AsNoTracking().Where(value => value.Id == operationId)
            .Select(value => value.TargetLibraryId).SingleAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await libraryLock.AcquireAsync(entryLibrary, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var operation = await database.ImportOperations.SingleAsync(value => value.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.State == ImportStates.Completed) return;
        var now = Now;
        operation.State = ImportStates.Completed;
        operation.Reason = null;
        operation.Error = null;
        operation.BindingId = bindingId;
        operation.NativeItemId = nativeItemId;
        operation.BoundAt = now;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
        operation.OpenGrabKey = null;
        var grab = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken)
            .ConfigureAwait(false);
        if (grab is not null)
        {
            grab.ActiveTarget = null;
            grab.UpdatedAt = now;
        }

        if (operation.EntryId is { } entryId)
        {
            // The imported representation starts retention from now; old completions and deadlines are ignored (P3.T7).
            await RetentionTargetReset.ResetAsync(database, entryId, operation.EpisodeId, now, cancellationToken).ConfigureAwait(false);
            if (!await database.History.AnyAsync(history => history.Id == operation.Id, cancellationToken).ConfigureAwait(false))
            {
                var episode = operation.EpisodeId is { } episodeId
                    ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                database.History.Add(new HistoryRecord
                {
                    Id = operation.Id, EntryId = entryId, EventType = "imported", CreatedAt = now,
                    Summary = Bound((episode is null ? "Imported " : $"Imported S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} ") +
                        (operation.VersionLabel is { } label ? label + " from " : "from ") + operation.ReleaseTitle),
                    Data = JsonSerializer.Serialize(new
                    {
                        operationId = operation.Id, operation.GrabId, operation.EpisodeId, releaseTitle = operation.ReleaseTitle,
                        versionLabel = operation.VersionLabel, logicalBytes = operation.SourceLogicalBytes,
                        bindingId, jellyfinItemId = nativeItemId, intent = operation.Intent
                    })
                });
            }
        }

        if (!await database.SeedReleaseOperations.AnyAsync(value => value.ImportOperationId == operation.Id, cancellationToken)
                .ConfigureAwait(false))
            database.SeedReleaseOperations.Add(new SeedReleaseOperation
            {
                ImportOperationId = operation.Id, GrabId = operation.GrabId, EntryId = operation.EntryId, EpisodeId = operation.EpisodeId,
                InfoHash = operation.InfoHash, DownloadClientId = operation.DownloadClientId, State = SeedReleaseStates.Waiting,
                Reason = SeedReleaseReasons.GoalUnmet, IndexerRatio = grab?.SeedRatio,
                IndexerSeconds = grab?.SeedMinutes is { } minutes ? minutes * 60L : null,
                SeedingPath = operation.SourceLocalPath ?? string.Empty, SeedingPhysicalIdentity = operation.SourcePhysicalIdentity ?? string.Empty,
                LibraryPath = operation.DestinationPath ?? string.Empty, SourceLogicalBytes = operation.SourceLogicalBytes ?? 0,
                PreparedAt = now, UpdatedAt = now
            });
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Import {Operation} completed: bound as native item {Item}", operation.Id, nativeItemId);
    }

    private sealed record DestinationPlan(string? Path, string? Root, string? Label, IReadOnlyList<string> MissingDirectories,
        string? Reason, string? Detail)
    {
        public static DestinationPlan Blocked(string reason, string detail) => new(null, null, null, [], reason, detail);
    }

    /// <summary>
    /// Chooses the destination: the bound native folder when one exists, otherwise a new folder under the first library
    /// root on the source's mount. Nothing existing is renamed and nothing is overwritten.
    /// </summary>
    private async Task<DestinationPlan> PlanDestinationAsync(ImportOperation operation, Entry entry, Episode? episode,
        UnixFileSnapshot source, CancellationToken cancellationToken)
    {
        var roots = LibraryRoots(operation.TargetLibraryId);
        if (roots.Count == 0)
            return DestinationPlan.Blocked(ImportReasons.LibraryRootMissing, "The target library has no readable folder.");
        var sourceMount = mounts.ReadMountTable();
        var sourceIdentity = sourceMount.Capture(source.CanonicalPath);
        var sameMountRoots = roots.Where(root => SameMount(sourceMount, sourceIdentity, root, source)).ToArray();
        var extension = ImportFileSelector.Extension(source.CanonicalPath);
        if (extension.Length == 0) extension = "mkv";

        if (episode is not null)
        {
            var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
            var addEpisodeVersion = operation.Intent == GrabIntents.AddVersion && settings.EpisodeUpgradesEnabled;
            if (await database.EpisodeBindings.AsNoTracking().AnyAsync(binding => binding.EpisodeId == episode.Id, cancellationToken)
                    .ConfigureAwait(false) && !addEpisodeVersion)
                return DestinationPlan.Blocked(ImportReasons.TargetExists,
                    "This episode already has a file; episode versions are imported only when episode upgrades are enabled.");
            var seriesFolder = await BoundSeriesFolderAsync(entry, roots, cancellationToken).ConfigureAwait(false);
            if (seriesFolder is not null && !SameMount(sourceMount, sourceIdentity, seriesFolder, source))
                return DestinationPlan.Blocked(ImportReasons.CrossFilesystem,
                    "The series folder is on a different mount from the download; nothing was copied.");
            string root;
            if (seriesFolder is null)
            {
                if (sameMountRoots.Length == 0) return CrossMount();
                root = sameMountRoots[0];
                seriesFolder = Path.Combine(root, ImportNaming.TitleFolder(entry.Title, entry.Year, entry.TmdbId));
            }
            else
            {
                var bound = seriesFolder;
                root = roots.First(candidate => MediaStorageIdentity.Contains(candidate, bound));
            }

            var seasonFolder = ExistingSeasonFolder(seriesFolder, episode.SeasonNumber) ??
                Path.Combine(seriesFolder, ImportNaming.SeasonFolder(episode.SeasonNumber));
            string? episodeLabel = null;
            if (addEpisodeVersion)
            {
                var episodeGrab = await database.GrabOperations.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken).ConfigureAwait(false);
                episodeLabel = ImportNaming.VersionLabel(episodeGrab is null ? null : JsonSerializer.Deserialize<ParsedRelease>(episodeGrab.ParsedJson));
            }

            var path = Path.Combine(seasonFolder, ImportNaming.EpisodeFile(Path.GetFileName(seriesFolder), episode.SeasonNumber,
                episode.EpisodeNumber, extension, episodeLabel));
            if (files.Probe(path) != PathPresence.Absent)
                return DestinationPlan.Blocked(ImportReasons.DestinationCollision, "A file already has the episode's destination name.");
            return new(path, root, episodeLabel, Missing(seriesFolder, seasonFolder), null, null);
        }

        var movieFolder = await BoundMovieFolderAsync(entry, roots, cancellationToken).ConfigureAwait(false);
        if (movieFolder is not null && !SameMount(sourceMount, sourceIdentity, movieFolder, source))
            return DestinationPlan.Blocked(ImportReasons.CrossFilesystem,
                "The movie's folder is on a different mount from the download; nothing was copied.");
        string movieRoot;
        if (movieFolder is null)
        {
            if (sameMountRoots.Length == 0) return CrossMount();
            movieRoot = sameMountRoots[0];
            movieFolder = Path.Combine(movieRoot, ImportNaming.TitleFolder(entry.Title, entry.Year, entry.TmdbId));
        }
        else
        {
            var bound = movieFolder;
            movieRoot = roots.First(candidate => MediaStorageIdentity.Contains(candidate, bound));
        }

        var grab = await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(value => value.Id == operation.GrabId, cancellationToken)
            .ConfigureAwait(false);
        var parsed = grab is null ? null : JsonSerializer.Deserialize<ParsedRelease>(grab.ParsedJson);
        var baseLabel = ImportNaming.VersionLabel(parsed);
        var folderName = Path.GetFileName(movieFolder);
        // The label must not collide with a sibling; a later version gets a numbered label rather than an overwrite.
        for (var attempt = 1; attempt <= 9; attempt++)
        {
            var label = attempt == 1 ? baseLabel : baseLabel + " v" + attempt;
            var path = Path.Combine(movieFolder, ImportNaming.MovieFile(folderName, label, extension));
            if (files.Probe(path) == PathPresence.Absent && !SiblingUsesLabel(movieFolder, folderName, label))
                return new(path, movieRoot, label, Missing(movieFolder), null, null);
        }

        return DestinationPlan.Blocked(ImportReasons.DestinationCollision, "Every version label for this movie is already used.");

        DestinationPlan CrossMount() => DestinationPlan.Blocked(ImportReasons.CrossFilesystem,
            "No folder of the target library is on the download's mount; nothing was copied.");
    }

    private static bool SiblingUsesLabel(string folder, string folderName, string label)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var prefix = folderName + " - " + label + ".";
            return Directory.EnumerateFiles(folder).Any(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> Missing(params string[] directories) =>
        directories.Where(directory => !Directory.Exists(directory)).ToArray();

    private static string? ExistingSeasonFolder(string seriesFolder, int season)
    {
        try
        {
            if (!Directory.Exists(seriesFolder)) return null;
            return Directory.EnumerateDirectories(seriesFolder).FirstOrDefault(directory =>
            {
                var name = Path.GetFileName(directory);
                return season == 0
                    ? string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(name, "Season 0", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(name, "Season 00", StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) &&
                      int.TryParse(name[7..].Trim(), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out var number) && number == season;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> BoundMovieFolderAsync(Entry entry, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var bindings = await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entry.Id && binding.MediaPath != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var binding in bindings.OrderBy(binding => binding.JellyfinItemId == binding.VersionGroupId ? 0 : 1)
                     .ThenBy(binding => binding.JellyfinItemId))
        {
            if (!files.TryCanonicalize(binding.MediaPath!, out var media)) continue;
            var folder = Path.GetDirectoryName(media);
            // A movie file directly in a library root has no folder of its own to group versions in.
            if (folder is null || roots.Any(root => string.Equals(Path.TrimEndingDirectorySeparator(root), folder, StringComparison.Ordinal)))
                continue;
            if (roots.Any(root => MediaStorageIdentity.Contains(root, folder))) return folder;
        }

        return null;
    }

    private async Task<string?> BoundSeriesFolderAsync(Entry entry, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var bindings = await (from binding in database.EpisodeBindings.AsNoTracking()
                join tracked in database.Episodes.AsNoTracking() on binding.EpisodeId equals tracked.Id
                where tracked.EntryId == entry.Id
                select binding).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var binding in bindings.OrderBy(binding => binding.SeriesItemId))
        {
            string? folder = null;
            try
            {
                if (library.GetItemById(binding.SeriesItemId) is { Path: { Length: > 0 } seriesPath }) folder = seriesPath;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                folder = null;
            }

            if (folder is null && binding.MediaPath is { } media)
            {
                var parent = Path.GetDirectoryName(media);
                var name = parent is null ? null : Path.GetFileName(parent);
                folder = name is not null && (name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase)) ? Path.GetDirectoryName(parent) : parent;
            }

            if (folder is not null && files.TryCanonicalize(folder, out var canonical) &&
                roots.Any(root => MediaStorageIdentity.Contains(root, canonical) &&
                    !string.Equals(Path.TrimEndingDirectorySeparator(root), canonical, StringComparison.Ordinal)))
                return canonical;
        }

        return null;
    }

    private IReadOnlyList<string> LibraryRoots(Guid libraryId)
    {
        try
        {
            var folder = library.GetVirtualFolders()?.FirstOrDefault(value =>
                Guid.TryParse(value.ItemId, out var id) && id == libraryId && value.CollectionType is
                    CollectionTypeOptions.movies or CollectionTypeOptions.tvshows);
            return (folder?.Locations ?? []).Select(location => files.TryCanonicalize(location, out var canonical) ? canonical : null)
                .OfType<string>().Where(Directory.Exists).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns true when <paramref name="target"/> (or its nearest existing ancestor) is on the source's mount and
    /// device. Two bind mounts of one filesystem share a device but still refuse <c>link(2)</c>, so both are compared.
    /// </summary>
    private bool SameMount(MountTable table, string? sourceMount, string target, UnixFileSnapshot source)
    {
        var existing = target;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing) return false;
            existing = parent;
        }

        if (!files.TryGetDirectoryDevice(existing, out var canonical, out var device)) return false;
        var sourceDevice = source.PhysicalIdentity.Length >= 17 ? source.PhysicalIdentity[..17] : string.Empty;
        return sourceMount is not null && table.Capture(canonical) == sourceMount && device == sourceDevice;
    }

    private async Task BlockAsync(ImportOperation operation, string reason, string? detail)
    {
        var now = Now;
        operation.State = ImportStates.Blocked;
        operation.Reason = reason;
        operation.Error = detail is null ? null : Bound(detail);
        operation.UpdatedAt = now;
        // One history event per reason change, never one per tick (P5.I3).
        if (operation.HistoryReason != reason && operation.EntryId is { } entryId)
        {
            operation.HistoryReason = reason;
            database.History.Add(new HistoryRecord
            {
                EntryId = entryId, EventType = "import_blocked", CreatedAt = now,
                Summary = Bound("Import stopped: " + ImportMessages.For(reason)),
                Data = JsonSerializer.Serialize(new { operationId = operation.Id, operation.EpisodeId, reason })
            });
        }

        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Import {Operation} blocked: {Reason}", operation.Id, reason);
    }

    private async Task FailAsync(ImportOperation operation, string reason, string detail)
    {
        var now = Now;
        operation.State = ImportStates.Failed;
        operation.Reason = reason;
        operation.Error = Bound(detail);
        operation.UpdatedAt = now;
        operation.CompletedAt = now;
        operation.OpenGrabKey = null;
        if (operation.EntryId is { } entryId && await database.Entries.AnyAsync(entry => entry.Id == entryId).ConfigureAwait(false))
            database.History.Add(new HistoryRecord
            {
                EntryId = entryId, EventType = "import_failed", CreatedAt = now, Summary = Bound("Import failed: " + ImportMessages.For(reason)),
                Data = JsonSerializer.Serialize(new { operationId = operation.Id, operation.EpisodeId, reason })
            });
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];
}

/// <summary>User-facing sentences for stable import reasons; the web keeps the same table in its constants module.</summary>
public static class ImportMessages
{
    /// <summary>Returns the sentence for a reason code.</summary>
    public static string For(string? reason) => reason switch
    {
        ImportReasons.PathUnmapped => "the download's path is not mapped to a folder Jellyfin can open.",
        ImportReasons.SourceMissing => "the downloaded file could not be found.",
        ImportReasons.SourceSizeMismatch => "the downloaded file's size does not match the torrent.",
        ImportReasons.NoVideoFile => "the torrent contains no video file.",
        ImportReasons.AmbiguousFiles => "the torrent contains several files that could be the title.",
        ImportReasons.ArchiveUnsupported => "the release is packed in an archive, which is never extracted.",
        ImportReasons.EpisodeMismatch => "the file is a different episode from the one grabbed.",
        ImportReasons.TargetExists => "this episode already has a file.",
        ImportReasons.CrossFilesystem => "the download and the library are on different mounts; nothing was copied.",
        ImportReasons.DestinationNotWritable => "the library folder cannot be written.",
        ImportReasons.DestinationCollision => "another file already has the destination name.",
        ImportReasons.LibraryRootMissing => "the library has no usable folder.",
        ImportReasons.ScanTimeout => "Jellyfin did not scan the new file in time.",
        ImportReasons.BindingNotObserved => "Jellyfin did not add the new file to the library.",
        ImportReasons.ClientUnreachable => "the download client cannot be reached.",
        ImportReasons.TorrentMissing => "the download client no longer has this torrent.",
        ImportReasons.ImportDisabled => "importing is turned off.",
        ImportReasons.Cancelled => "it was removed from the queue.",
        ImportReasons.TargetMissing => "the title was removed from the catalog.",
        ImportReasons.ClientMissing => "the download client is no longer configured.",
        _ => "an unexpected problem occurred."
    };
}
