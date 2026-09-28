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
    public static int ResolutionRank(string? path) => Parse(path).Resolution switch
    {
        "2160p" => 4,
        "1080p" => 3,
        "720p" => 2,
        "576p" or "480p" => 1,
        _ => 0
    };

    /// <summary>Ranks a native video width the same way.</summary>
    public static int WidthRank(int width) => width >= 3200 ? 4 : width >= 1800 ? 3 : width >= 1200 ? 2 : width > 0 ? 1 : 0;

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
        if (library is null) return held;
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

        return held;
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
