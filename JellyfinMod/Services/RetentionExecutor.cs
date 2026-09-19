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
    RetentionPolicyService policyService,
    RetentionConfigurationSource configuration,
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
        await policyService.SyncAsync(configuration.Current, cancellationToken).ConfigureAwait(false);
        var initialPreview = await preview.PreviewAsync(cancellationToken).ConfigureAwait(false);
        var initial = Find(initialPreview, bindingId);
        if (initial is null)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.BindingUnavailable);
        if (initial.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId, initial.Reason);

        var initialGroup = SamePathGroup(initialPreview, initial);
        var lockedLibraryIds = initialGroup.Select(candidate => candidate.TargetLibraryId).ToHashSet();
        await using var libraryLease = await AcquireLibrariesAsync(
            lockedLibraryIds, cancellationToken).ConfigureAwait(false);
        var existing = await database.RetentionOperations
            .Where(operation => operation.BindingId == bindingId &&
                (operation.State == RetentionOperationStates.Prepared ||
                 operation.State == RetentionOperationStates.Unlinked))
            .OrderByDescending(operation => operation.PreparedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var interrupted = await LoadOpenActionAsync(existing.ActionId, cancellationToken).ConfigureAwait(false);
            return await RecoverUnderLeaseAsync(interrupted, bindingId, cancellationToken).ConfigureAwait(false);
        }

        var currentPreview = await preview.PreviewAsync(cancellationToken).ConfigureAwait(false);
        var candidate = Find(currentPreview, bindingId);
        if (candidate is null || candidate.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId,
                candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable);
        var candidates = SamePathGroup(currentPreview, candidate);
        if (candidates.Any(item => item.State != RetentionPreviewStates.Due))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionPreviewReasons.SharedPathNotAllEligible);
        if (candidates.Any(item => !lockedLibraryIds.Contains(item.TargetLibraryId)))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.BindingSetChanged);

        var operations = new List<RetentionOperation>(candidates.Length);
        var actionId = Guid.NewGuid();
        var preparedAt = clock.GetUtcNow().UtcDateTime;
        foreach (var item in candidates)
        {
            if (!TryInspect(item, out var observed, out var inspectionReason))
                return RetentionExecutionResult.NotStarted(bindingId, inspectionReason);
            var evidence = await LoadEvidenceAsync(item, cancellationToken).ConfigureAwait(false);
            if (!evidence.Valid) return RetentionExecutionResult.NotStarted(bindingId, evidence.Reason);
            var storageIdentity = await LoadStorageIdentityAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(storageIdentity))
                return RetentionExecutionResult.NotStarted(bindingId, RetentionPreviewReasons.StorageUnavailable);
            operations.Add(new RetentionOperation
            {
                ActionId = actionId,
                BindingId = item.BindingId,
                EntryId = item.EntryId,
                EpisodeId = item.EpisodeId,
                JellyfinItemId = item.JellyfinItemId,
                TargetLibraryId = item.TargetLibraryId,
                PolicyVersion = evidence.PolicyVersion,
                MediaPath = observed.CanonicalPath,
                StorageIdentity = storageIdentity,
                PhysicalIdentity = observed.PhysicalIdentity,
                LogicalBytes = checked((long)observed.LogicalBytes),
                HardlinkCountBefore = observed.HardlinkCount,
                Reason = RetentionPreviewReasons.Eligible,
                PreparedAt = preparedAt
            });
        }

        database.RetentionOperations.AddRange(operations);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ExecutePreparedUnderLeaseAsync(operations, bindingId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inspects and resolves operations left prepared or unlinked by an interrupted process.</summary>
    public async Task<IReadOnlyList<RetentionExecutionResult>> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var actionIds = await database.RetentionOperations.AsNoTracking()
            .Where(operation => operation.State == RetentionOperationStates.Prepared ||
                operation.State == RetentionOperationStates.Unlinked)
            .OrderBy(operation => operation.PreparedAt).Select(operation => operation.ActionId).Distinct()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RetentionExecutionResult>();
        foreach (var actionId in actionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operations = await LoadOpenActionAsync(actionId, cancellationToken).ConfigureAwait(false);
            await using var libraryLease = await AcquireLibrariesAsync(
                operations.Select(operation => operation.TargetLibraryId), cancellationToken).ConfigureAwait(false);
            await RecoverUnderLeaseAsync(operations, null, cancellationToken).ConfigureAwait(false);
            results.AddRange(operations.Select(RetentionExecutionResult.From));
        }

        return results;
    }

    private async Task<RetentionExecutionResult> RecoverUnderLeaseAsync(
        IReadOnlyList<RetentionOperation> operations,
        Guid? requestedBindingId,
        CancellationToken cancellationToken)
    {
        var selected = operations.SingleOrDefault(operation => operation.BindingId == requestedBindingId) ??
            operations[0];
        if (operations.Any(operation => !IsStorageCurrent(operation)))
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                RetentionPreviewReasons.StorageUnavailable, null, cancellationToken).ConfigureAwait(false);
        if (operations.Select(operation => operation.MediaPath).Distinct(StringComparer.Ordinal).Count() != 1)
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                RetentionExecutionReasons.ActionPathMismatch, null, cancellationToken).ConfigureAwait(false);

        if (operations.Any(operation => operation.State == RetentionOperationStates.Unlinked))
        {
            if (File.Exists(selected.MediaPath))
                return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                    RetentionExecutionReasons.MediaReappeared, null, cancellationToken).ConfigureAwait(false);
            return await CompleteUnderLeaseAsync(operations, selected.BindingId, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(selected.MediaPath))
        {
            foreach (var operation in operations)
            {
                operation.State = RetentionOperationStates.Unlinked;
                operation.UnlinkedAt ??= clock.GetUtcNow().UtcDateTime;
                operation.PhysicalBytesReleased = operation.HardlinkCountBefore > 1 ? 0 : null;
            }
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var operation in operations) TryRemoveNative(operation);
            return await CompleteUnderLeaseAsync(operations, selected.BindingId, cancellationToken).ConfigureAwait(false);
        }

        if (!files.TryInspect(selected.MediaPath, out var observed) ||
            operations.Any(operation => !SameFile(operation, observed)))
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                RetentionPreviewReasons.MediaIdentityChanged, null, cancellationToken).ConfigureAwait(false);
        return await ExecutePreparedUnderLeaseAsync(operations, selected.BindingId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionExecutionResult> ExecutePreparedUnderLeaseAsync(
        IReadOnlyList<RetentionOperation> operations,
        Guid requestedBindingId,
        CancellationToken cancellationToken)
    {
        var currentPreview = await preview.PreviewAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in operations)
        {
            var candidate = Find(currentPreview, operation.BindingId);
            if (candidate is null || candidate.State != RetentionPreviewStates.Due)
                return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                    candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable, null, cancellationToken)
                    .ConfigureAwait(false);
            var evidence = await LoadEvidenceAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!evidence.Valid || evidence.PolicyVersion != operation.PolicyVersion)
                return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                    evidence.Valid ? RetentionExecutionReasons.PolicyChanged : evidence.Reason, null, cancellationToken)
                    .ConfigureAwait(false);
            if (!TryInspect(candidate, out var observed, out var inspectionReason) || !SameFile(operation, observed))
                return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                    inspectionReason == RetentionPreviewReasons.Eligible
                        ? RetentionPreviewReasons.MediaIdentityChanged
                        : inspectionReason,
                    null, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var livePolicy = await policyService.SyncAsync(configuration.Current, cancellationToken).ConfigureAwait(false);
        if (!livePolicy.Enabled)
            return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                RetentionEvaluationReasons.RetentionDisabled, null, cancellationToken).ConfigureAwait(false);
        if (operations.Any(operation => operation.PolicyVersion != livePolicy.Version))
            return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                RetentionExecutionReasons.PolicyChanged, null, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(operations[0].MediaPath);
            if (File.Exists(operations[0].MediaPath)) throw new IOException("The media path still exists after unlink.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Failed,
                RetentionExecutionReasons.UnlinkFailed, error, cancellationToken).ConfigureAwait(false);
        }

        var unlinkedAt = clock.GetUtcNow().UtcDateTime;
        foreach (var operation in operations)
        {
            operation.State = RetentionOperationStates.Unlinked;
            operation.Reason = RetentionExecutionReasons.Unlinked;
            operation.UnlinkedAt = unlinkedAt;
            operation.PhysicalBytesReleased = operation.HardlinkCountBefore > 1 ? 0 : null;
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in operations) TryRemoveNative(operation);
        return await CompleteUnderLeaseAsync(operations, requestedBindingId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RetentionExecutionResult> CompleteUnderLeaseAsync(
        IReadOnlyList<RetentionOperation> operations,
        Guid requestedBindingId,
        CancellationToken cancellationToken)
    {
        RetentionExecutionResult? requested = null;
        foreach (var operation in operations)
        {
            var result = await CompleteOneUnderLeaseAsync(operation, cancellationToken).ConfigureAwait(false);
            if (operation.BindingId == requestedBindingId) requested = result;
        }

        return requested ?? RetentionExecutionResult.From(operations[0]);
    }

    private async Task<RetentionExecutionResult> CompleteOneUnderLeaseAsync(
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
            if (remaining.Length == 0)
                await RetentionTargetReset.ResetAsync(database, operation.EntryId, episode.Id,
                    clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
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
            if (remaining.Length == 0)
                await RetentionTargetReset.ResetAsync(database, entry.Id, null,
                    clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        }

        var actionEntryLeaderId = await database.RetentionOperations.AsNoTracking()
            .Where(candidate => candidate.ActionId == operation.ActionId && candidate.EntryId == operation.EntryId)
            .OrderBy(candidate => candidate.Id).Select(candidate => candidate.Id)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        if (operation.Id == actionEntryLeaderId &&
            !await database.History.AnyAsync(history => history.Id == operation.Id, cancellationToken)
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
                    operation.ActionId,
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
        IReadOnlyList<RetentionOperation> operations,
        Guid requestedBindingId,
        string state,
        string reason,
        Exception? error,
        CancellationToken cancellationToken)
    {
        var completedAt = clock.GetUtcNow().UtcDateTime;
        foreach (var operation in operations)
        {
            operation.State = state;
            operation.Reason = reason;
            operation.Error = error is null ? operation.Error : Bound(error.Message);
            operation.CompletedAt = completedAt;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RetentionExecutionResult.From(
            operations.SingleOrDefault(operation => operation.BindingId == requestedBindingId) ?? operations[0]);
    }

    private async Task<RetentionOperation[]> LoadOpenActionAsync(Guid actionId, CancellationToken cancellationToken) =>
        await database.RetentionOperations.Where(operation => operation.ActionId == actionId &&
                (operation.State == RetentionOperationStates.Prepared ||
                 operation.State == RetentionOperationStates.Unlinked))
            .OrderBy(operation => operation.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);

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

    private static RetentionRepresentationDto[] SamePathGroup(
        RetentionPreviewDto result,
        RetentionRepresentationDto selected) =>
        result.Items.Where(candidate => string.Equals(candidate.CanonicalPath, selected.CanonicalPath,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.BindingId).ToArray();

    private async ValueTask<IAsyncDisposable> AcquireLibrariesAsync(
        IEnumerable<Guid> libraryIds,
        CancellationToken cancellationToken)
    {
        var leases = new List<IAsyncDisposable>();
        try
        {
            foreach (var libraryId in libraryIds.Distinct().Order())
                leases.Add(await libraryLock.AcquireAsync(libraryId, cancellationToken).ConfigureAwait(false));
            return new CombinedLease(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];

    private readonly record struct RetentionEvidence(bool Valid, long PolicyVersion, string Reason);

    private sealed class CombinedLease(IReadOnlyList<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            for (var index = leases.Count - 1; index >= 0; index--)
                await leases[index].DisposeAsync().ConfigureAwait(false);
        }
    }
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
    public const string ActionPathMismatch = "action_path_mismatch";
    public const string BindingSetChanged = "binding_set_changed";
    public const string MediaReappeared = "media_reappeared";
    public const string UnlinkFailed = "unlink_failed";
    public const string Unlinked = "unlinked";
    public const string Reclaimed = "reclaimed";
    public const string ReclaimedNativeCleanupFailed = "reclaimed_native_cleanup_failed";
}
