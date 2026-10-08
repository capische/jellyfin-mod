using System.Text.Json;
using JellyfinMod.Data;

namespace JellyfinMod.Services.Acquisition;

/// <summary>What a search covers (season and series packs, 2026-10-08).</summary>
public static class ReleaseScopes
{
    /// <summary>One movie entry: the implicit scope of a movie.</summary>
    public const string Title = "title";

    /// <summary>One episode.</summary>
    public const string Episode = "episode";

    /// <summary>Every aired, tracked episode of one season, through a pack of exactly that season.</summary>
    public const string Season = "season";

    /// <summary>Every aired, tracked episode outside specials, through a complete-series pack or a covering range.</summary>
    public const string Series = "series";

    /// <summary>Whether a scope is a pack scope.</summary>
    public static bool IsPack(string? scope) => scope is Season or Series;
}

/// <summary>One episode a pack search covers, with whether it already holds a file.</summary>
public sealed record CoveredEpisode(Guid Id, int SeasonNumber, int EpisodeNumber, bool Held, int? RuntimeMinutes);

/// <summary>Builds the search target of an entry or episode, shared by the picker and automation.</summary>
public static class ReleaseTargets
{
    /// <summary>Returns the target of one movie entry or one episode.</summary>
    public static ReleaseTarget For(Entry entry, Episode? episode)
    {
        var metadata = entry.MetadataJson is { } json ? JsonSerializer.Deserialize<TmdbMetadata>(json) : null;
        return new ReleaseTarget(entry.Id, episode?.Id, entry.MediaType, entry.Title, metadata?.OriginalTitle, entry.Year,
            episode?.SeasonNumber, episode?.EpisodeNumber,
            // An episode's own runtime only: a series average would be a guessed duration (P4.A4).
            entry.MediaType == "series" ? episode?.RuntimeMinutes : metadata?.RuntimeMinutes,
            entry.ImdbId ?? metadata?.ImdbId, entry.TmdbId, metadata?.TvdbId)
        {
            Scope = entry.MediaType == "series" ? ReleaseScopes.Episode : ReleaseScopes.Title
        };
    }

    /// <summary>
    /// The episodes a pack scope covers: the series' aired, tracked episodes of that season (season scope) or of every season
    /// but specials (series scope). An episode counts as aired once its air date has passed or it already holds a file.
    /// </summary>
    public static List<Episode> Covered(IEnumerable<Episode> episodes, string scope, int? seasonNumber, DateTime now) => episodes
        .Where(episode => episode.SeasonNumber > 0 && (scope != ReleaseScopes.Season || episode.SeasonNumber == seasonNumber))
        .Where(episode => episode.AirDate is { } aired && aired <= now || episode.State == FileState.OnDisk)
        .OrderBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber).ToList();

    /// <summary>
    /// Returns the target of a season or series pack. Size per hour is checked against the summed runtime of the covered
    /// episodes; an episode without a runtime counts as the series' typical one (the median of the known runtimes, else the
    /// series' own), and with neither the runtime stays unknown.
    /// </summary>
    public static ReleaseTarget ForPack(Entry entry, string scope, int? seasonNumber, IReadOnlyList<Episode> covered,
        IEnumerable<Episode> allEpisodes)
    {
        var metadata = entry.MetadataJson is { } json ? JsonSerializer.Deserialize<TmdbMetadata>(json) : null;
        var known = allEpisodes.Where(episode => episode.RuntimeMinutes is > 0).Select(episode => episode.RuntimeMinutes!.Value)
            .OrderBy(value => value).ToArray();
        int? typical = known.Length > 0 ? known[known.Length / 2] : metadata?.RuntimeMinutes is > 0 ? metadata.RuntimeMinutes : null;
        int? runtime = covered.Count == 0 ? null : 0;
        foreach (var episode in covered)
        {
            var minutes = episode.RuntimeMinutes is > 0 ? episode.RuntimeMinutes : typical;
            runtime = minutes is null || runtime is null ? null : runtime + minutes;
        }

        return new ReleaseTarget(entry.Id, null, entry.MediaType, entry.Title, metadata?.OriginalTitle, entry.Year,
            scope == ReleaseScopes.Season ? seasonNumber : null, null, runtime, entry.ImdbId ?? metadata?.ImdbId, entry.TmdbId,
            metadata?.TvdbId)
        {
            Scope = scope,
            Covered = covered.Select(episode => new CoveredEpisode(episode.Id, episode.SeasonNumber, episode.EpisodeNumber,
                episode.State == FileState.OnDisk, episode.RuntimeMinutes)).ToArray()
        };
    }
}
