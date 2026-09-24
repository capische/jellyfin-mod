using System.Collections.Concurrent;
using System.Text.Json;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Reconciles verified native observations into the durable catalog.</summary>
public sealed class ReconciliationService(
    ModDbContext database,
    ReconciliationLibraryLock libraryLock,
    MediaStorageIdentity? mediaStorage = null,
    TimeProvider? clock = null,
    ILibraryManager? library = null)
{
    private readonly MediaStorageIdentity _mediaStorage = mediaStorage ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    /// <summary>Reconciles one library-scoped native title observation.</summary>
    public async Task<ReconciliationResult> ReconcileAsync(NativeTitleSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var lease = await libraryLock.AcquireAsync(snapshot.TargetLibraryId, cancellationToken).ConfigureAwait(false);
        return await ReconcileAsync(snapshot, true, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ReconciliationResult> ReconcileUnderLeaseAsync(NativeTitleSnapshot snapshot,
        CancellationToken cancellationToken) => ReconcileAsync(snapshot, true, cancellationToken);

    /// <summary>
    /// Refreshes recorded storage identities in one library whose mount only changed device number or
    /// source (P2.R6), so a reboot does not block absence confirmation and retention indefinitely. The
    /// caller holds the library lease.
    /// </summary>
    internal async Task<int> RebaselineStorageUnderLeaseAsync(
        Guid libraryId,
        IReadOnlyList<string> libraryLocations,
        CancellationToken cancellationToken)
    {
        if (!LibraryStorageProbe.IsAvailable(libraryLocations, out _)) return 0;
        var mounts = _mediaStorage.ReadMountTable();
        var changed = 0;
        foreach (var binding in await database.EntryBindings.Where(binding => binding.TargetLibraryId == libraryId)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!MediaStorageIdentity.IsWithin(binding.MediaPath, libraryLocations)) continue;
            if (_mediaStorage.Rebaseline(binding.MediaPath, binding.StorageIdentity, mounts) is not { } current) continue;
            binding.StorageIdentity = current;
            changed++;
        }

        foreach (var binding in await database.EpisodeBindings.Where(binding => binding.TargetLibraryId == libraryId)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!MediaStorageIdentity.IsWithin(binding.MediaPath, libraryLocations)) continue;
            if (_mediaStorage.Rebaseline(binding.MediaPath, binding.StorageIdentity, mounts) is not { } current) continue;
            binding.StorageIdentity = current;
            changed++;
        }

        if (changed > 0) await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Re-baselines every available library, taking each library lease in turn.</summary>
    internal async Task<int> RebaselineStorageAsync(
        IEnumerable<NativeLibraryStorage> libraries,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var storage in libraries)
        {
            await using var lease = await libraryLock.AcquireAsync(storage.LibraryId, cancellationToken).ConfigureAwait(false);
            changed += await RebaselineStorageUnderLeaseAsync(storage.LibraryId, storage.Locations, cancellationToken)
                .ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// Clears bindings absent from a complete observation while the caller holds the library lease. Titles
    /// that could not be observed, matched or verified are excluded one at a time (P2.R6); they keep their
    /// bindings and state and the rest of the library is still confirmed.
    /// </summary>
    internal async Task<AbsenceConfirmationResult> ConfirmAbsenceUnderLeaseAsync(
        ConfirmedLibrarySnapshot observation,
        CancellationToken cancellationToken)
    {
        await RebaselineStorageUnderLeaseAsync(observation.LibraryId, observation.LibraryLocations, cancellationToken)
            .ConfigureAwait(false);
        var entries = await database.Entries.Where(entry => entry.TargetLibraryId == observation.LibraryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var entryIds = entries.Select(entry => entry.Id).ToHashSet();
        var bindings = await database.EntryBindings.Where(binding => entryIds.Contains(binding.EntryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var episodes = await database.Episodes.Where(episode => entryIds.Contains(episode.EntryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var episodeIds = episodes.Select(episode => episode.Id).ToHashSet();
        var episodeBindings = await database.EpisodeBindings.Where(binding => episodeIds.Contains(binding.EpisodeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var episodeOwners = episodes.ToDictionary(episode => episode.Id, episode => episode.EntryId);

        var excluded = entries.Where(entry => observation.ProtectedEntryIds.Contains(entry.Id) ||
                observation.ProtectedTmdbIds.Contains(entry.TmdbId) ||
                entry.JellyfinItemId is { } nativeId && observation.ProtectedNativeIds.Contains(nativeId))
            .Select(entry => entry.Id).ToHashSet();
        excluded.UnionWith(bindings.Where(binding => observation.ProtectedNativeIds.Contains(binding.JellyfinItemId))
            .Select(binding => binding.EntryId));
        excluded.UnionWith(episodeBindings.Where(binding => observation.ProtectedNativeIds.Contains(binding.SeriesItemId) ||
                observation.ProtectedNativeIds.Contains(binding.JellyfinItemId))
            .Select(binding => episodeOwners[binding.EpisodeId]));

        var unverified = new List<ExcludedTitle>();
        foreach (var binding in bindings.Where(binding => !excluded.Contains(binding.EntryId) &&
                     !observation.TitleIds.Contains(binding.JellyfinItemId)))
        {
            if (excluded.Contains(binding.EntryId) ||
                IsProvenAbsent(binding.MediaPath, binding.StorageIdentity, observation.LibraryLocations, out var detail))
                continue;
            excluded.Add(binding.EntryId);
            unverified.Add(new(binding.EntryId, entries.Single(entry => entry.Id == binding.EntryId).Title, detail));
        }

        foreach (var binding in episodeBindings.Where(binding => !observation.EpisodeIds.Contains(binding.JellyfinItemId)))
        {
            var ownerId = episodeOwners[binding.EpisodeId];
            if (excluded.Contains(ownerId) ||
                IsProvenAbsent(binding.MediaPath, binding.StorageIdentity, observation.LibraryLocations, out var detail))
                continue;
            excluded.Add(ownerId);
            unverified.Add(new(ownerId, entries.Single(entry => entry.Id == ownerId).Title, detail));
        }

        entries = entries.Where(entry => !excluded.Contains(entry.Id)).ToList();
        bindings = bindings.Where(binding => !excluded.Contains(binding.EntryId)).ToList();
        episodes = episodes.Where(episode => !excluded.Contains(episode.EntryId)).ToList();
        episodeBindings = episodeBindings.Where(binding => !excluded.Contains(episodeOwners[binding.EpisodeId])).ToList();
        var absentBindings = bindings.Where(binding =>
            !observation.TitleIds.Contains(binding.JellyfinItemId)).ToArray();
        var absentEpisodeBindings = episodeBindings.Where(binding =>
            !observation.EpisodeIds.Contains(binding.JellyfinItemId)).ToArray();

        database.EntryBindings.RemoveRange(absentBindings);
        database.EpisodeBindings.RemoveRange(absentEpisodeBindings);

        // A movie or episode that lost its last representation must not keep a schedule or completion
        // evidence that a later re-acquired file would inherit.
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var entryId in absentBindings.Select(binding => binding.EntryId).Distinct())
        {
            var entry = entries.Single(candidate => candidate.Id == entryId);
            if (entry.MediaType == "movie" && bindings.All(binding => binding.EntryId != entryId || absentBindings.Contains(binding)))
            {
                await RetentionTargetReset.ResetAsync(database, entryId, null, now, cancellationToken).ConfigureAwait(false);
                RecordRetentionReset(entryId, null, absentBindings.Where(binding => binding.EntryId == entryId).Select(binding => binding.MediaPath));
            }
        }

        foreach (var episodeId in absentEpisodeBindings.Select(binding => binding.EpisodeId).Distinct())
        {
            if (episodeBindings.All(binding => binding.EpisodeId != episodeId || absentEpisodeBindings.Contains(binding)))
            {
                var ownerEntryId = episodes.Single(episode => episode.Id == episodeId).EntryId;
                await RetentionTargetReset.ResetAsync(database, ownerEntryId, episodeId, now, cancellationToken)
                    .ConfigureAwait(false);
                RecordRetentionReset(ownerEntryId, episodeId,
                    absentEpisodeBindings.Where(binding => binding.EpisodeId == episodeId).Select(binding => binding.MediaPath));
            }
        }

        var missingItems = 0;
        foreach (var entry in entries)
        {
            // Only a title can be opened; a further media source of one names its owner instead (P6.M6).
            var playableBindings = bindings.Where(binding => binding.EntryId == entry.Id &&
                    observation.PlayableTitleIds.Contains(binding.JellyfinItemId))
                .OrderBy(binding => binding.OwnerItemId is null ? 0 : 1)
                .ThenBy(binding => binding.JellyfinItemId == binding.VersionGroupId ? 0 : 1)
                .ThenBy(binding => binding.VersionGroupId).ThenBy(binding => binding.JellyfinItemId).ToArray();
            var selected = playableBindings.FirstOrDefault(binding =>
                    (binding.OwnerItemId ?? binding.JellyfinItemId) == entry.JellyfinItemId) ??
                playableBindings.FirstOrDefault();
            if (selected is not null)
            {
                entry.JellyfinItemId = selected.OwnerItemId ?? selected.JellyfinItemId;
                entry.State = FileState.OnDisk;
            }
            else
            {
                // Retention already recorded this loss as a reclamation; it is not an external removal.
                var becameMissing = entry.State != FileState.Reclaimed &&
                    (entry.State == FileState.OnDisk || entry.JellyfinItemId.HasValue);
                entry.JellyfinItemId = null;
                if (entry.State == FileState.OnDisk) entry.State = FileState.None;
                if (becameMissing)
                {
                    missingItems++;
                    database.History.Add(new HistoryRecord
                    {
                        EntryId = entry.Id,
                        EventType = "media_missing",
                        Summary = "Playable media is no longer present in Jellyfin"
                    });
                }
            }

            foreach (var episode in episodes.Where(episode => episode.EntryId == entry.Id))
            {
                var playableEpisodeBindings = episodeBindings.Where(binding => binding.EpisodeId == episode.Id &&
                        observation.PlayableEpisodeIds.Contains(binding.JellyfinItemId))
                    .OrderBy(binding => binding.JellyfinItemId).ToArray();
                var selectedEpisode = playableEpisodeBindings
                    .FirstOrDefault(binding => binding.JellyfinItemId == episode.JellyfinItemId) ??
                    playableEpisodeBindings.FirstOrDefault();
                // An episode covered by another episode's multi-episode file has no binding of its own (P2.R9).
                if (selectedEpisode is null && episode.JellyfinItemId is { } coveringId &&
                    observation.PlayableEpisodeIds.Contains(coveringId) &&
                    episodeBindings.All(binding => binding.EpisodeId != episode.Id))
                    continue;
                var becameMissing = selectedEpisode is null &&
                    (episode.State == FileState.OnDisk || episode.JellyfinItemId.HasValue);
                episode.JellyfinItemId = selectedEpisode?.JellyfinItemId;
                if (selectedEpisode is not null) episode.State = FileState.OnDisk;
                else if (episode.State == FileState.OnDisk) episode.State = FileState.None;
                if (becameMissing)
                {
                    missingItems++;
                    database.History.Add(new HistoryRecord
                    {
                        EntryId = entry.Id,
                        EventType = "episode_media_missing",
                        Summary = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} is no longer present in Jellyfin",
                        Data = JsonSerializer.Serialize(new { episodeId = episode.Id, tmdbId = episode.TmdbId,
                            episode.SeasonNumber, episode.EpisodeNumber })
                    });
                }
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(missingItems, true, null, excluded.Count, unverified);
    }

    /// <summary>
    /// Records that a target's retention clock and evidence were reset because Jellyfin no longer reported its last
    /// file (Jellyfin 12 analysis C5): the reset is never silent, and History names the files that were not observed.
    /// </summary>
    private void RecordRetentionReset(Guid entryId, Guid? episodeId, IEnumerable<string?> paths)
    {
        var files = paths.Where(path => !string.IsNullOrEmpty(path)).Select(path => Path.GetFileName(path!)).ToArray();
        database.History.Add(new HistoryRecord
        {
            EntryId = entryId,
            EventType = "retention_reset",
            Summary = "Retention restarted: Jellyfin no longer reports " + (files.Length == 1 ? files[0] : $"{files.Length} files"),
            Data = JsonSerializer.Serialize(new { episodeId, reason = "binding_not_observed", files })
        });
    }

    private bool IsProvenAbsent(string? path, string? identity, IReadOnlyList<string> locations, out string detail)
    {
        // A binding recorded before identities existed, or from a library location that was since removed,
        // has no mount to compare; prove the file itself is gone instead.
        if (string.IsNullOrWhiteSpace(identity) || !MediaStorageIdentity.IsWithin(path, locations))
            return MediaStorageIdentity.IsProvablyAbsent(path, out detail);
        return _mediaStorage.IsCurrent(path, identity, locations, out detail);
    }

    private async Task<ReconciliationResult> ReconcileAsync(
        NativeTitleSnapshot snapshot,
        bool retryUniqueConflict,
        CancellationToken cancellationToken)
    {
        if (snapshot.MediaType is not ("movie" or "series"))
            throw new ArgumentException("Media type must be movie or series.", nameof(snapshot));
        if (snapshot.TargetLibraryId == Guid.Empty)
            throw new ArgumentException("A target library is required.", nameof(snapshot));
        if (snapshot.TmdbId is not > 0)
            return new(ReconciliationOutcome.Unmatched, null, 0, "The native item has no usable TMDB identity.");
        if (snapshot.Representations.Count == 0)
            throw new ArgumentException("At least one native representation is required.", nameof(snapshot));

        var representationIds = snapshot.Representations.Select(item => item.JellyfinItemId).ToHashSet();
        if (representationIds.Contains(Guid.Empty) || representationIds.Count != snapshot.Representations.Count)
            throw new ArgumentException("Native representation identities must be non-empty and unique.", nameof(snapshot));
        if (snapshot.Representations.Any(representation => representation.TargetLibraryId != snapshot.TargetLibraryId ||
            representation.VersionGroupId == Guid.Empty ||
            representation.VersionGroupId.HasValue && !representationIds.Contains(representation.VersionGroupId.Value)))
            throw new ArgumentException("Every native representation must carry the observed library provenance.", nameof(snapshot));
        // A further media source must name an observed title that is itself a title, never another media source.
        var titleIds = snapshot.Representations.Where(representation => representation.OwnerItemId is null)
            .Select(representation => representation.JellyfinItemId).ToHashSet();
        if (snapshot.Representations.Any(representation => representation.OwnerItemId is { } ownerId &&
            (ownerId == representation.JellyfinItemId || !titleIds.Contains(ownerId))))
            throw new ArgumentException("Every further media source must belong to an observed native title.", nameof(snapshot));
        if (snapshot.MediaType == "movie" && snapshot.Episodes.Count > 0)
            throw new ArgumentException("Movie observations cannot contain episodes.", nameof(snapshot));
        if (snapshot.Episodes.Any(episode => episode.JellyfinItemId == Guid.Empty ||
            !representationIds.Contains(episode.SeriesItemId)))
            throw new ArgumentException("Every episode must have an identity and belong to an observed series representation.", nameof(snapshot));
        if (snapshot.Episodes.Select(episode => episode.JellyfinItemId).Distinct().Count() != snapshot.Episodes.Count)
            throw new ArgumentException("Native episode identities must be unique within an observation.", nameof(snapshot));

        // One disagreeing episode is skipped with a diagnostic; the rest of the series still reconciles (P2.R9).
        var skipped = new Dictionary<Guid, string>();
        var rebindable = new Dictionary<Guid, (Guid EpisodeId, NativeEpisodeSnapshot Observation)>();
        foreach (var (observation, detail) in FindObservationConflicts(snapshot.Episodes))
            skipped.TryAdd(observation.JellyfinItemId, detail);

        await RehomeOrphanedEntryAsync(snapshot, representationIds, cancellationToken).ConfigureAwait(false);

        var conflictingEntry = await (from binding in database.EntryBindings.AsNoTracking()
            join boundEntry in database.Entries.AsNoTracking() on binding.EntryId equals boundEntry.Id
            where representationIds.Contains(binding.JellyfinItemId) &&
                  (boundEntry.MediaType != snapshot.MediaType || boundEntry.TmdbId != snapshot.TmdbId ||
                   boundEntry.TargetLibraryId != snapshot.TargetLibraryId)
            select boundEntry).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        conflictingEntry ??= await database.Entries.AsNoTracking().FirstOrDefaultAsync(entry =>
            entry.JellyfinItemId != null && representationIds.Contains(entry.JellyfinItemId.Value) &&
            (entry.MediaType != snapshot.MediaType || entry.TmdbId != snapshot.TmdbId || entry.TargetLibraryId != snapshot.TargetLibraryId),
            cancellationToken).ConfigureAwait(false);
        if (conflictingEntry is not null)
        {
            // Two libraries that share a path both list the same native item. The library whose name sorts
            // first owns the binding; the other reports the overlap instead of a conflict (P2.R7).
            if (conflictingEntry.MediaType == snapshot.MediaType && conflictingEntry.TmdbId == snapshot.TmdbId &&
                conflictingEntry.TargetLibraryId is { } ownerLibrary && IsLiveLibrary(ownerLibrary))
                return new(ReconciliationOutcome.Overlap, conflictingEntry.Id, 0,
                    "This title is owned by another library that shares its path.");
            return new(ReconciliationOutcome.Conflict, conflictingEntry.Id, 0,
                "A native representation is already bound to a different catalog identity.");
        }

        var episodeIds = snapshot.Episodes.Select(episode => episode.JellyfinItemId).ToHashSet();
        var boundEpisodes = await (from binding in database.EpisodeBindings.AsNoTracking()
            join episode in database.Episodes.AsNoTracking() on binding.EpisodeId equals episode.Id
            join owner in database.Entries.AsNoTracking() on episode.EntryId equals owner.Id
            where episodeIds.Contains(binding.JellyfinItemId)
            select new ExistingEpisodeBinding(binding.JellyfinItemId, binding.SeriesItemId, binding.TargetLibraryId,
                episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, owner.Id, owner.MediaType,
                owner.TmdbId, owner.TargetLibraryId, episode.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var canonicalEpisodes = await (from episode in database.Episodes.AsNoTracking()
            join owner in database.Entries.AsNoTracking() on episode.EntryId equals owner.Id
            where episode.JellyfinItemId != null && episodeIds.Contains(episode.JellyfinItemId.Value)
            select new ExistingEpisodeBinding(episode.JellyfinItemId.GetValueOrDefault(), Guid.Empty, Guid.Empty,
                episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, owner.Id, owner.MediaType,
                owner.TmdbId, owner.TargetLibraryId, episode.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A position-identity episode (P10.E1) whose native episode gained a TMDB id adopts it instead of conflicting,
        // unless another episode of the same entry already holds that TMDB id.
        var observedTmdbIds = snapshot.Episodes.Where(episode => episode.TmdbId is > 0)
            .Select(episode => episode.TmdbId!.Value).ToHashSet();
        var positionOwners = boundEpisodes.Concat(canonicalEpisodes).Where(binding => binding.EpisodeTmdbId == 0)
            .Select(binding => binding.EntryId).ToHashSet();
        var heldTmdbIds = positionOwners.Count == 0 || observedTmdbIds.Count == 0
            ? []
            : (await database.Episodes.AsNoTracking()
                .Where(episode => positionOwners.Contains(episode.EntryId) && observedTmdbIds.Contains(episode.TmdbId))
                .Select(episode => new { episode.EntryId, episode.TmdbId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(held => (held.EntryId, held.TmdbId)).ToHashSet();
        foreach (var conflict in FindDurableEpisodeConflicts(snapshot, boundEpisodes.Concat(canonicalEpisodes), heldTmdbIds))
        {
            skipped.TryAdd(conflict.Observation.JellyfinItemId, conflict.Detail);
            if (conflict.RebindableEpisodeId is { } trackedId)
                rebindable.TryAdd(conflict.Observation.JellyfinItemId, (trackedId, conflict.Observation));
        }

        var entry = await database.Entries.SingleOrDefaultAsync(candidate => candidate.MediaType == snapshot.MediaType &&
            candidate.TmdbId == snapshot.TmdbId && candidate.TargetLibraryId == snapshot.TargetLibraryId,
            cancellationToken).ConfigureAwait(false);
        var created = entry is null;
        if (entry is null)
        {
            entry = new Entry
            {
                MediaType = snapshot.MediaType,
                TmdbId = snapshot.TmdbId.GetValueOrDefault(),
                TargetLibraryId = snapshot.TargetLibraryId,
                Title = snapshot.Title,
                Year = snapshot.Year,
                ImdbId = snapshot.ImdbId,
                Overview = snapshot.Overview,
                PosterPath = snapshot.PosterPath,
                MetadataJson = snapshot.MetadataJson,
                Monitored = false
            };
            // A backfilled title was added when Jellyfin first saw it, not when the plugin did (P3.T14).
            if (snapshot.DateCreated is { } nativeCreated && nativeCreated > DateTime.MinValue)
                entry.AddedAt = DateTime.SpecifyKind(nativeCreated, DateTimeKind.Utc);
            database.Entries.Add(entry);
        }

        // Remember what Jellyfin's own visibility rules saw, for when the file is gone (P3.T15).
        if (snapshot.NativeTags is { } nativeTags)
        {
            var tagsJson = JsonSerializer.Serialize(nativeTags.Order(StringComparer.Ordinal).ToArray());
            if (entry.NativeRating != snapshot.NativeRating || entry.NativeTagsJson != tagsJson)
            {
                entry.NativeRating = snapshot.NativeRating;
                entry.NativeTagsJson = tagsJson;
            }
        }

        var entryBindings = created
            ? []
            : await database.EntryBindings.Where(binding => binding.EntryId == entry.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var knownRepresentations = entryBindings.ToDictionary(binding => binding.JellyfinItemId);
        var entryBindingChanges = 0;
        foreach (var representation in snapshot.Representations)
        {
            if (knownRepresentations.TryGetValue(representation.JellyfinItemId, out var binding))
            {
                var versionGroupId = representation.VersionGroupId ?? representation.JellyfinItemId;
                var structuralChange = binding.VersionGroupId != versionGroupId ||
                    binding.TargetLibraryId != representation.TargetLibraryId ||
                    binding.OwnerItemId != representation.OwnerItemId;
                if (structuralChange || binding.MediaPath != representation.MediaPath ||
                    binding.StorageIdentity != representation.StorageIdentity)
                {
                    binding.VersionGroupId = versionGroupId;
                    binding.TargetLibraryId = representation.TargetLibraryId;
                    binding.OwnerItemId = representation.OwnerItemId;
                    binding.MediaPath = representation.MediaPath;
                    binding.StorageIdentity = representation.StorageIdentity;
                    if (structuralChange) entryBindingChanges++;
                }

                continue;
            }

            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = entry.Id,
                JellyfinItemId = representation.JellyfinItemId,
                TargetLibraryId = representation.TargetLibraryId,
                VersionGroupId = representation.VersionGroupId ?? representation.JellyfinItemId,
                OwnerItemId = representation.OwnerItemId,
                MediaPath = representation.MediaPath,
                StorageIdentity = representation.StorageIdentity
            });
            entryBindingChanges++;
        }

        // A further media source has no existence of its own: when the title that owned it is gone from a
        // complete observation of this entry, the row is stale rather than absent media. Its file, if it is
        // still there, is named by one of the observed representations above (P6.M6).
        foreach (var stale in entryBindings.Where(binding => binding.OwnerItemId is { } ownerId &&
                     !representationIds.Contains(ownerId) && !representationIds.Contains(binding.JellyfinItemId)))
        {
            database.EntryBindings.Remove(stale);
            entryBindingChanges++;
        }

        var playableRepresentations = snapshot.MediaType == "series"
            ? snapshot.Representations.Where(representation => snapshot.HasPlayableEpisode(representation.JellyfinItemId))
                .ToArray()
            : snapshot.Representations.Where(representation => representation.IsPlayable).ToArray();
        var selected = SelectRepresentation(entry.JellyfinItemId, playableRepresentations);

        var entryChanged = false;
        var episodeChanges = 0;
        if (selected is not null)
        {
            entryChanged = entry.JellyfinItemId != selected.NavigableItemId || entry.State != FileState.OnDisk;
            entry.JellyfinItemId = selected.NavigableItemId;
            entry.State = FileState.OnDisk;
        }

        if (snapshot.MediaType == "series")
        {
            var episodes = created
                ? []
                : await database.Episodes.Where(episode => episode.EntryId == entry.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            var trackedEpisodeIds = episodes.Select(episode => episode.Id).ToHashSet();
            var episodeBindings = created
                ? []
                : await database.EpisodeBindings.Where(binding => trackedEpisodeIds.Contains(binding.EpisodeId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (observation, trackedId) in FindEpisodeConflicts(episodes, snapshot.Episodes))
            {
                skipped.TryAdd(observation.JellyfinItemId,
                    "A native episode is already bound to a different provider identity.");
                rebindable.TryAdd(observation.JellyfinItemId, (trackedId, observation));
            }

            var usable = snapshot.Episodes.Where(observation => !skipped.ContainsKey(observation.JellyfinItemId)).ToArray();
            var evaluatedEpisodes = created
                ? []
                : (await database.RetentionEvaluations.AsNoTracking()
                    .Where(evaluation => evaluation.EntryId == entry.Id && evaluation.EpisodeId != null)
                    .Select(evaluation => evaluation.TargetId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet();
            episodeChanges = ReconcileEpisodes(entry.Id, snapshot.TargetLibraryId, episodes, episodeBindings,
                usable, created, evaluatedEpisodes);
            await RecordEpisodeConflictsAsync(entry.Id, usable, rebindable, skipped, cancellationToken)
                .ConfigureAwait(false);
        }

        if (created)
        {
            database.History.Add(new HistoryRecord
            {
                EntryId = entry.Id,
                EventType = "backfilled",
                Summary = "Discovered in Jellyfin library"
            });
        }
        else if (entryChanged)
        {
            database.History.Add(new HistoryRecord
            {
                EntryId = entry.Id,
                EventType = "media_available",
                Summary = "Matched playable media in Jellyfin",
                Data = JsonSerializer.Serialize(new { jellyfinItemId = entry.JellyfinItemId })
            });
        }
        else if (entryBindingChanges > 0)
        {
            database.History.Add(new HistoryRecord
            {
                EntryId = entry.Id,
                EventType = "representations_reconciled",
                Summary = "Recorded additional native media representations",
                Data = JsonSerializer.Serialize(new { addedRepresentations = entryBindingChanges })
            });
        }
        else if (episodeChanges > 0)
        {
            database.History.Add(new HistoryRecord
            {
                EntryId = entry.Id,
                EventType = "episodes_reconciled",
                Summary = "Updated native episode bindings",
                Data = JsonSerializer.Serialize(new { changedEpisodes = episodeChanges })
            });
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 } && retryUniqueConflict)
        {
            database.ChangeTracker.Clear();
            return await ReconcileAsync(snapshot, false, cancellationToken).ConfigureAwait(false);
        }

        return new(created ? ReconciliationOutcome.Created : entryChanged || entryBindingChanges > 0 || episodeChanges > 0
            ? ReconciliationOutcome.Updated : ReconciliationOutcome.Unchanged, entry.Id, episodeChanges, null)
        {
            EpisodeDiagnostics = snapshot.Episodes.Where(observation => skipped.ContainsKey(observation.JellyfinItemId))
                .Select(observation => $"S{observation.SeasonNumber:00}E{observation.EpisodeNumber:00}: " +
                    skipped[observation.JellyfinItemId]).ToArray()
        };
    }

    /// <summary>
    /// Keeps one durable row per bound native episode whose provider identity disagrees with its tracked
    /// episode, so an administrator can rebind or keep it (P2.R9). A kept identity stops being reported
    /// until Jellyfin reports yet another identity; an agreeing observation resolves the row.
    /// </summary>
    private async Task RecordEpisodeConflictsAsync(
        Guid entryId,
        IReadOnlyList<NativeEpisodeSnapshot> usable,
        IReadOnlyDictionary<Guid, (Guid EpisodeId, NativeEpisodeSnapshot Observation)> rebindable,
        IDictionary<Guid, string> skipped,
        CancellationToken cancellationToken)
    {
        var nativeIds = usable.Select(observation => observation.JellyfinItemId).Concat(rebindable.Keys).ToHashSet();
        var existing = await database.EpisodeConflicts.Where(conflict => nativeIds.Contains(conflict.JellyfinItemId))
            .ToDictionaryAsync(conflict => conflict.JellyfinItemId, cancellationToken).ConfigureAwait(false);
        foreach (var observation in usable)
            if (existing.Remove(observation.JellyfinItemId, out var resolved))
                database.EpisodeConflicts.Remove(resolved);
        foreach (var (nativeId, (trackedId, observation)) in rebindable)
        {
            var observedTmdbId = observation.TmdbId.GetValueOrDefault();
            if (existing.TryGetValue(nativeId, out var row))
            {
                if (row.State == EpisodeConflictStates.Kept && row.ObservedTmdbId == observedTmdbId &&
                    row.EpisodeId == trackedId)
                {
                    // The administrator kept this identity; the binding stays and nothing is reported.
                    skipped[nativeId] = "kept";
                    continue;
                }

                row.EntryId = entryId;
                row.EpisodeId = trackedId;
                row.ObservedTmdbId = observedTmdbId;
                row.ObservedSeasonNumber = observation.SeasonNumber;
                row.ObservedEpisodeNumber = observation.EpisodeNumber;
                row.State = EpisodeConflictStates.Open;
                continue;
            }

            database.EpisodeConflicts.Add(new EpisodeConflict
            {
                EntryId = entryId,
                EpisodeId = trackedId,
                JellyfinItemId = nativeId,
                ObservedTmdbId = observedTmdbId,
                ObservedSeasonNumber = observation.SeasonNumber,
                ObservedEpisodeNumber = observation.EpisodeNumber,
                DetectedAt = _clock.GetUtcNow().UtcDateTime
            });
        }

        foreach (var kept in skipped.Where(pair => pair.Value == "kept").Select(pair => pair.Key).ToArray())
            skipped.Remove(kept);
    }

    /// <summary>
    /// Moves an entry whose library was re-created or renamed to the library where its native media now
    /// appears (P2.R7), keeping Keep, monitoring, retention state and history. A file-less entry for the
    /// same title in the new library is merged into it.
    /// </summary>
    private async Task RehomeOrphanedEntryAsync(
        NativeTitleSnapshot snapshot,
        IReadOnlySet<Guid> representationIds,
        CancellationToken cancellationToken)
    {
        var episodeIds = snapshot.Episodes.Select(episode => episode.JellyfinItemId).ToHashSet();
        var candidates = await database.Entries.AsNoTracking().Where(entry =>
                entry.MediaType == snapshot.MediaType && entry.TmdbId == snapshot.TmdbId &&
                entry.TargetLibraryId != snapshot.TargetLibraryId &&
                (entry.JellyfinItemId != null && representationIds.Contains(entry.JellyfinItemId.Value) ||
                 database.EntryBindings.Any(binding => binding.EntryId == entry.Id &&
                     representationIds.Contains(binding.JellyfinItemId)) ||
                 database.Episodes.Any(episode => episode.EntryId == entry.Id &&
                     database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id &&
                         episodeIds.Contains(binding.JellyfinItemId)))))
            .Select(entry => new { entry.Id, entry.TargetLibraryId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var orphan = candidates.FirstOrDefault(candidate =>
            candidate.TargetLibraryId is not { } libraryId || !IsLiveLibrary(libraryId));
        if (orphan is null) return;

        await using var oldLease = orphan.TargetLibraryId is { } oldLibrary && oldLibrary != snapshot.TargetLibraryId
            ? await libraryLock.AcquireAsync(oldLibrary, cancellationToken).ConfigureAwait(false)
            : null;
        var entry = await database.Entries.SingleOrDefaultAsync(candidate => candidate.Id == orphan.Id, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null || entry.TargetLibraryId == snapshot.TargetLibraryId) return;
        var previousLibrary = entry.TargetLibraryId;

        var existing = await database.Entries.SingleOrDefaultAsync(candidate => candidate.MediaType == snapshot.MediaType &&
            candidate.TmdbId == snapshot.TmdbId && candidate.TargetLibraryId == snapshot.TargetLibraryId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Only a file-less wanted entry can be merged; one with its own media stays a conflict.
            if (await database.EntryBindings.AnyAsync(binding => binding.EntryId == existing.Id, cancellationToken)
                    .ConfigureAwait(false) ||
                await database.Episodes.AnyAsync(episode => episode.EntryId == existing.Id &&
                    database.EpisodeBindings.Any(binding => binding.EpisodeId == episode.Id), cancellationToken)
                    .ConfigureAwait(false))
                return;
            if (existing.RetentionPolicy == RetentionPolicy.Never) entry.RetentionPolicy = RetentionPolicy.Never;
            entry.Monitored |= existing.Monitored;
            var trackedEpisodes = await database.Episodes.Where(episode => episode.EntryId == entry.Id)
                .Select(episode => episode.TmdbId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var wanted in await database.Episodes.Where(episode => episode.EntryId == existing.Id)
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (trackedEpisodes.Contains(wanted.TmdbId))
                    database.Episodes.Remove(wanted);
                else
                    wanted.EntryId = entry.Id;
            }

            foreach (var record in await database.History.Where(history => history.EntryId == existing.Id)
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
                record.EntryId = entry.Id;
            // The wanted entry's rows move first so its removal cannot cascade them away.
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            database.Entries.Remove(existing);
        }

        entry.TargetLibraryId = snapshot.TargetLibraryId;
        foreach (var binding in await database.EntryBindings.Where(binding => binding.EntryId == entry.Id)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
            binding.TargetLibraryId = snapshot.TargetLibraryId;
        foreach (var binding in await (from episodeBinding in database.EpisodeBindings
                     join episode in database.Episodes on episodeBinding.EpisodeId equals episode.Id
                     where episode.EntryId == entry.Id
                     select episodeBinding).ToListAsync(cancellationToken).ConfigureAwait(false))
            binding.TargetLibraryId = snapshot.TargetLibraryId;
        database.History.Add(new HistoryRecord
        {
            EntryId = entry.Id,
            EventType = "library_moved",
            Summary = "Moved to the library where its media now appears",
            Data = JsonSerializer.Serialize(new { fromLibraryId = previousLibrary, toLibraryId = snapshot.TargetLibraryId,
                mergedEntryId = existing?.Id })
        });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        database.ChangeTracker.Clear();
    }

    private HashSet<Guid>? _liveLibraries;

    /// <summary>Checks the configured movie and TV libraries, read only when an ownership question arises.</summary>
    private bool IsLiveLibrary(Guid libraryId)
    {
        if (library is null) return true;
        _liveLibraries ??= library.GetVirtualFolders()
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
        return _liveLibraries.Contains(libraryId);
    }

    /// <summary>
    /// Chooses the native item the entry points at. Only a title can be opened, so a further media source of one
    /// (P6.M6) is represented here by the title that owns it, never by its own identity.
    /// </summary>
    private static NativeRepresentation? SelectRepresentation(Guid? currentId, IReadOnlyList<NativeRepresentation> candidates)
    {
        var titles = candidates.Where(candidate => candidate.OwnerItemId is null).ToArray();
        return titles.FirstOrDefault(candidate => candidate.JellyfinItemId == currentId) ??
            titles.OrderBy(candidate => candidate.JellyfinItemId == candidate.VersionGroupId ? 0 : 1)
                .ThenBy(candidate => candidate.VersionGroupId ?? candidate.JellyfinItemId)
                .ThenBy(candidate => candidate.JellyfinItemId).FirstOrDefault() ??
            // A title whose only playable media is a further media source still opens through that title.
            candidates.FirstOrDefault(candidate => candidate.NavigableItemId == currentId) ??
            candidates.OrderBy(candidate => candidate.VersionGroupId ?? candidate.JellyfinItemId)
                .ThenBy(candidate => candidate.JellyfinItemId).FirstOrDefault();
    }

    private static IEnumerable<(NativeEpisodeSnapshot Observation, string Detail)> FindObservationConflicts(
        IReadOnlyList<NativeEpisodeSnapshot> observations)
    {
        foreach (var group in observations.Where(observation => observation.TmdbId is > 0)
                     .GroupBy(observation => (observation.SeasonNumber, observation.EpisodeNumber))
                     .Where(group => group.Select(observation => observation.TmdbId).Distinct().Skip(1).Any()))
            foreach (var observation in group)
                yield return (observation,
                    "Explicit episode provider identities conflict at the same season and episode position.");
        foreach (var group in observations.Where(observation => observation.TmdbId is > 0)
                     .GroupBy(observation => observation.TmdbId)
                     .Where(group => group.Select(observation => (observation.SeasonNumber, observation.EpisodeNumber))
                         .Distinct().Skip(1).Any()))
            foreach (var observation in group)
                yield return (observation,
                    "Copies of one episode provider identity disagree about its season and episode position.");
    }

    private static IEnumerable<DurableEpisodeConflict> FindDurableEpisodeConflicts(
        NativeTitleSnapshot snapshot,
        IEnumerable<ExistingEpisodeBinding> bindings,
        IReadOnlySet<(Guid EntryId, int TmdbId)> heldTmdbIds)
    {
        var byNativeId = bindings.GroupBy(binding => binding.JellyfinItemId).ToDictionary(group => group.Key, group => group.First());
        foreach (var observation in snapshot.Episodes)
        {
            if (!byNativeId.TryGetValue(observation.JellyfinItemId, out var binding))
                continue;
            if (binding.MediaType != snapshot.MediaType || binding.EntryTmdbId != snapshot.TmdbId ||
                binding.EntryLibraryId != snapshot.TargetLibraryId ||
                binding.BindingLibraryId != Guid.Empty && binding.BindingLibraryId != snapshot.TargetLibraryId ||
                binding.SeriesItemId != Guid.Empty && binding.SeriesItemId != observation.SeriesItemId)
            {
                yield return new(observation, "A native episode is already bound to a different catalog identity.", null);
                continue;
            }

            // A position-identity episode at the same position adopts a TMDB id its native episode gained (P10.E1).
            if (IsAdoptable(binding.EpisodeTmdbId, binding.SeasonNumber, binding.EpisodeNumber, observation) &&
                !heldTmdbIds.Contains((binding.EntryId, observation.TmdbId!.Value)))
                continue;

            // TMDB is identity: a changed position with the same provider id is renumbering, not a conflict.
            if (observation.TmdbId is > 0 && binding.EpisodeTmdbId != observation.TmdbId)
                yield return new(observation, "A native episode is already bound to a different provider identity.",
                    binding.EpisodeId);
            else if (observation.TmdbId is not > 0 &&
                     (binding.SeasonNumber != observation.SeasonNumber || binding.EpisodeNumber != observation.EpisodeNumber))
                yield return new(observation, "A native episode without a provider identity changed position.", null);
        }
    }

    private static IEnumerable<(NativeEpisodeSnapshot Observation, Guid TrackedEpisodeId)> FindEpisodeConflicts(
        IReadOnlyList<Episode> episodes, IReadOnlyList<NativeEpisodeSnapshot> observations)
    {
        var trackedByNativeId = episodes.Where(episode => episode.JellyfinItemId.HasValue)
            .GroupBy(episode => episode.JellyfinItemId!.Value).ToDictionary(group => group.Key, group => group.First());
        var trackedTmdbIds = episodes.Where(episode => !episode.IsPositionIdentity).Select(episode => episode.TmdbId).ToHashSet();
        foreach (var observation in observations.Where(observation => observation.TmdbId is > 0))
            if (trackedByNativeId.TryGetValue(observation.JellyfinItemId, out var bound) && bound.TmdbId != observation.TmdbId &&
                !(IsAdoptable(bound.TmdbId, bound.SeasonNumber, bound.EpisodeNumber, observation) &&
                  !trackedTmdbIds.Contains(observation.TmdbId!.Value)))
                yield return (observation, bound.Id);
    }

    /// <summary>
    /// Whether a position-identity episode (TMDB id zero, P10.E1) may take the TMDB id its native episode now reports:
    /// only at the very same season and episode, so a renumbered native episode is never folded into another one.
    /// </summary>
    private static bool IsAdoptable(int trackedTmdbId, int seasonNumber, int episodeNumber, NativeEpisodeSnapshot observation) =>
        trackedTmdbId <= 0 && observation.TmdbId is > 0 &&
        observation.SeasonNumber == seasonNumber && observation.EpisodeNumber == episodeNumber;

    private int ReconcileEpisodes(
        Guid entryId,
        Guid targetLibraryId,
        IReadOnlyList<Episode> episodes,
        IReadOnlyList<EpisodeBinding> episodeBindings,
        IReadOnlyList<NativeEpisodeSnapshot> observations,
        bool backfilled,
        IReadOnlySet<Guid> evaluatedEpisodes)
    {
        var changedEpisodeIds = new HashSet<Guid>();
        var boundBefore = episodeBindings.Select(binding => binding.EpisodeId).ToHashSet();
        var knownBindings = episodeBindings.ToDictionary(binding => binding.JellyfinItemId);

        // A native episode that gained a TMDB id (the library switched scraper) keeps its position-identity row, which
        // takes the id, rather than leaving the row behind and tracking the same file twice (P10.E1).
        var heldTmdbIds = episodes.Where(episode => !episode.IsPositionIdentity).Select(episode => episode.TmdbId).ToHashSet();
        foreach (var episode in episodes.Where(episode => episode.IsPositionIdentity))
        {
            var gained = observations.Where(observation => knownBindings.TryGetValue(observation.JellyfinItemId, out var bound) &&
                    bound.EpisodeId == episode.Id && IsAdoptable(episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, observation))
                .Select(observation => observation.TmdbId!.Value).Distinct().ToArray();
            if (gained.Length != 1 || !heldTmdbIds.Add(gained[0])) continue;
            episode.TmdbId = gained[0];
            changedEpisodeIds.Add(episode.Id);
        }

        foreach (var episode in episodes)
        {
            var allCandidates = observations.Where(observation => observation.TmdbId == episode.TmdbId).ToArray();
            if (allCandidates.Length == 0)
            {
                allCandidates = observations.Where(observation => observation.TmdbId is null &&
                    observation.SeasonNumber == episode.SeasonNumber && observation.EpisodeNumber == episode.EpisodeNumber).ToArray();
            }

            // One native episode belongs to one tracked episode: a copy another row already binds is never claimed by
            // position here as well (P10.E1).
            allCandidates = allCandidates.Where(candidate => !knownBindings.TryGetValue(candidate.JellyfinItemId, out var owner) ||
                owner.EpisodeId == episode.Id).ToArray();
            foreach (var candidate in allCandidates)
            {
                if (knownBindings.TryGetValue(candidate.JellyfinItemId, out var existingBinding))
                {
                    var structuralChange = existingBinding.SeriesItemId != candidate.SeriesItemId ||
                        existingBinding.TargetLibraryId != targetLibraryId;
                    if (structuralChange ||
                        existingBinding.MediaPath != candidate.MediaPath ||
                        existingBinding.StorageIdentity != candidate.StorageIdentity)
                    {
                        existingBinding.SeriesItemId = candidate.SeriesItemId;
                        existingBinding.TargetLibraryId = targetLibraryId;
                        existingBinding.MediaPath = candidate.MediaPath;
                        existingBinding.StorageIdentity = candidate.StorageIdentity;
                        if (structuralChange) changedEpisodeIds.Add(episode.Id);
                    }

                    continue;
                }

                var newBinding = new EpisodeBinding
                {
                    EpisodeId = episode.Id,
                    JellyfinItemId = candidate.JellyfinItemId,
                    SeriesItemId = candidate.SeriesItemId,
                    TargetLibraryId = targetLibraryId,
                    MediaPath = candidate.MediaPath,
                    StorageIdentity = candidate.StorageIdentity
                };
                database.EpisodeBindings.Add(newBinding);
                knownBindings.Add(candidate.JellyfinItemId, newBinding);
                changedEpisodeIds.Add(episode.Id);
            }

            var candidates = allCandidates.Where(observation => observation.IsPlayable).ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates.FirstOrDefault(candidate => candidate.JellyfinItemId == episode.JellyfinItemId) ??
                    candidates.OrderBy(candidate => candidate.JellyfinItemId).First();
                var becameAvailable = episode.State != FileState.OnDisk || !episode.JellyfinItemId.HasValue;
                if (episode.JellyfinItemId != selected.JellyfinItemId || episode.State != FileState.OnDisk)
                {
                    episode.JellyfinItemId = selected.JellyfinItemId;
                    episode.State = FileState.OnDisk;
                    changedEpisodeIds.Add(episode.Id);
                    if (!backfilled && becameAvailable)
                    {
                        database.History.Add(new HistoryRecord
                        {
                            EntryId = entryId,
                            EventType = "episode_media_available",
                            Summary = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} matched playable media in Jellyfin",
                            Data = JsonSerializer.Serialize(new { episodeId = episode.Id, tmdbId = episode.TmdbId,
                                episode.SeasonNumber, episode.EpisodeNumber,
                                jellyfinItemId = selected.JellyfinItemId })
                        });
                    }
                }
            }

            // A position-identity episode takes its display metadata from the native episodes at its position (P10.E1).
            var providerCandidate = allCandidates.Where(candidate => candidate.TmdbId is > 0 || episode.IsPositionIdentity)
                .OrderBy(candidate => candidate.JellyfinItemId).FirstOrDefault();
            if (providerCandidate is not null && UpdateEpisodeMetadata(episode, providerCandidate))
                changedEpisodeIds.Add(episode.Id);
        }

        // An S01E01-E02 file also makes the following tracked episodes available. They have no binding of
        // their own, because one native item binds one episode; retention blocks such files (P3.T16).
        foreach (var observation in observations.Where(observation => observation.IsPlayable &&
                     observation.EpisodeNumberEnd > observation.EpisodeNumber))
        {
            for (var number = observation.EpisodeNumber + 1; number <= observation.EpisodeNumberEnd; number++)
            {
                var covered = episodes.FirstOrDefault(episode => episode.SeasonNumber == observation.SeasonNumber &&
                    episode.EpisodeNumber == number);
                if (covered is null || covered.JellyfinItemId == observation.JellyfinItemId ||
                    knownBindings.Values.Any(binding => binding.EpisodeId == covered.Id)) continue;
                var becameAvailable = covered.State != FileState.OnDisk || !covered.JellyfinItemId.HasValue;
                covered.JellyfinItemId = observation.JellyfinItemId;
                covered.State = FileState.OnDisk;
                changedEpisodeIds.Add(covered.Id);
                if (!backfilled && becameAvailable)
                    database.History.Add(new HistoryRecord
                    {
                        EntryId = entryId,
                        EventType = "episode_media_available",
                        Summary = $"S{covered.SeasonNumber:00}E{covered.EpisodeNumber:00} is part of a multi-episode file in Jellyfin",
                        Data = JsonSerializer.Serialize(new { episodeId = covered.Id, tmdbId = covered.TmdbId,
                            covered.SeasonNumber, covered.EpisodeNumber, jellyfinItemId = observation.JellyfinItemId })
                    });
            }
        }

        var trackedProviderIds = episodes.Select(episode => episode.TmdbId).ToHashSet();
        var createdProviderPositions = new List<(int SeasonNumber, int EpisodeNumber)>();
        foreach (var providerGroup in observations.Where(observation => observation.TmdbId is > 0 &&
                     !trackedProviderIds.Contains(observation.TmdbId.Value)).GroupBy(observation => observation.TmdbId!.Value))
        {
            var representative = providerGroup.OrderBy(observation => observation.JellyfinItemId).First();
            // A position-identity episode already holds this position (P10.E1). Another copy there is not folded into
            // it, and a second row cannot take the position (older databases keep that index unique): the copy stays
            // untracked until the position row adopts the TMDB id through its own native episode or a Refresh.
            if (episodes.Any(episode => episode.IsPositionIdentity && episode.SeasonNumber == representative.SeasonNumber &&
                    episode.EpisodeNumber == representative.EpisodeNumber))
                continue;
            var playable = providerGroup.Where(observation => observation.IsPlayable)
                .OrderBy(observation => observation.JellyfinItemId).FirstOrDefault();
            var newEpisode = new Episode
            {
                EntryId = entryId,
                TmdbId = providerGroup.Key,
                SeasonNumber = representative.SeasonNumber,
                EpisodeNumber = representative.EpisodeNumber,
                Title = representative.Title ?? string.Empty,
                Overview = representative.Overview,
                StillPath = representative.StillPath,
                AirDate = representative.AirDate,
                RuntimeMinutes = representative.RuntimeMinutes,
                Monitored = !backfilled,
                JellyfinItemId = playable?.JellyfinItemId,
                State = playable is null ? FileState.None : FileState.OnDisk
            };
            database.Episodes.Add(newEpisode);
            foreach (var observation in providerGroup)
            {
                var binding = new EpisodeBinding
                {
                    EpisodeId = newEpisode.Id,
                    JellyfinItemId = observation.JellyfinItemId,
                    SeriesItemId = observation.SeriesItemId,
                    TargetLibraryId = targetLibraryId,
                    MediaPath = observation.MediaPath,
                    StorageIdentity = observation.StorageIdentity
                };
                database.EpisodeBindings.Add(binding);
                knownBindings.Add(observation.JellyfinItemId, binding);
            }

            changedEpisodeIds.Add(newEpisode.Id);
            createdProviderPositions.Add((newEpisode.SeasonNumber, newEpisode.EpisodeNumber));
        }

        // A numbered native episode without a TMDB id is tracked by its position (P10.E1). Libraries scraped from TVDB
        // carry no TMDB episode ids at all, and without this row the episode could have no retention, Keep or detail of
        // its own. A row is created only where no tracked episode of this entry holds that position already, so an
        // unclaimed copy is never folded into an episode that might differ; every copy at a new position becomes a
        // version of the one row. Discovered, not wanted: the row is not monitored.
        var trackedPositions = episodes.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber))
            .Concat(createdProviderPositions).ToHashSet();
        foreach (var positionGroup in observations.Where(observation => observation.TmdbId is null &&
                         !knownBindings.ContainsKey(observation.JellyfinItemId))
                     .GroupBy(observation => (observation.SeasonNumber, observation.EpisodeNumber))
                     .Where(group => !trackedPositions.Contains(group.Key)))
        {
            var representative = positionGroup.OrderBy(observation => observation.JellyfinItemId).First();
            var playable = positionGroup.Where(observation => observation.IsPlayable)
                .OrderBy(observation => observation.JellyfinItemId).FirstOrDefault();
            var positionEpisode = new Episode
            {
                EntryId = entryId,
                TmdbId = 0,
                SeasonNumber = positionGroup.Key.SeasonNumber,
                EpisodeNumber = positionGroup.Key.EpisodeNumber,
                Title = representative.Title ?? string.Empty,
                Overview = representative.Overview,
                AirDate = representative.AirDate,
                RuntimeMinutes = representative.RuntimeMinutes,
                Monitored = false,
                JellyfinItemId = playable?.JellyfinItemId,
                State = playable is null ? FileState.None : FileState.OnDisk
            };
            database.Episodes.Add(positionEpisode);
            foreach (var observation in positionGroup)
            {
                var binding = new EpisodeBinding
                {
                    EpisodeId = positionEpisode.Id,
                    JellyfinItemId = observation.JellyfinItemId,
                    SeriesItemId = observation.SeriesItemId,
                    TargetLibraryId = targetLibraryId,
                    MediaPath = observation.MediaPath,
                    StorageIdentity = observation.StorageIdentity
                };
                database.EpisodeBindings.Add(binding);
                knownBindings.Add(observation.JellyfinItemId, binding);
            }

            trackedPositions.Add(positionGroup.Key);
            changedEpisodeIds.Add(positionEpisode.Id);
        }

        // An episode's retention baseline is the moment it gains its first representation (P10.E1): only a completion
        // after it counts, whether the user plays the episode or just marks it played, and a watched state Jellyfin
        // already held for the file does not. Episodes evaluated before keep their evaluation untouched.
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var episodeId in knownBindings.Values.Select(binding => binding.EpisodeId).Distinct()
                     .Where(episodeId => !boundBefore.Contains(episodeId) && !evaluatedEpisodes.Contains(episodeId)))
        {
            database.RetentionEvaluations.Add(new RetentionEvaluation
            {
                EntryId = entryId,
                EpisodeId = episodeId,
                TargetId = episodeId,
                State = RetentionEvaluationStates.Waiting,
                Reason = RetentionEvaluationReasons.WaitingForCompletion,
                BaselineAt = now,
                RequiresFreshCompletion = true
            });
        }

        return changedEpisodeIds.Count;
    }

    private static bool UpdateEpisodeMetadata(Episode episode, NativeEpisodeSnapshot observation)
    {
        var changed = false;
        if (episode.SeasonNumber != observation.SeasonNumber)
        {
            episode.SeasonNumber = observation.SeasonNumber;
            changed = true;
        }

        if (episode.EpisodeNumber != observation.EpisodeNumber)
        {
            episode.EpisodeNumber = observation.EpisodeNumber;
            changed = true;
        }

        changed |= AssignIfPresent(observation.Title, episode.Title, value => episode.Title = value);
        changed |= AssignIfPresent(observation.Overview, episode.Overview, value => episode.Overview = value);
        changed |= AssignIfPresent(observation.StillPath, episode.StillPath, value => episode.StillPath = value);
        if (observation.AirDate.HasValue && episode.AirDate != observation.AirDate)
        {
            episode.AirDate = observation.AirDate;
            changed = true;
        }

        if (observation.RuntimeMinutes.HasValue && episode.RuntimeMinutes != observation.RuntimeMinutes)
        {
            episode.RuntimeMinutes = observation.RuntimeMinutes;
            changed = true;
        }

        return changed;
    }

    private static bool AssignIfPresent(string? value, string? current, Action<string> assign)
    {
        if (value is null || value == current)
            return false;
        assign(value);
        return true;
    }
}

/// <summary>A complete positive observation of one native title and its known copies.</summary>
public sealed record NativeTitleSnapshot(string MediaType, int? TmdbId, Guid TargetLibraryId, string Title,
    int? Year, string? ImdbId, string? Overview, string? PosterPath, string? MetadataJson,
    IReadOnlyList<NativeRepresentation> Representations, IReadOnlyList<NativeEpisodeSnapshot> Episodes,
    DateTime? DateCreated = null)
{
    /// <summary>The effective native parental rating of the representative item (P3.T15).</summary>
    public string? NativeRating { get; init; }

    /// <summary>The native tags of the representative item (P3.T15).</summary>
    public IReadOnlyList<string>? NativeTags { get; init; }

    /// <summary>Native episodes left out of matching because they have no season and episode number (P2.R6).</summary>
    public IReadOnlyList<SkippedNativeEpisode> SkippedEpisodes { get; init; } = [];

    /// <summary>Returns true when a series copy has at least one playable episode, numbered or not.</summary>
    public bool HasPlayableEpisode(Guid seriesItemId) =>
        Episodes.Any(episode => episode.SeriesItemId == seriesItemId && episode.IsPlayable) ||
        SkippedEpisodes.Any(episode => episode.SeriesItemId == seriesItemId && episode.IsPlayable);
}

/// <summary>One native movie or series representation: a title, or one further media source of one.</summary>
/// <param name="JellyfinItemId">The native item behind this media source.</param>
/// <param name="TargetLibraryId">The library the observation came from.</param>
/// <param name="IsPlayable">Whether the representation has media.</param>
/// <param name="VersionGroupId">Jellyfin's primary version identity for the group this belongs to.</param>
/// <param name="MediaPath">The file this media source plays.</param>
/// <param name="StorageIdentity">The mount identity observed for that file.</param>
/// <param name="OwnerItemId">
/// The navigable title this is a further media source of, or null when the representation is that title itself (P6.M6).
/// </param>
public sealed record NativeRepresentation(Guid JellyfinItemId, Guid TargetLibraryId, bool IsPlayable,
    Guid? VersionGroupId = null, string? MediaPath = null, string? StorageIdentity = null, Guid? OwnerItemId = null)
{
    /// <summary>The native item a user opens to play this representation.</summary>
    public Guid NavigableItemId => OwnerItemId ?? JellyfinItemId;
}

/// <summary>One playable or unavailable native episode observation.</summary>
public sealed record NativeEpisodeSnapshot(Guid JellyfinItemId, Guid SeriesItemId, int? TmdbId,
    int SeasonNumber, int EpisodeNumber, bool IsPlayable, string? Title = null, string? Overview = null,
    string? StillPath = null, DateTime? AirDate = null, int? RuntimeMinutes = null,
    string? MediaPath = null, string? StorageIdentity = null, int? EpisodeNumberEnd = null);

/// <summary>A complete successful observation used to remove stale native bindings for one available library.</summary>
/// <remarks>Protected identities belong to titles that failed, conflicted or lost their provider identity.</remarks>
internal sealed record ConfirmedLibrarySnapshot(Guid LibraryId, IReadOnlyList<string> LibraryLocations,
    IReadOnlySet<Guid> TitleIds, IReadOnlySet<Guid> PlayableTitleIds, IReadOnlySet<Guid> EpisodeIds,
    IReadOnlySet<Guid> PlayableEpisodeIds, IReadOnlySet<Guid> ProtectedNativeIds,
    IReadOnlySet<int> ProtectedTmdbIds, IReadOnlySet<Guid> ProtectedEntryIds);

internal sealed record AbsenceConfirmationResult(int MissingItems, bool IsComplete, string? Detail,
    int ExcludedTitles = 0, IReadOnlyList<ExcludedTitle>? UnverifiedTitles = null);

/// <summary>A title whose absent media could not be proven gone, so it kept its bindings and state.</summary>
internal sealed record ExcludedTitle(Guid EntryId, string Title, string Detail);

/// <summary>The durable effect of one reconciliation operation.</summary>
public sealed record ReconciliationResult(ReconciliationOutcome Outcome, Guid? EntryId, int ChangedEpisodes, string? Detail)
{
    /// <summary>Episodes skipped because their identity disagrees, one line each (P2.R9).</summary>
    public IReadOnlyList<string> EpisodeDiagnostics { get; init; } = [];
}

/// <summary>Stable reconciliation outcomes used by backfill progress reporting.</summary>
public enum ReconciliationOutcome
{
    /// <summary>A new catalog row was backfilled.</summary>
    Created,
    /// <summary>An existing row or episode binding changed.</summary>
    Updated,
    /// <summary>The observation already matched durable state.</summary>
    Unchanged,
    /// <summary>No provider identity was available.</summary>
    Unmatched,
    /// <summary>The observation conflicts with durable provider identity.</summary>
    Conflict,
    /// <summary>Another library that shares the path owns this title (P2.R7).</summary>
    Overlap
}

internal sealed record ExistingEpisodeBinding(Guid JellyfinItemId, Guid SeriesItemId, Guid BindingLibraryId,
    int EpisodeTmdbId, int SeasonNumber, int EpisodeNumber, Guid EntryId, string MediaType, int EntryTmdbId,
    Guid? EntryLibraryId, Guid EpisodeId);

internal sealed record DurableEpisodeConflict(NativeEpisodeSnapshot Observation, string Detail, Guid? RebindableEpisodeId);

/// <summary>How long an Add or Refresh waits for a library that reconciliation is checking (P2.R8).</summary>
public sealed record LibraryWriteBudget(TimeSpan Wait)
{
    /// <summary>The production budget, well inside the request's 60-second TMDB budget.</summary>
    public static LibraryWriteBudget Default { get; } = new(TimeSpan.FromSeconds(20));
}

/// <summary>Serializes overlapping catalog writes for one target library within the host process.</summary>
public sealed class ReconciliationLibraryLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Acquires exclusive reconciliation access for a library.</summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(libraryId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>
    /// Waits at most <paramref name="wait"/> for the library, so a request can report "library busy"
    /// instead of spending its whole budget behind a long absence confirmation (P2.R8).
    /// </summary>
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(Guid libraryId, TimeSpan wait,
        CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(libraryId, static _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(wait, cancellationToken).ConfigureAwait(false) ? new Lease(gate) : null;
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
