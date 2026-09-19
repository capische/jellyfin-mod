using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;

namespace JellyfinMod.Services.Import;

/// <summary>Maps a path as the download client reports it to the same path as this server sees it (P5.I2).</summary>
public static class ImportPaths
{
    /// <summary>
    /// Applies the longest matching explicit mapping, then the client's own verified download-folder pair. Returns null when
    /// nothing maps the path, which blocks the import as <c>path_unmapped</c>.
    /// </summary>
    public static string? Map(string clientPath, IEnumerable<DownloadClientPathMapping> mappings, AcquisitionDownloadClient client)
    {
        var normalized = Normalize(clientPath);
        if (normalized is null) return null;
        // An unverified mapping is kept on the client but never used: when it is the best match, the import blocks as
        // path_unmapped instead of falling back to a shorter prefix that would point somewhere else.
        var pairs = mappings
            .Select(mapping => (Client: Normalize(mapping.ClientPathPrefix), Local: Normalize(mapping.LocalPathPrefix),
                Verified: mapping.VerifiedAt is not null, Order: mapping.Order))
            .Append((Client: Normalize(client.DownloadDirectory), Local: Normalize(client.LocalDirectory), Verified: true,
                Order: int.MaxValue))
            .Where(pair => pair.Client is not null && pair.Local is not null)
            .OrderByDescending(pair => pair.Client!.Length).ThenBy(pair => pair.Order);
        foreach (var (prefix, local, verified, _) in pairs)
        {
            if (!Within(prefix!, normalized)) continue;
            if (!verified) return null;
            var relative = normalized.Length == prefix!.Length ? string.Empty : normalized[(prefix.Length + (prefix == "/" ? 0 : 1))..];
            return relative.Length == 0 ? local : local!.TrimEnd('/') + "/" + relative;
        }

        return null;
    }

    /// <summary>Normalizes an absolute path: forward slashes, no trailing slash, no <c>..</c> segment.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Trim().Replace('\\', '/');
        if (!value.StartsWith('/') || value.Contains('\0', StringComparison.Ordinal)) return null;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return null;
        return "/" + string.Join('/', segments);
    }

    /// <summary>Returns true when <paramref name="path"/> equals or lies below <paramref name="prefix"/>.</summary>
    public static bool Within(string prefix, string path) =>
        prefix == "/" || path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
}

/// <summary>The file chosen from a completed torrent, or why none could be chosen.</summary>
public sealed record ImportFileChoice(ClientTorrentFile? File, string? Reason, string? Detail);

/// <summary>Chooses the one file of a torrent that is the title (P5.I1 defaults table).</summary>
public static partial class ImportFileSelector
{
    /// <summary>
    /// Exactly one allow-listed video file of at least 90 % of the largest such file, excluding samples, trailers and
    /// extras. An episode's file must also parse to the grabbed season and episode.
    /// </summary>
    public static ImportFileChoice Choose(ClientTorrentStatus torrent, string videoExtensions, Episode? episode)
    {
        var allowed = videoExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.TrimStart('.').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var wanted = torrent.Files.Where(file => file.Wanted).ToArray();
        var videos = wanted.Where(file => allowed.Contains(Extension(file.Name)) && !IsExtra(file.Name, torrent.Name)).ToArray();
        if (videos.Length == 0)
        {
            return wanted.Any(file => ImportDefaults.ArchiveExtensions.Contains(Extension(file.Name)) || RarPartPattern().IsMatch(file.Name))
                ? new(null, ImportReasons.ArchiveUnsupported, "The release is packed in an archive; archives are never extracted.")
                : new(null, ImportReasons.NoVideoFile, "The torrent holds no allow-listed video file.");
        }

        var largest = videos.Max(file => file.Length);
        var candidates = videos.Where(file => file.Length >= largest * 0.9).ToArray();
        if (candidates.Length != 1)
            return new(null, ImportReasons.AmbiguousFiles, $"{candidates.Length} files could be the title.");
        var chosen = candidates[0];
        if (episode is not null && !MatchesEpisode(chosen.Name, episode))
            return new(null, ImportReasons.EpisodeMismatch,
                $"The file is not numbered S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}.");
        return new(chosen, null, null);
    }

