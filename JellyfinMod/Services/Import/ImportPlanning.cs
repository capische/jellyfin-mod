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
    public static string? Map(string clientPath, IEnumerable<DownloadClientPathMapping> mappings, AcquisitionDownloadClient client) =>
        Resolve(clientPath, mappings, client, out var local) == PathMapOutcome.Mapped ? local : null;

    /// <summary>
    /// Resolves a client path through the longest matching explicit mapping, then the client's own verified download-folder
    /// pair, and says whether a verified prefix matched, an unverified prefix was the best match, or nothing matched.
    /// </summary>
    public static PathMapOutcome Resolve(string clientPath, IEnumerable<DownloadClientPathMapping> mappings,
        AcquisitionDownloadClient client, out string? localPath)
    {
        localPath = null;
        var normalized = Normalize(clientPath);
        if (normalized is null) return PathMapOutcome.Unmatched;
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
            if (!verified) return PathMapOutcome.Unverified;
            var relative = normalized.Length == prefix!.Length ? string.Empty : normalized[(prefix.Length + (prefix == "/" ? 0 : 1))..];
            localPath = relative.Length == 0 ? local : local!.TrimEnd('/') + "/" + relative;
            return PathMapOutcome.Mapped;
        }

        return PathMapOutcome.Unmatched;
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

    /// <summary>
    /// The files a client's remove-with-data would delete, as this server sees them: each of the torrent's current files at
    /// its current download directory, mapped and resolved through every symbolic link. Null when any of them cannot be
    /// mapped or resolved, which must refuse the deletion (whole-review P1 5 and P1 9).
    /// </summary>
    public static IReadOnlyList<string>? ResolveTorrentData(ClientTorrentStatus torrent, IEnumerable<DownloadClientPathMapping> mappings,
        AcquisitionDownloadClient client, UnixFileInspector files)
    {
        var mappingList = mappings as IReadOnlyCollection<DownloadClientPathMapping> ?? mappings.ToArray();
        var result = new List<string>(torrent.Files.Count);
        foreach (var file in torrent.Files)
        {
            if (Normalize((torrent.DownloadDirectory ?? string.Empty) + "/" + file.Name) is not { } clientPath ||
                Map(clientPath, mappingList, client) is not { } localPath ||
                !files.TryResolveForDeletion(localPath, out var resolved))
                return null;
            result.Add(resolved);
        }

        return result;
    }

    /// <summary>
    /// The version of a client's mappings: every save replaces the rows with new identities, so any save that leaves a
    /// mapping, even of the same prefixes, changes it. Two empty sets have the same version; an edit started from an empty
    /// set and saved over another empty one overwrites nothing (Codex delta review 6, P2).
    /// </summary>
    public static string MappingsVersion(IEnumerable<JellyfinMod.Data.DownloadClientPathMapping> mappings) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',
            mappings.OrderBy(mapping => mapping.Order).Select(mapping =>
                $"{mapping.Id:N}|{mapping.Order}|{mapping.ClientPathPrefix}|{mapping.LocalPathPrefix}")))))[..16];

    /// <summary>Returns true when <paramref name="path"/> equals or lies below <paramref name="prefix"/>.</summary>
    public static bool Within(string prefix, string path) =>
        prefix == "/" || path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
}

/// <summary>How a client path resolved against a client's path mappings.</summary>
public enum PathMapOutcome
{
    /// <summary>No mapping prefix covers the path.</summary>
    Unmatched,

    /// <summary>A verified prefix mapped the path to a local one.</summary>
    Mapped,

    /// <summary>The best matching prefix is unverified, so the path must not be used.</summary>
    Unverified
}

/// <summary>The file chosen from a completed torrent, or why none could be chosen.</summary>
public sealed record ImportFileChoice(ClientTorrentFile? File, string? Reason, string? Detail);

