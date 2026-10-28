using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>
/// Which held file is the phone copy (Phase 12 M31). Nothing stores copies: they follow from the files a title holds, by the
/// rule the detail page already uses to pick a copy per device (V1 decision 4). The best copy, the one TVs and computers play,
/// is the highest resolution; the phone copy is the best file at 1080p or below (the 1080p tier, 1440p included, as
/// <see cref="VersionQuality.RankOf"/> ranks it), standard dynamic range first. A single file is both and nothing is marked;
/// the phone copy is missing when every held file is above 1080p. A file whose resolution cannot be read is never taken for
/// either, so an unknown file never makes the phone copy "missing".
/// </summary>
public static class PhoneCopies
{
    /// <summary>The highest resolution tier a phone copy may have (1080p, <see cref="VersionQuality.RankOf"/>).</summary>
    public const int PhoneTier = 3;

    /// <summary>Whether a resolution (<c>1080p</c>) is one a phone copy may have.</summary>
    public static bool Fits(string? resolution) => VersionQuality.RankOf(resolution) is > 0 and <= PhoneTier;

    /// <summary>
    /// Marks the phone copy among a title's versions, as listed (the plugin's default first): only with two or more files, the
    /// best one at 1080p or below, SDR before HDR at the same tier, the first listed on a tie.
    /// </summary>
    public static IReadOnlyList<VersionDto> Mark(IReadOnlyList<VersionDto> versions)
    {
        if (versions.Count < 2) return versions;
        var phone = versions.Select((version, index) => (version, index))
            .Where(item => Fits(item.version.Resolution))
            .OrderByDescending(item => VersionQuality.RankOf(item.version.Resolution))
            .ThenBy(item => HighRange(item.version.VideoRange) ? 1 : 0)
            .ThenBy(item => item.index)
            .Select(item => item.version).FirstOrDefault();
        return phone is null ? versions : versions.Select(version => ReferenceEquals(version, phone) ? version with { PhoneCopy = true } : version).ToArray();
    }

    /// <summary>Whether a title's listed versions lack a phone copy: at least one file and every file above 1080p.</summary>
    public static bool Missing(IReadOnlyList<VersionDto> versions) =>
        versions.Count > 0 && versions.All(version => VersionQuality.RankOf(version.Resolution) > PhoneTier);

    /// <summary>
    /// Whether a movie or an episode holds files and none of them at 1080p or below, from the files it holds (bound, and not yet
    /// bound when <paramref name="library"/> is given); a file whose name says nothing is read from Jellyfin's probed size.
    /// </summary>
    public static async Task<bool> MissingAsync(ModDbContext database, Guid entryId, Guid? episodeId, ILibraryManager? library,
        CancellationToken cancellationToken)
    {
        var held = await VersionQuality.HeldAsync(database, entryId, episodeId, cancellationToken, library).ConfigureAwait(false);
        return held.Count > 0 && held.All(version => Rank(version, library) > PhoneTier);
    }

    /// <summary>
    /// Whether a file at 1080p or below was removed from this movie or episode by retention or by Remove This Version (Q6, user
    /// 2026-10-28): automation never fetches such a copy again; Get Another Quality still can.
    /// </summary>
    public static async Task<bool> RemovedAsync(ModDbContext database, Guid entryId, Guid? episodeId, CancellationToken cancellationToken)
    {
        var paths = await database.RetentionOperations.AsNoTracking()
            .Where(operation => operation.EntryId == entryId && operation.EpisodeId == episodeId &&
                operation.State == RetentionOperationStates.Completed &&
                (operation.Provenance == RetentionProvenances.Retention || operation.Provenance == RetentionProvenances.VersionRemoved))
            .Select(operation => operation.MediaPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        return paths.Any(path => Fits(VersionQuality.Parse(path).Resolution));
    }

    private static int Rank(HeldVersion version, ILibraryManager? library)
    {
        var rank = VersionQuality.RankOf(version.Resolution);
        if (rank > 0 || library is null) return rank;
        try
        {
            return library.GetItemById(version.JellyfinItemId) is MediaBrowser.Controller.Entities.Video video
                ? VersionQuality.NativeRank(video.Width, video.Height)
                : 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return 0;
        }
    }

    private static bool HighRange(string? videoRange) =>
        videoRange is { Length: > 0 } range && !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase);
}
