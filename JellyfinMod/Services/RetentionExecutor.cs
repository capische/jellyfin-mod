using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Reclaims one verified media representation and recovers interrupted operations.</summary>
public sealed class RetentionExecutor(
    ModDbContext database,
    RetentionPreviewService preview,
    RetentionExecutionGate executionGate,
    ReconciliationLibraryLock libraryLock,
    ILibraryManager library,
    MediaStorageIdentity storage,
    UnixFileInspector files,
    TimeProvider clock,
    ILogger<RetentionExecutor> logger)
{
    /// <summary>Revalidates and reclaims one binding selected by the shared retention preview.</summary>
    public async Task<RetentionExecutionResult> ReclaimAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var initial = Find(await preview.PreviewAsync(cancellationToken).ConfigureAwait(false), bindingId);
        if (initial is null)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.BindingUnavailable);
        if (initial.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId, initial.Reason);

        await using var libraryLease = await libraryLock.AcquireAsync(initial.TargetLibraryId, cancellationToken)
            .ConfigureAwait(false);
        var existing = await database.RetentionOperations
            .Where(operation => operation.BindingId == bindingId &&
                (operation.State == RetentionOperationStates.Prepared ||
                 operation.State == RetentionOperationStates.Unlinked))
            .OrderByDescending(operation => operation.PreparedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null) return await RecoverUnderLeaseAsync(existing, cancellationToken).ConfigureAwait(false);

        var candidate = Find(await preview.PreviewAsync(cancellationToken).ConfigureAwait(false), bindingId);
        if (candidate is null || candidate.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId,
                candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable);
        if (!TryInspect(candidate, out var observed, out var inspectionReason))
            return RetentionExecutionResult.NotStarted(bindingId, inspectionReason);

        var evidence = await LoadEvidenceAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!evidence.Valid)
            return RetentionExecutionResult.NotStarted(bindingId, evidence.Reason);
        var storageIdentity = await LoadStorageIdentityAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storageIdentity))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionPreviewReasons.StorageUnavailable);

        var operation = new RetentionOperation
        {
            BindingId = candidate.BindingId,
            EntryId = candidate.EntryId,
            EpisodeId = candidate.EpisodeId,
            JellyfinItemId = candidate.JellyfinItemId,
            TargetLibraryId = candidate.TargetLibraryId,
            PolicyVersion = evidence.PolicyVersion,
            MediaPath = observed.CanonicalPath,
            StorageIdentity = storageIdentity,
            PhysicalIdentity = observed.PhysicalIdentity,
            LogicalBytes = checked((long)observed.LogicalBytes),
            HardlinkCountBefore = observed.HardlinkCount,
            Reason = RetentionPreviewReasons.Eligible,
            PreparedAt = clock.GetUtcNow().UtcDateTime
        };
        database.RetentionOperations.Add(operation);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ExecutePreparedUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inspects and resolves operations left prepared or unlinked by an interrupted process.</summary>
    public async Task<IReadOnlyList<RetentionExecutionResult>> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var operationIds = await database.RetentionOperations.AsNoTracking()
            .Where(operation => operation.State == RetentionOperationStates.Prepared ||
                operation.State == RetentionOperationStates.Unlinked)
            .OrderBy(operation => operation.PreparedAt).Select(operation => operation.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RetentionExecutionResult>(operationIds.Length);
        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = await database.RetentionOperations.SingleAsync(
                candidate => candidate.Id == operationId, cancellationToken).ConfigureAwait(false);
            await using var libraryLease = await libraryLock.AcquireAsync(operation.TargetLibraryId, cancellationToken)
                .ConfigureAwait(false);
            results.Add(await RecoverUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<RetentionExecutionResult> RecoverUnderLeaseAsync(
        RetentionOperation operation,
        CancellationToken cancellationToken)
    {
        if (!IsStorageCurrent(operation))
            return await FinishAsync(operation, RetentionOperationStates.Failed,
                RetentionPreviewReasons.StorageUnavailable, null, cancellationToken).ConfigureAwait(false);

        if (operation.State == RetentionOperationStates.Unlinked)
        {
            if (File.Exists(operation.MediaPath))
                return await FinishAsync(operation, RetentionOperationStates.Failed,
                    RetentionExecutionReasons.MediaReappeared, null, cancellationToken).ConfigureAwait(false);
            return await CompleteUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(operation.MediaPath))
        {
            operation.State = RetentionOperationStates.Unlinked;
            operation.UnlinkedAt ??= clock.GetUtcNow().UtcDateTime;
            operation.PhysicalBytesReleased = operation.HardlinkCountBefore > 1 ? 0 : null;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            TryRemoveNative(operation);
            return await CompleteUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        if (!files.TryInspect(operation.MediaPath, out var observed) ||
            !SameFile(operation, observed))
            return await FinishAsync(operation, RetentionOperationStates.Failed,
                RetentionPreviewReasons.MediaIdentityChanged, null, cancellationToken).ConfigureAwait(false);
        return await ExecutePreparedUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionExecutionResult> ExecutePreparedUnderLeaseAsync(
        RetentionOperation operation,
        CancellationToken cancellationToken)
    {
        var candidate = Find(await preview.PreviewAsync(cancellationToken).ConfigureAwait(false), operation.BindingId);
        if (candidate is null || candidate.State != RetentionPreviewStates.Due)
            return await FinishAsync(operation, RetentionOperationStates.Blocked,
                candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable, null, cancellationToken)
                .ConfigureAwait(false);
        var evidence = await LoadEvidenceAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!evidence.Valid || evidence.PolicyVersion != operation.PolicyVersion)
            return await FinishAsync(operation, RetentionOperationStates.Blocked,
                evidence.Valid ? RetentionExecutionReasons.PolicyChanged : evidence.Reason, null, cancellationToken)
                .ConfigureAwait(false);
        if (!TryInspect(candidate, out var observed, out var inspectionReason) || !SameFile(operation, observed))
            return await FinishAsync(operation, RetentionOperationStates.Blocked,
                inspectionReason == RetentionPreviewReasons.Eligible
                    ? RetentionPreviewReasons.MediaIdentityChanged
                    : inspectionReason,
                null, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(operation.MediaPath);
            if (File.Exists(operation.MediaPath)) throw new IOException("The media path still exists after unlink.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return await FinishAsync(operation, RetentionOperationStates.Failed,
                RetentionExecutionReasons.UnlinkFailed, error, cancellationToken).ConfigureAwait(false);
        }

        operation.State = RetentionOperationStates.Unlinked;
        operation.Reason = RetentionExecutionReasons.Unlinked;
        operation.UnlinkedAt = clock.GetUtcNow().UtcDateTime;
        operation.PhysicalBytesReleased = operation.HardlinkCountBefore > 1 ? 0 : null;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        TryRemoveNative(operation);
        return await CompleteUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionExecutionResult> CompleteUnderLeaseAsync(
        RetentionOperation operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (operation.EpisodeId.HasValue)
        {
            var binding = await database.EpisodeBindings.SingleOrDefaultAsync(
                candidate => candidate.Id == operation.BindingId, cancellationToken).ConfigureAwait(false);
            if (binding is not null) database.EpisodeBindings.Remove(binding);
            var remaining = await database.EpisodeBindings.AsNoTracking()
                .Where(candidate => candidate.EpisodeId == operation.EpisodeId && candidate.Id != operation.BindingId)
                .OrderBy(candidate => candidate.JellyfinItemId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var episode = await database.Episodes.SingleAsync(
                candidate => candidate.Id == operation.EpisodeId, cancellationToken).ConfigureAwait(false);
            episode.JellyfinItemId = remaining
                .FirstOrDefault(candidate => candidate.JellyfinItemId == episode.JellyfinItemId)?.JellyfinItemId ??
                remaining.FirstOrDefault()?.JellyfinItemId;
            episode.State = remaining.Length == 0 ? FileState.Reclaimed : FileState.OnDisk;
        }
        else
        {
            var binding = await database.EntryBindings.SingleOrDefaultAsync(
                candidate => candidate.Id == operation.BindingId, cancellationToken).ConfigureAwait(false);
            if (binding is not null) database.EntryBindings.Remove(binding);
            var remaining = await database.EntryBindings.AsNoTracking()
                .Where(candidate => candidate.EntryId == operation.EntryId && candidate.Id != operation.BindingId)
                .OrderBy(candidate => candidate.JellyfinItemId == candidate.VersionGroupId ? 0 : 1)
                .ThenBy(candidate => candidate.VersionGroupId).ThenBy(candidate => candidate.JellyfinItemId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var entry = await database.Entries.SingleAsync(candidate => candidate.Id == operation.EntryId,
                cancellationToken).ConfigureAwait(false);
            entry.JellyfinItemId = remaining
                .FirstOrDefault(candidate => candidate.JellyfinItemId == entry.JellyfinItemId)?.JellyfinItemId ??
                remaining.FirstOrDefault()?.JellyfinItemId;
            entry.State = remaining.Length == 0 ? FileState.Reclaimed : FileState.OnDisk;
        }

        if (!await database.History.AnyAsync(history => history.Id == operation.Id, cancellationToken)
                .ConfigureAwait(false))
        {
            database.History.Add(new HistoryRecord
            {
                Id = operation.Id,
                EntryId = operation.EntryId,
                EventType = "reclaimed",
                Summary = operation.EpisodeId.HasValue
                    ? "Episode media reclaimed after its retention window"
                    : "Media reclaimed after its retention window",
                Data = JsonSerializer.Serialize(new
                {
                    operationId = operation.Id,
                    operation.BindingId,
                    operation.EpisodeId,
                    operation.JellyfinItemId,
                    logicalBytesUnlinked = operation.LogicalBytes,
                    physicalBytesReleased = operation.PhysicalBytesReleased
                }),
                CreatedAt = operation.UnlinkedAt ?? clock.GetUtcNow().UtcDateTime
            });
        }

        operation.State = RetentionOperationStates.Completed;
        operation.Reason = string.IsNullOrWhiteSpace(operation.Error)
            ? RetentionExecutionReasons.Reclaimed
            : RetentionExecutionReasons.ReclaimedNativeCleanupFailed;
        operation.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RetentionExecutionResult.From(operation);
    }

    private void TryRemoveNative(RetentionOperation operation)
    {
        try
        {
            var native = library.GetItemById(operation.JellyfinItemId);
            if (native is null) return;
            library.DeleteItem(native, new DeleteOptions
            {
                DeleteFileLocation = false,
                DeleteFromExternalProvider = false
            }, notifyParentItem: true);
        }
        catch (Exception error)
        {
            operation.Error = Bound(error.Message);
            logger.LogWarning(error, "Media was unlinked but native item {ItemId} could not be removed", operation.JellyfinItemId);
        }
    }

    private async Task<RetentionExecutionResult> FinishAsync(
        RetentionOperation operation,
        string state,
        string reason,
        Exception? error,
        CancellationToken cancellationToken)
    {
        operation.State = state;
        operation.Reason = reason;
        operation.Error = error is null ? operation.Error : Bound(error.Message);
        operation.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RetentionExecutionResult.From(operation);
    }

    private async Task<RetentionEvidence> LoadEvidenceAsync(
        RetentionRepresentationDto candidate,
        CancellationToken cancellationToken)
    {
        var targetId = candidate.EpisodeId ?? candidate.EntryId;
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        if (policy is null || !policy.Enabled)
            return new(false, policy?.Version ?? 0, RetentionEvaluationReasons.RetentionDisabled);
        var evaluation = await database.RetentionEvaluations.AsNoTracking().SingleOrDefaultAsync(
            item => item.TargetId == targetId, cancellationToken).ConfigureAwait(false);
        if (evaluation is null || evaluation.State != RetentionEvaluationStates.Scheduled)
            return new(false, evaluation?.PolicyVersion ?? policy.Version,
                evaluation?.Reason ?? RetentionPreviewReasons.EvaluationMissing);
        if (evaluation.PolicyVersion != policy.Version)
            return new(false, evaluation.PolicyVersion, RetentionExecutionReasons.PolicyChanged);
        return new(true, policy.Version, RetentionPreviewReasons.Eligible);
    }

    private async Task<string?> LoadStorageIdentityAsync(
        RetentionRepresentationDto candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.EpisodeId.HasValue)
            return await database.EpisodeBindings.AsNoTracking().Where(binding => binding.Id == candidate.BindingId)
                .Select(binding => binding.StorageIdentity).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return await database.EntryBindings.AsNoTracking().Where(binding => binding.Id == candidate.BindingId)
            .Select(binding => binding.StorageIdentity).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsStorageCurrent(RetentionOperation operation)
    {
        try
        {
            var roots = library.GetVirtualFolders()
                .FirstOrDefault(folder => Guid.TryParse(folder.ItemId, out var id) && id == operation.TargetLibraryId)
                ?.Locations ?? [];
            return storage.IsCurrent(operation.MediaPath, operation.StorageIdentity, roots, out _);
        }
        catch
        {
            return false;
        }
    }

    private bool TryInspect(
        RetentionRepresentationDto candidate,
        out UnixFileSnapshot observed,
        out string reason)
    {
        observed = default;
        reason = RetentionPreviewReasons.MediaPathUnavailable;
        if (string.IsNullOrWhiteSpace(candidate.CanonicalPath) ||
            !files.TryInspect(candidate.CanonicalPath, out observed)) return false;
        if (candidate.LogicalBytes != observed.LogicalBytes || candidate.HardlinkCount != observed.HardlinkCount)
        {
            reason = RetentionPreviewReasons.MediaIdentityChanged;
            return false;
        }

        reason = RetentionPreviewReasons.Eligible;
        return true;
    }

    private static bool SameFile(RetentionOperation operation, UnixFileSnapshot observed) =>
        string.Equals(operation.MediaPath, observed.CanonicalPath, StringComparison.Ordinal) &&
        string.Equals(operation.PhysicalIdentity, observed.PhysicalIdentity, StringComparison.Ordinal) &&
        operation.LogicalBytes == checked((long)observed.LogicalBytes) &&
        operation.HardlinkCountBefore == observed.HardlinkCount;

    private static RetentionRepresentationDto? Find(RetentionPreviewDto result, Guid bindingId) =>
        result.Items.SingleOrDefault(candidate => candidate.BindingId == bindingId);

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];

    private readonly record struct RetentionEvidence(bool Valid, long PolicyVersion, string Reason);
}

/// <summary>Serializes retention operations within one plugin process.</summary>
public sealed class RetentionExecutionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Acquires exclusive retention execution access.</summary>
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

/// <summary>One reclamation attempt result.</summary>
public sealed record RetentionExecutionResult(
    Guid BindingId,
    Guid? OperationId,
    string State,
    string Reason,
    long LogicalBytesUnlinked,
    long? PhysicalBytesReleased)
{
    internal static RetentionExecutionResult NotStarted(Guid bindingId, string reason) =>
        new(bindingId, null, RetentionOperationStates.Blocked, reason, 0, null);

    internal static RetentionExecutionResult From(RetentionOperation operation) =>
        new(operation.BindingId, operation.Id, operation.State, operation.Reason ?? string.Empty,
            operation.UnlinkedAt.HasValue ? operation.LogicalBytes : 0, operation.PhysicalBytesReleased);
}

internal static class RetentionExecutionReasons
{
    public const string BindingUnavailable = "binding_unavailable";
    public const string PolicyChanged = "policy_changed";
    public const string MediaReappeared = "media_reappeared";
    public const string UnlinkFailed = "unlink_failed";
    public const string Unlinked = "unlinked";
    public const string Reclaimed = "reclaimed";
    public const string ReclaimedNativeCleanupFailed = "reclaimed_native_cleanup_failed";
}
