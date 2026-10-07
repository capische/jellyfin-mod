using System.Globalization;
using System.Net;
using System.Text.Json;
using JellyfinMod.Data;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Ratings;

/// <summary>Where MDBList is reached. Only an integration host registers one, to point at its boundary server.</summary>
public sealed record RatingsEndpoint(Uri BaseAddress)
{
    /// <summary>The public MDBList API.</summary>
    public static Uri Default { get; } = new("https://api.mdblist.com/");
}

/// <summary>One rating as MDBList reported it, already mapped to a source and scale.</summary>
public sealed record FetchedRating(string Source, string Scale, double Value, int? Votes, string? Url);

/// <summary>What one MDBList call produced. Never carries the key, the request URL or a response body.</summary>
/// <param name="Outcome">A <see cref="RatingsOutcomes"/> code.</param>
/// <param name="Ratings">The ratings that arrived (only for <c>ok</c>).</param>
/// <param name="RetryAfter">When a 429 asked to be called again, if it said.</param>
/// <param name="Status">The HTTP status, when there was one.</param>
public sealed record MdbListResult(string Outcome, IReadOnlyList<FetchedRating> Ratings, DateTime? RetryAfter, int? Status);

/// <summary>
/// Reads one title's ratings from MDBList (P9.R3): <c>GET {base}/tmdb/{movie|show}/{tmdbId}?apikey=…</c>. The key travels
/// as the query parameter MDBList expects, so this class never logs a URL; .NET 10's HTTP client logging redacts query
/// strings, and the integration suite and R8 leak-check every log line.
/// </summary>
public sealed class MdbListClient(IHttpClientFactory clients, Func<PluginConfiguration> configuration, ILogger<MdbListClient> logger,
    TimeProvider clock, RatingsEndpoint? endpoint = null)
{
    /// <summary>The title the Test button asks for: TMDB movie 278.</summary>
    public const int TestTmdbId = 278;

    /// <summary>How long one call may take.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>The base address in use: an integration host's, the instance's hidden override, or MDBList's own.</summary>
    public Uri BaseAddress => endpoint?.BaseAddress ?? Override(configuration()) ?? RatingsEndpoint.Default;

    /// <summary>The hidden XML override, when it is a usable address (plan decision 6).</summary>
    public static Uri? Override(PluginConfiguration configuration) =>
        configuration.RatingsProviderBaseUrl is { Length: > 0 } value && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri : null;

    /// <summary>Fetches one title. Every failure is a code; nothing throws except cancellation of the caller's token.</summary>
    public async Task<MdbListResult> FetchAsync(string apiKey, string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        var kind = mediaType == "series" ? "show" : "movie";
        var root = BaseAddress.ToString().TrimEnd('/');
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{root}/tmdb/{kind}/{tmdbId.ToString(CultureInfo.InvariantCulture)}?apikey={Uri.EscapeDataString(apiKey)}");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await clients.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Done(RatingsOutcomes.Unauthorized, status);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new MdbListResult(RatingsOutcomes.RateLimited, [], RetryAfter(response), status);
            if (response.StatusCode == HttpStatusCode.NotFound) return Done(RatingsOutcomes.NotFound, status);
            if (!response.IsSuccessStatusCode) return Done(RatingsOutcomes.Failed, status);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return Parse(document.RootElement, tmdbId, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Done(RatingsOutcomes.Timeout, null);
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning("MDBList could not be reached: {ErrorType}", error.GetType().Name);
            return Done(RatingsOutcomes.Unreachable, null);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or FormatException)
        {
            return Done(RatingsOutcomes.Malformed, null);
        }
    }

    private static MdbListResult Done(string outcome, int? status) => new(outcome, [], null, status);

    private static MdbListResult Parse(JsonElement root, int tmdbId, int status)
    {
        if (root.ValueKind != JsonValueKind.Object) return Done(RatingsOutcomes.Malformed, status);
        if (!root.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array)
        {
            // An answer with an error field and no ratings: a refused key is named as such; anything else is unknown.
            var error = root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? string.Empty : null;
            if (error is not null && error.Contains("key", StringComparison.OrdinalIgnoreCase)) return Done(RatingsOutcomes.Unauthorized, status);
            if (error is not null && error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return Done(RatingsOutcomes.NotFound, status);
            return Done(RatingsOutcomes.Malformed, status);
        }

        // An answer about another title is not this title's ratings.
        if (root.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty("tmdb", out var id) &&
            Integer(id) is { } returned && returned != tmdbId) return Done(RatingsOutcomes.Malformed, status);

        var result = new List<FetchedRating>();
        foreach (var item in ratings.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Done(RatingsOutcomes.Malformed, status);
            var source = item.TryGetProperty("source", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            var votes = item.TryGetProperty("votes", out var count) ? Integer(count) : null;
            var value = item.TryGetProperty("value", out var number) ? Number(number) : null;
            if (RatingSources.FromMdbList(source, value, votes) is not { } mapped) continue;
            var url = item.TryGetProperty("url", out var link) ? link.ValueKind switch
            {
                JsonValueKind.String => link.GetString(),
                JsonValueKind.Number => link.GetRawText(),
                _ => null
            } : null;
            result.Add(new FetchedRating(mapped.Source, mapped.Scale, mapped.Value, votes is > 0 ? votes : null,
                url is { Length: > 0 and <= 512 } ? url : null));
        }

        // One value per source; MDBList has been seen to repeat a source.
        return new MdbListResult(RatingsOutcomes.Ok, result.DistinctBy(rating => rating.Source).ToArray(), null, status);
    }

    private DateTime? RetryAfter(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) return now + delta;
            if (retry.Date is { } date) return date.UtcDateTime;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return null;
    }

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static int? Integer(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) && number is >= 0 and <= int.MaxValue => (int)number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= int.MaxValue => (int)parsed,
        _ => null
    };
}
