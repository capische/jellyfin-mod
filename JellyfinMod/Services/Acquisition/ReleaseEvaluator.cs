using System.Text.Json.Serialization;

namespace JellyfinMod.Services.Acquisition;

/// <summary>What a search is for: one library-scoped movie entry or one stable episode.</summary>
public sealed record ReleaseTarget(
    Guid EntryId,
    Guid? EpisodeId,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? RuntimeMinutes,
    string? ImdbId,
    int TmdbId,
    int? TvdbId);

/// <summary>A profile as evaluated: ordered qualities and optional size-per-hour limits.</summary>
public sealed record EvaluationProfile(
    Guid Id,
    string Name,
    int Revision,
    IReadOnlyList<string> Qualities,
    long? MinimumBytesPerHour,
    long? MaximumBytesPerHour);

/// <summary>How a feed row was found, which decides whether its identity is verified.</summary>
public enum SearchIdentity
{
    /// <summary>Found by an advertised provider-id search parameter.</summary>
    ProviderId,

    /// <summary>Found by free-text query only; never grabbable.</summary>
    QueryOnly
}

/// <summary>One stable hard rejection.</summary>
public sealed record ReleaseRejection(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>One documented score contribution.</summary>
public sealed record ScoreContribution(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("points")] int Points);

/// <summary>A deterministic evaluation of one feed row against a target and profile.</summary>
public sealed record ReleaseEvaluation(
    bool Eligible,
    int Score,
    IReadOnlyList<ScoreContribution> Contributions,
    IReadOnlyList<ReleaseRejection> Rejections,
    string Identity);

/// <summary>The facts about one feed row that evaluation uses; unknown values stay null.</summary>
public sealed record ReleaseFacts(
    ParsedRelease Parsed,
    SearchIdentity Search,
    long? Size,
    int? Seeders,
    bool? Freeleech,
    string? ImdbId,
    int? TmdbId,
    int? TvdbId,
    int? SeasonAttribute,
    int? EpisodeAttribute,
    bool HasLocator,
    bool LocatorHostAllowed,
    string? UnsupportedHashReason);

/// <summary>
/// Applies hard constraints before scoring and exposes every rejection and score contribution (P4.A4).
/// </summary>
/// <remarks>
/// Scoring version <see cref="Version"/>: quality rank is <c>(count - index) * 100</c> for the profile's ordered
/// list; proper or repack adds 15; freeleech adds 10; known seeders add up to 20 on a logarithmic scale. Ties are
/// broken by seeders (unknown last), then indexer priority, then newer publication, then release id.
/// </remarks>
public static class ReleaseEvaluator
{
    /// <summary>The scoring rules version recorded on every grab.</summary>
    public const string Version = "p4.a4.v1";

    /// <summary>Evaluates one row.</summary>
    public static ReleaseEvaluation Evaluate(ReleaseTarget target, EvaluationProfile profile, ReleaseFacts facts)
    {
        var rejections = new List<ReleaseRejection>();
        var parsed = facts.Parsed;
        var identity = IdentityRejections(target, facts, rejections);

        if (!facts.HasLocator)
            rejections.Add(new("no_download_locator", "The indexer did not provide a torrent or magnet link."));
        else if (!facts.LocatorHostAllowed)
            rejections.Add(new("download_host_not_allowed",
                "The torrent link points to a host this indexer is not allowed to use."));
        if (facts.UnsupportedHashReason is { } hashReason)
            rejections.Add(new("unsupported_hash", hashReason));

        if (parsed.Source == "cam")
            rejections.Add(new("quality_forbidden", "Camera, telesync and screener copies are never acquired."));
        else if (parsed.Quality is null)
            rejections.Add(new("quality_unknown", parsed.Resolution is null
                ? "The resolution could not be read from the release title."
                : "The source could not be read from the release title."));
        else if (!profile.Qualities.Contains(parsed.Quality))
            rejections.Add(new("quality_not_allowed", $"{parsed.Quality} is not allowed by profile {profile.Name}."));

        if (profile.MinimumBytesPerHour.HasValue || profile.MaximumBytesPerHour.HasValue)
        {
            if (facts.Size is not > 0)
                rejections.Add(new("size_unknown", "The profile limits size, but the indexer did not report one."));
            else if (target.RuntimeMinutes is not > 0)
                rejections.Add(new("runtime_unknown", "The profile limits size per hour, but the runtime is unknown."));
            else
            {
                var perHour = (long)(facts.Size.Value / (target.RuntimeMinutes.Value / 60.0));
                if (profile.MinimumBytesPerHour is { } minimum && perHour < minimum)
                    rejections.Add(new("size_below_minimum", "The release is smaller than the profile allows."));
                if (profile.MaximumBytesPerHour is { } maximum && perHour > maximum)
                    rejections.Add(new("size_above_maximum", "The release is larger than the profile allows."));
            }
        }

        var contributions = new List<ScoreContribution>();
        if (parsed.Quality is not null && profile.Qualities.ToList().IndexOf(parsed.Quality) is var index and >= 0)
            contributions.Add(new("quality_rank", (profile.Qualities.Count - index) * 100));
        if (parsed.Proper || parsed.Repack) contributions.Add(new("proper_or_repack", 15));
        if (facts.Freeleech == true) contributions.Add(new("freeleech", 10));
        if (facts.Seeders is { } seeders and > 0)
            contributions.Add(new("seeders", Math.Min(20, (int)Math.Floor(Math.Log2(seeders + 1) * 4))));
        return new ReleaseEvaluation(rejections.Count == 0, contributions.Sum(item => item.Points), contributions,
            rejections, identity);
    }

