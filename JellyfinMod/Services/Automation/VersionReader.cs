using System.Globalization;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>
/// Describes each held version for the version selector (P6.M8) from fields the Jellyfin 12.0.0 host returns for its media
/// streams, plus each version's own retention state (P6.M7). Nothing is guessed: unknown fields stay null.
/// </summary>
public sealed class VersionReader(ModDbContext database, IMediaSourceManager? mediaSources, UnixFileInspector files,
    ILibraryManager? library = null, IUserDataManager? userData = null)
{
    /// <summary>Reads the versions of a movie entry or one episode.</summary>
    /// <param name="entryId">The entry.</param>
    /// <param name="episodeId">The episode, or null for a movie.</param>
    /// <param name="evaluation">The target's retention evaluation.</param>
    /// <param name="administrator">Whether the viewer is an administrator.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="viewer">The viewer, whose own progress marks the version they are part-way through (V1).</param>
    public async Task<IReadOnlyList<VersionDto>> ForAsync(Guid entryId, Guid? episodeId, RetentionEvaluation? evaluation, bool administrator,
        CancellationToken cancellationToken, Jellyfin.Database.Implementations.Entities.User? viewer = null)
    {
        // A version is a media source: its own item carries the streams and is what Play must ask for, while the
        // item a client opens is the title that owns it (P6.M6).
        List<(Guid BindingId, Guid ItemId, Guid OwnerId, string? Path, bool IsDefault)> bindings = episodeId is { } id
            ? (await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == id).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).Select(binding => (binding.Id, binding.JellyfinItemId, binding.OwnerItemId ?? binding.JellyfinItemId,
                    binding.MediaPath, binding.OwnerItemId is null)).ToList()
            : (await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entryId).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).Select(binding => (binding.Id, binding.JellyfinItemId,
                    binding.OwnerItemId ?? binding.JellyfinItemId, binding.MediaPath,
                    binding.OwnerItemId is null && binding.JellyfinItemId == binding.VersionGroupId)).ToList();
        if (bindings.Count == 0) return [];
        // Jellyfin's own default is its main item; an episode bound before V1 without one gets its first binding (P6.M8).
        if (episodeId is not null && bindings.Count(binding => binding.IsDefault) != 1)
            bindings = bindings.Select((binding, index) => binding with { IsDefault = index == 0 }).ToList();
        var trackedCount = bindings.Count;
        // Files Jellyfin plays for the title that the plugin has not bound yet are rows too, so the list never hides a copy
        // that Delete would remove or that holds a quality (V1, analysis C11, C12).
        foreach (var untracked in UntrackedVersions(bindings.Select(binding => binding.OwnerId).Distinct(),
                     bindings.Select(binding => binding.ItemId).ToHashSet(), bindings.Select(binding => binding.Path)))
            bindings.Add((Guid.Empty, untracked.Item.Id, bindings[0].OwnerId, untracked.Path, false));
        // Keyed by the file the import landed, because a version's binding can be re-identified when a sibling goes.
        var labels = (await database.ImportOperations.AsNoTracking()
                .Where(operation => operation.DestinationPath != null && operation.VersionLabel != null && operation.EntryId == entryId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(operation => operation.DestinationPath!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(operation => operation.CompletedAt).First().VersionLabel!,
                StringComparer.Ordinal);
        var seeds = await database.SeedReleaseOperations.AsNoTracking()
            .Where(seed => seed.EntryId == entryId && SeedReleaseStates.Open.Contains(seed.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // A kept file is kept by path or by identity, like the preview reads it (PHASE10 Q3).
        var keeps = await database.VersionKeeps.AsNoTracking().Where(keep => keep.EntryId == entryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<VersionDto>();
        foreach (var binding in bindings.OrderBy(binding => binding.IsDefault ? 0 : 1).ThenBy(binding => binding.Path, StringComparer.Ordinal))
        {
            var (quality, resolution) = VersionQuality.Parse(binding.Path);
            IReadOnlyList<MediaStream> streams = [];
            try
            {
                streams = mediaSources?.GetMediaStreams(binding.ItemId) ?? [];
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                streams = [];
            }

            var video = streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
            var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio).OrderByDescending(stream => stream.IsDefault).FirstOrDefault();
            UnixFileSnapshot file = default;
            var inspected = binding.Path is not null && files.TryInspect(binding.Path, out file);
            var seeding = inspected && seeds.Any(seed => seed.SeedingPhysicalIdentity == file.PhysicalIdentity && seed.GoalMetAt is null);
            var kept = keeps.Any(keep => keep.MediaPath == binding.Path ||
                inspected && (keep.MediaPath == file.CanonicalPath || keep.PhysicalIdentity == file.PhysicalIdentity));
            var tracked = binding.BindingId != Guid.Empty;
            var retention = !tracked ? null
                : kept ? new VersionRetentionDto("blocked", administrator ? "version_kept" : "protected")
                : seeding
                ? new VersionRetentionDto("waiting", administrator ? SeedReleaseReasons.GoalUnmet : "seeding")
                : evaluation is null ? null
                : new VersionRetentionDto(evaluation.State, administrator ? evaluation.Reason : null);
            var native = Native(binding.ItemId);
            result.Add(new VersionDto(binding.OwnerId, binding.ItemId.ToString("N", CultureInfo.InvariantCulture), binding.BindingId,
                (binding.Path is null ? null : labels.GetValueOrDefault(binding.Path)) ?? Label(binding.Path),
                quality, resolution ?? ResolutionOf(video),
                video?.Width, video?.Height, video?.Codec, video?.VideoRange.ToString(), video?.BitDepth, audio?.Codec, audio?.Channels,
                inspected ? (long)file.LogicalBytes : null, binding.IsDefault, retention)
            {
                Kept = kept,
                Tracked = tracked,
                // Remove this version (decision 3): administrators, a bound single file that no per-file Keep holds.
                Removable = administrator && tracked && !kept && native is not Video { AdditionalParts.Length: > 0 },
                IsLast = tracked && trackedCount == 1,
                InProgress = InProgress(viewer, native),
                EpisodeRange = EpisodeRange(native)
            });
        }

        return result;
    }

    private IEnumerable<NativeVersion> UntrackedVersions(IEnumerable<Guid> owners, IReadOnlySet<Guid> boundItems, IEnumerable<string?> boundPaths)
    {
        if (library is null) return [];
        var paths = boundPaths.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<NativeVersion>();
        foreach (var owner in owners)
        {
            try
            {
                if (library.GetItemById(owner) is not Video video) continue;
                result.AddRange(NativeVersions.Read(library, NativeVersions.MainOf(library, video), null).Versions
                    .Where(version => !boundItems.Contains(version.Item.Id) && !paths.Contains(version.Path)));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The bound versions stand; the rest is read again next time.
            }
        }

        return result.DistinctBy(version => version.Item.Id);
    }

    private BaseItem? Native(Guid itemId)
    {
        try
        {
            return library?.GetItemById(itemId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Whether the viewer is part-way through this version, so no device default may replace it (decision 4).</summary>
    private bool InProgress(Jellyfin.Database.Implementations.Entities.User? viewer, BaseItem? native)
    {
        try
        {
            return viewer is not null && native is not null && userData?.GetUserData(viewer, native)?.PlaybackPositionTicks > 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary><c>S01E01-E02</c> for a file holding several episodes; null otherwise.</summary>
    private static string? EpisodeRange(BaseItem? native) =>
        native is MediaBrowser.Controller.Entities.TV.Episode { IndexNumber: { } first, IndexNumberEnd: { } last } episode && last > first
            ? string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber ?? 0:00}E{first:00}-E{last:00}")
            : null;

    /// <summary>Assesses whether a movie can be upgraded now, and names an open upgrade.</summary>
    public async Task<UpgradeStateDto> UpgradeAsync(Entry entry, CancellationToken cancellationToken)
    {
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var profileId = entry.QualityProfileId ?? settings.DefaultQualityProfileId;
        var profile = profileId is { } id
            ? await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var held = await VersionQuality.HeldAsync(database, entry.Id, null, cancellationToken, library).ConfigureAwait(false);
        var assessment = UpgradeAssessment.For(profile, held, false, settings.EpisodeUpgradesEnabled);
        var open = await database.UpgradeOperations.AsNoTracking().Where(upgrade => upgrade.EntryId == entry.Id && upgrade.EpisodeId == null &&
                UpgradeStates.Open.Contains(upgrade.State))
            .Select(upgrade => upgrade.State).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new UpgradeStateDto(assessment.Eligible, assessment.Cutoff, assessment.HeldBest, assessment.Mode, assessment.BlockedReason, open);
    }

    /// <summary>The version label Jellyfin shows: the text after the last " - " of a file name.</summary>
    private static string? Label(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) return null;
        var separator = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator < 0 ? null : name[(separator + 3)..];
    }

    private static string? ResolutionOf(MediaStream? video) => video?.Height switch
    {
        >= 2000 => "2160p",
        >= 1000 => "1080p",
        >= 700 => "720p",
        > 0 => "480p",
        _ => null
    };
}
