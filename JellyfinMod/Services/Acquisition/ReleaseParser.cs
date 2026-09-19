using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JellyfinMod.Services.Acquisition;

/// <summary>The implemented quality vocabulary: one source and one resolution (P4.A4).</summary>
public static class QualityCatalog
{
    /// <summary>Gets every quality identifier a profile may list, as <c>source-resolution</c>.</summary>
    public static IReadOnlyList<(string Id, string Source, string Resolution)> All { get; } = Build();

    /// <summary>Returns true when the identifier is part of the implemented vocabulary.</summary>
    public static bool IsKnown(string id) => All.Any(quality => quality.Id == id);

    /// <summary>Returns the identifier for a parsed source and resolution, or null when either is unknown.</summary>
    public static string? Identify(string? source, string? resolution) =>
        source is null || resolution is null ? null : $"{source}-{resolution}";

    private static List<(string, string, string)> Build()
    {
        var result = new List<(string, string, string)>();
        foreach (var resolution in new[] { "2160p", "1080p" }) result.Add(($"remux-{resolution}", "remux", resolution));
        foreach (var source in new[] { "bluray", "webdl", "webrip", "hdtv" })
            foreach (var resolution in new[] { "2160p", "1080p", "720p", "576p", "480p" })
                result.Add(($"{source}-{resolution}", source, resolution));
        foreach (var resolution in new[] { "576p", "480p" }) result.Add(($"dvd-{resolution}", "dvd", resolution));
        return result;
    }
}

