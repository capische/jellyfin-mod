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

    /// <summary>
    /// Gets the resolution of a profile's best quality, as a number to sort on (2160, 1440, 1080, 720, 576, 480), where the
    /// best quality is the first one the profile lists. Zero when the profile lists nothing known. Profiles list best
    /// resolution first (user, 2026-10-19: "4K, 2K"), so the catalog order itself cannot serve: it places remux-1080p above
    /// bluray-1440p, which would put a 1080p profile above a 2K one.
    /// </summary>
    public static int BestResolution(IReadOnlyList<string> qualities) =>
        qualities.Count > 0 && All.FirstOrDefault(quality => quality.Id == qualities[0]).Resolution is { } resolution &&
        int.TryParse(resolution.TrimEnd('p'), out var pixels) ? pixels : 0;

    /// <summary>Returns the identifier for a parsed source and resolution, or null when either is unknown.</summary>
    public static string? Identify(string? source, string? resolution) =>
        source is null || resolution is null ? null : $"{source}-{resolution}";

    private static List<(string, string, string)> Build()
    {
        var result = new List<(string, string, string)>();
        foreach (var resolution in new[] { "2160p", "1080p" }) result.Add(($"remux-{resolution}", "remux", resolution));
        // 1440p (2K) is an encode or web resolution: no remux (no 1440p disc) and no broadcast carries it (P4.FU9).
        foreach (var source in new[] { "bluray", "webdl", "webrip", "hdtv" })
            foreach (var resolution in source is not "hdtv"
                         ? new[] { "2160p", "1440p", "1080p", "720p", "576p", "480p" }
                         : new[] { "2160p", "1080p", "720p", "576p", "480p" })
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
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quality,
    [property: JsonPropertyName("seasonLast"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SeasonLast = null,
    [property: JsonPropertyName("seriesPack")] bool SeriesPack = false,
    [property: JsonPropertyName("languages")] IReadOnlyList<string>? Languages = null,
    [property: JsonPropertyName("subtitles")] IReadOnlyList<string>? Subtitles = null,
    [property: JsonPropertyName("audioChannels"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AudioChannels = null)
{
    /// <summary>
    /// The seasons a pack names: a single season, a range <c>first..last</c>, or empty for a complete-series pack that names
    /// none (season and series packs, 2026-10-08).
    /// </summary>
    public IReadOnlyList<int> PackSeasons() => !SeasonPack || SeasonNumber is not { } first
        ? []
        : Enumerable.Range(first, Math.Max(1, (SeasonLast ?? first) - first + 1)).ToArray();
}

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
        "dl", "rip", "web", "hd", "x264", "x265", "h264", "h265", "hevc", "avc", "1080p", "720p", "2160p", "1440p",
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
        int? seasonLast = null;
        var seriesPack = false;

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
            // 1x01-02 and 1x01x02 name more than one episode; reading only the first would let a double episode pass as
            // a single one (whole-review chunk 2a, P2 7).
            foreach (Capture extra in cross.Groups["more"].Captures)
            {
                var number = Int(DigitsPattern().Match(extra.Value).Value);
                if (!episodes.Contains(number)) episodes.Add(number);
            }

            if (episodes.Count == 2 && cross.Groups["range"].Success)
            {
                var (first, last) = (episodes[0], episodes[1]);
                episodes = Enumerable.Range(first, Math.Max(1, last - first + 1)).ToList();
            }

            markerIndex = cross.Index;
        }
        else if (SeasonRangePattern().Match(title) is { Success: true } range &&
                 Int(range.Groups["last"].Value) is var lastSeason && lastSeason > Int(range.Groups["first"].Value))
        {
            // S01-S03, S01-03, Seasons 1-3: a multi-season pack keeps both ends (season and series packs, 2026-10-08).
            season = Int(range.Groups["first"].Value);
            seasonLast = lastSeason;
            seasonPack = true;
            seriesPack = true;
            markerIndex = range.Index;
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

        // "Complete", "Complete Series" or "Complete Seasons" before any other marker ends the title; with no season named
        // it is a complete-series pack. "S02 Complete" stays a pack of season 2 (season and series packs, 2026-10-08).
        if (episodes.Count == 0 && !daily && CompleteMarker(title) is { } complete && (markerIndex < 0 || complete < markerIndex))
        {
            markerIndex = complete;
            seasonPack = true;
            if (season is null)
            {
                seriesPack = true;
                absolute = false;
            }
        }

        var qualityIndex = FirstQualityIndex(title);
        int? year = null;
        var years = YearPattern().Matches(title).Where(match => match.Index > 0 &&
            (markerIndex < 0 || match.Index < markerIndex) && (qualityIndex < 0 || match.Index < qualityIndex)).ToArray();
        var titleEnd = markerIndex >= 0 ? markerIndex : qualityIndex >= 0 ? qualityIndex : title.Length;
        if (years.Length > 0 && !daily)
        {
            var last = years[^1];
            year = Int(last.Value);
            // A series year right before the episode marker (Ghosts.2021.S01E01) qualifies the title; it is not part of it
            // (whole-review chunk 2a, P2 6). A year followed by more words before the marker stays in the name.
            if (markerIndex < 0 || title[(last.Index + last.Length)..markerIndex].Trim(' ', '.', '_', '-', '(', ')', '[', ']').Length == 0)
                titleEnd = last.Index;
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
            QualityCatalog.Identify(source, resolution), seasonLast, seriesPack, TitleLanguages(tail), TitleSubtitles(tail),
            ChannelsPattern().Match(tail) is { Success: true } channels ? channels.Groups["count"].Value : null);
    }

    /// <summary>
    /// The languages and subtitles of a row for the picker (2026-10-10): what the indexer's <c>language</c> and <c>subs</c>
    /// attributes say, else what the title says. Codes are upper-case ISO 639-1; <c>MULTI</c> and <c>DUAL</c> stay words.
    /// </summary>
    public static ParsedRelease WithIndexerLanguages(ParsedRelease parsed, string? language, string? subs)
    {
        var languages = AttributeCodes(language);
        var subtitles = AttributeCodes(subs);
        return parsed with
        {
            Languages = languages.Count > 0 ? languages : parsed.Languages ?? [],
            Subtitles = subtitles.Count > 0 ? subtitles : parsed.Subtitles ?? []
        };
    }

    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "EN", ["eng"] = "EN", ["en"] = "EN", ["french"] = "FR", ["fre"] = "FR", ["fra"] = "FR", ["fr"] = "FR",
        ["german"] = "DE", ["ger"] = "DE", ["deu"] = "DE", ["de"] = "DE", ["spanish"] = "ES", ["spa"] = "ES", ["es"] = "ES",
        ["italian"] = "IT", ["ita"] = "IT", ["it"] = "IT", ["portuguese"] = "PT", ["por"] = "PT", ["pt"] = "PT",
        ["russian"] = "RU", ["rus"] = "RU", ["ru"] = "RU", ["japanese"] = "JA", ["jpn"] = "JA", ["ja"] = "JA",
        ["korean"] = "KO", ["kor"] = "KO", ["ko"] = "KO", ["chinese"] = "ZH", ["chi"] = "ZH", ["zho"] = "ZH", ["zh"] = "ZH",
        ["dutch"] = "NL", ["dut"] = "NL", ["nld"] = "NL", ["nl"] = "NL", ["polish"] = "PL", ["pol"] = "PL", ["pl"] = "PL",
        ["swedish"] = "SV", ["swe"] = "SV", ["sv"] = "SV", ["turkish"] = "TR", ["tur"] = "TR", ["tr"] = "TR",
        ["hindi"] = "HI", ["hin"] = "HI", ["hi"] = "HI", ["arabic"] = "AR", ["ara"] = "AR", ["ar"] = "AR",
        ["multi"] = "MULTI", ["dual"] = "DUAL"
    };

    /// <summary>Codes from a comma or slash separated attribute such as <c>English, French</c> or <c>en/fr</c>; unknown words are dropped.</summary>
    private static List<string> AttributeCodes(string? value)
    {
        var codes = new List<string>();
        foreach (var word in (value ?? string.Empty).Split([',', '/', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (LanguageCodes.TryGetValue(word, out var code) && !codes.Contains(code)) codes.Add(code);
        return codes.Take(12).ToList();
    }

    private static IReadOnlyList<string> TitleLanguages(string tail)
    {
        var codes = new List<string>();
        void Add(Regex pattern, string code)
        {
            if (pattern.IsMatch(tail) && !codes.Contains(code)) codes.Add(code);
        }

        Add(MultiPattern(), "MULTI");
        Add(DualPattern(), "DUAL");
        Add(GermanPattern(), "DE");
        Add(FrenchPattern(), "FR");
        Add(ItalianPattern(), "IT");
        Add(SpanishPattern(), "ES");
        Add(RussianPattern(), "RU");
        Add(JapanesePattern(), "JA");
        Add(KoreanPattern(), "KO");
        return codes;
    }

    private static IReadOnlyList<string> TitleSubtitles(string tail)
    {
        var codes = new List<string>();
        if (VostfrPattern().IsMatch(tail)) codes.Add("FR");
        if (SubsPattern().Match(tail) is { Success: true } subs)
        {
            var code = subs.Groups["lang"].Success && LanguageCodes.TryGetValue(subs.Groups["lang"].Value, out var named) ? named
                : subs.Groups["multi"].Success ? "MULTI" : "SUB";
            if (!codes.Contains(code)) codes.Add(code);
        }

        return codes;
    }

    /// <summary>
    /// The index of a "Complete" marker: <c>Complete Series</c>, <c>Complete Season(s)</c>, or a bare <c>Complete</c> followed
    /// only by a year, a quality word, a bracket or the end, so a title such as "The Complete Works" is not read as a pack.
    /// </summary>
    private static int? CompleteMarker(string title)
    {
        foreach (Match match in CompletePattern().Matches(title))
        {
            if (match.Index == 0) continue;
            if (match.Groups["kind"].Success) return match.Index;
            var rest = title[(match.Index + match.Length)..].TrimStart(' ', '.', '_', '-');
            if (rest.Length == 0 || rest[0] is '[' or '(' || YearPattern().Match(rest) is { Success: true, Index: 0 } ||
                FirstQualityIndex(rest) == 0)
                return match.Index;
        }

        return null;
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
    [GeneratedRegex(B + @"(?<season>\d{1,2})x(?<episode>\d{2,3})(?:(?<more>x\d{2,3})|(?<range>(?<more>-\d{2,3})))*" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CrossPattern();
    [GeneratedRegex(B + @"(?:S(?<season>\d{1,3})|Season[ ._-]?(?<season>\d{1,3}))" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();
    [GeneratedRegex(B + @"complete(?:[ ._-](?<kind>series|seasons?|collection))?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex CompletePattern();
    [GeneratedRegex(B + @"(?:S(?<first>\d{1,3})[ ._]?-[ ._]?S?(?<last>\d{1,3})|Seasons?[ ._-]?(?<first>\d{1,3})[ ._]?-[ ._]?(?<last>\d{1,3}))" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRangePattern();
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
    [GeneratedRegex(B + @"(?:2160p|1440p|1080p|1080i|720p|576p|480p|4k|uhd)" + E, RegexOptions.IgnoreCase)]
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
    [GeneratedRegex(@"(?<![0-9])(?<count>1\.0|2\.0|2\.1|5\.1|7\.1)(?![0-9])")]
    private static partial Regex ChannelsPattern();
    [GeneratedRegex(B + @"multi" + E, RegexOptions.IgnoreCase)]
    private static partial Regex MultiPattern();
    [GeneratedRegex(B + @"dual(?:[ .-]?audio)?" + E, RegexOptions.IgnoreCase)]
    private static partial Regex DualPattern();
    [GeneratedRegex(B + @"(?:german|deutsch|ger)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex GermanPattern();
    [GeneratedRegex(B + @"(?:french|truefrench|vff|vfq|vf2?|vfi)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex FrenchPattern();
    [GeneratedRegex(B + @"(?:italian|ita)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex ItalianPattern();
    [GeneratedRegex(B + @"(?:spanish|castellano|latino|spa)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SpanishPattern();
    [GeneratedRegex(B + @"(?:russian|rus)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex RussianPattern();
    [GeneratedRegex(B + @"(?:japanese|jap|jpn)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex JapanesePattern();
    [GeneratedRegex(B + @"(?:korean|kor)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex KoreanPattern();
    [GeneratedRegex(B + @"vostfr" + E, RegexOptions.IgnoreCase)]
    private static partial Regex VostfrPattern();
    [GeneratedRegex(B + @"(?:(?<multi>multi)[ .-]?subs?|(?<lang>eng(?:lish)?|fre(?:nch)?|ger(?:man)?|spa(?:nish)?|ita(?:lian)?)[ .-]?(?:sub|subs|subbed)|subbed|subs)" + E, RegexOptions.IgnoreCase)]
    private static partial Regex SubsPattern();
    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,32})(?:\[[^\]]*\])?$")]
    private static partial Regex GroupPattern();
}
