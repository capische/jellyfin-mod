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
    public async Task<RetentionPreviewDto> PreviewAsync(CancellationToken cancellationToken)
    {
        await retention.EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var policy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            snapshot => snapshot.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        var evaluations = await database.RetentionEvaluations.AsNoTracking()
            .ToDictionaryAsync(evaluation => evaluation.TargetId, cancellationToken).ConfigureAwait(false);
        var targets = await LoadTargetsAsync(evaluations, cancellationToken).ConfigureAwait(false);
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
        var inspected = targets.Select(target => Inspect(target, libraryRoots, now)).ToArray();
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

        var seedCandidates = inspected.Where(candidate => candidate.State == RetentionPreviewStates.PendingProtection)
            .ToArray();
        var unresolvedSeedFiles = 0;
        if (seedCandidates.Length > 0)
        {
            var seedSnapshot = await transmission.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
                evaluations.GetValueOrDefault(row.Entry.Id)))
            .Concat(episodes.Select(row => new PreviewTarget(row.Binding.Id, row.Entry.Id, row.Episode.Id,
                row.Binding.JellyfinItemId, row.Binding.JellyfinItemId, row.Binding.TargetLibraryId, row.Binding.MediaPath,
                row.Binding.StorageIdentity, row.Binding.SeriesItemId, row.Entry,
                evaluations.GetValueOrDefault(row.Episode.Id))))
            .ToArray();
    }

    private PreviewCandidate Inspect(
        PreviewTarget target,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> libraryRoots,
        DateTime now)
    {
        var evaluation = target.Evaluation;
        if (evaluation is null)
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.EvaluationMissing, null);
        if (evaluation.State != RetentionEvaluationStates.Scheduled)
            return new(target, evaluation.State, evaluation.Reason, evaluation.Deadline, null);
        if (!evaluation.Deadline.HasValue || evaluation.Deadline.Value > now)
            return new(target, RetentionPreviewStates.Scheduled, RetentionPreviewReasons.NotDue,
                evaluation.Deadline, null);
        if (!libraryRoots.TryGetValue(target.TargetLibraryId, out var roots) || roots.Count == 0)
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.LibraryRootMissing, evaluation.Deadline);
        try
        {
            if (!storage.IsCurrent(target.Path, target.StorageIdentity, roots, out _))
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.StorageUnavailable, evaluation.Deadline);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.StorageUnavailable, evaluation.Deadline);
        }
        BaseItem? native;
        try
        {
            native = library.GetItemById(target.JellyfinItemId);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.NativeBindingUnavailable, evaluation.Deadline);
        }
        if (native is null || string.IsNullOrWhiteSpace(native.Path))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.NativeBindingMissing, evaluation.Deadline);
        try
        {
            if (new FileInfo(native.Path).LinkTarget is not null)
                return PreviewCandidate.Blocked(target, RetentionPreviewReasons.SymlinkRepresentation, evaluation.Deadline);
        }
        catch
        {
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaPathUnavailable, evaluation.Deadline);
        }
        if (!files.TryInspect(native.Path, out var observed))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaPathUnavailable, evaluation.Deadline);
        if (!roots.Select(root => files.TryCanonicalize(root, out var canonical) ? canonical : null)
                .OfType<string>().Any(root => Contains(root, observed.CanonicalPath)))
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.SymlinkEscape, evaluation.Deadline, observed);
        if (!files.TryInspect(target.Path!, out var bound) || bound.PhysicalIdentity != observed.PhysicalIdentity)
            return PreviewCandidate.Blocked(target, RetentionPreviewReasons.MediaIdentityChanged, evaluation.Deadline, observed);
        return new(target, RetentionPreviewStates.PendingProtection, RetentionPreviewReasons.ProtectionPending,
            evaluation.Deadline, observed);
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
        RetentionEvaluation? Evaluation);

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
    public const string SeedingIncomplete = "seeding_incomplete";
    public const string SeedGoalUnbounded = "seed_goal_unbounded";
    public const string SeedGoalUnmet = "seed_goal_unmet";
    public const string SharedPathNotAllEligible = "shared_path_not_all_eligible";
    public const string Eligible = "eligible";
    public const string ProtectionPending = "protection_pending";
    public const string NativeBindingUnavailable = "native_binding_unavailable";
}
