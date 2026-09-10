using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Net;
using JellyfinMod.Data;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>Fetches metadata from TMDB without exposing credentials or remote response bodies.</summary>
public sealed class TmdbClient(IHttpClientFactory clients, Func<PluginConfiguration> configuration, ILogger<TmdbClient> logger)
{
    /// <summary>Fetches a movie or series and its regional content certifications.</summary>
    public async Task<TmdbMetadata> GetDetailsAsync(string mediaType, int id, CancellationToken cancellationToken)
    {
        var path = MediaPath(mediaType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        using var json = await GetAsync($"{path}/{id}?append_to_response=external_ids,{(mediaType == "movie" ? "release_dates" : "content_ratings")}", cancellationToken);
        if (mediaType == "series")
        {
            if (!json.RootElement.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array)
                throw new TmdbException("TMDB returned incomplete series metadata.", HttpStatusCode.BadGateway);
            var numbers = seasons.EnumerateArray().Select(season => Number(season, "season_number")).ToArray();
            if (numbers.Any(number => number is null or < 0) || numbers.Distinct().Count() != numbers.Length)
                throw new TmdbException("TMDB returned conflicting season identities.", HttpStatusCode.BadGateway);
        }
        var result = Parse(json.RootElement, mediaType);
        if (result.TmdbId != id || string.IsNullOrWhiteSpace(result.Title))
        {
            throw new TmdbException("TMDB returned incomplete metadata.", HttpStatusCode.BadGateway);
        }

        return result;
    }

    /// <summary>Fetches one remote search page. Permission checks and exclusions happen before exposing it.</summary>
    public async Task<TmdbPage> SearchAsync(string query, string mediaType, int page, CancellationToken cancellationToken)
    {
        var path = MediaPath(mediaType);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new ArgumentException("A search query of 1–200 characters is required.", nameof(query));
        if (page is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
        using var json = await GetAsync($"search/{path}?query={Uri.EscapeDataString(query.Trim())}&include_adult=false&page={page}", cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("results", out var searchResults) || searchResults.ValueKind != JsonValueKind.Array)
            throw new TmdbException("TMDB returned incomplete search metadata.", HttpStatusCode.BadGateway);
        var results = Array(root, "results").Select(item => Parse(item, mediaType)).Where(item => item.TmdbId > 0 && !string.IsNullOrWhiteSpace(item.Title)).ToArray();
        return new TmdbPage(results, page < Math.Min(Number(root, "total_pages") ?? 0, 500));
    }

    /// <summary>Fetches all known season episodes before a series add commits any rows.</summary>
    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(TmdbMetadata series, CancellationToken cancellationToken)
    {
        if (series.MediaType != "series") return [];
        var episodes = new ConcurrentBag<Episode>();
        await Parallel.ForEachAsync(series.Seasons, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (season, token) =>
        {
            using var json = await GetAsync($"tv/{series.TmdbId}/season/{season.Number}", token);
            if (!json.RootElement.TryGetProperty("episodes", out var episodeArray) || episodeArray.ValueKind != JsonValueKind.Array ||
                (json.RootElement.TryGetProperty("season_number", out _) && Number(json.RootElement, "season_number") != season.Number) ||
                episodeArray.GetArrayLength() != season.EpisodeCount)
                throw new TmdbException("TMDB returned incomplete season metadata.", HttpStatusCode.BadGateway);
            foreach (var item in episodeArray.EnumerateArray())
            {
                var id = Number(item, "id");
                var number = Number(item, "episode_number");
                if (id is not > 0 || number is not > 0 || Number(item, "season_number") != season.Number)
                    throw new TmdbException("TMDB returned incomplete episode metadata.", HttpStatusCode.BadGateway);
                episodes.Add(new Episode
                {
                    TmdbId = id.Value, SeasonNumber = season.Number, EpisodeNumber = number.Value,
                    Title = Text(item, "name") ?? $"Episode {number}", Overview = Text(item, "overview"),
                    StillPath = Text(item, "still_path"), RuntimeMinutes = Number(item, "runtime"),
                    AirDate = DateTime.TryParseExact(Text(item, "air_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null
                });
            }
        });

        if (episodes.DistinctBy(e => e.TmdbId).Count() != episodes.Count || episodes.DistinctBy(e => (e.SeasonNumber, e.EpisodeNumber)).Count() != episodes.Count)
            throw new TmdbException("TMDB returned conflicting episode identities.", HttpStatusCode.BadGateway);
        return episodes.OrderBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber).ToArray();
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        var config = configuration();
        var token = config.TmdbReadAccessToken.Trim();
        var key = config.TmdbApiKey.Trim();
        if (token.Length == 0 && key.Length == 0)
            throw new TmdbException("An administrator must configure a TMDB API Read Access Token or API key.", HttpStatusCode.ServiceUnavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var credential = key.Length > 0 ? $"&api_key={Uri.EscapeDataString(key)}" : string.Empty;
            var separator = path.Contains('?') ? "&" : "?";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/{path}{separator}language=en-US{credential}");
            if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(NamedClient.Default).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TMDB request {Path} returned {StatusCode}", path.Split('?', 2)[0], (int)response.StatusCode);
                throw new TmdbException(response.StatusCode == HttpStatusCode.NotFound ? "Title not found on TMDB." : "TMDB is temporarily unavailable.",
                    response.StatusCode == HttpStatusCode.NotFound ? HttpStatusCode.NotFound : HttpStatusCode.BadGateway);
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
            throw new TmdbException("TMDB request timed out.", HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning(error, "TMDB request {Path} failed", path.Split('?', 2)[0]);
            throw new TmdbException("TMDB is temporarily unavailable.", HttpStatusCode.BadGateway);
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
                Text(s, "name") ?? string.Empty, Number(s, "episode_count") ?? 0, Text(s, "air_date"), Text(s, "poster_path"))).ToArray());
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
    [property: JsonPropertyName("seasons")] TmdbSeason[] Seasons);

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
public sealed class TmdbException(string message, HttpStatusCode statusCode) : Exception(message)
{
    /// <summary>Gets the HTTP status to return.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
