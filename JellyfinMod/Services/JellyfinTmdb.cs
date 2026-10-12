using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using JellyfinMod.Data;
using Microsoft.Extensions.Logging;
using TMDbLib.Objects.Movies;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace JellyfinMod.Services;

/// <summary>
/// Jellyfin's own TMDb connection (P1.FU13): the host's <c>TmdbClientManager</c>, with the key the server's metadata
/// provider uses. <see cref="TmdbClient"/> uses it when no TMDB token is saved.
/// </summary>
/// <remarks>
/// The manager is a public singleton of the host's container, but no package publishes it, so it is found by name and its
/// methods are bound to delegates of the exact signatures of Jellyfin 12.0.0, once, at first use. A changed signature makes
/// the connection unavailable with one error in the log, never a wrong call. The TMDbLib types it returns are compiled
/// against the package and bound to the host's copy at runtime. Differences from the plugin's own requests: the manager
/// reports any failure as nothing (so a missing title and a refused key look the same), searches have no further pages,
/// a season is one request, and it caches answers for an hour.
/// </remarks>
public sealed class JellyfinTmdb(IServiceProvider services, ILogger<JellyfinTmdb> logger)
{
    /// <summary>The manager's type, in the host's MediaBrowser.Providers assembly.</summary>
    private const string ManagerType = "MediaBrowser.Providers.Plugins.Tmdb.TmdbClientManager, MediaBrowser.Providers";

    /// <summary>The language every request asks for, as the plugin's own requests do.</summary>
    private const string Language = "en-US";

    private readonly Lazy<Binding?> _binding = new(() => Bind(services, logger));

    /// <summary>Gets a value indicating whether Jellyfin's TMDb connection was found and matches this build.</summary>
    public bool Available => _binding.Value is not null;

    /// <summary>Fetches a movie or series with its certifications, as <see cref="TmdbClient.GetDetailsAsync"/> does.</summary>
    internal async Task<TmdbMetadata> GetDetailsAsync(string mediaType, int id, CancellationToken cancellationToken)
    {
        var binding = Require();
        if (mediaType == "movie")
        {
            var movie = await CallAsync($"movie/{id}", token => binding.GetMovie(id, Language, null, null, token), cancellationToken)
                ?? throw NotFound($"movie/{id}");
            return Movie(movie);
        }

        var series = await CallAsync($"tv/{id}", token => binding.GetSeries(id, Language, null, null, token), cancellationToken)
            ?? throw NotFound($"tv/{id}");
        if (series.Seasons is null) throw new TmdbException("TMDB returned incomplete series metadata.", HttpStatusCode.BadGateway);
        var result = Series(series);
        if (result.Seasons.Any(season => season.Number < 0) || result.Seasons.DistinctBy(season => season.Number).Count() != result.Seasons.Length)
            throw new TmdbException("TMDB returned conflicting season identities.", HttpStatusCode.BadGateway);
        return result;
    }

    /// <summary>Searches TMDB. There are no further pages: the manager returns the first one only.</summary>
    internal async Task<TmdbPage> SearchAsync(string query, string mediaType, int page, CancellationToken cancellationToken)
    {
        var binding = Require();
        if (page > 1) return new TmdbPage([], false);
        IEnumerable<TmdbMetadata>? results = mediaType == "movie"
            ? (await CallAsync("search/movie", token => binding.SearchMovies(query, 0, Language, null, token), cancellationToken))?.Select(Found)
            : (await CallAsync("search/tv", token => binding.SearchSeries(query, Language, null, 0, token), cancellationToken))?.Select(Found);
        if (results is null) throw new TmdbException("TMDB is temporarily unavailable.", HttpStatusCode.BadGateway);
        return new TmdbPage(results.Where(item => item.TmdbId > 0 && !string.IsNullOrWhiteSpace(item.Title)).ToArray(), false);
    }

    /// <summary>Reads one season's episodes into <paramref name="episodes"/>; incomplete or mismatched metadata fails the series.</summary>
    internal async Task ReadSeasonAsync(int seriesId, int seasonNumber, ConcurrentBag<Episode> episodes, CancellationToken cancellationToken)
    {
        var binding = Require();
        var season = await CallAsync($"tv/{seriesId}/season/{seasonNumber}",
            token => binding.GetSeason(seriesId, seasonNumber, Language, null, null, token), cancellationToken);
        if (season?.Episodes is null || season.SeasonNumber != seasonNumber)
        {
            logger.LogWarning("TMDB season {Season} of {Series} through Jellyfin's TMDb connection returned no usable season", seasonNumber, seriesId);
            throw new TmdbException("TMDB returned incomplete season metadata.", HttpStatusCode.BadGateway);
        }

        foreach (var item in season.Episodes)
        {
            if (item is null || item.Id is not > 0 || item.EpisodeNumber is not (> 0 and <= int.MaxValue) || item.SeasonNumber != seasonNumber)
                throw new TmdbException("TMDB returned incomplete episode metadata.", HttpStatusCode.BadGateway);
            var number = (int)item.EpisodeNumber;
            episodes.Add(new Episode
            {
                TmdbId = item.Id, SeasonNumber = seasonNumber, EpisodeNumber = number,
                Title = string.IsNullOrEmpty(item.Name) ? $"Episode {number}" : item.Name, Overview = item.Overview,
                StillPath = item.StillPath, RuntimeMinutes = item.Runtime, VoteAverage = item.VoteAverage, AirDate = Day(item.AirDate),
                // The season lists each episode's guest stars and crew, as the plugin's own requests read them (P1.FU15).
                CreditsJson = EpisodeCredits.From((item.GuestStars ?? []).Where(person => person is not null).Select(Credit),
                    (item.Crew ?? []).Where(person => person is not null)
                        .Select(person => (person.Id, person.Name, person.Department, person.Job, person.ProfilePath))).ToJson()
            });
        }
    }

