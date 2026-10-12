using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Net;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Fetches metadata from TMDB without exposing credentials or remote response bodies.</summary>
/// <remarks>
/// The Read Access Token lives in the SQLite settings row since P7.S7. A value or reference still on the XML
/// configuration wins, because it is newer: the one-time import moves it at the next startup.
/// The token is optional (P1.FU13): with none saved, every call goes through Jellyfin's own TMDb connection
/// (<see cref="JellyfinTmdb"/>), and only when that is unavailable too does a call fail as not configured.
/// </remarks>
public sealed class TmdbClient(IHttpClientFactory clients, Func<PluginConfiguration> configuration, ILogger<TmdbClient> logger,
    AcquisitionSecretStore? secrets = null, Func<ModDbContext>? database = null, TmdbEndpoint? endpoint = null,
    JellyfinTmdb? jellyfin = null)
{
    /// <summary>The source a call used: the saved token (or v3 key).</summary>
    public const string TokenSource = "token";

    /// <summary>The source a call used: Jellyfin's own TMDb connection.</summary>
    public const string JellyfinSource = "jellyfin";

    /// <summary>
    /// Checks the configured credential against TMDB without reading any title (P7.S7 Discovery Test). With no token saved,
    /// it checks Jellyfin's TMDb connection instead, with one read of a well-known series (P1.FU13).
    /// </summary>
    /// <returns>A stable code, a sentence and the source tested; never the credential or a raw exception.</returns>
    public async Task<(string Code, string Message, string? Source)> TestAsync(CancellationToken cancellationToken)
    {
        string? source = null;
        try
        {
            var (credential, viaJellyfin) = await SourceAsync(cancellationToken).ConfigureAwait(false);
            if (viaJellyfin is not null)
            {
                source = JellyfinSource;
                await viaJellyfin.GetDetailsAsync("series", JellyfinTmdb.ProbeSeriesId, cancellationToken).ConfigureAwait(false);
                return ("ok", "TMDB answered through Jellyfin's built-in TMDb connection.", source);
            }

            source = TokenSource;
            using var _ = await GetAsync("configuration", credential!, cancellationToken).ConfigureAwait(false);
            return ("ok", "TMDB accepted the token.", source);
        }
        catch (TmdbException failure)
        {
            return (failure.Code, failure.Code switch
            {
                "not_configured" => "No TMDB token is saved, and Jellyfin's built-in TMDb connection is unavailable.",
                "unauthorized" => "TMDB rejected the token. Check it was copied completely.",
                "timeout" => "TMDB did not answer in time.",
                "unreachable" => "TMDB could not be reached from this server.",
                "not_found" when source == JellyfinSource => "Jellyfin's TMDb connection returned nothing. TMDB may be refusing its key or unreachable.",
                _ => "TMDB answered with an error."
            }, source);
        }
    }

    /// <summary>
    /// Checks Jellyfin's TMDb connection whatever token is saved: one well-known series and its first season, read only
    /// (P1.FU13 live diagnostic). Nothing is recorded.
    /// </summary>
    /// <returns>A stable code and a sentence with the counts read.</returns>
    public async Task<(string Code, string Message)> TestJellyfinAsync(CancellationToken cancellationToken)
    {
        if (jellyfin is not { Available: true })
            return ("not_available", "Jellyfin's built-in TMDb connection is unavailable on this server.");
        try
        {
            var (seasons, episodes) = await jellyfin.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return ("ok", $"Jellyfin's TMDb connection answered: {seasons} seasons, {episodes} episodes in season 1.");
        }
        catch (TmdbException failure)
        {
            return (failure.Code, $"Jellyfin's TMDb connection failed: {failure.Message}");
        }
    }

    /// <summary>Fetches a movie or series and its regional content certifications.</summary>
    public async Task<TmdbMetadata> GetDetailsAsync(string mediaType, int id, CancellationToken cancellationToken)
    {
        var path = MediaPath(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var (credential, viaJellyfin) = await SourceAsync(cancellationToken);
        var result = viaJellyfin is not null ? await viaJellyfin.GetDetailsAsync(mediaType, id, cancellationToken)
            : await DetailsOverHttpAsync(path, mediaType, id, credential!, cancellationToken);
        if (result.TmdbId != id || string.IsNullOrWhiteSpace(result.Title))
        {
            throw new TmdbException("TMDB returned incomplete metadata.", HttpStatusCode.BadGateway);
        }

        return result;
    }

    private async Task<TmdbMetadata> DetailsOverHttpAsync(string path, string mediaType, int id, Credential credential,
        CancellationToken cancellationToken)
    {
        using var json = await GetAsync($"{path}/{id}?append_to_response=external_ids,{(mediaType == "movie" ? "release_dates" : "content_ratings")}",
            credential, cancellationToken);
        if (mediaType == "series")
        {
            if (!json.RootElement.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
                throw new TmdbException("TMDB returned incomplete series metadata.", HttpStatusCode.BadGateway);
            var numbers = seasons.EnumerateArray().Select(season => Number(season, "season_number")).ToArray();
            if (numbers.Any(number => number is null or < 0) || numbers.Distinct().Count() != numbers.Length)
                throw new TmdbException("TMDB returned conflicting season identities.", HttpStatusCode.BadGateway);
        }

        return Parse(json.RootElement, mediaType);
    }

    /// <summary>Fetches one remote search page. Permission checks and exclusions happen before exposing it.</summary>
    public async Task<TmdbPage> SearchAsync(string query, string mediaType, int page, CancellationToken cancellationToken)
    {
        var path = MediaPath(mediaType);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("A search query of 1–200 characters is required.", nameof(query));
        if (page is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        var (credential, viaJellyfin) = await SourceAsync(cancellationToken);
        if (viaJellyfin is not null) return await viaJellyfin.SearchAsync(query.Trim(), mediaType, page, cancellationToken);
        using var json = await GetAsync($"search/{path}?query={Uri.EscapeDataString(query.Trim())}&include_adult=false&page={page}",
            credential!, cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("results", out var searchResults) || searchResults.ValueKind != JsonValueKind.Array)
            throw new TmdbException("TMDB returned incomplete search metadata.", HttpStatusCode.BadGateway);
        var results = Array(root, "results").Select(item => Parse(item, mediaType)).Where(item => item.TmdbId > 0 && !string.IsNullOrWhiteSpace(item.Title)).ToArray();
        return new TmdbPage(results, page < Math.Min(Number(root, "total_pages") ?? 0, 500));
    }

    /// <summary>The most objects TMDB appends to one response (<c>append_to_response</c>).</summary>
    private const int MaxAppendedSeasons = 20;

    /// <summary>Fetches all known season episodes before a series add commits any rows.</summary>
    /// <remarks>
    /// One request reads up to twenty seasons, appended to the series (P1.FU4), instead of one request per season. A season
    /// TMDB leaves out of an appended response is read on its own, so an occasional omission never fails the series.
    /// Through Jellyfin's TMDb connection each season is one request (P1.FU13), four at a time.
    /// </remarks>
    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(TmdbMetadata series, CancellationToken cancellationToken)
    {
        if (series.MediaType != "series") return [];
        var episodes = new ConcurrentBag<Episode>();
        var omitted = new ConcurrentBag<int>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
        var (credential, viaJellyfin) = await SourceAsync(cancellationToken);
        if (viaJellyfin is not null)
        {
            await Parallel.ForEachAsync(series.Seasons.Select(season => season.Number), parallel,
                async (number, token) => await viaJellyfin.ReadSeasonAsync(series.TmdbId, number, episodes, token));
            return Checked(episodes);
        }

        await Parallel.ForEachAsync(series.Seasons.Select(season => season.Number).Chunk(MaxAppendedSeasons), parallel,
            async (numbers, token) =>
            {
                using var json = await GetAsync($"tv/{series.TmdbId}?append_to_response=" +
                    string.Join(",", numbers.Select(number => $"season/{number}")), credential!, token);
                foreach (var number in numbers)
                {
                    if (json.RootElement.TryGetProperty($"season/{number}", out var season) && season.ValueKind != JsonValueKind.Null)
                        ReadSeason(season, number, episodes);
                    else
                        omitted.Add(number);
                }
            });
        await Parallel.ForEachAsync(omitted, parallel, async (number, token) =>
        {
            using var json = await GetAsync($"tv/{series.TmdbId}/season/{number}", credential!, token);
            ReadSeason(json.RootElement, number, episodes);
        });

        return Checked(episodes);
    }

    /// <summary>A series' episodes in order, once no two share an id or a position.</summary>
    private static Episode[] Checked(ConcurrentBag<Episode> episodes)
    {
        if (episodes.DistinctBy(e => e.TmdbId).Count() != episodes.Count || episodes.DistinctBy(e => (e.SeasonNumber, e.EpisodeNumber)).Count() != episodes.Count)
            throw new TmdbException("TMDB returned conflicting episode identities.", HttpStatusCode.BadGateway);
        return episodes.OrderBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber).ToArray();
    }

    /// <summary>Reads one season's episodes, appended or on its own; incomplete or mismatched metadata fails the series.</summary>
    private static void ReadSeason(JsonElement season, int seasonNumber, ConcurrentBag<Episode> episodes)
    {
        if (season.ValueKind != JsonValueKind.Object || !season.TryGetProperty("episodes", out var episodeArray) ||
            episodeArray.ValueKind != JsonValueKind.Array ||
            (season.TryGetProperty("season_number", out _) && Number(season, "season_number") != seasonNumber))
            throw new TmdbException("TMDB returned incomplete season metadata.", HttpStatusCode.BadGateway);
        foreach (var item in episodeArray.EnumerateArray())
        {
            var id = Number(item, "id");
            var number = Number(item, "episode_number");
            if (id is not > 0 || number is not > 0 || Number(item, "season_number") != seasonNumber)
                throw new TmdbException("TMDB returned incomplete episode metadata.", HttpStatusCode.BadGateway);
            episodes.Add(new Episode
            {
                TmdbId = id.Value, SeasonNumber = seasonNumber, EpisodeNumber = number.Value,
                Title = Text(item, "name") ?? $"Episode {number}", Overview = Text(item, "overview"),
                StillPath = Text(item, "still_path"), RuntimeMinutes = Number(item, "runtime"),
                AirDate = DateTime.TryParseExact(Text(item, "air_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null
            });
        }
    }

    private async Task<string?> StoredTokenRefAsync(CancellationToken cancellationToken)
    {
        if (database is null) return null;
        await using var context = database();
        return await context.AcquisitionSettings.AsNoTracking().Where(row => row.Id == AcquisitionSettings.SingletonId)
            .Select(row => row.TmdbReadAccessTokenRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadSecretAsync(string? reference, CancellationToken cancellationToken) =>
        secrets is null ? string.Empty : await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false) ?? string.Empty;

    /// <summary>The saved TMDB credential: the Read Access Token, the v3 key, or both.</summary>
    private sealed record Credential(string Token, string Key);

    /// <summary>
    /// Where a call goes: the saved token or key when there is one, else Jellyfin's TMDb connection (P1.FU13), else nowhere.
    /// </summary>
    private async Task<(Credential? Credential, JellyfinTmdb? Jellyfin)> SourceAsync(CancellationToken cancellationToken)
    {
        var config = configuration();
        // Saved credentials live in the secret store; a value on the configuration object is used as given.
        var token = (config.TmdbReadAccessToken.Length > 0 ? config.TmdbReadAccessToken
            : await ReadSecretAsync(config.TmdbReadAccessTokenRef ?? await StoredTokenRefAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false)).Trim();
        var key = (config.TmdbApiKey.Length > 0 ? config.TmdbApiKey
            : await ReadSecretAsync(config.TmdbApiKeyRef, cancellationToken).ConfigureAwait(false)).Trim();
        if (token.Length > 0 || key.Length > 0) return (new Credential(token, key), null);
        if (jellyfin is { Available: true }) return (null, jellyfin);
        throw new TmdbException("TMDB is not set up: save a TMDB token, or make Jellyfin's built-in TMDb connection available.",
            HttpStatusCode.ServiceUnavailable, "not_configured");
    }

    private async Task<JsonDocument> GetAsync(string path, Credential saved, CancellationToken cancellationToken)
    {
        var (token, key) = saved;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var credential = key.Length > 0 ? $"&api_key={Uri.EscapeDataString(key)}" : string.Empty;
            var separator = path.Contains('?') ? "&" : "?";
            var root = (endpoint?.BaseAddress ?? TmdbEndpoint.Default).ToString().TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/3/{path}{separator}language=en-US{credential}");
            if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(NamedClient.Default).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TMDB request {Path} returned {StatusCode}", path.Split('?', 2)[0], (int)response.StatusCode);
                throw new TmdbException(response.StatusCode == HttpStatusCode.NotFound ? "Title not found on TMDB." : "TMDB is temporarily unavailable.",
                    response.StatusCode == HttpStatusCode.NotFound ? HttpStatusCode.NotFound : HttpStatusCode.BadGateway,
                    response.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized"
                    : response.StatusCode == HttpStatusCode.NotFound ? "not_found" : "tmdb_error");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new TmdbException("TMDB returned invalid metadata.", HttpStatusCode.BadGateway);
            }
            return document;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TmdbException("TMDB request timed out.", HttpStatusCode.GatewayTimeout, "timeout");
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning("TMDB request {Path} failed: {ErrorType}", path.Split('?', 2)[0], error.GetType().Name);
            throw new TmdbException("TMDB is temporarily unavailable.", HttpStatusCode.BadGateway, "unreachable");
        }
        catch (JsonException)
        {
            throw new TmdbException("TMDB returned invalid metadata.", HttpStatusCode.BadGateway);
        }
    }

    private static string MediaPath(string mediaType) => mediaType switch
    {
        "movie" => "movie",
        "series" => "tv",
        _ => throw new ArgumentException("Media type must be movie or series.", nameof(mediaType))
    };

    private static TmdbMetadata Parse(JsonElement item, string mediaType)
    {
        var date = Text(item, mediaType == "movie" ? "release_date" : "first_air_date");
        DateTime? premiere = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        var certifications = new List<TmdbCertification>();
        if (item.TryGetProperty("release_dates", out var releases))
        {
            foreach (var country in Array(releases, "results"))
            {
                foreach (var release in Array(country, "release_dates"))
                {
                    if (Text(country, "iso_3166_1") is { } region && Text(release, "certification") is { Length: > 0 } rating)
                        certifications.Add(new(region, rating));
                }
            }
        }

        if (item.TryGetProperty("content_ratings", out var ratings))
        {
            foreach (var rating in Array(ratings, "results"))
                if (Text(rating, "iso_3166_1") is { } region && Text(rating, "rating") is { Length: > 0 } value)
                    certifications.Add(new(region, value));
        }

        item.TryGetProperty("external_ids", out var externalIds);
        return new TmdbMetadata(
            mediaType, Number(item, "id") ?? 0, Text(item, mediaType == "movie" ? "title" : "name") ?? string.Empty,
            premiere, Text(item, "overview"), Text(item, "poster_path"), Text(item, "backdrop_path"),
            Text(item, "imdb_id") ?? Text(externalIds, "imdb_id"), Number(externalIds, "tvdb_id"),
            item.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True,
            item.TryGetProperty("vote_average", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var number) ? number : null,
            Number(item, "runtime"), Array(item, "genres").Select(g => Text(g, "name")).OfType<string>().ToArray(),
            certifications.Distinct().ToArray(), Array(item, "seasons").Select(s => new TmdbSeason(Number(s, "season_number") ?? 0,
                Text(s, "name") ?? string.Empty, Number(s, "episode_count") ?? 0, Text(s, "air_date"), Text(s, "poster_path"))).ToArray(),
            Text(item, mediaType == "movie" ? "original_title" : "original_name"));
    }

    private static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static IEnumerable<JsonElement> Array(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}

/// <summary>A single remote page; total counts are intentionally not exposed as filtered totals.</summary>
public sealed record TmdbPage(IReadOnlyList<TmdbMetadata> Items,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>Metadata used internally for discovery, access checks and durable snapshots.</summary>
public sealed record TmdbMetadata(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("tmdbId")] int TmdbId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("premiereDate")] DateTime? PremiereDate,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("posterPath")] string? PosterPath,
    [property: JsonPropertyName("backdropPath")] string? BackdropPath,
    [property: JsonPropertyName("imdbId")] string? ImdbId,
    [property: JsonPropertyName("tvdbId")] int? TvdbId,
    [property: JsonPropertyName("adult")] bool Adult,
    [property: JsonPropertyName("communityRating")] double? CommunityRating,
    [property: JsonPropertyName("runtimeMinutes")] int? RuntimeMinutes,
    [property: JsonPropertyName("genres")] string[] Genres,
    [property: JsonPropertyName("certifications")] TmdbCertification[] Certifications,
    [property: JsonPropertyName("seasons")] TmdbSeason[] Seasons,
    [property: JsonPropertyName("originalTitle")] string? OriginalTitle = null);

/// <summary>A regional parental certification.</summary>
public sealed record TmdbCertification(
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("rating")] string Rating);

/// <summary>Summary metadata for a series season, without synthetic native episode records.</summary>
public sealed record TmdbSeason(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("episodeCount")] int EpisodeCount,
    [property: JsonPropertyName("airDate")] string? AirDate,
    [property: JsonPropertyName("posterPath")] string? PosterPath);

/// <summary>A sanitized failure safe to return to an authenticated client.</summary>
public sealed class TmdbException(string message, HttpStatusCode statusCode, string code = "tmdb_error") : Exception(message)
{
    /// <summary>Gets the HTTP status to return.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Gets the stable failure code a connection test reports.</summary>
    public string Code { get; } = code;
}

/// <summary>Where TMDB is reached. Only an integration host changes it, to point at its boundary server.</summary>
public sealed record TmdbEndpoint(Uri BaseAddress)
{
    /// <summary>The public TMDB API.</summary>
    public static Uri Default { get; } = new("https://api.themoviedb.org/");
}
