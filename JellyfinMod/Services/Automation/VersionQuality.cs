using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Automation;

/// <summary>One held version of a target, read from its binding's file name.</summary>
/// <param name="BindingId">The binding.</param>
/// <param name="JellyfinItemId">The native item.</param>
/// <param name="MediaPath">The native media path.</param>
/// <param name="Quality">The quality identifier parsed from the file name, or null.</param>
/// <param name="Resolution">The parsed resolution, or null.</param>
/// <param name="MultiEpisode">Whether the file's own name holds more than one episode (<c>S01E01-E02</c>).</param>
public sealed record HeldVersion(Guid BindingId, Guid JellyfinItemId, string? MediaPath, string? Quality, string? Resolution,
    bool MultiEpisode = false);

/// <summary>
/// Reads the quality of held versions from their file names, with the same independent parser Phase 4 uses for release
/// titles (P6.M5/M7). Unknown stays unknown: a version whose quality cannot be read is never assumed to be good.
/// </summary>
public static class VersionQuality
{
    /// <summary>Parses the quality of a media file from its name, then its folder.</summary>
    public static (string? Quality, string? Resolution) Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, null);
        foreach (var name in new[] { Path.GetFileNameWithoutExtension(path), Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty) })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Jellyfin's version suffix " - 1080p BluRay" parses like a release title once the separator is dotted.
            var parsed = ReleaseParser.Parse(name.Replace(" - ", ".", StringComparison.Ordinal).Replace(' ', '.'));
            if (parsed.Resolution is not null) return (parsed.Quality, parsed.Resolution);
        }

        return (null, null);
    }

    /// <summary>Ranks a resolution: 2160p highest, unknown 0.</summary>
    public static int ResolutionRank(string? path) => RankOf(Parse(path).Resolution);

    /// <summary>Ranks a parsed resolution (<c>1080p</c>): 2160p highest, unknown 0.</summary>
    public static int RankOf(string? resolution) => resolution switch
    {
        "2160p" => 4,
        "1080p" => 3,
        "720p" => 2,
        "576p" or "480p" => 1,
        _ => 0
    };

    /// <summary>
    /// Classifies a native video size as the larger of its width tier and its height tier, on the boundaries Jellyfin 12's own
    /// media info uses, so a scope-ratio 3840×1600 file is 2160p and 1920×800 is 1080p like the stock row says, not the
    /// tier its height alone would give. Sizes above 4K stay 2160p, the highest tier; null when neither side is known.
    /// </summary>
    public static string? NativeResolution(int? width, int? height)
    {
        int w = width ?? 0, h = height ?? 0;
        if (w <= 0 && h <= 0) return null;
        if (w > 2560 || h > 1440) return "2160p";
        if (w > 1280 || h > 962) return "1080p";
        if (w > 1024 || h > 576) return "720p";
        return "480p";
    }

    /// <summary>Ranks a native video size the same way as a parsed resolution.</summary>
    public static int NativeRank(int? width, int? height) => RankOf(NativeResolution(width, height));

    /// <summary>
    /// Orders known resolutions finely, 576p above 480p, for deciding which of an episode's versions Jellyfin should take as
    /// its default (Codex review of the user fixes, finding 4); unknown 0. Retention keeps its coarser <see cref="RankOf"/>.
    /// </summary>
    public static int OrderOf(string? resolution) => resolution switch
    {
        "2160p" => 5,
        "1080p" => 4,
        "720p" => 3,
        "576p" => 2,
        "480p" => 1,
        _ => 0
    };

    /// <summary>
    /// The same order from the size Jellyfin probed, classified by <see cref="NativeResolution"/>. Its lowest tier holds both
    /// 576p and 480p; inside it a picture at least 540 lines tall (PAL 720×576, 1024×576, 960×540) is ordered as 576p, above
    /// NTSC 480 lines, so a 576p file is never taken for the lower one. Unknown 0.
    /// </summary>
    public static int OrderOf(int? width, int? height)
    {
        var resolution = NativeResolution(width, height);
        return OrderOf(resolution == "480p" && height >= 540 ? "576p" : resolution);
    }

    /// <summary>The quality and resolution a version label names (<c>720p WEB-DL</c>), for a file whose own name does not.</summary>
    public static (string? Quality, string? Resolution) ParseLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return (null, null);
        var parsed = ReleaseParser.Parse("Version." + label.Trim().Replace(' ', '.'));
        return (parsed.Quality, parsed.Resolution);
    }

    /// <summary>Returns a quality's position in a profile, best first; qualities outside the profile rank last.</summary>
    public static int ProfileIndex(IReadOnlyList<string> profile, string? quality)
    {
        if (quality is null) return int.MaxValue;
        var index = profile.ToList().IndexOf(quality);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// Loads the held versions of a movie entry or an episode: every bound file, and with <paramref name="library"/> every
    /// further file Jellyfin plays for it that is not bound yet, with an empty binding id (V1, analysis C12), so a copy the
    /// plugin has not tracked is still a held quality and is never grabbed again.
    /// </summary>
    public static async Task<IReadOnlyList<HeldVersion>> HeldAsync(ModDbContext database, Guid entryId, Guid? episodeId,
        CancellationToken cancellationToken, MediaBrowser.Controller.Library.ILibraryManager? library = null)
    {
        var bound = episodeId is { } id
            ? (await database.EpisodeBindings.AsNoTracking().Where(binding => binding.EpisodeId == id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(binding => (binding.Id, binding.JellyfinItemId, Owner: binding.OwnerItemId ?? binding.JellyfinItemId, binding.MediaPath))
                .ToArray()
            : (await database.EntryBindings.AsNoTracking().Where(binding => binding.EntryId == entryId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(binding => (binding.Id, binding.JellyfinItemId, Owner: binding.OwnerItemId ?? binding.JellyfinItemId, binding.MediaPath))
                .ToArray();
        var held = bound.Select(binding => Held(binding.Id, binding.JellyfinItemId, binding.MediaPath)).ToList();
        if (library is not null)
            foreach (var owner in bound.Select(binding => binding.Owner).Distinct())
            {
                try
                {
                    if (library.GetItemById(owner) is not MediaBrowser.Controller.Entities.Video video) continue;
                    var versions = NativeVersions.Read(library, NativeVersions.MainOf(library, video), null);
                    foreach (var version in versions.Versions.Where(version => held.All(known =>
                                 known.JellyfinItemId != version.Item.Id && !string.Equals(known.MediaPath, version.Path, StringComparison.Ordinal))))
                        held.Add(Held(Guid.Empty, version.Item.Id, version.Path));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // What cannot be read now is read again on the next run; the bound versions stand.
                }
            }

        return await WithImportedQualityAsync(database, entryId, held, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a held file whose name carries no quality the quality of the release the plugin imported it from (V1, D1). An
    /// episode's first file is named <c>Series (Year) SNNENN</c> with no label, so without this its quality was unknown and
    /// automation never upgraded it (<c>held_quality_unknown</c>); files already imported that way are covered too, because
    /// the release is read from the import record. Only while the file at that path is still the inode the import linked:
    /// a file put there since is not that release, and its quality stays unknown.
    /// </summary>
    private static async Task<List<HeldVersion>> WithImportedQualityAsync(ModDbContext database, Guid entryId, List<HeldVersion> held,
        CancellationToken cancellationToken)
    {
        if (!held.Any(version => version.Quality is null && !string.IsNullOrWhiteSpace(version.MediaPath))) return held;
        var imports = await database.ImportOperations.AsNoTracking()
            .Where(operation => operation.EntryId == entryId && operation.State == ImportStates.Completed &&
                operation.DestinationPath != null && operation.DestinationPhysicalIdentity != null)
            .Select(operation => new { operation.DestinationPath, operation.DestinationPhysicalIdentity, operation.ReleaseTitle,
                operation.UpdatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (imports.Count == 0) return held;
        var files = new UnixFileInspector();
        return held.Select(version =>
        {
            if (version.Quality is not null || string.IsNullOrWhiteSpace(version.MediaPath) ||
                !files.TryInspect(version.MediaPath, out var file))
                return version;
            var import = imports.Where(candidate => string.Equals(candidate.DestinationPath, file.CanonicalPath, StringComparison.Ordinal) &&
                    string.Equals(candidate.DestinationPhysicalIdentity, file.PhysicalIdentity, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
            if (import is null) return version;
            var release = ReleaseParser.Parse(import.ReleaseTitle);
            return release.Quality is null ? version : version with { Quality = release.Quality, Resolution = release.Resolution ?? version.Resolution };
        }).ToList();
    }

    private static HeldVersion Held(Guid bindingId, Guid itemId, string? path)
    {
        var (quality, resolution) = Parse(path);
        var multiEpisode = !string.IsNullOrWhiteSpace(path) &&
            ReleaseParser.Parse(Path.GetFileNameWithoutExtension(path)).EpisodeNumbers.Count > 1;
        return new HeldVersion(bindingId, itemId, path, quality, resolution, multiEpisode);
    }
}

/// <summary>Whether a target's profile asks for a better version, and why not.</summary>
/// <param name="Eligible">Whether automation may upgrade it now.</param>
/// <param name="Cutoff">The profile's cutoff.</param>
/// <param name="HeldBest">The best held quality.</param>
/// <param name="Mode">The profile's upgrade mode.</param>
/// <param name="BlockedReason">A stable reason when not eligible.</param>
/// <param name="Superseded">The held version an upgrade supersedes: the best held one, which is below the cutoff.</param>
public sealed record UpgradeAssessment(bool Eligible, string? Cutoff, string? HeldBest, string Mode, string? BlockedReason,
    HeldVersion? Superseded)
{
    /// <summary>Assesses held versions against a profile (P6 defaults table).</summary>
    public static UpgradeAssessment For(AcquisitionQualityProfile? profile, IReadOnlyList<HeldVersion> held, bool episode,
        bool episodeUpgradesEnabled)
    {
        var mode = profile?.UpgradeMode ?? "replace";
        if (held.Count == 0) return new(false, profile?.Cutoff, null, mode, "no_file", null);
        var qualities = profile is null ? [] : AcquisitionConfiguration.Qualities(profile);
        var best = held.OrderBy(version => VersionQuality.ProfileIndex(qualities, version.Quality)).First();
        if (profile is null || !profile.UpgradeAllowed || profile.Cutoff is null)
            return new(false, profile?.Cutoff, best.Quality, mode, AutomationReasons.UpgradeNotAllowed, null);
        if (episode && !episodeUpgradesEnabled)
            return new(false, profile.Cutoff, best.Quality, mode, AutomationReasons.EpisodeVersionsUnsupported, null);
        // A single-episode upgrade would group under the first episode while the later ones still need the multi-episode
        // file, which is never replaced: the grab could never finish its job (V1, answer 4).
        if (episode && held.Any(version => version.MultiEpisode))
            return new(false, profile.Cutoff, best.Quality, mode, AutomationReasons.MultiEpisodeHeld, null);
        // A file whose quality cannot be read from its name might already be excellent; it is never replaced blindly.
        if (best.Quality is null)
            return new(false, profile.Cutoff, null, mode, AutomationReasons.HeldQualityUnknown, null);
        if (VersionQuality.ProfileIndex(qualities, best.Quality) <= VersionQuality.ProfileIndex(qualities, profile.Cutoff))
            return new(false, profile.Cutoff, best.Quality, mode, AutomationReasons.AlreadyHeldAtCutoff, null);
        // A copy Jellyfin plays that is not bound yet cannot be replaced; reconciliation binds it first (V1).
        if (best.BindingId == Guid.Empty)
            return new(false, profile.Cutoff, best.Quality, mode, AutomationReasons.VersionsUntracked, null);
        return new(true, profile.Cutoff, best.Quality, mode, null, best);
    }
}