    /// <summary>
    /// A read-only check of the connection: one well-known series and its first season (P1.FU13 live diagnostic).
    /// </summary>
    /// <returns>The series' season count and the first season's episode count.</returns>
    internal async Task<(int Seasons, int Episodes)> ProbeAsync(CancellationToken cancellationToken)
    {
        var series = await GetDetailsAsync("series", ProbeSeriesId, cancellationToken);
        if (series.TmdbId != ProbeSeriesId || string.IsNullOrWhiteSpace(series.Title))
            throw new TmdbException("TMDB returned incomplete metadata.", HttpStatusCode.BadGateway);
        var episodes = new ConcurrentBag<Episode>();
        await ReadSeasonAsync(ProbeSeriesId, 1, episodes, cancellationToken);
        return (series.Seasons.Count(season => season.Number > 0), episodes.Count);
    }

    /// <summary>The series a connection test reads: Breaking Bad, whose listing has not changed in years.</summary>
    internal const int ProbeSeriesId = 1396;

    private Binding Require() => _binding.Value ??
        throw new TmdbException("Jellyfin's TMDb connection is unavailable.", HttpStatusCode.ServiceUnavailable, "not_configured");

    private TmdbException NotFound(string path)
    {
        // The manager reports every failure as nothing, so this is a missing title or an error it does not tell apart.
        logger.LogWarning("TMDB request {Path} through Jellyfin's TMDb connection returned nothing (not found, or an error it does not report)", path);
        return new TmdbException("Title not found on TMDB.", HttpStatusCode.NotFound, "not_found");
    }

    /// <summary>One call through the manager, with the plugin's own 15-second limit and sanitised failures.</summary>
    private async Task<T?> CallAsync<T>(string path, Func<CancellationToken, Task<T?>> call, CancellationToken cancellationToken)
        where T : class
    {
        logger.LogInformation("TMDB request {Path} through Jellyfin's TMDb connection", path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await call(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TmdbException("TMDB request timed out.", HttpStatusCode.GatewayTimeout, "timeout");
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning("TMDB request {Path} through Jellyfin's TMDb connection failed: {ErrorType}", path, error.GetType().Name);
            throw new TmdbException("TMDB is temporarily unavailable.", HttpStatusCode.BadGateway, "unreachable");
        }
        catch (Exception error) when (error is not OperationCanceledException and not TmdbException)
        {
            logger.LogWarning("TMDB request {Path} through Jellyfin's TMDb connection failed: {ErrorType}", path, error.GetType().Name);
            throw new TmdbException("TMDB answered with an error.", HttpStatusCode.BadGateway);
        }
    }

    private static TmdbMetadata Movie(Movie movie) => new(
        "movie", movie.Id, movie.Title ?? string.Empty, Day(movie.ReleaseDate), movie.Overview, movie.PosterPath, movie.BackdropPath,
        movie.ImdbId, null, movie.Adult, movie.VoteAverage, movie.Runtime,
        (movie.Genres ?? []).Select(genre => genre.Name).OfType<string>().ToArray(),
        (movie.Releases?.Countries ?? []).Where(country => country.Iso_3166_1 is not null && !string.IsNullOrEmpty(country.Certification))
            .Select(country => new TmdbCertification(country.Iso_3166_1!, country.Certification!)).Distinct().ToArray(),
        [], movie.OriginalTitle);

    private static TmdbMetadata Series(TvShow series)
    {
        int? tvdb = int.TryParse(series.ExternalIds?.TvdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new TmdbMetadata(
            "series", series.Id, series.Name ?? string.Empty, Day(series.FirstAirDate), series.Overview, series.PosterPath, series.BackdropPath,
            series.ExternalIds?.ImdbId, tvdb, series.Adult, series.VoteAverage, null,
            (series.Genres ?? []).Select(genre => genre.Name).OfType<string>().ToArray(),
            (series.ContentRatings?.Results ?? []).Where(rating => rating.Iso_3166_1 is not null && !string.IsNullOrEmpty(rating.Rating))
                .Select(rating => new TmdbCertification(rating.Iso_3166_1!, rating.Rating!)).Distinct().ToArray(),
            (series.Seasons ?? []).Select(season => new TmdbSeason(season.SeasonNumber, season.Name ?? string.Empty, season.EpisodeCount,
                season.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), season.PosterPath, season.Overview)).ToArray(),
            series.OriginalName,
            // The manager reads a series with its credits; its regular cast is kept as the plugin's own requests keep it (P1.FU15).
            Cast: EpisodeCredits.Cast((series.Credits?.Cast ?? []).Where(person => person is not null).Select(Credit)));
    }

    private static TmdbCastCredit Credit(TMDbLib.Objects.TvShows.Cast person) =>
        new(person.Id, person.Name ?? string.Empty, person.Character, person.ProfilePath, person.Order);

    private static TmdbMetadata Found(SearchMovie item) => new(
        "movie", item.Id, item.Title ?? string.Empty, Day(item.ReleaseDate), item.Overview, item.PosterPath, item.BackdropPath,
        null, null, item.Adult, item.VoteAverage, null, [], [], [], item.OriginalTitle);

    private static TmdbMetadata Found(SearchTv item) => new(
        "series", item.Id, item.Name ?? string.Empty, Day(item.FirstAirDate), item.Overview, item.PosterPath, item.BackdropPath,
        null, null, false, item.VoteAverage, null, [], [], [], item.OriginalName);

    /// <summary>A TMDB date as the plugin's own requests read it: that day, at midnight UTC.</summary>
    private static DateTime? Day(DateTime? value) => value is { } date ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc) : null;