/// <summary>Attributes independently parsed from a release title. Unknown values stay null.</summary>
public sealed record ParsedRelease(
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Title,
    [property: JsonPropertyName("year"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Year,
    [property: JsonPropertyName("seasonNumber"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeasonNumber,
    [property: JsonPropertyName("episodeNumbers")] IReadOnlyList<int> EpisodeNumbers,
    [property: JsonPropertyName("seasonPack")] bool SeasonPack,
    [property: JsonPropertyName("absoluteNumbering")] bool AbsoluteNumbering,
    [property: JsonPropertyName("dailyNumbering")] bool DailyNumbering,
    [property: JsonPropertyName("resolution"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Resolution,
    [property: JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
    [property: JsonPropertyName("codec"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Codec,
    [property: JsonPropertyName("audio"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Audio,
    [property: JsonPropertyName("hdr"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Hdr,
    [property: JsonPropertyName("group"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Group,
    [property: JsonPropertyName("proper")] bool Proper,
    [property: JsonPropertyName("repack")] bool Repack,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quality);

/// <summary>
/// An independently written release-title parser for movie and single-episode forms (P4.A4).
/// </summary>
/// <remarks>
/// Written for this GPL-2.0-only project from the Torznab/scene naming conventions alone; no Sonarr or Radarr
/// (GPL-3.0) parser code or expressions were used. It recognises only what acquisition needs to reject or rank
/// a release and leaves every other attribute null rather than guessing.
/// </remarks>
public static partial class ReleaseParser
{
    private static readonly HashSet<string> NotGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "dl", "rip", "web", "hd", "x264", "x265", "h264", "h265", "hevc", "avc", "1080p", "720p", "2160p",
        "480p", "576p", "remux", "bluray", "hdtv", "atmos", "dts", "aac", "ac3", "ddp", "truehd", "proper",
        "repack", "internal", "multi", "subs"
    };

    /// <summary>Parses a raw release title.</summary>
    public static ParsedRelease Parse(string rawTitle)
    {
        var title = (rawTitle ?? string.Empty).Trim();
        if (title.Length > 512) title = title[..512];
        // Strip a trailing container extension; it is not part of the name.
        title = ExtensionPattern().Replace(title, string.Empty);

        int? season = null;
        var episodes = new List<int>();
        var seasonPack = false;
        var absolute = false;
        var daily = false;
        var markerIndex = -1;

        var episodeMatch = EpisodePattern().Match(title);
        if (episodeMatch.Success)
        {
            season = Int(episodeMatch.Groups["season"].Value);
            episodes.Add(Int(episodeMatch.Groups["episode"].Value));
            foreach (Capture extra in episodeMatch.Groups["more"].Captures)
            {
                var number = Int(DigitsPattern().Match(extra.Value).Value);
                if (!episodes.Contains(number)) episodes.Add(number);
            }

            if (episodes.Count == 2 && episodeMatch.Groups["range"].Success)
            {
                // S01E01-E03 or S01E01-03 names a range, not two episodes.
                var (first, last) = (episodes[0], episodes[1]);
                episodes = Enumerable.Range(first, Math.Max(1, last - first + 1)).ToList();
            }

            markerIndex = episodeMatch.Index;
        }
        else if (CrossPattern().Match(title) is { Success: true } cross)
        {
            season = Int(cross.Groups["season"].Value);
            episodes.Add(Int(cross.Groups["episode"].Value));
            markerIndex = cross.Index;
        }
        else if (SeasonPattern().Match(title) is { Success: true } seasonOnly)
        {
            season = Int(seasonOnly.Groups["season"].Value);
            seasonPack = true;
            markerIndex = seasonOnly.Index;
        }
        else if (DailyPattern().Match(title) is { Success: true } date)
        {
            daily = true;
            markerIndex = date.Index;
        }
        else if (AbsolutePattern().Match(title) is { Success: true } absoluteMatch)
        {
            absolute = true;
            markerIndex = absoluteMatch.Index;
        }

        if (CompletePattern().IsMatch(title) && episodes.Count == 0) seasonPack = true;

        var qualityIndex = FirstQualityIndex(title);
        int? year = null;
        var years = YearPattern().Matches(title).Where(match => match.Index > 0 &&
            (markerIndex < 0 || match.Index < markerIndex) && (qualityIndex < 0 || match.Index < qualityIndex)).ToArray();
        var titleEnd = markerIndex >= 0 ? markerIndex : qualityIndex >= 0 ? qualityIndex : title.Length;
        if (years.Length > 0 && !daily)
        {
            var last = years[^1];
            year = Int(last.Value);
            if (markerIndex < 0) titleEnd = last.Index;
        }

        titleEnd = Math.Clamp(titleEnd, 0, title.Length);
        var name = Clean(title[..titleEnd]);
        // Quality words are read only after the title, so a film called "Cam" or "Proper" is not misread.
        var tail = title[titleEnd..];
        var resolution = Resolution(tail);
        var source = Source(tail);
        var group = GroupPattern().Match(title) is { Success: true } groupMatch &&
            !NotGroups.Contains(groupMatch.Groups["group"].Value) && !DigitsOnly().IsMatch(groupMatch.Groups["group"].Value)
                ? groupMatch.Groups["group"].Value : null;
        return new ParsedRelease(
            name.Length == 0 ? null : name, year, season, episodes, seasonPack, absolute, daily, resolution, source,
            Codec(tail), Audio(tail), Hdr(tail), group, ProperPattern().IsMatch(tail), RepackPattern().IsMatch(tail),
            QualityCatalog.Identify(source, resolution));
    }

    /// <summary>Normalizes a title for identity comparison: case, accents, punctuation and a leading article.</summary>
    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (character is '\'' or '’') continue;
            if (character == '&') builder.Append(" and ");
            else builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "the" or "a" or "an") words.RemoveAt(0);
        return string.Join(' ', words);
    }

    private static int FirstQualityIndex(string title)
    {
        var indexes = new[]
        {
            ResolutionPattern().Match(title), SourcePattern().Match(title), CodecPattern().Match(title)
        }.Where(match => match.Success).Select(match => match.Index).ToArray();
        return indexes.Length == 0 ? -1 : indexes.Min();
    }

    private static string Clean(string value) =>
        string.Join(' ', SeparatorPattern().Replace(value, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Resolution(string title)
    {
        var match = ResolutionPattern().Match(title);
        if (!match.Success) return null;
        var value = match.Value.ToLowerInvariant();
        return value switch
        {
            "2160p" or "4k" or "uhd" => "2160p",
            "1080p" or "1080i" => "1080p",
            _ => value
        };
    }

    private static string? Source(string title)
    {
        if (CamPattern().IsMatch(title)) return "cam";
        if (RemuxPattern().IsMatch(title)) return "remux";
        if (BlurayPattern().IsMatch(title)) return "bluray";
        if (WebRipPattern().IsMatch(title)) return "webrip";
        if (WebDlPattern().IsMatch(title)) return "webdl";
        if (HdtvPattern().IsMatch(title)) return "hdtv";
        if (DvdPattern().IsMatch(title)) return "dvd";
        return null;
    }

    private static string? Codec(string title)
    {
        var match = CodecPattern().Match(title);
        if (!match.Success) return null;
        var value = match.Value.ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return value switch
        {
            "x264" or "h264" or "avc" => "h264",
            "x265" or "h265" or "hevc" => "h265",
            "av1" => "av1",
            "xvid" or "divx" => "xvid",
            "vc1" => "vc1",
            _ => "mpeg2"
        };
    }

    private static string? Audio(string title)
    {
        var parts = new List<string>();
        if (TrueHdPattern().IsMatch(title)) parts.Add("TrueHD");
        else if (DtsHdPattern().IsMatch(title)) parts.Add("DTS-HD MA");
        else if (DtsXPattern().IsMatch(title)) parts.Add("DTS:X");
        else if (DtsPattern().IsMatch(title)) parts.Add("DTS");
        else if (DdpPattern().IsMatch(title)) parts.Add("DD+");
        else if (DdPattern().IsMatch(title)) parts.Add("DD");
        else if (AacPattern().IsMatch(title)) parts.Add("AAC");
        else if (FlacPattern().IsMatch(title)) parts.Add("FLAC");
        else if (OpusPattern().IsMatch(title)) parts.Add("Opus");
        if (AtmosPattern().IsMatch(title)) parts.Add("Atmos");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? Hdr(string title)
    {
        var parts = new List<string>();
        if (DolbyVisionPattern().IsMatch(title)) parts.Add("DV");
        if (Hdr10PlusPattern().IsMatch(title)) parts.Add("HDR10+");
        else if (Hdr10Pattern().IsMatch(title)) parts.Add("HDR10");
        else if (HdrPattern().IsMatch(title)) parts.Add("HDR");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static int Int(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    // A token boundary: start/end or a separator, so "DTS" never matches inside a word.
    private const string B = @"(?<![A-Za-z0-9])";
    private const string E = @"(?![A-Za-z0-9])";

    [GeneratedRegex(@"\.(mkv|mp4|avi|m4v|ts)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionPattern();
    [GeneratedRegex(B + @"S(?<season>\d{1,3})[ ._-]?E(?<episode>\d{1,4})(?:(?<more>[ ._-]?E\d{1,4})|(?<range>(?<more>-E?\d{1,4})))*" + E, RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();
    [GeneratedRegex(B + @"(?<season>\d{1,2})x(?<episode>\d{2,3})" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CrossPattern();
    [GeneratedRegex(B + @"(?:S(?<season>\d{1,3})|Season[ ._-]?(?<season>\d{1,3}))" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();
    [GeneratedRegex(B + @"(?:complete[ ._-](?:series|season)s?|seasons?[ ._-]\d{1,2}[ ._-]?-[ ._-]?\d{1,2})" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CompletePattern();
    [GeneratedRegex(B + @"(?:19|20)\d{2}[ ._-](?:0[1-9]|1[0-2])[ ._-](?:0[1-9]|[12]\d|3[01])" + E)]
    private static partial Regex DailyPattern();
    [GeneratedRegex(@"(?:[ ._]-[ ._]|[ ._]E[Pp]?)(?!(?:19|20)\d{2}(?!\d))(?<number>\d{2,4})(?:v\d)?(?=[ ._\[(]|$)")]
    private static partial Regex AbsolutePattern();
    [GeneratedRegex(B + @"(?:19\d{2}|20\d{2})" + E)]
    private static partial Regex YearPattern();
    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsOnly();
    [GeneratedRegex(@"[._\-\[\]()]+")]
    private static partial Regex SeparatorPattern();
    [GeneratedRegex(B + @"(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();
    [GeneratedRegex(B + @"(?:remux|blu[ .-]?ray|bdrip|brrip|web[ .-]?dl|web[ .-]?rip|web|hdtv|pdtv|dvdrip|dvd|hdcam|telesync|hdts)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SourcePattern();
    [GeneratedRegex(B + @"(?:cam|hdcam|camrip|ts|hdts|telesync|tc|telecine|scr|screener|dvdscr|workprint)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CamPattern();
    [GeneratedRegex(B + @"(?:remux|bdremux)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex RemuxPattern();
    [GeneratedRegex(B + @"(?:blu[ .-]?ray|bdrip|brrip|bd25|bd50|bd)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex BlurayPattern();
    [GeneratedRegex(B + @"web[ .-]?rip" + E, RegexOptions.IgnoreCase)]
    private static partial Regex WebRipPattern();
    [GeneratedRegex(B + @"(?:web[ .-]?dl|web)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex WebDlPattern();
    [GeneratedRegex(B + @"(?:hdtv|pdtv|sdtv|dsr|dsrip)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex HdtvPattern();
    [GeneratedRegex(B + @"(?:dvdrip|dvd5|dvd9|dvdr|dvd)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DvdPattern();
    [GeneratedRegex(B + @"(?:x264|x265|h[ .]?264|h[ .]?265|hevc|avc|av1|xvid|divx|vc-?1|mpeg-?2)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CodecPattern();
    [GeneratedRegex(B + @"true[ .-]?hd" + E, RegexOptions.IgnoreCase)]
    private static partial Regex TrueHdPattern();
    [GeneratedRegex(B + @"dts[ .-]?hd(?:[ .-]?ma)?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DtsHdPattern();
    [GeneratedRegex(B + @"dts[ .-:]?x" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DtsXPattern();
    [GeneratedRegex(B + @"dts" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DtsPattern();
    [GeneratedRegex(B + @"(?:ddp|dd\+|e-?ac-?3)(?:[ .]?\d[ .]\d)?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DdpPattern();
    [GeneratedRegex(B + @"(?:dd(?:[ .]?\d[ .]\d)|ac-?3)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DdPattern();
    [GeneratedRegex(B + @"aac(?:[ .]?\d[ .]\d)?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex AacPattern();
    [GeneratedRegex(B + @"flac" + E, RegexOptions.IgnoreCase)]
    private static partial Regex FlacPattern();
    [GeneratedRegex(B + @"opus" + E, RegexOptions.IgnoreCase)]
    private static partial Regex OpusPattern();
    [GeneratedRegex(B + @"atmos" + E, RegexOptions.IgnoreCase)]
    private static partial Regex AtmosPattern();
    [GeneratedRegex(B + @"(?:dv|dovi|dolby[ .-]?vision)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DolbyVisionPattern();
    [GeneratedRegex(B + @"hdr10(?:\+|plus)", RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10PlusPattern();
    [GeneratedRegex(B + @"hdr10" + E, RegexOptions.IgnoreCase)]
    private static partial Regex Hdr10Pattern();
    [GeneratedRegex(B + @"hdr" + E, RegexOptions.IgnoreCase)]
    private static partial Regex HdrPattern();
    [GeneratedRegex(B + @"proper" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ProperPattern();
    [GeneratedRegex(B + @"(?:repack|rerip)\d?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex RepackPattern();
    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,32})(?:\[[^\]]*\])?$")]
    private static partial Regex GroupPattern();
}
