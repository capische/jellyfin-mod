using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>
/// Which held file is the phone copy (Phase 12 M31). Nothing stores copies: they follow from the files a title holds, by the
/// rule the detail page already uses to pick a copy per device (V1 decision 4). The best copy, the one TVs and computers play,
/// is the highest resolution; the phone copy is the best file at 1080p or below, standard dynamic range first. A 1440p is above
/// it (D114, 2026-10-29: with many files a phone must not start the 2K the mark does not name), though retention ranks 1440p
/// with 1080p (P4.FU9). A single file is both and nothing is marked; the phone copy is missing when every held file is above
/// 1080p. A file whose resolution cannot be read is never taken for
/// either, so an unknown file never makes the phone copy "missing".
/// </summary>
public static class PhoneCopies
{
    /// <summary>Whether a resolution (<c>1080p</c>) is one a phone copy may have: 1080p or below, never 1440p.</summary>
    public static bool Fits(string? resolution) => resolution is "1080p" or "720p" or "576p" or "480p";

    /// <summary>Whether a known resolution is above what a phone copy may have (2160p, 1440p).</summary>
    private static bool Above(string? resolution) => VersionQuality.RankOf(resolution) > 0 && !Fits(resolution);

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
        versions.Count > 0 && versions.All(version => Above(version.Resolution));

    /// <summary>
    /// Whether a movie or an episode holds files and none of them at 1080p or below, from the files it holds (bound, and not yet
    /// bound when <paramref name="library"/> is given); a file whose name says nothing is read from Jellyfin's probed size.
    /// </summary>
    public static async Task<bool> MissingAsync(ModDbContext database, Guid entryId, Guid? episodeId, ILibraryManager? library,
        CancellationToken cancellationToken)
    {
        var held = await VersionQuality.HeldAsync(database, entryId, episodeId, cancellationToken, library).ConfigureAwait(false);
        return held.Count > 0 && held.All(version => Above(ResolutionOf(version, library)));
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

    private static string? ResolutionOf(HeldVersion version, ILibraryManager? library)
    {
        if (VersionQuality.RankOf(version.Resolution) > 0 || library is null) return version.Resolution;
        try
        {
            return library.GetItemById(version.JellyfinItemId) is MediaBrowser.Controller.Entities.Video video
                ? VersionQuality.NativeResolution(video.Width, video.Height)
                : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool HighRange(string? videoRange) =>
        videoRange is { Length: > 0 } range && !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase);
}
