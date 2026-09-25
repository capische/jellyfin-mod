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
    ILogger<RetentionExecutor> logger,
    RetentionLiveCheck liveCheck)
{
    /// <summary>Revalidates and reclaims one binding selected by the shared retention preview.</summary>
    public async Task<RetentionExecutionResult> ReclaimAsync(Guid bindingId, CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);

        // An interrupted operation is recovered before any preview: once its file is unlinked the preview
        // can no longer report the binding as due, and the operation would stay open forever (P3.T9).
        var existing = await database.RetentionOperations
            .Where(operation => operation.BindingId == bindingId &&
                (operation.State == RetentionOperationStates.Prepared ||
                 operation.State == RetentionOperationStates.Unlinked))
            .OrderByDescending(operation => operation.PreparedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var interrupted = await LoadOpenActionAsync(existing.ActionId, cancellationToken).ConfigureAwait(false);
            await using var recoveryLease = await AcquireLibrariesAsync(
                interrupted.Select(operation => operation.TargetLibraryId)
                    .Concat(LibrariesContaining(interrupted[0].MediaPath)), cancellationToken).ConfigureAwait(false);
            return await RecoverUnderLeaseAsync(interrupted, bindingId, cancellationToken).ConfigureAwait(false);
        }

        await policyService.SyncAsync(configuration.Current, cancellationToken).ConfigureAwait(false);
        // Only the targets this action can touch are re-evaluated; the run evaluated every title once before it started
        // (RET3-R6, RET4-R5).
        var targetIds = await TargetsOfBindingsAsync([bindingId], cancellationToken).ConfigureAwait(false);
        var initialPreview = await preview.PreviewAsync(cancellationToken, null, targetIds).ConfigureAwait(false);
        var initial = Find(initialPreview, bindingId);
        if (initial is null)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.BindingUnavailable);
        if (initial.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId, initial.Reason);

        var initialGroup = SamePathGroup(initialPreview, initial);
        var lockedLibraryIds = initialGroup.Select(candidate => candidate.TargetLibraryId).ToHashSet();
        lockedLibraryIds.UnionWith(LibrariesContaining(initial.CanonicalPath));
        await using var libraryLease = await AcquireLibrariesAsync(
            lockedLibraryIds, cancellationToken).ConfigureAwait(false);
        var groupTargets = initialGroup.Select(item => item.EpisodeId ?? item.EntryId).Concat(targetIds)
            .Concat(await TargetsOfBindingsAsync([.. await CurrentBindingSetAsync(initial.CanonicalPath!, cancellationToken)
                .ConfigureAwait(false)], cancellationToken).ConfigureAwait(false)).ToHashSet();
        var currentPreview = await preview.PreviewAsync(cancellationToken, null, groupTargets).ConfigureAwait(false);
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
            // A read-only mount or directory would fail every run; never prepare an operation for it.
            if (!files.CanUnlink(observed.CanonicalPath))
                return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.MediaNotWritable);
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

    /// <summary>
    /// Replaces the superseded version of a completed upgrade (P6.M5) through the same executor, locks and checks as a
    /// reclaim, except that the watched rule and the window do not apply (PHASE10 Q10): a per-file Keep, storage identity, the shared-inode group, active sessions,
    /// resume, favourites, Keep and seeding all still protect the file. The operation's provenance is
    /// <c>upgrade_replaced</c>, so reconciliation attributes the disappearance to the plugin.
    /// </summary>
    public async Task<RetentionExecutionResult> ReplaceAsync(Guid bindingId, Guid upgradeOperationId, CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var existing = await database.RetentionOperations
            .Where(operation => operation.BindingId == bindingId &&
                (operation.State == RetentionOperationStates.Prepared ||
                 operation.State == RetentionOperationStates.Unlinked))
            .OrderByDescending(operation => operation.PreparedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var interrupted = await LoadOpenActionAsync(existing.ActionId, cancellationToken).ConfigureAwait(false);
            await using var recoveryLease = await AcquireLibrariesAsync(
                interrupted.Select(operation => operation.TargetLibraryId)
                    .Concat(LibrariesContaining(interrupted[0].MediaPath)), cancellationToken).ConfigureAwait(false);
            return await RecoverUnderLeaseAsync(interrupted, bindingId, cancellationToken).ConfigureAwait(false);
        }

        // A deliberately disabled retention also stops replacements: turning it off must stop every deletion.
        var policy = await policyService.SyncAsync(configuration.Current, cancellationToken).ConfigureAwait(false);
        if (!policy.Enabled)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionEvaluationReasons.RetentionDisabled);
        // A file bound to its episode by number only was never shown to be that episode; replacing it could remove the
        // right file for a wrong-numbered release (RET2-R3). It stays until reconciliation verifies it.
        if (await IdentityUnverifiedAsync(bindingId, cancellationToken).ConfigureAwait(false))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.IdentityUnverified);
        var replacement = new HashSet<Guid> { bindingId };
        var replacedTargets = await TargetsOfBindingsAsync([bindingId], cancellationToken).ConfigureAwait(false);
        var initialPreview = await preview.PreviewAsync(cancellationToken, replacement, replacedTargets).ConfigureAwait(false);
        var initial = Find(initialPreview, bindingId);
        if (initial is null)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.BindingUnavailable);
        if (initial.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId, initial.Reason);

        var lockedLibraryIds = SamePathGroup(initialPreview, initial).Select(candidate => candidate.TargetLibraryId).ToHashSet();
        lockedLibraryIds.UnionWith(LibrariesContaining(initial.CanonicalPath));
        await using var libraryLease = await AcquireLibrariesAsync(lockedLibraryIds, cancellationToken).ConfigureAwait(false);
        var currentPreview = await preview.PreviewAsync(cancellationToken, replacement,
            SamePathGroup(initialPreview, initial).Select(item => item.EpisodeId ?? item.EntryId).Concat(replacedTargets).ToHashSet())
            .ConfigureAwait(false);
        var candidate = Find(currentPreview, bindingId);
        if (candidate is null || candidate.State != RetentionPreviewStates.Due)
            return RetentionExecutionResult.NotStarted(bindingId, candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable);
        // A file shared with another binding is replaced only when that binding may lose it too; it never can here.
        if (SamePathGroup(currentPreview, candidate).Length != 1)
            return RetentionExecutionResult.NotStarted(bindingId, RetentionPreviewReasons.SharedPathNotAllEligible);
        if (!TryInspect(candidate, out var observed, out var inspectionReason))
            return RetentionExecutionResult.NotStarted(bindingId, inspectionReason);
        if (!files.CanUnlink(observed.CanonicalPath))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.MediaNotWritable);
        if (await IdentityUnverifiedAsync(bindingId, cancellationToken).ConfigureAwait(false))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionExecutionReasons.IdentityUnverified);
        var storageIdentity = await LoadStorageIdentityAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storageIdentity))
            return RetentionExecutionResult.NotStarted(bindingId, RetentionPreviewReasons.StorageUnavailable);
        var operation = new RetentionOperation
        {
            ActionId = Guid.NewGuid(), BindingId = candidate.BindingId, EntryId = candidate.EntryId, EpisodeId = candidate.EpisodeId,
            JellyfinItemId = candidate.JellyfinItemId, TargetLibraryId = candidate.TargetLibraryId, PolicyVersion = policy.Version,
            MediaPath = observed.CanonicalPath, StorageIdentity = storageIdentity, PhysicalIdentity = observed.PhysicalIdentity,
            LogicalBytes = checked((long)observed.LogicalBytes), HardlinkCountBefore = observed.HardlinkCount,
            Reason = RetentionPreviewReasons.Eligible, PreparedAt = clock.GetUtcNow().UtcDateTime,
            Provenance = RetentionProvenances.UpgradeReplaced, UpgradeOperationId = upgradeOperationId
        };
        database.RetentionOperations.Add(operation);
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ExecutePreparedUnderLeaseAsync([operation], bindingId, cancellationToken).ConfigureAwait(false);
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
                operations.Select(operation => operation.TargetLibraryId)
                    .Concat(LibrariesContaining(operations[0].MediaPath)), cancellationToken).ConfigureAwait(false);
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
        if (operations.Any(operation => operation.State == RetentionOperationStates.Unlinked))
        {
            // The file was already unlinked by this plugin; only the catalog bookkeeping remains. It is
            // finished outside the caller's cancellation, and left open while storage cannot be checked.
            var unlinkedPresence = files.Probe(selected.MediaPath);
            if (unlinkedPresence == PathPresence.Unknown || operations.Any(operation => !IsStorageCurrent(operation)))
                return RetentionExecutionResult.From(selected);
            if (unlinkedPresence == PathPresence.Present)
                return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                    RetentionExecutionReasons.MediaReappeared, null, CancellationToken.None).ConfigureAwait(false);
            return await CompleteUnderLeaseAsync(operations, selected.BindingId, CancellationToken.None).ConfigureAwait(false);
        }

        if (operations.Any(operation => !IsStorageCurrent(operation)))
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                RetentionPreviewReasons.StorageUnavailable, null, cancellationToken).ConfigureAwait(false);
        if (operations.Select(operation => operation.MediaPath).Distinct(StringComparer.Ordinal).Count() != 1)
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Failed,
                RetentionExecutionReasons.ActionPathMismatch, null, cancellationToken).ConfigureAwait(false);

        var preparedPresence = files.Probe(selected.MediaPath);
        if (preparedPresence == PathPresence.Unknown) return RetentionExecutionResult.From(selected);
        if (preparedPresence == PathPresence.Absent)
        {
            // Nothing proves this plugin removed the file, so it is not a reclamation: no history and no
            // bytes. Reconciliation will record the loss like any other external removal.
            return await FinishAsync(operations, selected.BindingId, RetentionOperationStates.Vanished,
                RetentionExecutionReasons.MediaVanished, null, cancellationToken).ConfigureAwait(false);
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
        // An upgrade replacement skips the watched rule and the window (P6.M5, PHASE10 Q10); every physical check below
        // still runs.
        var replacement = operations.All(operation => operation.Provenance == RetentionProvenances.UpgradeReplaced);
        // The action's targets, and every target with a binding at the file now (one added since prepare included), are
        // evaluated afresh: the same set a full evaluation would have refreshed for this file.
        var bindingsAtFile = await CurrentBindingSetAsync(operations[0].MediaPath, cancellationToken).ConfigureAwait(false);
        var actionTargets = (await TargetsOfBindingsAsync(operations.Select(operation => operation.BindingId).Concat(bindingsAtFile)
            .ToArray(), cancellationToken).ConfigureAwait(false)).Concat(operations
            .Where(operation => (operation.EpisodeId ?? operation.EntryId).HasValue)
            .Select(operation => (operation.EpisodeId ?? operation.EntryId)!.Value)).ToHashSet();
        var currentPreview = await preview.PreviewAsync(cancellationToken,
            replacement ? operations.Select(operation => operation.BindingId).ToHashSet() : null, actionTargets).ConfigureAwait(false);
        foreach (var operation in operations)
        {
            var candidate = Find(currentPreview, operation.BindingId);
            if (candidate is null || candidate.State != RetentionPreviewStates.Due)
                return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                    candidate?.Reason ?? RetentionExecutionReasons.BindingUnavailable, null, cancellationToken)
                    .ConfigureAwait(false);
            var evidence = replacement
                ? new RetentionEvidence(true, operation.PolicyVersion, RetentionPreviewReasons.Eligible)
                : await LoadEvidenceAsync(candidate, cancellationToken).ConfigureAwait(false);
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

        if (replacement)
            foreach (var operation in operations)
                if (await IdentityUnverifiedAsync(operation.BindingId, cancellationToken).ConfigureAwait(false))
                    return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                        RetentionExecutionReasons.IdentityUnverified, null, cancellationToken).ConfigureAwait(false);

        // Stored observations can miss a favourite, unwatched or resume event, and a session can be
        // playing another version. Re-read live Jellyfin state for every affected target last.
        foreach (var operation in operations)
        {
            var liveReason = await liveCheck.BlockReasonAsync(operation, livePolicy, cancellationToken, requireCompletion: !replacement)
                .ConfigureAwait(false);
            if (liveReason is not null)
                return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                    liveReason, null, cancellationToken).ConfigureAwait(false);
        }

        // A binding added since prepare (for example from an overlapping library) shares the file but was
        // never checked; the whole action stops rather than unlinking media it protects (P3.T12).
        if (!(await CurrentBindingSetAsync(operations[0].MediaPath, cancellationToken).ConfigureAwait(false))
                .SetEquals(operations.Select(operation => operation.BindingId)))
            return await FinishAsync(operations, requestedBindingId, RetentionOperationStates.Blocked,
                RetentionExecutionReasons.BindingSetChanged, null, cancellationToken).ConfigureAwait(false);

        // The last point at which cancellation is honoured; everything after the unlink must finish.
        cancellationToken.ThrowIfCancellationRequested();
        var unlink = files.UnlinkPinned(operations[0].MediaPath, operations[0].PhysicalIdentity);
        if (!unlink.Removed)
            return await FinishAsync(operations, requestedBindingId,
                unlink.IsReplacement ? RetentionOperationStates.Blocked : RetentionOperationStates.Failed,
                unlink.IsReplacement ? RetentionPreviewReasons.MediaIdentityChanged : RetentionExecutionReasons.UnlinkFailed,
                new IOException(unlink.Detail), cancellationToken).ConfigureAwait(false);

        var unlinkedAt = clock.GetUtcNow().UtcDateTime;
        foreach (var operation in operations)
        {
            operation.State = RetentionOperationStates.Unlinked;
            operation.Reason = RetentionExecutionReasons.Unlinked;
            operation.UnlinkedAt = unlinkedAt;
            operation.PhysicalBytesReleased = operation.HardlinkCountBefore > 1 ? 0 : null;
        }
        await SaveAfterUnlinkAsync(operations).ConfigureAwait(false);
        foreach (var operation in operations) TryRemoveNative(operation);
        return await CompleteUnderLeaseAsync(operations, requestedBindingId, CancellationToken.None).ConfigureAwait(false);
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
        // BEGIN IMMEDIATE waits for the write lock and may give up on a busy database; nothing has changed yet, so it
        // is retried (RET3-R6). A failure after it leaves the operation unlinked, and the next run completes it.
        await using var transaction = await SqliteBusy.RetryAsync(
            () => database.Database.BeginTransactionAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        if (operation.EntryId is not { } entryId)
        {
            // Removal is refused while an operation is open, so this only happens to legacy rows.
            operation.State = RetentionOperationStates.Completed;
            operation.Reason = RetentionExecutionReasons.Reclaimed;
            operation.CompletedAt = clock.GetUtcNow().UtcDateTime;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RetentionExecutionResult.From(operation);
        }

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
            // Episodes a multi-episode file covered without files of their own went with it (PHASE10 Q5).
            foreach (var covered in await database.Episodes.Where(candidate => candidate.EntryId == entryId &&
                         candidate.Id != episode.Id && candidate.JellyfinItemId == operation.JellyfinItemId &&
                         !database.EpisodeBindings.Any(binding => binding.EpisodeId == candidate.Id))
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                covered.JellyfinItemId = null;
                covered.State = FileState.Reclaimed;
            }

            // A file re-added at this path gets this item id again; its old evidence must not come back with it (RET2-R1).
            if (remaining.Length > 0)
                await ReconciliationService.ForgetRepresentationEvidenceAsync(database, episode.Id, operation.JellyfinItemId,
                    cancellationToken).ConfigureAwait(false);
            if (remaining.Length == 0)
            {
                await RetentionTargetReset.ResetAsync(database, entryId, episode.Id,
                    clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
                // The series itself is reclaimed once no episode of it has media left (P3.T14).
                var seriesEpisodeIds = await database.Episodes.AsNoTracking().Where(candidate => candidate.EntryId == entryId)
                    .Select(candidate => candidate.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (!await database.EpisodeBindings.AnyAsync(candidate => seriesEpisodeIds.Contains(candidate.EpisodeId) &&
                        candidate.Id != operation.BindingId, cancellationToken).ConfigureAwait(false))
                    (await database.Entries.SingleAsync(candidate => candidate.Id == entryId, cancellationToken)
                        .ConfigureAwait(false)).State = FileState.Reclaimed;
            }
        }
        else
        {
            var binding = await database.EntryBindings.SingleOrDefaultAsync(
                candidate => candidate.Id == operation.BindingId, cancellationToken).ConfigureAwait(false);
            if (binding is not null) database.EntryBindings.Remove(binding);
            // A further media source is not navigable; the title that owns it is the one an entry points at (P6.M6).
            var remaining = await database.EntryBindings.AsNoTracking()
                .Where(candidate => candidate.EntryId == operation.EntryId && candidate.Id != operation.BindingId)
                .OrderBy(candidate => candidate.OwnerItemId == null ? 0 : 1)
                .ThenBy(candidate => candidate.JellyfinItemId == candidate.VersionGroupId ? 0 : 1)
                .ThenBy(candidate => candidate.VersionGroupId).ThenBy(candidate => candidate.JellyfinItemId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var entry = await database.Entries.SingleAsync(candidate => candidate.Id == operation.EntryId,
                cancellationToken).ConfigureAwait(false);
            entry.JellyfinItemId = entry.JellyfinItemId is { } current &&
                remaining.Any(candidate => (candidate.OwnerItemId ?? candidate.JellyfinItemId) == current)
                    ? current
                    : remaining.FirstOrDefault() is { } next ? next.OwnerItemId ?? next.JellyfinItemId : null;
            entry.State = remaining.Length == 0 ? FileState.Reclaimed : FileState.OnDisk;
            if (remaining.Length > 0)
                await ReconciliationService.ForgetRepresentationEvidenceAsync(database, entry.Id, operation.JellyfinItemId,
                    cancellationToken).ConfigureAwait(false);
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
            var replaced = operation.Provenance == RetentionProvenances.UpgradeReplaced;
            database.History.Add(new HistoryRecord
            {
                Id = operation.Id,
                EntryId = entryId,
                EventType = replaced ? "upgrade_replaced" : "reclaimed",
                Summary = replaced
                    ? operation.EpisodeId.HasValue ? "Replaced the episode's older version after an upgrade"
                        : "Replaced the older version after an upgrade"
                    : operation.EpisodeId.HasValue
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
                    physicalBytesReleased = operation.PhysicalBytesReleased,
                    provenance = operation.Provenance,
                    operation.UpgradeOperationId
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

    /// <summary>
    /// Records that the file is gone. The unlink already happened, so this save must not be lost to a busy database: it is
    /// retried for about two minutes (RET3-R6), and a final failure is logged as an error. The operation then stays
    /// prepared with its file absent, which the next run records as vanished; nothing else is deleted either way.
    /// </summary>
    private async Task SaveAfterUnlinkAsync(IReadOnlyList<RetentionOperation> operations)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (SqliteBusy.IsBusy(error) && attempt < 8)
            {
                logger.LogWarning("JellyfinMod unlinked {Path} but the database was busy; recording it again (attempt {Attempt})",
                    operations[0].MediaPath, attempt);
                await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogError(error, "JellyfinMod unlinked {Path} for retention operation {ActionId} but could not record it; " +
                    "the next run will find the operation prepared with its file gone", operations[0].MediaPath, operations[0].ActionId);
                throw;
            }
        }
    }

    /// <summary>The entries that own these movie or episode bindings.</summary>
    /// <summary>The retention targets of these bindings: a movie binding's entry, an episode binding's episode.</summary>
    private async Task<HashSet<Guid>> TargetsOfBindingsAsync(IReadOnlyCollection<Guid> bindingIds, CancellationToken cancellationToken)
    {
        var movies = await database.EntryBindings.AsNoTracking().Where(binding => bindingIds.Contains(binding.Id))
            .Select(binding => binding.EntryId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var episodes = await database.EpisodeBindings.AsNoTracking().Where(binding => bindingIds.Contains(binding.Id))
            .Select(binding => binding.EpisodeId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return movies.Concat(episodes).ToHashSet();
    }

    /// <summary>Retries native item removal for reclaimed media whose cleanup failed earlier (P3.T14).</summary>
    public async Task<int> RetryNativeCleanupAsync(CancellationToken cancellationToken)
    {
        await using var executionLease = await executionGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var pending = await database.RetentionOperations
            .Where(operation => operation.State == RetentionOperationStates.Completed &&
                operation.Reason == RetentionExecutionReasons.ReclaimedNativeCleanupFailed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var cleaned = 0;
        foreach (var operation in pending)
        {
            if (!TryRemoveNative(operation)) continue;
            operation.Reason = RetentionExecutionReasons.Reclaimed;
            operation.Error = null;
            cleaned++;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return cleaned;
    }

    private bool TryRemoveNative(RetentionOperation operation)
    {
        try
        {
            var native = library.GetItemById(operation.JellyfinItemId);
            if (native is null) return true;
            library.DeleteItem(native, new DeleteOptions
            {
                DeleteFileLocation = false,
                DeleteFromExternalProvider = false
            }, notifyParentItem: true);
            return true;
        }
        catch (Exception error)
        {
            operation.Error = Bound(error.Message);
            logger.LogWarning(error, "Media was unlinked but native item {ItemId} could not be removed", operation.JellyfinItemId);
            return false;
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

    /// <summary>Whether an episode binding was made by number only and never verified (RET2-R3); a movie binding never is.</summary>
    private Task<bool> IdentityUnverifiedAsync(Guid bindingId, CancellationToken cancellationToken) =>
        database.EpisodeBindings.AsNoTracking().AnyAsync(binding => binding.Id == bindingId && binding.IdentityUnverified,
            cancellationToken);

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

    /// <summary>Every movie or episode binding whose configured media path resolves to this canonical file.</summary>
    private async Task<HashSet<Guid>> CurrentBindingSetAsync(string canonicalPath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(canonicalPath);
        var candidates = await database.EntryBindings.AsNoTracking()
            .Where(binding => binding.MediaPath != null && binding.MediaPath.EndsWith(fileName))
            .Select(binding => new { binding.Id, binding.MediaPath })
            .Concat(database.EpisodeBindings.AsNoTracking()
                .Where(binding => binding.MediaPath != null && binding.MediaPath.EndsWith(fileName))
                .Select(binding => new { binding.Id, binding.MediaPath }))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Where(candidate => string.Equals(candidate.MediaPath, canonicalPath, StringComparison.Ordinal) ||
                files.TryCanonicalize(candidate.MediaPath!, out var resolved) &&
                string.Equals(resolved, canonicalPath, StringComparison.Ordinal))
            .Select(candidate => candidate.Id).ToHashSet();
    }

    /// <summary>Libraries whose configured roots contain the path, which must all be locked around its unlink.</summary>
    private IEnumerable<Guid> LibrariesContaining(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (var folder in library.GetVirtualFolders() ?? [])
            if (Guid.TryParse(folder.ItemId, out var id) && MediaStorageIdentity.IsWithin(path, folder.Locations ?? []))
                yield return id;
    }

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
    public const string MediaNotWritable = "media_not_writable";
    public const string IdentityUnverified = "identity_unverified";
    public const string MediaVanished = "media_vanished";
    public const string MediaStateUnknown = "media_state_unknown";
    public const string Unlinked = "unlinked";
    public const string Reclaimed = "reclaimed";
    public const string ReclaimedNativeCleanupFailed = "reclaimed_native_cleanup_failed";
}
