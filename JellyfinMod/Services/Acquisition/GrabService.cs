using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>Stable grab intents (P6.M6).</summary>
public static class GrabIntents
{
    /// <summary>Acquire a title that has no file, or replace one through an upgrade.</summary>
    public const string Acquire = "acquire";

    /// <summary>Add another version beside a playable one.</summary>
    public const string AddVersion = "addVersion";
}

/// <summary>A stable grab failure mapped to an HTTP status by the API.</summary>
public sealed class GrabException(int status, string code, string message, Guid? operationId = null) : Exception(message)
{
    /// <summary>Gets the HTTP status.</summary>
    public int Status { get; } = status;

    /// <summary>Gets the stable code.</summary>
    public string Code { get; } = code;

    /// <summary>Gets the operation that already owns the target or hash, when one does.</summary>
    public Guid? OperationId { get; } = operationId;
}

/// <summary>
/// Torrent metadata fetched for a held grab. Kept in memory only: it can embed a tracker passkey, and a restart
/// during the hold fails the operation instead of persisting a credential (user decisions 2 and 3).
/// </summary>
public sealed class GrabPayloadVault
{
    private readonly ConcurrentDictionary<Guid, TorrentLocator> _payloads = new();

    /// <summary>Stores a payload.</summary>
    public void Put(Guid operationId, TorrentLocator locator) => _payloads[operationId] = locator;

    /// <summary>Returns a payload without removing it.</summary>
    public TorrentLocator? Get(Guid operationId) => _payloads.GetValueOrDefault(operationId);

    /// <summary>Discards a payload.</summary>
    public void Remove(Guid operationId) => _payloads.TryRemove(operationId, out _);
}

