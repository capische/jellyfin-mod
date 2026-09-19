using System.Globalization;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>
/// Describes each held version for the version selector (P6.M8) from fields the pinned 10.11 host returns for its media
/// streams, plus each version's own retention state (P6.M7). Nothing is guessed: unknown fields stay null.
/// </summary>
public sealed class VersionReader(ModDbContext database, IMediaSourceManager? mediaSources, UnixFileInspector files)
{
    /// <summary>Reads the versions of a movie entry or one episode.</summary>
    public async Task<IReadOnlyList<VersionDto>> ForAsync(Guid entryId, Guid? episodeId, RetentionEvaluation? evaluation, bool administrator,
        CancellationToken cancellationToken)
    {
        List<(Guid BindingId, Guid ItemId, string? Path, bool IsDefault)> bindings = episodeId is { } id
            ? (await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == id).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).Select(binding => (binding.Id, binding.JellyfinItemId, binding.MediaPath, false)).ToList()
            : (await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entryId).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).Select(binding => (binding.Id, binding.JellyfinItemId, binding.MediaPath,
                    binding.JellyfinItemId == binding.VersionGroupId)).ToList();
        if (bindings.Count == 0) return [];
        if (episodeId is not null && bindings.Count > 0)
            bindings[0] = bindings[0] with { IsDefault = true };
        var labels = (await database.ImportOperations.AsNoTracking()
                .Where(operation => operation.BindingId != null && operation.VersionLabel != null && operation.EntryId == entryId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(operation => operation.BindingId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(operation => operation.CompletedAt).First().VersionLabel!);
        var seeds = await database.SeedReleaseOperations.AsNoTracking()
            .Where(seed => seed.EntryId == entryId && SeedReleaseStates.Open.Contains(seed.State))
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
            var retention = seeding
                ? new VersionRetentionDto("waiting", administrator ? SeedReleaseReasons.GoalUnmet : "seeding")
                : evaluation is null ? null
                : new VersionRetentionDto(evaluation.State, administrator ? evaluation.Reason : null);
            result.Add(new VersionDto(binding.ItemId, binding.ItemId.ToString("N", CultureInfo.InvariantCulture), binding.BindingId,
                labels.GetValueOrDefault(binding.BindingId) ?? Label(binding.Path), quality, resolution ?? ResolutionOf(video),
                video?.Width, video?.Height, video?.Codec, video?.VideoRange.ToString(), video?.BitDepth, audio?.Codec, audio?.Channels,
                inspected ? (long)file.LogicalBytes : null, binding.IsDefault, retention));
        }

        return result;
    }

    /// <summary>Assesses whether a movie can be upgraded now, and names an open upgrade.</summary>
    public async Task<UpgradeStateDto> UpgradeAsync(Entry entry, CancellationToken cancellationToken)
    {
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var profileId = entry.QualityProfileId ?? settings.DefaultQualityProfileId;
        var profile = profileId is { } id
            ? await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var held = await VersionQuality.HeldAsync(database, entry.Id, null, cancellationToken).ConfigureAwait(false);
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