    /// <summary>Returns the lower-case extension without its dot.</summary>
    public static string Extension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// A file is an extra when a folder below the torrent root is an extras folder, or when its own name carries an extras
    /// word that the release title itself does not (so "Sample Movie" is not mistaken for a sample).
    /// </summary>
    private static bool IsExtra(string name, string torrentName)
    {
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 2 && segments[1..^1].Any(segment => ExtraFolderPattern().IsMatch(segment))) return true;
        if (segments.Length == 2 && ExtraFolderPattern().IsMatch(segments[0]) && segments[0] != torrentName) return true;
        var file = Path.GetFileNameWithoutExtension(segments[^1]);
        return ExtraPattern().Matches(file).Any(match => !ExtraPattern().Matches(torrentName)
            .Any(title => string.Equals(title.Groups[2].Value, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesEpisode(string name, Episode episode)
    {
        // The file name decides; its folders are consulted only when the file name carries no numbering.
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 1; index >= 0; index--)
        {
            var parsed = ReleaseParser.Parse(index == segments.Length - 1 ? Path.GetFileNameWithoutExtension(segments[index]) : segments[index]);
            if (parsed.SeasonNumber is null || parsed.EpisodeNumbers.Count == 0) continue;
            return parsed.SeasonNumber == episode.SeasonNumber && parsed.EpisodeNumbers.Count == 1 &&
                parsed.EpisodeNumbers[0] == episode.EpisodeNumber;
        }

        return false;
    }

    [GeneratedRegex(@"(^|[\s._\-\[\(])(sample|trailer|extras?|featurette|behind[\s._\-]?the[\s._\-]?scenes)([\s._\-\]\)]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraPattern();

    [GeneratedRegex(@"^(samples?|trailers?|extras?|featurettes?|behind the scenes|deleted scenes)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraFolderPattern();

    [GeneratedRegex(@"\.r\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPartPattern();
}

/// <summary>Jellyfin-compatible names for imported files (P5.I1 defaults table).</summary>
public static partial class ImportNaming
{
    private const int MaxNameLength = 180;

    /// <summary>The folder name of a new movie or series folder: <c>Title (Year) [tmdbid-N]</c>.</summary>
    public static string TitleFolder(string title, int? year, int tmdbId)
    {
        var name = Sanitize(title);
        if (name.Length == 0) name = "Untitled";
        if (year is { } value) name += " (" + value.ToString(CultureInfo.InvariantCulture) + ")";
        return Trim(name + " [tmdbid-" + tmdbId.ToString(CultureInfo.InvariantCulture) + "]");
    }

    /// <summary>
    /// A movie file inside <paramref name="folderName"/>. Jellyfin groups versions only when every file name starts with
    /// the folder name character for character, followed by <c> - Label</c>.
    /// </summary>
    public static string MovieFile(string folderName, string label, string extension) =>
        folderName + " - " + Sanitize(label) + "." + extension;

    /// <summary>
    /// An episode file: <c>Series (Year) SNNENN.ext</c>. A version label is added only for an explicit additional version
    /// (P6.M6), which stays off until the host is shown to group episode versions.
    /// </summary>
    public static string EpisodeFile(string seriesFolderName, int season, int episode, string extension, string? label = null) =>
        Trim(StripProviderTag(seriesFolderName)) + " " +
        string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}") +
        (label is null ? string.Empty : " - " + Sanitize(label)) + "." + extension;

    /// <summary>The season folder name.</summary>
    public static string SeasonFolder(int season) =>
        season == 0 ? "Specials" : string.Create(CultureInfo.InvariantCulture, $"Season {season:00}");

    /// <summary>
    /// The version label: <c>resolution source</c>, else resolution, else release group, else <c>v1</c>.
    /// </summary>
    public static string VersionLabel(ParsedRelease? parsed)
    {
        var resolution = parsed?.Resolution;
        var source = parsed?.Source switch
        {
            "bluray" => "BluRay",
            "webdl" => "WEB-DL",
            "webrip" => "WEBRip",
            "hdtv" => "HDTV",
            "dvd" => "DVD",
            "remux" => "Remux",
            _ => null
        };
        if (resolution is not null && source is not null) return resolution + " " + source;
        if (resolution is not null) return resolution;
        if (!string.IsNullOrWhiteSpace(parsed?.Group) && Sanitize(parsed.Group).Length > 0) return Sanitize(parsed.Group);
        return "v1";
    }

    /// <summary>Removes path separators, control and reserved characters, and trailing dots and spaces.</summary>
    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        return Trim(SpacesPattern().Replace(builder.ToString(), " ").Trim().TrimEnd('.', ' '));
    }

    private static string Trim(string value) => value.Length <= MaxNameLength ? value : value[..MaxNameLength].TrimEnd('.', ' ');

    private static string StripProviderTag(string folderName) => ProviderTagPattern().Replace(folderName, string.Empty).Trim();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesPattern();

    [GeneratedRegex(@"\s*\[(tmdbid|imdbid|tvdbid)-[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderTagPattern();
}