/// <summary>Serializes transitions of one operation, so Cancel and the hold's end can never both win.</summary>
public sealed class GrabLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Acquires exclusive access to one operation.</summary>
    public async Task<IDisposable> AcquireAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(operationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>The request after API validation.</summary>
/// <param name="SearchId">The search the release came from.</param>
/// <param name="ReleaseId">The release within that search.</param>
/// <param name="IdempotencyKey">The caller's idempotency key.</param>
/// <param name="Automatic">Whether automation makes the grab (P6.M3).</param>
/// <param name="UpgradeOperationId">The upgrade the grab belongs to (P6.M5).</param>
public sealed record GrabRequest(Guid SearchId, string ReleaseId, string IdempotencyKey, bool Automatic = false,
    Guid? UpgradeOperationId = null);

/// <summary>
/// The client-agnostic acquisition engine (P4.A5): persist intent, hold, submit once through the configured
/// driver, verify by identity lookup, and recover uncertain outcomes without blind resubmission.
/// </summary>
public sealed class GrabService(
    ModDbContext database,
    AcquisitionConfiguration configuration,
    DownloadClientDrivers drivers,
    ReleaseSearchCache searches,
    TorznabClient torznab,
    DownloadDestinationValidator destinations,
    GrabPayloadVault vault,
    GrabLocks locks,
    GrabHoldOptions hold,
    ReconciliationLibraryLock libraryLock,
    TimeProvider time,
    ILogger<GrabService> logger,
    Import.ImportMonitor? importMonitor = null)
{
    /// <summary>How long after submission an absent torrent still counts as possibly in flight.</summary>
    public static readonly TimeSpan AbsentGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(30);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Creates, or idempotently returns, a held grab. Access to the target is checked by the caller.</summary>
    /// <returns>The operation and whether this call created it.</returns>
    public async Task<(GrabOperation Operation, bool Created)> CreateAsync(Guid userId, GrabRequest request,
        Func<Entry, Episode?, bool> canAccess, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.SearchId.ToString("N") + "\n" + request.ReleaseId)));
        if (await FindByKeyAsync(userId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } replay)
            return (Replay(replay, fingerprint), false);

        var (snapshot, expired) = searches.Find(request.SearchId, userId);
        if (snapshot is null) throw new GrabException(404, "search_not_found", "This search is not available. Search again.");
        if (expired) throw new GrabException(410, "search_expired", "This search has expired. Search again.");
        var candidate = snapshot.Candidates.SingleOrDefault(value => value.ReleaseId == request.ReleaseId)
            ?? throw new GrabException(404, "release_not_found", "This release is not part of the search.");
        if (!candidate.Evaluation.Eligible)
            throw new GrabException(409, "release_rejected", "Rejected releases cannot be grabbed.");
        var addVersion = snapshot.Intent == GrabIntents.AddVersion;
        // Another quality must be another quality: a held one is refused (P6.M6).
        if (addVersion && candidate.Parsed.Quality is { } quality && snapshot.HeldQualities.Contains(quality))
            throw new GrabException(409, "held_quality", "This quality is already in the library.");

        var target = snapshot.Target;
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == target.EntryId, cancellationToken)
            .ConfigureAwait(false);
        var episode = target.EpisodeId is { } episodeId
            ? await database.Episodes.AsNoTracking().SingleOrDefaultAsync(value => value.Id == episodeId && value.EntryId == target.EntryId,
                cancellationToken).ConfigureAwait(false)
            : null;
        // Access is rechecked at commit, even though the search was authorized earlier (P4.A1).
        if (entry is null || target.EpisodeId.HasValue && episode is null || !canAccess(entry, episode))
            throw new GrabException(404, "target_not_found", "The title is not available.");

        var state = await configuration.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        if (!state.Settings.Enabled) throw new GrabException(409, "acquisition_disabled", "Grabbing is turned off in the plugin settings.");
        if (!state.Ready)
            throw new GrabException(409, "acquisition_not_ready", "Acquisition is not ready: " + string.Join(", ", state.Blockers) + ".");
        await EnsureCurrentAsync(snapshot, candidate, state, cancellationToken).ConfigureAwait(false);
        var client = state.Client!;
        if (entry.TargetLibraryId is not { } libraryId || !destinations.Supports(client.LocalDirectory, libraryId))
            throw new GrabException(409, "destination_not_same_filesystem",
                "The download folder does not share a filesystem with this title's library.");

        TorrentLocator locator;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SubmitTimeout);
            try
            {
                locator = await torznab.ResolveAsync(candidate.DownloadUrl, candidate.MagnetUrl, candidate.AllowedHosts, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (TorznabException error)
            {
                throw new GrabException(502, error.Code, error.Message);
            }
            catch (HttpRequestException)
            {
                throw new GrabException(502, "download_failed", "The torrent could not be downloaded from the indexer.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GrabException(504, "download_timeout", "The indexer did not deliver the torrent in time.");
            }
        }

        if (candidate.InfoHash is { } advertised && advertised != locator.InfoHash)
            throw new GrabException(409, "hash_mismatch", "The torrent does not match the infohash the indexer advertised.");

        var now = Now;
        var operation = new GrabOperation
        {
            RequestedBy = userId, IdempotencyKey = request.IdempotencyKey, RequestFingerprint = fingerprint,
            EntryId = entry.Id, EpisodeId = episode?.Id,
            // An added version relaxes the one-active-grab rule for its own snapshot only: it owns a separate key.
            ActiveTarget = TargetKey(entry.Id, episode?.Id) + (addVersion ? "+add" : string.Empty),
            Intent = snapshot.Intent, Automatic = request.Automatic, UpgradeOperationId = request.UpgradeOperationId,
            ActiveHash = HashKey(client.Id, locator.InfoHash), SearchId = snapshot.SearchId, ReleaseId = candidate.ReleaseId,
            IndexerId = candidate.IndexerId, IndexerName = candidate.IndexerName, SourceGuid = candidate.SourceGuid,
            RawTitle = candidate.RawTitle, ParsedJson = JsonSerializer.Serialize(candidate.Parsed), Size = candidate.Size ?? locator.Size,
            ProfileId = snapshot.Profile.Id, ProfileRevision = snapshot.Profile.Revision, ScoringVersion = ReleaseEvaluator.Version,
            Score = candidate.Evaluation.Score, DownloadClientId = client.Id, DownloadClientRevision = client.Revision,
            InfoHash = locator.InfoHash, Label = client.Label, DownloadDirectory = client.DownloadDirectory,
            SeedRatio = candidate.SeedRatio, SeedMinutes = candidate.SeedMinutes, State = GrabStates.Pending,
            CreatedAt = now, UpdatedAt = now, HoldUntil = now + hold.Hold
        };
        // The library lease makes the insert atomic with entry removal, which checks for active grabs under it.
        await using (await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false))
        {
            if (!await database.Entries.AnyAsync(value => value.Id == entry.Id, cancellationToken).ConfigureAwait(false))
                throw new GrabException(404, "target_not_found", "The title is not available.");
            database.GrabOperations.Add(operation);
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                database.ChangeTracker.Clear();
                if (await FindByKeyAsync(userId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } concurrent)
                    return (Replay(concurrent, fingerprint), false);
                var owner = await database.GrabOperations.AsNoTracking()
                    .FirstOrDefaultAsync(value => value.ActiveTarget == operation.ActiveTarget, cancellationToken).ConfigureAwait(false);
                if (owner is not null)
                    throw new GrabException(409, "grab_active", "Another grab for this title is still active.", owner.Id);
                owner = await database.GrabOperations.AsNoTracking()
                    .FirstOrDefaultAsync(value => value.ActiveHash == operation.ActiveHash, cancellationToken).ConfigureAwait(false);
                throw new GrabException(409, "duplicate_hash", "This torrent is already owned by another grab.", owner?.Id);
            }
        }

        vault.Put(operation.Id, locator);
        logger.LogInformation("Held grab {Operation} for {Entry} until {HoldUntil}", operation.Id, entry.Id, operation.HoldUntil);
        return (operation, true);
    }

    /// <summary>Cancels a held grab; repeating it returns the same cancelled operation without another event.</summary>
    public async Task<GrabOperation> CancelAsync(Guid operationId, Guid userId, CancellationToken cancellationToken)
    {
        using var lease = await locks.AcquireAsync(operationId, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        var operation = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken)
            .ConfigureAwait(false) ?? throw new GrabException(404, "grab_not_found", "The grab does not exist.");
        if (operation.State == GrabStates.Cancelled) return operation;
        if (operation.State != GrabStates.Pending)
            throw new GrabException(409, "grab_not_cancellable", "The grab was already sent to the download client.", operation.Id);
        operation.State = GrabStates.Cancelled;
        operation.CancelledAt = operation.UpdatedAt = Now;
        operation.CancelledBy = userId;
        Release(operation);
        AddHistory(operation, "grab_cancelled", "Cancelled grab of " + Describe(operation) + " before it was sent");
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        vault.Remove(operation.Id);
        return operation;
    }

    /// <summary>Submits a grab whose hold has ended. Anything but a held operation is left alone.</summary>
    public async Task DispatchAsync(Guid operationId, CancellationToken cancellationToken)
    {
        using var lease = await locks.AcquireAsync(operationId, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        var operation = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation is null || operation.State != GrabStates.Pending) return;
        if (vault.Get(operationId) is not { } locator)
        {
            await FailAsync(operation, "interrupted_before_submit", false).ConfigureAwait(false);
            return;
        }

        // Nothing below trusts the request: configuration, destination and target are validated again at submission.
        var state = await configuration.GetStateAsync(database, cancellationToken).ConfigureAwait(false);
        var entry = operation.EntryId is { } entryId
            ? await database.Entries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == entryId, cancellationToken).ConfigureAwait(false)
            : null;
        var failure = !state.Settings.Enabled ? "acquisition_disabled"
            : !state.Ready ? "acquisition_not_ready"
            : state.Client!.Id != operation.DownloadClientId || state.Client.Revision != operation.DownloadClientRevision ? "configuration_changed"
            : entry?.TargetLibraryId is not { } libraryId ? "target_not_found"
            : !destinations.Supports(state.Client.LocalDirectory, libraryId) ? "destination_not_same_filesystem"
            : null;
        if (failure is not null)
        {
            await FailAsync(operation, failure, false).ConfigureAwait(false);
            return;
        }

        var driver = drivers.Get(state.Client!.Kind)!;
        DownloadClientConnection connection;
        try
        {
            connection = await configuration.ConnectAsync(state.Client, cancellationToken).ConfigureAwait(false);
            // A matching torrent that exists before this operation sends anything belongs to someone else.
            if (await driver.FindAsync(connection, operation.InfoHash!, cancellationToken).ConfigureAwait(false) is not null)
            {
                await FailAsync(operation, "client_torrent_exists", false).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or TaskCanceledException)
        {
            await FailAsync(operation, error switch
            {
                DownloadClientRejectedException rejected => rejected.Code,
                DownloadClientUnavailableException unavailable => unavailable.Code,
                _ => "client_unreachable"
            }, false).ConfigureAwait(false);
            return;
        }

        operation.State = GrabStates.Submitting;
        operation.SubmittedAt = operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        vault.Remove(operationId);

        var submission = new DownloadSubmission(operation.Id, operation.InfoHash!, locator.Metainfo, locator.MagnetUri,
            [operation.Label, OwnerLabel(operation.Id)], operation.DownloadDirectory);
        SubmissionResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SubmitTimeout);
            try
            {
                result = await driver.SubmitAsync(connection, submission, timeout.Token).ConfigureAwait(false);
            }
            catch (DownloadClientRejectedException rejected)
            {
                await FailAsync(operation, rejected.Code, true).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The request may have reached the client: never resubmit, resolve by identity lookup instead.
                logger.LogWarning("Grab {Operation} submission outcome is unknown: {Error}", operation.Id, error.GetType().Name);
                await MarkUnknownAsync(operation, "client_unconfirmed").ConfigureAwait(false);
                return;
            }
        }

        if (result == SubmissionResult.Duplicate)
        {
            // An unrelated torrent appeared between the check and the add; it is never claimed.
            await FailAsync(operation, "client_torrent_exists", true).ConfigureAwait(false);
            return;
        }

        await VerifyAsync(operation, driver, connection, allowSettingsRepair: true, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an operation by client identity lookup: after a restart, a lost response or an administrator's
    /// explicit recheck. Held operations whose payload was lost fail without anything having been sent.
    /// </summary>
    public async Task<GrabOperation?> RecheckAsync(Guid operationId, bool startup, CancellationToken cancellationToken)
    {
        using var lease = await locks.AcquireAsync(operationId, cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
        var operation = await database.GrabOperations.SingleOrDefaultAsync(value => value.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation is null) return null;
        if (operation.State == GrabStates.Pending)
        {
            if (vault.Get(operation.Id) is null) await FailAsync(operation, "interrupted_before_submit", false).ConfigureAwait(false);
            return operation;
        }

        if (operation.State is not (GrabStates.Submitting or GrabStates.Unknown or GrabStates.Accepted) || operation.ActiveHash is null)
            return operation;
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == operation.DownloadClientId, cancellationToken).ConfigureAwait(false);
        if (client is null || drivers.Get(client.Kind) is not { } driver) return operation;
        DownloadClientConnection connection;
        try
        {
            connection = await configuration.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadClientRejectedException)
        {
            return operation;
        }

        if (operation.State == GrabStates.Accepted)
        {
            try
            {
                if (await driver.FindAsync(connection, operation.InfoHash!, cancellationToken).ConfigureAwait(false) is null)
                {
                    // The administrator removed it in the client; the history stays, the target is free again.
                    Release(operation);
                    operation.UpdatedAt = Now;
                    await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
                HttpRequestException or TaskCanceledException)
            {
                // Unknown client state keeps the ownership.
            }

            return operation;
        }

        if (operation.State == GrabStates.Submitting && !startup && operation.SubmittedAt > Now - AbsentGrace)
            return operation;
        await VerifyAsync(operation, driver, connection, allowSettingsRepair: true, cancellationToken,
            absentIsFailure: startup || operation.SubmittedAt <= Now - AbsentGrace).ConfigureAwait(false);
        return operation;
    }

    /// <summary>Lists operations that still need resolution.</summary>
    public Task<List<Guid>> UnresolvedAsync(CancellationToken cancellationToken) => database.GrabOperations.AsNoTracking()
        .Where(value => value.State == GrabStates.Pending || value.State == GrabStates.Submitting || value.State == GrabStates.Unknown)
        .Select(value => value.Id).ToListAsync(cancellationToken);

    private async Task VerifyAsync(GrabOperation operation, IDownloadClientDriver driver, DownloadClientConnection connection,
        bool allowSettingsRepair, CancellationToken cancellationToken, bool absentIsFailure = false)
    {
        DownloadClientTorrent? torrent;
        try
        {
            torrent = await driver.FindAsync(connection, operation.InfoHash!, cancellationToken).ConfigureAwait(false);
            if (torrent is not null && IsOwned(operation, torrent) && !torrent.SeedLimitsUnlimited && allowSettingsRepair)
            {
                // Only a torrent carrying this operation's own label is ever changed.
                await driver.ApplySeedSettingsAsync(connection, operation.InfoHash!, cancellationToken).ConfigureAwait(false);
                torrent = await driver.FindAsync(connection, operation.InfoHash!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is DownloadClientRejectedException or DownloadClientUnavailableException or
            HttpRequestException or TaskCanceledException)
        {
            await MarkUnknownAsync(operation, "client_unconfirmed").ConfigureAwait(false);
            return;
        }

        if (torrent is null)
        {
            if (absentIsFailure) await FailAsync(operation, "client_absent", true).ConfigureAwait(false);
            else await MarkUnknownAsync(operation, "client_unconfirmed").ConfigureAwait(false);
            return;
        }

        if (!IsOwned(operation, torrent))
        {
            await FailAsync(operation, "client_torrent_exists", true).ConfigureAwait(false);
            return;
        }

        if (!torrent.Labels.Contains(operation.Label, StringComparer.Ordinal) || !SameDirectory(torrent.DownloadDirectory, operation.DownloadDirectory) ||
            !torrent.SeedLimitsUnlimited)
        {
            await MarkUnknownAsync(operation, "client_settings_mismatch").ConfigureAwait(false);
            return;
        }

        operation.State = GrabStates.Accepted;
        operation.FailureCode = null;
        operation.AcceptedAt = operation.UpdatedAt = Now;
        AddHistory(operation, operation.Automatic ? "auto_grabbed" : "grabbed",
            (operation.Automatic ? "Automatically grabbed " : "Grabbed ") + Describe(operation) + " from " + operation.IndexerName);
        // Phase 5 owns the download from acceptance on; its import operation is created in the same commit (P5.I3).
        if (!await database.ImportOperations.AnyAsync(value => value.GrabId == operation.Id, CancellationToken.None).ConfigureAwait(false))
            await Import.ImportService.CreateForGrabAsync(database, operation, Now, CancellationToken.None).ConfigureAwait(false);
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Grab {Operation} accepted by the download client", operation.Id);
        importMonitor?.Wake();
    }

    private async Task FailAsync(GrabOperation operation, string code, bool attempted)
    {
        operation.State = GrabStates.Failed;
        operation.FailureCode = code;
        operation.UpdatedAt = Now;
        Release(operation);
        // A held grab that never reached the client is not an acquisition attempt worth a history line.
        if (attempted) AddHistory(operation, "grab_failed", "Could not hand " + Describe(operation) + " to the download client");
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        vault.Remove(operation.Id);
        logger.LogInformation("Grab {Operation} failed: {Code}", operation.Id, code);
    }

    private async Task MarkUnknownAsync(GrabOperation operation, string code)
    {
        operation.State = GrabStates.Unknown;
        operation.FailureCode = code;
        operation.UpdatedAt = Now;
        await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void AddHistory(GrabOperation operation, string eventType, string summary)
    {
        if (operation.EntryId is not { } entryId) return;
        database.History.Add(new HistoryRecord
        {
            EntryId = entryId, EventType = eventType, Summary = summary.Length > 1024 ? summary[..1024] : summary,
            CreatedAt = Now,
            Data = JsonSerializer.Serialize(new
            {
                operationId = operation.Id, episodeId = operation.EpisodeId, infoHash = operation.InfoHash,
                indexer = operation.IndexerName, state = operation.State, failureCode = operation.FailureCode
            })
        });
    }

    private async Task<GrabOperation?> FindByKeyAsync(Guid userId, string key, CancellationToken cancellationToken) =>
        await database.GrabOperations.AsNoTracking().SingleOrDefaultAsync(
            value => value.RequestedBy == userId && value.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);

    private static GrabOperation Replay(GrabOperation existing, string fingerprint) => existing.RequestFingerprint == fingerprint
        ? existing
        : throw new GrabException(409, "idempotency_conflict", "This idempotency key was used for a different release.", existing.Id);

    private async Task EnsureCurrentAsync(ReleaseSearchSnapshot snapshot, ReleaseCandidate candidate, AcquisitionState state,
        CancellationToken cancellationToken)
    {
        var profile = await database.AcquisitionQualityProfiles.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == snapshot.Profile.Id, cancellationToken).ConfigureAwait(false);
        var indexer = await database.AcquisitionIndexers.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == candidate.IndexerId, cancellationToken).ConfigureAwait(false);
        if (state.Settings.Revision != snapshot.SettingsRevision || profile?.Revision != snapshot.Profile.Revision ||
            indexer is not { Enabled: true } || indexer.Revision != candidate.IndexerRevision)
            throw new GrabException(409, "search_stale", "Settings changed after this search. Search again.");
    }

    private static bool IsOwned(GrabOperation operation, DownloadClientTorrent torrent) =>
        torrent.Labels.Contains(OwnerLabel(operation.Id), StringComparer.Ordinal);

    private static bool SameDirectory(string? observed, string expected) =>
        observed is not null && observed.TrimEnd('/') == expected.TrimEnd('/');

    private static void Release(GrabOperation operation)
    {
        operation.ActiveTarget = null;
        operation.ActiveHash = null;
    }

    private static string Describe(GrabOperation operation)
    {
        var parsed = JsonSerializer.Deserialize<ParsedRelease>(operation.ParsedJson);
        var quality = parsed?.Quality ?? "unknown quality";
        var episode = parsed is { SeasonNumber: { } season, EpisodeNumbers: [var number] } ? $"S{season:00}E{number:00} " : string.Empty;
        return episode + quality;
    }

    /// <summary>The per-operation client label that proves ownership during recovery.</summary>
    public static string OwnerLabel(Guid operationId) => "jfmod-" + operationId.ToString("N");

    /// <summary>The durable target ownership key.</summary>
    public static string TargetKey(Guid entryId, Guid? episodeId) => (episodeId ?? entryId).ToString("N");

    /// <summary>The system identity automatic grabs are made under; it is never a Jellyfin user.</summary>
    public static readonly Guid AutomationUserId = Guid.Parse("00000000-0000-4000-8000-00000000a07a");

    private static string HashKey(Guid clientId, string infoHash) => clientId.ToString("N") + ":" + infoHash;
}
