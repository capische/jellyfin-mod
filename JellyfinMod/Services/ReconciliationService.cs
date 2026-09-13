using System.Collections.Concurrent;
using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services;

/// <summary>Reconciles verified native observations into the durable catalog.</summary>
public sealed class ReconciliationService(ModDbContext database, ReconciliationLibraryLock libraryLock)
{
    /// <summary>Reconciles one library-scoped native title observation.</summary>
    public async Task<ReconciliationResult> ReconcileAsync(NativeTitleSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var lease = await libraryLock.AcquireAsync(snapshot.TargetLibraryId, cancellationToken).ConfigureAwait(false);
        return await ReconcileAsync(snapshot, true, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ReconciliationResult> ReconcileUnderLeaseAsync(NativeTitleSnapshot snapshot,
        CancellationToken cancellationToken) => ReconcileAsync(snapshot, true, cancellationToken);

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
        if (snapshot.MediaType == "movie" && snapshot.Episodes.Count > 0)
            throw new ArgumentException("Movie observations cannot contain episodes.", nameof(snapshot));
        if (snapshot.Episodes.Any(episode => episode.JellyfinItemId == Guid.Empty ||
            !representationIds.Contains(episode.SeriesItemId)))
            throw new ArgumentException("Every episode must have an identity and belong to an observed series representation.", nameof(snapshot));
        if (snapshot.Episodes.Select(episode => episode.JellyfinItemId).Distinct().Count() != snapshot.Episodes.Count)
            throw new ArgumentException("Native episode identities must be unique within an observation.", nameof(snapshot));

        var observationConflict = FindObservationConflict(snapshot.Episodes);
        if (observationConflict is not null)
            return new(ReconciliationOutcome.Conflict, null, 0, observationConflict);

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
            return new(ReconciliationOutcome.Conflict, conflictingEntry.Id, 0,
                "A native representation is already bound to a different catalog identity.");

        var episodeIds = snapshot.Episodes.Select(episode => episode.JellyfinItemId).ToHashSet();
        var boundEpisodes = await (from binding in database.EpisodeBindings.AsNoTracking()
            join episode in database.Episodes.AsNoTracking() on binding.EpisodeId equals episode.Id
            join owner in database.Entries.AsNoTracking() on episode.EntryId equals owner.Id
            where episodeIds.Contains(binding.JellyfinItemId)
            select new ExistingEpisodeBinding(binding.JellyfinItemId, binding.SeriesItemId, binding.TargetLibraryId,
                episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, owner.Id, owner.MediaType,
                owner.TmdbId, owner.TargetLibraryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var canonicalEpisodes = await (from episode in database.Episodes.AsNoTracking()
            join owner in database.Entries.AsNoTracking() on episode.EntryId equals owner.Id
            where episode.JellyfinItemId != null && episodeIds.Contains(episode.JellyfinItemId.Value)
            select new ExistingEpisodeBinding(episode.JellyfinItemId.GetValueOrDefault(), Guid.Empty, Guid.Empty,
                episode.TmdbId, episode.SeasonNumber, episode.EpisodeNumber, owner.Id, owner.MediaType,
                owner.TmdbId, owner.TargetLibraryId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var durableEpisodeConflict = FindDurableEpisodeConflict(snapshot, boundEpisodes.Concat(canonicalEpisodes));
        if (durableEpisodeConflict is not null)
            return new(ReconciliationOutcome.Conflict, durableEpisodeConflict.EntryId, 0, durableEpisodeConflict.Detail);

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
            database.Entries.Add(entry);
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
                if (binding.VersionGroupId != versionGroupId || binding.TargetLibraryId != representation.TargetLibraryId)
                {
                    binding.VersionGroupId = versionGroupId;
                    binding.TargetLibraryId = representation.TargetLibraryId;
                    entryBindingChanges++;
                }

                continue;
            }

            database.EntryBindings.Add(new EntryBinding
            {
                EntryId = entry.Id,
                JellyfinItemId = representation.JellyfinItemId,
                TargetLibraryId = representation.TargetLibraryId,
                VersionGroupId = representation.VersionGroupId ?? representation.JellyfinItemId
            });
            entryBindingChanges++;
        }

        var playableRepresentations = snapshot.MediaType == "series"
            ? snapshot.Representations.Where(representation => snapshot.Episodes.Any(episode =>
                episode.SeriesItemId == representation.JellyfinItemId && episode.IsPlayable)).ToArray()
            : snapshot.Representations.Where(representation => representation.IsPlayable).ToArray();
        var selected = SelectRepresentation(entry.JellyfinItemId, playableRepresentations);

        var entryChanged = false;
        var episodeChanges = 0;
        if (selected is not null)
        {
            entryChanged = entry.JellyfinItemId != selected.JellyfinItemId || entry.State != FileState.OnDisk;
            entry.JellyfinItemId = selected.JellyfinItemId;
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
            var conflict = FindEpisodeConflict(episodes, snapshot.Episodes);
            if (conflict is not null)
            {
                database.ChangeTracker.Clear();
                return new(ReconciliationOutcome.Conflict, entry.Id, 0, conflict);
            }

            episodeChanges = ReconcileEpisodes(entry.Id, snapshot.TargetLibraryId, episodes, episodeBindings,
                snapshot.Episodes, created);
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
            ? ReconciliationOutcome.Updated : ReconciliationOutcome.Unchanged, entry.Id, episodeChanges, null);
    }

    private static NativeRepresentation? SelectRepresentation(Guid? currentId, IReadOnlyList<NativeRepresentation> candidates)
        => candidates.FirstOrDefault(candidate => candidate.JellyfinItemId == currentId) ??
            candidates.OrderBy(candidate => candidate.JellyfinItemId == candidate.VersionGroupId ? 0 : 1)
                .ThenBy(candidate => candidate.VersionGroupId ?? candidate.JellyfinItemId)
                .ThenBy(candidate => candidate.JellyfinItemId).FirstOrDefault();

    private static string? FindObservationConflict(IReadOnlyList<NativeEpisodeSnapshot> observations)
    {
        var conflictingPosition = observations.Where(observation => observation.TmdbId is > 0)
            .GroupBy(observation => (observation.SeasonNumber, observation.EpisodeNumber))
            .FirstOrDefault(group => group.Select(observation => observation.TmdbId).Distinct().Skip(1).Any());
        if (conflictingPosition is not null)
            return "Explicit episode provider identities conflict at the same season and episode position.";
        var conflictingProvider = observations.Where(observation => observation.TmdbId is > 0)
            .GroupBy(observation => observation.TmdbId)
            .FirstOrDefault(group => group.Select(observation => (observation.SeasonNumber, observation.EpisodeNumber))
                .Distinct().Skip(1).Any());
        return conflictingProvider is null
            ? null
            : "Copies of one episode provider identity disagree about its season and episode position.";
    }

    private static DurableEpisodeConflict? FindDurableEpisodeConflict(
        NativeTitleSnapshot snapshot,
        IEnumerable<ExistingEpisodeBinding> bindings)
    {
        foreach (var observation in snapshot.Episodes)
        {
            var binding = bindings.FirstOrDefault(candidate => candidate.JellyfinItemId == observation.JellyfinItemId);
            if (binding is null)
                continue;
            if (binding.MediaType != snapshot.MediaType || binding.EntryTmdbId != snapshot.TmdbId ||
                binding.EntryLibraryId != snapshot.TargetLibraryId ||
                binding.BindingLibraryId != Guid.Empty && binding.BindingLibraryId != snapshot.TargetLibraryId ||
                binding.SeriesItemId != Guid.Empty && binding.SeriesItemId != observation.SeriesItemId)
            {
                return new(binding.EntryId, "A native episode is already bound to a different catalog identity.");
            }

            if (observation.TmdbId is > 0 && binding.EpisodeTmdbId != observation.TmdbId ||
                observation.TmdbId is not > 0 &&
                (binding.SeasonNumber != observation.SeasonNumber || binding.EpisodeNumber != observation.EpisodeNumber))
            {
                return new(binding.EntryId, "A native episode is already bound to a different episode identity.");
            }
        }

        return null;
    }

    private static string? FindEpisodeConflict(IReadOnlyList<Episode> episodes, IReadOnlyList<NativeEpisodeSnapshot> observations)
    {
        var trackedByNativeId = episodes.Where(episode => episode.JellyfinItemId.HasValue)
            .ToDictionary(episode => episode.JellyfinItemId!.Value);
        var observedProviderIds = observations.Where(observation => observation.TmdbId is > 0)
            .Select(observation => observation.TmdbId!.Value).ToHashSet();
        foreach (var observation in observations.Where(observation => observation.TmdbId is > 0))
        {
            if (trackedByNativeId.TryGetValue(observation.JellyfinItemId, out var bound) && bound.TmdbId != observation.TmdbId)
                return "A native episode is already bound to a different provider identity.";
            var samePosition = episodes.FirstOrDefault(episode => episode.SeasonNumber == observation.SeasonNumber &&
                episode.EpisodeNumber == observation.EpisodeNumber);
            if (samePosition is not null && samePosition.TmdbId != observation.TmdbId &&
                !observedProviderIds.Contains(samePosition.TmdbId))
                return "An explicit episode provider identity conflicts with the tracked episode position.";
        }

        return null;
    }

    private int ReconcileEpisodes(
        Guid entryId,
        Guid targetLibraryId,
        IReadOnlyList<Episode> episodes,
        IReadOnlyList<EpisodeBinding> episodeBindings,
        IReadOnlyList<NativeEpisodeSnapshot> observations,
        bool backfilled)
    {
        var changedEpisodeIds = new HashSet<Guid>();
        var knownNativeIds = episodeBindings.Select(binding => binding.JellyfinItemId).ToHashSet();
        foreach (var episode in episodes)
        {
            var allCandidates = observations.Where(observation => observation.TmdbId == episode.TmdbId).ToArray();
            if (allCandidates.Length == 0)
            {
                allCandidates = observations.Where(observation => observation.TmdbId is null &&
                    observation.SeasonNumber == episode.SeasonNumber && observation.EpisodeNumber == episode.EpisodeNumber).ToArray();
            }

            foreach (var candidate in allCandidates.Where(candidate => !knownNativeIds.Contains(candidate.JellyfinItemId)))
            {
                database.EpisodeBindings.Add(new EpisodeBinding
                {
                    EpisodeId = episode.Id,
                    JellyfinItemId = candidate.JellyfinItemId,
                    SeriesItemId = candidate.SeriesItemId,
                    TargetLibraryId = targetLibraryId
                });
                knownNativeIds.Add(candidate.JellyfinItemId);
                changedEpisodeIds.Add(episode.Id);
            }

            var candidates = allCandidates.Where(observation => observation.IsPlayable).ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates.FirstOrDefault(candidate => candidate.JellyfinItemId == episode.JellyfinItemId) ??
                    candidates.OrderBy(candidate => candidate.JellyfinItemId).First();
                if (episode.JellyfinItemId != selected.JellyfinItemId || episode.State != FileState.OnDisk)
                {
                    episode.JellyfinItemId = selected.JellyfinItemId;
                    episode.State = FileState.OnDisk;
                    changedEpisodeIds.Add(episode.Id);
                }
            }

            var providerCandidate = allCandidates.Where(candidate => candidate.TmdbId is > 0)
                .OrderBy(candidate => candidate.JellyfinItemId).FirstOrDefault();
            if (providerCandidate is not null && UpdateEpisodeMetadata(episode, providerCandidate))
                changedEpisodeIds.Add(episode.Id);
        }

        var trackedProviderIds = episodes.Select(episode => episode.TmdbId).ToHashSet();
        foreach (var providerGroup in observations.Where(observation => observation.TmdbId is > 0 &&
                     !trackedProviderIds.Contains(observation.TmdbId.Value)).GroupBy(observation => observation.TmdbId!.Value))
        {
            var representative = providerGroup.OrderBy(observation => observation.JellyfinItemId).First();
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
                database.EpisodeBindings.Add(new EpisodeBinding
                {
                    EpisodeId = newEpisode.Id,
                    JellyfinItemId = observation.JellyfinItemId,
                    SeriesItemId = observation.SeriesItemId,
                    TargetLibraryId = targetLibraryId
                });
                knownNativeIds.Add(observation.JellyfinItemId);
            }

            changedEpisodeIds.Add(newEpisode.Id);
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
    IReadOnlyList<NativeRepresentation> Representations, IReadOnlyList<NativeEpisodeSnapshot> Episodes);

/// <summary>One native movie or series representation.</summary>
public sealed record NativeRepresentation(Guid JellyfinItemId, Guid TargetLibraryId, bool IsPlayable, Guid? VersionGroupId = null);

/// <summary>One playable or unavailable native episode observation.</summary>
public sealed record NativeEpisodeSnapshot(Guid JellyfinItemId, Guid SeriesItemId, int? TmdbId,
    int SeasonNumber, int EpisodeNumber, bool IsPlayable, string? Title = null, string? Overview = null,
    string? StillPath = null, DateTime? AirDate = null, int? RuntimeMinutes = null);

/// <summary>The durable effect of one reconciliation operation.</summary>
public sealed record ReconciliationResult(ReconciliationOutcome Outcome, Guid? EntryId, int ChangedEpisodes, string? Detail);

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
    Conflict
}

internal sealed record ExistingEpisodeBinding(Guid JellyfinItemId, Guid SeriesItemId, Guid BindingLibraryId,
    int EpisodeTmdbId, int SeasonNumber, int EpisodeNumber, Guid EntryId, string MediaType, int EntryTmdbId,
    Guid? EntryLibraryId);

internal sealed record DurableEpisodeConflict(Guid EntryId, string Detail);

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

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
