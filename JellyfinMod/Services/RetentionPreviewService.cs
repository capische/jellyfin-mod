using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Evaluates the same physical protections used by preview and eventual retention execution.</summary>
public sealed class RetentionPreviewService(
    ModDbContext database,
    RetentionEvaluator retention,
    ILibraryManager library,
    IUserManager users,
    IUserDataManager userData,
    LibraryAccess access,
    ISessionManager sessions,
    MediaStorageIdentity storage,
    UnixFileInspector files,
    TransmissionSeedClient transmission,
    TimeProvider clock)
{
    /// <summary>Builds an admin-only preview without deleting or changing media.</summary>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <param name="replacementBindingIds">
    /// Bindings being replaced by an upgrade (P6.M5): they skip the watched-completion schedule but keep every physical
    /// protection (storage, native item, path, active session, favourite series, seeding and the shared-inode group).
    /// </param>
    /// <param name="evaluateEntries">
    /// Null re-evaluates every target first (the administrator's preview and the start of a run). Otherwise only the
    /// targets of these entries are re-evaluated, which the executor passes under its locks for the titles an action
    /// touches (RET3-R6); every other row is reported from its stored evaluation and cannot be acted on by that action.
    /// </param>
    public async Task<RetentionPreviewDto> PreviewAsync(CancellationToken cancellationToken,
        IReadOnlySet<Guid>? replacementBindingIds = null, IReadOnlyCollection<Guid>? evaluateEntries = null)
    {
        using var operation = SqliteWriteDiagnostics.Operation("retention preview");
        if (evaluateEntries is null) await retention.EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
        else await retention.EvaluateEntriesAsync(evaluateEntries, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            snapshot => snapshot.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        var evaluations = await database.RetentionEvaluations.AsNoTracking()
            .ToDictionaryAsync(evaluation => evaluation.TargetId, cancellationToken).ConfigureAwait(false);
        var targets = await LoadTargetsAsync(evaluations, cancellationToken).ConfigureAwait(false);
        // Files an administrator kept while the title's other versions may go (PHASE10 Q3), by path and by identity.
        var versionKeeps = await database.VersionKeeps.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var keptPaths = versionKeeps.Select(keep => keep.MediaPath).ToHashSet(StringComparer.Ordinal);
        var keptIdentities = versionKeeps.Where(keep => !string.IsNullOrEmpty(keep.PhysicalIdentity))
            .Select(keep => keep.PhysicalIdentity!).ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> libraryRoots;
        try
        {
            libraryRoots = library.GetVirtualFolders()
                .Select(folder => (Folder: folder, Id: Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty))
                .Where(item => item.Id != Guid.Empty)
                .ToDictionary(item => item.Id, item => (IReadOnlyList<string>)(item.Folder.Locations ?? []));
        }
        catch
        {
            libraryRoots = new Dictionary<Guid, IReadOnlyList<string>>();
        }

        HashSet<Guid>? activeItemIds;
        try
        {
            // An alternate version plays under its primary item; its media source names the version.
            activeItemIds = sessions.Sessions
                .SelectMany(session => new[]
                {
                    session.FullNowPlayingItem?.Id, session.NowPlayingItem?.Id,
                    Guid.TryParse(session.PlayState?.MediaSourceId, out var source) ? source : null
                })
                .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        }
        catch
        {
            activeItemIds = null;
        }

        // Playing any member of a movie's version group protects every version in it (prior-M6).
        var activeGroups = activeItemIds is null ? null : targets
            .Where(target => activeItemIds.Contains(target.JellyfinItemId) || activeItemIds.Contains(target.VersionGroupId))
            .Select(target => target.VersionGroupId).ToHashSet();
        // Every file bound to each movie or episode, to compare with the files Jellyfin plays as its versions. Files, not
        // item ids: Jellyfin 12 derives an extra version's id differently from 10.11 (analysis C3).
        var trackedItems = targets.GroupBy(target => target.EpisodeId ?? target.EntryId)
            .ToDictionary(group => group.Key, group => (IReadOnlySet<string>)group.Where(target => target.Path is not null)
                .Select(target => target.Path!).ToHashSet(StringComparer.Ordinal));
        var mergedElsewhere = FilesMergedIntoOtherTitles(targets);
        var inspected = targets.Select(target => Inspect(target, libraryRoots, now,
            replacementBindingIds?.Contains(target.BindingId) == true, keptPaths, keptIdentities,
            trackedItems[target.EpisodeId ?? target.EntryId], mergedElsewhere)).ToArray();
        await ApplyMultiEpisodeRuleAsync(inspected, policy, evaluations, now, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in inspected.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection))
        {
            if (activeItemIds is null)
            {
                candidate.Block(RetentionPreviewReasons.ActiveSessionUnknown);
                continue;
            }

            if (activeItemIds.Contains(candidate.Target.JellyfinItemId) || activeGroups!.Contains(candidate.Target.VersionGroupId))
            {
                candidate.Block(RetentionPreviewReasons.ActiveSession);
                continue;
            }

            if (policy?.ExemptFavourites == true && candidate.Target.EpisodeId.HasValue &&
                IsSeriesFavorite(candidate.Target))
            {
                candidate.Block(RetentionPreviewReasons.FavoriteSeries);
            }
        }

        // Media on a read-only mount can never be reclaimed, and the executor refuses it. Reporting it as due
        // would tell an administrator that untouchable media is about to be deleted (P3.T18).
        foreach (var candidate in inspected.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection))
        {
            var path = candidate.File?.CanonicalPath ?? candidate.Target.Path;
            if (!string.IsNullOrEmpty(path) && !files.CanUnlink(path))
                candidate.Block(RetentionPreviewReasons.MediaNotWritable);
        }

        var seedCandidates = inspected.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection)
            .ToArray();
        if (seedCandidates.Length > 0)
        {
            // A library file that shares its inode with a seeding copy the plugin owns follows the plugin's effective seed
            // goal (P5.I6): blocked until the goal is met, then due. The client's per-torrent mode is unlimited by design
            // (P4.A5), so the Transmission reader alone would report an unbounded goal forever.
            var pluginSeeds = await database.SeedReleaseOperations.AsNoTracking()
                .Where(seed => SeedReleaseStates.Open.Contains(seed.State) && seed.SeedingPhysicalIdentity != string.Empty)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var byIdentity = pluginSeeds.GroupBy(seed => seed.SeedingPhysicalIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var candidate in seedCandidates)
            {
                if (!byIdentity.TryGetValue(candidate.File!.Value.PhysicalIdentity, out var owned)) continue;
                if (owned.Any(seed => seed.GoalMetAt is null)) candidate.Block(RetentionPreviewReasons.SeedGoalUnmet, true);
                else candidate.MarkDue(torrentManaged: true, []);
            }

            seedCandidates = seedCandidates.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection).ToArray();
        }

        var unresolvedSeedFiles = 0;
        if (seedCandidates.Length > 0)
        {
            var seedSnapshot = await transmission.GetSnapshotAsync(cancellationToken, database).ConfigureAwait(false);
            unresolvedSeedFiles = seedSnapshot.UnresolvedFiles;
            foreach (var candidate in seedCandidates)
            {
                if (!seedSnapshot.Available)
                {
                    candidate.Block(seedSnapshot.UnavailableReason ?? RetentionPreviewReasons.SeedStateUnknown);
                    continue;
                }

                if (!seedSnapshot.FilesByPhysicalIdentity.TryGetValue(candidate.File!.Value.PhysicalIdentity, out var torrentFiles))
                {
                    if (!seedSnapshot.CompleteFileIndex)
                    {
                        candidate.Block(RetentionPreviewReasons.SeedIndexIncomplete);
                        continue;
                    }

                    candidate.MarkDue(torrentManaged: false, []);
                    continue;
                }

                if (torrentFiles.Any(file => !file.FileComplete))
                {
                    candidate.Block(RetentionPreviewReasons.SeedingIncomplete, true, torrentFiles);
                    continue;
                }

                if (torrentFiles.Any(file => !file.HasFiniteSeedGoal))
                {
                    candidate.Block(RetentionPreviewReasons.SeedGoalUnbounded, true, torrentFiles);
                    continue;
                }

                if (torrentFiles.Any(file => !file.SeedGoalSatisfied))
                {
                    candidate.Block(RetentionPreviewReasons.SeedGoalUnmet, true, torrentFiles);
                    continue;
                }

                candidate.MarkDue(torrentManaged: true, torrentFiles);
            }
        }

        if (inspected.Any(candidate => candidate.State == RetentionPreviewStates.Due))
        {
            foreach (var candidate in inspected.Where(candidate => !candidate.File.HasValue))
            {
                if (candidate.Target.Path is { } path && files.TryInspect(path, out var observed))
                    candidate.AttachFileForSharedCheck(observed);
            }
        }

        foreach (var group in inspected.Where(candidate => candidate.File.HasValue)
                     .GroupBy(candidate => candidate.File!.Value.PhysicalIdentity, StringComparer.Ordinal))
        {
            var rows = group.ToArray();
            if (rows.Length < 2 || rows.All(row => row.State == RetentionPreviewStates.Due)) continue;
            foreach (var due in rows.Where(row => row.State == RetentionPreviewStates.Due))
                due.Block(RetentionPreviewReasons.SharedPathNotAllEligible, due.TorrentManaged, due.TorrentFiles);
        }

        var items = inspected.OrderBy(candidate => candidate.Target.EntryId)
            .ThenBy(candidate => candidate.Target.EpisodeId).ThenBy(candidate => candidate.Target.BindingId)
            .Select(candidate => candidate.ToDto()).ToArray();
        return new(now, items.Length, items.Count(item => item.State == RetentionPreviewStates.Due),
            items.Count(item => item.State == RetentionPreviewStates.Blocked),
            items.Count(item => item.State == RetentionPreviewStates.Scheduled),
            items.Count(item => item.State == RetentionEvaluationStates.Waiting),
            items.Count(item => item.State == RetentionEvaluationStates.Disabled), items, unresolvedSeedFiles);
    }

    private async Task<PreviewTarget[]> LoadTargetsAsync(
        IReadOnlyDictionary<Guid, RetentionEvaluation> evaluations,
        CancellationToken cancellationToken)
    {
        var movies = await database.EntryBindings.AsNoTracking()
            .Join(database.Entries.AsNoTracking().Where(entry => entry.MediaType == "movie"),
                binding => binding.EntryId, entry => entry.Id,
                (binding, entry) => new { Binding = binding, Entry = entry })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var episodes = await database.EpisodeBindings.AsNoTracking()
            .Join(database.Episodes.AsNoTracking(), binding => binding.EpisodeId, episode => episode.Id,
                (binding, episode) => new { Binding = binding, Episode = episode })
            .Join(database.Entries.AsNoTracking(), row => row.Episode.EntryId, entry => entry.Id,
                (row, entry) => new { row.Binding, row.Episode, Entry = entry })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return movies.Select(row => new PreviewTarget(row.Binding.Id, row.Entry.Id, null,
                row.Binding.JellyfinItemId, row.Binding.VersionGroupId, row.Binding.TargetLibraryId, row.Binding.MediaPath,
                row.Binding.StorageIdentity, null, row.Entry,
                evaluations.GetValueOrDefault(row.Entry.Id), RetentionOverrides.IsKept(row.Entry, (Episode?)null)))
            .Concat(episodes.Select(row => new PreviewTarget(row.Binding.Id, row.Entry.Id, row.Episode.Id,
                row.Binding.JellyfinItemId, row.Binding.JellyfinItemId, row.Binding.TargetLibraryId, row.Binding.MediaPath,
                row.Binding.StorageIdentity, row.Binding.SeriesItemId, row.Entry,
                evaluations.GetValueOrDefault(row.Episode.Id), RetentionOverrides.IsKept(row.Entry, row.Episode))))
            .ToArray();
    }

    private PreviewCandidate Inspect(
        PreviewTarget target,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> libraryRoots,
        DateTime now,
        bool replacement,
        IReadOnlySet<string> keptPaths,
        IReadOnlySet<string> keptIdentities,
        IReadOnlySet<string> trackedItems,
        IReadOnlyDictionary<string, HashSet<Guid>> mergedElsewhere)
    {
        var evaluation = target.Evaluation;
        // A kept file is never reclaimed or replaced, whatever its title's schedule says (PHASE10 Q3).
        if (target.Path is { } keptPath && keptPaths.Contains(keptPath))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.VersionKept, evaluation?.Deadline);
        // Keep on the title or episode, read with the targets just now, wins over whatever the evaluation row says: a row
        // saved by a batch that started before the Keep can still read "scheduled" (RET2-R4).
        if (target.Kept)
            return PreviewCandidate.Blocked(target, RetentionEvaluationReasons.Kept, evaluation?.Deadline);
        // An upgrade replaces the older version straight away, without the watched rule or the window (PHASE6 M5, confirmed
        // by the user as PHASE10 Q10 on 2026-09-24). Keep, a per-file Keep, seeding and read-only media still protect it.
        if (!replacement)
        {
            if (evaluation is null)
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.EvaluationMissing, null);
            if (evaluation.State != RetentionEvaluationStates.Scheduled)
                return new(target, evaluation.State, evaluation.Reason, evaluation.Deadline, null);
            if (!evaluation.Deadline.HasValue || evaluation.Deadline.Value > now)
                return new(target, RetentionPreviewStates.Scheduled, RetentionPreviewReasons.NotDue,
                    evaluation.Deadline, null);
        }

        if (!libraryRoots.TryGetValue(target.TargetLibraryId, out var roots) || roots.Count == 0)
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.LibraryRootMissing, evaluation?.Deadline);
        try
        {
            if (!storage.IsCurrent(target.Path, target.StorageIdentity, roots, out _))
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.StorageUnavailable, evaluation?.Deadline);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.StorageUnavailable, evaluation?.Deadline);
        }
        BaseItem? native;
        try
        {
            native = library.GetItemById(target.JellyfinItemId);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.NativeBindingUnavailable, evaluation?.Deadline);
        }
        if (native is null || string.IsNullOrWhiteSpace(native.Path))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.NativeBindingMissing, evaluation?.Deadline);
        // The executor unlinks one exact file. A stacked movie keeps its other parts, and a multi-episode
        // file also holds later episodes that may be unwatched, so neither is reclaimed (P3.T16).
        if (native is Video { AdditionalParts.Length: > 0 })
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MultiPartUnsupported, evaluation?.Deadline);
        // A file holding several episodes is reclaimed only when every episode in it is due (PHASE10 Q5, answered
        // 2026-09-24); that is decided below, once every candidate is known. An upgrade never replaces one.
        (int Season, int First, int Last)? covered = null;
        if (native is MediaBrowser.Controller.Entities.TV.Episode { IndexNumberEnd: { } lastEpisode } multiEpisode &&
            lastEpisode > (multiEpisode.IndexNumber ?? lastEpisode))
        {
            if (replacement || !target.EpisodeId.HasValue || multiEpisode.IndexNumber is not { } firstEpisode ||
                multiEpisode.ParentIndexNumber is not { } season)
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MultiEpisodeUnsupported, evaluation?.Deadline);
            covered = (season, firstEpisode, lastEpisode);
        }
        // Jellyfin 12.0.0 (the runtime host) groups several files of one movie, and now of one episode, as versions of
        // one main item, and it groups S01E01-E02 with S01E01 because its episode key ignores the ending number. Only
        // what the plugin binds is checked, watched and kept, so while Jellyfin plays a version the plugin does not
        // track, or the bound item has itself become an extra version, nothing of that title is reclaimed (PHASE10 S17,
        // Jellyfin 12 analysis C1, C7, C10), until versions are enumerated (task V1) and tracked (E7).
        // Jellyfin 12 records a merge ("Group versions") only on the main item: the file merged into it still looks like a
        // title of its own. A file that another tracked title plays as one of its versions is part of a title whose other
        // files this target does not track, so it is blocked like any other untracked version (C2, found live).
        if (target.Path is { } mergedPath && mergedElsewhere.TryGetValue(mergedPath, out var mergers) &&
            mergers.Any(owner => owner != (target.EpisodeId ?? target.EntryId)))
            return PreviewCandidate.Blocked(target, target.EpisodeId.HasValue
                ? RetentionPreviewReasons.EpisodeVersionsUntracked
                : RetentionPreviewReasons.VersionsUntracked, evaluation?.Deadline);
        if (native is Video video && VersionsUntracked(video, trackedItems, target.EpisodeId.HasValue))
            return PreviewCandidate.Blocked(target, target.EpisodeId.HasValue
                ? RetentionPreviewReasons.EpisodeVersionsUntracked
                : RetentionPreviewReasons.VersionsUntracked, evaluation?.Deadline);
        try
        {
            if (new FileInfo(native.Path).LinkTarget is not null)
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.SymlinkRepresentation, evaluation?.Deadline);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaPathUnavailable, evaluation?.Deadline);
        }
        if (!files.TryInspect(native.Path, out var observed))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaPathUnavailable, evaluation?.Deadline);
        if (!roots.Select(root => files.TryCanonicalize(root, out var canonical) ? canonical : null)
                .OfType<string>().Any(root => Contains(root, observed.CanonicalPath)))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.SymlinkEscape, evaluation?.Deadline, observed);
        if (!files.TryInspect(target.Path!, out var bound) || bound.PhysicalIdentity != observed.PhysicalIdentity)
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaIdentityChanged, evaluation?.Deadline, observed);
        if (keptIdentities.Contains(observed.PhysicalIdentity) || keptPaths.Contains(observed.CanonicalPath))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.VersionKept, evaluation?.Deadline, observed);
        return new(target, RetentionPreviewStates.PendingProtection, RetentionPreviewReasons.ProtectionPending,
            evaluation?.Deadline, observed) { Covered = covered, Native = native };
    }

    /// <summary>
    /// A file holding several episodes (S01E01-E02) is reclaimed only when every episode in it is due (PHASE10 Q5):
    /// no episode it covers is kept, every covered episode that has files of its own is due on its own schedule, and the
    /// file itself was watched under the watched-user policy after the retention baseline, because for an episode whose
    /// only copy is this file, this file is that episode. The longest window among the covered episodes applies.
    /// </summary>
    private async Task ApplyMultiEpisodeRuleAsync(IReadOnlyList<PreviewCandidate> inspected, RetentionPolicySnapshot? policy,
        IReadOnlyDictionary<Guid, RetentionEvaluation> evaluations, DateTime now, CancellationToken cancellationToken)
    {
        var multi = inspected.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection &&
            candidate.Covered.HasValue).ToArray();
        if (multi.Length == 0) return;
        foreach (var candidate in multi)
        {
            var (season, first, last) = candidate.Covered!.Value;
            var target = candidate.Target;
            var evaluation = target.Evaluation;
            if (policy is not { Enabled: true } || evaluation is null)
            {
                candidate.Block(RetentionPreviewReasons.MultiEpisodeNotAllDue);
                continue;
            }

            var coveredRows = await database.Episodes.AsNoTracking()
                .Where(episode => episode.EntryId == target.EntryId && episode.SeasonNumber == season &&
                    episode.EpisodeNumber >= first && episode.EpisodeNumber <= last && episode.Id != target.EpisodeId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ownRow = await database.Episodes.AsNoTracking().SingleOrDefaultAsync(
                episode => episode.Id == target.EpisodeId, cancellationToken).ConfigureAwait(false);
            if (ownRow is null || coveredRows.Any(row => RetentionOverrides.IsKept(target.Entry, row)))
            {
                candidate.Block(RetentionPreviewReasons.MultiEpisodeNotAllDue);
                continue;
            }

            var coveredIds = coveredRows.Select(row => row.Id).ToArray();
            var withFiles = (await database.EpisodeBindings.AsNoTracking()
                    .Where(binding => coveredIds.Contains(binding.EpisodeId))
                    .Select(binding => binding.EpisodeId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet();
            if (coveredRows.Where(row => withFiles.Contains(row.Id)).Any(row =>
                    !evaluations.TryGetValue(row.Id, out var own) || own.State != RetentionEvaluationStates.Scheduled ||
                    own.Deadline is not { } ownDeadline || ownDeadline > now))
            {
                candidate.Block(RetentionPreviewReasons.MultiEpisodeNotAllDue);
                continue;
            }

            var completion = FileCompletion(candidate, policy, Latest(evaluation.BaselineAt, policy.GraceStartAt ?? evaluation.BaselineAt), now);
            if (completion is not { } completedAt)
            {
                candidate.Block(RetentionPreviewReasons.MultiEpisodeNotAllDue);
                continue;
            }

            var eligibleAt = Latest(Latest(completedAt, policy.GraceStartAt ?? completedAt), evaluation.BaselineAt);
            var deadline = policy.TestWindowMinutes > 0
                ? eligibleAt.AddMinutes(policy.TestWindowMinutes)
                : eligibleAt.AddDays(coveredRows.Append(ownRow).Max(row =>
                    RetentionOverrides.WindowDays(target.Entry, row.RetentionPolicy, row.ReclaimAfterDays, policy.ReclaimAfterDays)));
            if (deadline > now) candidate.Block(RetentionPreviewReasons.MultiEpisodeNotAllDue);
        }
    }

    /// <summary>When the file itself was finished under the watched-user policy after <paramref name="floor"/>, or null.</summary>
    private DateTime? FileCompletion(PreviewCandidate candidate, RetentionPolicySnapshot policy, DateTime floor, DateTime now)
    {
        try
        {
            if (candidate.Native is not { } native) return null;
            var eligible = users.GetUsers().Where(IsActive)
                .Where(user => access.CanUseLibrary(user, candidate.Target.Entry.MediaType, candidate.Target.Entry.TargetLibraryId))
                .ToArray();
            if (eligible.Length == 0) return null;
            var completed = new Dictionary<Guid, DateTime>();
            foreach (var user in eligible)
            {
                var state = userData.GetUserData(user, native);
                if (state is null) return null;
                if (state.PlaybackPositionTicks > 0) return null;
                if (state.Played && state.LastPlayedDate is { } played)
                {
                    var utc = DateTime.SpecifyKind(played, DateTimeKind.Utc);
                    if (utc >= floor && utc <= now) completed[user.Id] = utc;
                }
            }

            return policy.WatchedUserMode switch
            {
                WatchedUserMode.AllUsers when eligible.All(user => completed.ContainsKey(user.Id)) => completed.Values.Max(),
                WatchedUserMode.SelectedUser when policy.SelectedUserId is { } selected &&
                    completed.TryGetValue(selected, out var at) => at,
                WatchedUserMode.AnyUser when completed.Count > 0 => completed.Values.Min(),
                _ => null
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private static DateTime Latest(DateTime left, DateTime right) => left > right ? left : right;

    /// <summary>
    /// Whether Jellyfin plays a version of this title that the plugin does not track: the bound item is an extra
    /// version whose main item cannot be read, or a file of the main item or of any of its versions is not bound.
    /// An episode with any other version is untracked until E7. Anything that cannot be read counts as untracked.
    /// </summary>
    /// <remarks>
    /// The versions are the union of every list Jellyfin 12 keeps: the paths in the item (<c>LocalAlternateVersions</c>),
    /// the linked children (<c>LinkedAlternateVersions</c>, each resolved by its item id; one that cannot be resolved
    /// counts as untracked), and the ids its library manager uses to build media sources
    /// (<c>GetLocalAlternateVersionIds</c>, <c>GetLinkedAlternateVersions</c>). A movie whose every file is bound passes;
    /// one untracked file blocks the whole title (RET3-R2).
    /// </remarks>
    private bool VersionsUntracked(Video video, IReadOnlySet<string> trackedPaths, bool episode)
    {
        try
        {
            if (episode && (video.LocalAlternateVersions is { Length: > 0 } || video.LinkedAlternateVersions is { Length: > 0 } ||
                    JellyfinNativeTitleSource.PrimaryVersionId(video).HasValue ||
                    library.GetLocalAlternateVersionIds(video).Any() || library.GetLinkedAlternateVersions(video).Any()))
                return true;
            var primaryId = JellyfinNativeTitleSource.PrimaryVersionId(video);
            var primary = primaryId is { } id ? library.GetItemById(id) as Video : video;
            if (primary is null || string.IsNullOrEmpty(primary.Path)) return true;
            var played = new List<string?> { primary.Path };
            played.AddRange(primary.LocalAlternateVersions ?? []);
            played.AddRange((primary.LinkedAlternateVersions ?? [])
                .Select(link => link.ItemId is { } linked ? library.GetItemById(linked)?.Path : null));
            played.AddRange(library.GetLocalAlternateVersionIds(primary).Select(version => library.GetItemById(version)?.Path));
            played.AddRange(library.GetLinkedAlternateVersions(primary).Select(version => version?.Path));
            return played.Any(path => string.IsNullOrEmpty(path) || !trackedPaths.Contains(path));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>
    /// The files each tracked title plays as merged (linked) versions of its own, with each linked version's local
    /// alternates, keyed by path, with the targets (movie or episode) whose bound item lists them. Anything that cannot be
    /// read is left out here; the per-target check then still reads the item itself. A merge whose main item the plugin
    /// does not bind is seen only through the merged item's own <c>PrimaryVersionId</c> (<see cref="VersionsUntracked"/>);
    /// once a later scan clears that, only the version enumeration of task V1 can find it (RET4-R2).
    /// </summary>
    private Dictionary<string, HashSet<Guid>> FilesMergedIntoOtherTitles(IEnumerable<PreviewTarget> targets)
    {
        var result = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            try
            {
                if (library.GetItemById(target.JellyfinItemId) is not Video video) continue;
                // Jellyfin plays each linked version together with that version's own local alternates (Video
                // GetAllItemsForMediaSources): a copy merged in from a folder that holds two files brings both (RET4-R2).
                var linkedVersions = (video.LinkedAlternateVersions ?? [])
                    .Select(link => link.ItemId is { } linked ? library.GetItemById(linked) as Video : null)
                    .Concat(library.GetLinkedAlternateVersions(video))
                    .OfType<Video>()
                    .ToArray();
                var paths = linkedVersions.SelectMany(linked => new[] { linked.Path }
                    .Concat(linked.LocalAlternateVersions ?? [])
                    .Concat(library.GetLocalAlternateVersionIds(linked).Select(version => library.GetItemById(version)?.Path)));
                foreach (var path in paths.Where(path => !string.IsNullOrEmpty(path)))
                {
                    if (!result.TryGetValue(path!, out var owners)) result[path!] = owners = [];
                    owners.Add(target.EpisodeId ?? target.EntryId);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
            }
        }

        return result;
    }

    private bool IsSeriesFavorite(PreviewTarget target)
    {
        try
        {
            if (!target.SeriesItemId.HasValue || library.GetItemById(target.SeriesItemId.Value) is not BaseItem series)
                return false;
            return users.GetUsers().Where(IsActive)
                .Where(user => access.CanUseLibrary(user, target.Entry.MediaType, target.Entry.TargetLibraryId))
                .Any(user => userData.GetUserData(user, series)?.IsFavorite == true);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsActive(User user) =>
        !user.Permissions.Any(permission => permission.Kind == PermissionKind.IsDisabled && permission.Value);

    private static bool Contains(string root, string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(normalized, path, StringComparison.Ordinal) ||
            path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record PreviewTarget(
        Guid BindingId,
        Guid EntryId,
        Guid? EpisodeId,
        Guid JellyfinItemId,
        Guid VersionGroupId,
        Guid TargetLibraryId,
        string? Path,
        string? StorageIdentity,
        Guid? SeriesItemId,
        Entry Entry,
        RetentionEvaluation? Evaluation,
        bool Kept);

    private sealed class PreviewCandidate(
        PreviewTarget target,
        string state,
        string reason,
        DateTime? deadline,
        UnixFileSnapshot? file)
    {
        public PreviewTarget Target { get; } = target;
        public string State { get; private set; } = state;
        public string Reason { get; private set; } = reason;
        public DateTime? Deadline { get; } = deadline;
        public UnixFileSnapshot? File { get; private set; } = file;
        public bool? TorrentManaged { get; private set; }
        public IReadOnlyList<TransmissionFileProtection> TorrentFiles { get; private set; } = [];

        /// <summary>Gets the season and episode range of a multi-episode file, or null for a single episode.</summary>
        public (int Season, int First, int Last)? Covered { get; init; }

        /// <summary>Gets the native item this candidate was inspected against.</summary>
        public BaseItem? Native { get; init; }

        public static PreviewCandidate Blocked(
            PreviewTarget target,
            string reason,
            DateTime? deadline,
            UnixFileSnapshot? file = null) =>
            new(target, RetentionPreviewStates.Blocked, reason, deadline, file);

        public void AttachFileForSharedCheck(UnixFileSnapshot file) => File ??= file;

        public void Block(
            string reason,
            bool? torrentManaged = null,
            IReadOnlyList<TransmissionFileProtection>? torrentFiles = null)
        {
            State = RetentionPreviewStates.Blocked;
            Reason = reason;
            TorrentManaged = torrentManaged;
            TorrentFiles = torrentFiles ?? [];
        }

        public void MarkDue(bool torrentManaged, IReadOnlyList<TransmissionFileProtection> torrentFiles)
        {
            State = RetentionPreviewStates.Due;
            Reason = RetentionPreviewReasons.Eligible;
            TorrentManaged = torrentManaged;
            TorrentFiles = torrentFiles;
        }

        public RetentionRepresentationDto ToDto() => new(
            Target.BindingId, Target.EntryId, Target.EpisodeId, Target.JellyfinItemId, Target.TargetLibraryId,
            Target.Path, File?.CanonicalPath, State, Reason, Deadline, File?.LogicalBytes, File?.HardlinkCount,
            TorrentManaged, TorrentFiles.Count == 0 ? null : TorrentFiles.Min(item => item.UploadRatio),
            TorrentFiles.Where(item => item.RatioGoal.HasValue).Select(item => item.RatioGoal!.Value)
                .DefaultIfEmpty().Max() is var ratio && ratio > 0 ? ratio : null,
            TorrentFiles.Count == 0 ? null : TorrentFiles.Min(item => item.SecondsSeeding));
    }
}

internal static class RetentionPreviewStates
{
    public const string PendingProtection = "pending";
    public const string Blocked = "blocked";
    public const string Scheduled = "scheduled";
    public const string Due = "due";
}

internal static class RetentionPreviewReasons
{
    public const string EvaluationMissing = "evaluation_missing";
    public const string NotDue = "not_due";
    public const string LibraryRootMissing = "library_root_missing";
    public const string StorageUnavailable = "storage_unavailable";
    public const string NativeBindingMissing = "native_binding_missing";
    public const string SymlinkRepresentation = "symlink_representation";
    public const string MediaPathUnavailable = "media_path_unavailable";
    public const string SymlinkEscape = "symlink_escape";
    public const string MediaIdentityChanged = "media_identity_changed";
    public const string ActiveSession = "active_session";
    public const string ActiveSessionUnknown = "active_session_unknown";
    public const string FavoriteSeries = "favorite_series";
    public const string SeedStateUnknown = "seed_state_unknown";
    public const string SeedIndexIncomplete = "seed_index_incomplete";
    public const string MultiPartUnsupported = "multi_part_unsupported";
    public const string MultiEpisodeUnsupported = "multi_episode_unsupported";
    public const string MultiEpisodeNotAllDue = "multi_episode_not_all_due";
    public const string VersionKept = "version_kept";
    public const string EpisodeVersionsUntracked = "episode_versions_untracked";
    public const string VersionsUntracked = "versions_untracked";
    public const string SeedingIncomplete = "seeding_incomplete";
    public const string SeedGoalUnbounded = "seed_goal_unbounded";
    public const string SeedGoalUnmet = "seed_goal_unmet";
    public const string SharedPathNotAllEligible = "shared_path_not_all_eligible";
    public const string Eligible = "eligible";
    public const string MediaNotWritable = "media_not_writable";
    public const string ProtectionPending = "protection_pending";
    public const string NativeBindingUnavailable = "native_binding_unavailable";
}