    private static string IdentityRejections(ReleaseTarget target, ReleaseFacts facts, List<ReleaseRejection> rejections)
    {
        var parsed = facts.Parsed;
        var episodic = parsed.EpisodeNumbers.Count > 0 || parsed.SeasonPack || parsed.DailyNumbering;
        if (target.MediaType == "movie")
        {
            if (episodic) rejections.Add(new("media_type_mismatch", "The release is a TV episode or season."));
        }
        else
        {
            if (target.SeasonNumber == 0)
                rejections.Add(new("ambiguous_special",
                    "Specials are numbered differently by TMDB and indexers, so they cannot be matched safely yet."));
            else if (parsed.SeasonPack)
                rejections.Add(new("season_pack", "Season packs are not supported until their import mapping exists."));
            else if (parsed.EpisodeNumbers.Count > 1)
                rejections.Add(new("multi_episode", "Multi-episode releases are not supported yet."));
            else if (parsed.DailyNumbering)
                rejections.Add(new("ambiguous_numbering", "Date-numbered releases cannot be matched to an episode."));
            else if (parsed.AbsoluteNumbering && parsed.EpisodeNumbers.Count == 0)
                rejections.Add(new("absolute_numbering", "Absolute-numbered releases are not supported yet."));
            else if (parsed.EpisodeNumbers.Count == 0 || parsed.SeasonNumber is null)
                rejections.Add(new("ambiguous_numbering", "No season and episode number could be read."));
            else if (parsed.SeasonNumber != target.SeasonNumber || parsed.EpisodeNumbers[0] != target.EpisodeNumber)
                rejections.Add(new("episode_mismatch",
                    $"The release is S{parsed.SeasonNumber:00}E{parsed.EpisodeNumbers[0]:00}, not the requested episode."));
            if (facts.SeasonAttribute is { } season && season != target.SeasonNumber ||
                facts.EpisodeAttribute is { } episode && episode != target.EpisodeNumber)
                rejections.Add(new("episode_mismatch", "The indexer reports a different season or episode."));
        }

        // A reported provider id that disagrees is a hard mismatch, whatever the title says.
        var idMismatch = target.MediaType == "movie"
            ? facts.ImdbId is { } imdb && !SameImdb(imdb, target.ImdbId) || facts.TmdbId is { } tmdb && tmdb != target.TmdbId
            : facts.TvdbId is { } tvdb && tvdb != target.TvdbId;
        if (idMismatch)
        {
            rejections.Add(new("identity_mismatch", "The indexer reports a different title identity."));
            return "mismatch";
        }

        // Most public trackers advertise no id search at all, so a text match is the only identity they can
        // offer. The parsed title and year must both agree; such a release is grabbable by hand and, unless the
        // indexer is trusted for it, never grabbed automatically (user decision 2026-09-20).
        var titleOnly = facts.Search == SearchIdentity.QueryOnly;
        var idConfirmed = target.MediaType == "movie"
            ? facts.ImdbId is not null && SameImdb(facts.ImdbId, target.ImdbId) || facts.TmdbId == target.TmdbId
            : facts.TvdbId is not null && facts.TvdbId == target.TvdbId;
        var titleMatches = TitleMatches(target, parsed.Title);
        if (idConfirmed) return "verified";
        if (!titleMatches)
        {
            rejections.Add(new("title_mismatch", "The release title does not match this title."));
            return "unverified";
        }

        if (target.MediaType == "movie")
        {
            if (parsed.Year is null)
            {
                rejections.Add(new("year_missing", "The release has no year and the indexer did not confirm the title."));
                return "unverified";
            }

            if (target.Year is { } year && parsed.Year.Value != year)
            {
                rejections.Add(new("year_mismatch", $"The release is from {parsed.Year}, not {year}."));
                return "unverified";
            }
        }

        return titleOnly ? "title" : "verified";
    }

    private static bool TitleMatches(ReleaseTarget target, string? parsedTitle)
    {
        var release = ReleaseParser.NormalizeTitle(parsedTitle);
        if (release.Length == 0) return false;
        return release == ReleaseParser.NormalizeTitle(target.Title) ||
            release == ReleaseParser.NormalizeTitle(target.OriginalTitle);
    }

    private static bool SameImdb(string reported, string? target)
    {
        static string Digits(string? value) => (value ?? string.Empty).Trim().TrimStart('t', 'T').TrimStart('0');
        return target is not null && Digits(reported).Length > 0 && Digits(reported) == Digits(target);
    }
}