    private static Binding? Bind(IServiceProvider services, ILogger logger)
    {
        var type = Type.GetType(ManagerType, throwOnError: false);
        if (type is null)
        {
            logger.LogWarning("Jellyfin's TMDb connection is unavailable: this server has no {Type}. Discovery needs a saved TMDB token", ManagerType);
            return null;
        }

        object? manager;
        try
        {
            manager = services.GetService(type);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            logger.LogWarning("Jellyfin's TMDb connection is unavailable: {ErrorType} resolving it. Discovery needs a saved TMDB token", error.GetType().Name);
            return null;
        }

        if (manager is null)
        {
            logger.LogWarning("Jellyfin's TMDb connection is unavailable: the server does not register it. Discovery needs a saved TMDB token");
            return null;
        }

        try
        {
            var binding = new Binding(
                Method<Func<int, string?, string?, string?, CancellationToken, Task<Movie?>>>(type, manager, "GetMovieAsync",
                    typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)),
                Method<Func<int, string?, string?, string?, CancellationToken, Task<TvShow?>>>(type, manager, "GetSeriesAsync",
                    typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)),
                Method<Func<int, int, string?, string?, string?, CancellationToken, Task<TvSeason?>>>(type, manager, "GetSeasonAsync",
                    typeof(int), typeof(int), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)),
                Method<Func<string, int, string, string?, CancellationToken, Task<IReadOnlyList<SearchMovie>?>>>(type, manager, "SearchMovieAsync",
                    typeof(string), typeof(int), typeof(string), typeof(string), typeof(CancellationToken)),
                Method<Func<string, string, string?, int, CancellationToken, Task<IReadOnlyList<SearchTv>?>>>(type, manager, "SearchSeriesAsync",
                    typeof(string), typeof(string), typeof(string), typeof(int), typeof(CancellationToken)));
            logger.LogInformation("Jellyfin's TMDb connection is available; it is used when no TMDB token is saved");
            return binding;
        }
        catch (Exception error) when (error is MissingMethodException or ArgumentException or TypeLoadException or FileNotFoundException
            or FileLoadException or MemberAccessException)
        {
            // Loud on purpose: the plugin was built for another Jellyfin, and calls must not guess (P1.FU13).
            logger.LogError("Jellyfin's TMDb connection cannot be used: {Type} does not match what this plugin was built for " +
                "(Jellyfin 12.0.0, TMDbLib 3.0.0): {Reason}. Discovery needs a saved TMDB token until the plugin is rebuilt for this server",
                ManagerType, error.Message);
            return null;
        }
    }

    private static TDelegate Method<TDelegate>(Type type, object target, string name, params Type[] parameters)
        where TDelegate : Delegate =>
        (type.GetMethod(name, parameters) ?? throw new MissingMethodException(type.FullName, name)).CreateDelegate<TDelegate>(target);

    /// <summary>The manager's methods, bound once.</summary>
    private sealed record Binding(
        Func<int, string?, string?, string?, CancellationToken, Task<Movie?>> GetMovie,
        Func<int, string?, string?, string?, CancellationToken, Task<TvShow?>> GetSeries,
        Func<int, int, string?, string?, string?, CancellationToken, Task<TvSeason?>> GetSeason,
        Func<string, int, string, string?, CancellationToken, Task<IReadOnlyList<SearchMovie>?>> SearchMovies,
        Func<string, string, string?, int, CancellationToken, Task<IReadOnlyList<SearchTv>?>> SearchSeries);
}