/// <summary>
/// A pack's files mapped to the episodes it claimed (season and series packs, 2026-10-08), the files no claimed episode takes
/// with why, or why nothing could be mapped at all.
/// </summary>
public sealed record PackMapping(IReadOnlyDictionary<(int Season, int Episode), ClientTorrentFile> Files,
    IReadOnlyList<(string Name, string Reason)> Skipped, string? Reason, string? Detail)
{
    /// <summary>The sentence for a skip reason.</summary>
    public static string Describe(string reason) => reason switch
    {
        "special" => "specials are never imported from a pack.",
        "other_season" => "it belongs to a season the pack was not grabbed for.",
        "not_claimed" => "its episode was not grabbed from this pack.",
        "multi_episode" => "it holds several episodes in one file.",
        "unnumbered" => "no season and episode number could be read from its name.",
        "duplicate" => "another file of the pack is the same episode.",
        _ => "it could not be matched to an episode."
    };
}

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

    /// <summary>
    /// Maps a completed pack's video files to the episodes it claimed by their names, and a season folder when the file name
    /// carries only an episode number. Samples, trailers and extras, and files under a tenth of the largest video, are
    /// ignored silently; specials, other seasons, unclaimed episodes, multi-episode and unnumbered files are skipped and
    /// named. Two files of one episode keep the larger and skip the other.
    /// </summary>
    public static PackMapping MapPack(ClientTorrentStatus torrent, string videoExtensions, IReadOnlySet<(int Season, int Episode)> claimed)
    {
        var allowed = videoExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.TrimStart('.').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var wanted = torrent.Files.Where(file => file.Wanted).ToArray();
        var videos = wanted.Where(file => allowed.Contains(Extension(file.Name)) && !IsExtra(file.Name, torrent.Name)).ToArray();
        if (videos.Length == 0)
        {
            var archive = wanted.Any(file => ImportDefaults.ArchiveExtensions.Contains(Extension(file.Name)) || RarPartPattern().IsMatch(file.Name));
            return new(new Dictionary<(int, int), ClientTorrentFile>(), [],
                archive ? ImportReasons.ArchiveUnsupported : ImportReasons.NoVideoFile,
                archive ? "The release is packed in an archive; archives are never extracted." : "The torrent holds no allow-listed video file.");
        }

        var floor = videos.Max(file => file.Length) / 10;
        var claimedSeasons = claimed.Select(item => item.Season).ToHashSet();
        var files = new Dictionary<(int, int), ClientTorrentFile>();
        var skipped = new List<(string, string)>();
        foreach (var file in videos.Where(file => file.Length >= floor).OrderByDescending(file => file.Length))
        {
            var (season, episodes) = Numbering(file.Name);
            string? reason = season is null || episodes.Count == 0 ? "unnumbered"
                : season == 0 ? "special"
                : episodes.Count > 1 ? "multi_episode"
                : !claimedSeasons.Contains(season.Value) ? "other_season"
                : !claimed.Contains((season.Value, episodes[0])) ? "not_claimed"
                : files.ContainsKey((season.Value, episodes[0])) ? "duplicate"
                : null;
            if (reason is null) files[(season!.Value, episodes[0])] = file;
            else skipped.Add((file.Name, reason));
        }

        return new(files, skipped, null, null);
    }

    /// <summary>
    /// A file's season and episodes: from its own name, else from a folder's; a name with only an episode number
    /// (<c>E03</c>, <c>Episode 3</c>, <c>03 - Title</c>) takes its season from the nearest season folder.
    /// </summary>
    private static (int? Season, IReadOnlyList<int> Episodes) Numbering(string name)
    {
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var own = ReleaseParser.Parse(Path.GetFileNameWithoutExtension(segments[^1]));
        if (own.SeasonNumber is not null && own.EpisodeNumbers.Count > 0) return (own.SeasonNumber, own.EpisodeNumbers);
        int? folderSeason = null;
        for (var index = segments.Length - 2; index >= 0 && folderSeason is null; index--)
        {
            var folder = ReleaseParser.Parse(segments[index]);
            if (folder.SeasonNumber is not null && folder.SeasonLast is null) folderSeason = folder.SeasonNumber;
        }

        if (folderSeason is null) return (null, []);
        var stem = Path.GetFileNameWithoutExtension(segments[^1]);
        var bare = BareEpisodePattern().Match(stem);
        if (!bare.Success) return (null, []);
        List<int> episodes = [int.Parse(bare.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture)];
        // Every number right after the first (E01E02, E01E02E03, E01-E02-E03, E01-02, 01-02, 01 & 02) makes a multi-episode
        // file, never the first episode alone: it is skipped as several episodes, like S01E01E02 (Codex review of the pack
        // plugin, finding 6; re-review, finding 4: three or more). A quality or a title after the number ("E01 - 720p",
        // "03 - Title") is not another episode.
        var rest = stem[(bare.Index + bare.Length)..];
        for (var more = BareEpisodeContinuation().Match(rest); more.Success; more = BareEpisodeContinuation().Match(rest))
        {
            episodes.Add(int.Parse(more.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture));
            rest = rest[more.Length..];
        }

        return (folderSeason, episodes);
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

    [GeneratedRegex(@"^(?:.*?[\s._\-])??(?:E|Ep|Episode)[\s._\-]?(?<episode>\d{1,3})(?![0-9])|^(?<episode>\d{1,3})(?=[\s._\-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareEpisodePattern();

    [GeneratedRegex(@"^(?:[\s._]*[-&+~][\s._]*(?:E|Ep|Episode)?[\s._]?|[\s._]*(?:E|Ep|Episode)[\s._]?)(?<episode>\d{1,3})(?=[\s._\-\[(]|$|(?:E|Ep|Episode)[\s._]?\d)", RegexOptions.IgnoreCase)]
    private static partial Regex BareEpisodeContinuation();
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

    /// <summary>
    /// The first video directly in <paramref name="folder"/> that stops Jellyfin 12 from grouping the folder's videos as
    /// versions of one movie, or null when a new <c>Folder - Label</c> file would be grouped with them (v12.0
    /// <c>VideoListResolver.IsEligibleForMultiVersion</c>): every name must start with the folder name, followed by nothing,
    /// a <c>-</c>, <c>_</c> or <c>.</c>, or a bracketed label. Extras (<c>-trailer</c> and the like) do not take part. This is
    /// stricter than Jellyfin, which also strips release words first, so it can only refuse a case Jellyfin would group.
    /// </summary>
    public static string? NonGroupingVideo(string folder, string folderName)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!VideoExtensionPattern().IsMatch(Path.GetExtension(file)) || ExtraSuffixPattern().IsMatch(name)) continue;
                if (!name.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(file);
                var rest = name[folderName.Length..].Trim();
                if (rest.Length == 0 || rest[0] is '-' or '_' or '.' || rest[0] == '[' && rest.Contains(']', StringComparison.Ordinal))
                    continue;
                return Path.GetFileName(file);
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder cannot be shown to group; nothing is linked into it.
            return Path.GetFileName(folder);
        }
    }

    /// <summary>Whether a held episode file's own name reads as exactly this season and episode (one episode, not a range).</summary>
    public static bool IsSingleEpisodeFile(string path, int season, int episode)
    {
        var parsed = ReleaseParser.Parse(Path.GetFileNameWithoutExtension(path));
        return parsed.SeasonNumber == season && parsed.EpisodeNumbers.Count == 1 && parsed.EpisodeNumbers[0] == episode;
    }

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

    /// <summary>
    /// A version label that names no resolution, for another version that must not become Jellyfin's main one: Jellyfin 12 puts
    /// a file whose name matches <c>[0-9]{3,}[ip]</c> ahead of one without (v12.0 <c>VideoListResolver</c>). The source, else
    /// the release group, else <c>Version</c> (user, 2026-10-09).
    /// </summary>
    public static string UnrankedVersionLabel(ParsedRelease? parsed)
    {
        var label = VersionLabel(parsed);
        if (parsed?.Resolution is { } resolution && label.StartsWith(resolution, StringComparison.Ordinal))
            label = label[resolution.Length..].Trim();
        if (label.Length == 0 || label == "v1" || ResolutionTokenPattern().IsMatch(label))
            label = !string.IsNullOrWhiteSpace(parsed?.Group) && Sanitize(parsed.Group) is { Length: > 0 } group &&
                !ResolutionTokenPattern().IsMatch(group) ? group : "Version";
        return label;
    }

    /// <summary>
    /// The stems a lower version may be named after, so that it sorts right after the main file it goes beside: the main file's
    /// own name when it names no resolution. Otherwise that name with Jellyfin's resolution tokens (<c>720p</c>) and the
    /// plugin's own aliases (<c>4K</c>, <c>UHD</c>) taken out, then with each alias kept but ended by a letter (<c>4Kx</c>),
    /// which neither reads as a resolution nor sorts ahead of the main file. A resolution the main file names is never carried
    /// into the new file's name (Codex review of the user fixes, finding 1); each name is still checked against Jellyfin's own
    /// grouping before it is used.
    /// </summary>
    public static IReadOnlyList<string> UnrankedStems(string? mainStem)
    {
        if (string.IsNullOrWhiteSpace(mainStem)) return [];
        if (!NamesResolution(mainStem)) return [mainStem];
        var bare = ResolutionTokenPattern().Replace(mainStem, string.Empty);
        string Tidy(string value) => SpacesPattern().Replace(value, " ").Trim(' ', '-', '.', '_');
        return new[] { Tidy(AliasPattern().Replace(bare, string.Empty)), Tidy(AliasPattern().Replace(bare, match => match.Value + "x")) }
            .Where(stem => stem.Length > 0 && !NamesResolution(stem)).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Whether a name says a resolution to Jellyfin (its version ordering token) or to the plugin's own readers
    /// (<see cref="Automation.VersionQuality.Parse"/>, which also reads <c>4K</c> and <c>UHD</c>).
    /// </summary>
    public static bool NamesResolution(string stem) =>
        ResolutionTokenPattern().IsMatch(stem) || AliasPattern().IsMatch(stem) ||
        Automation.VersionQuality.Parse("/" + stem + ".mkv").Resolution is not null;

    /// <summary>Another version named after the file it goes beside: <c>Show - S01E01 - Pilot - WEB-DL.mkv</c>, never cut short.</summary>
    public static string VersionBeside(string mainStem, string label, string extension) =>
        mainStem + " - " + Sanitize(label) + "." + extension;

    /// <summary>Whether a file name fits the filesystem's 255-byte limit; a longer one is never cut, it is not used.</summary>
    public static bool FitsFileName(string name) => name.Length > 0 && Encoding.UTF8.GetByteCount(name) <= 255;

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

    [GeneratedRegex(@"^\.(mkv|mp4|m4v|avi|mov|wmv|ts|m2ts|webm|mpg|mpeg|flv|iso|vob|ogv|3gp|divx|xvid|rmvb|strm)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoExtensionPattern();

    [GeneratedRegex(@"(^|[-_. ])(trailer|sample|featurette|behindthescenes|deleted|deletedscene|interview|scene|short|clip|other|extra|teaser)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraSuffixPattern();

    private static string StripProviderTag(string folderName) => ProviderTagPattern().Replace(folderName, string.Empty).Trim();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesPattern();

    /// <summary>The resolutions the plugin's release parser reads, aliases included (<c>ReleaseParser.ResolutionPattern</c>).</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex AliasPattern();

    /// <summary>Jellyfin 12's resolution pattern for version ordering (v12.0 <c>VideoListResolver.ResolutionRegex</c>).</summary>
    [GeneratedRegex(@"[0-9]{2}[0-9]+[ip]", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionTokenPattern();

    [GeneratedRegex(@"\s*\[(tmdbid|imdbid|tvdbid)-[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderTagPattern();
}
