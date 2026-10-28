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
/// <remarks>The provider's own link for the source is never read or kept (review 2026-10-07 round 2, P1).</remarks>
public sealed record FetchedRating(string Source, string Scale, double Value, int? Votes);

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
            // Structure is checked, not guessed (review 2026-10-07, P2 5 and round 2, P2 6): an item that is not an object, a
            // source that is not a string, or a value, score or vote count of the wrong kind makes the whole answer malformed, so
            // it changes no stored value and counts as a provider failure. A number MDBList simply does not have (null, "",
            // "N/A") is absent, which is not malformed.
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("source", out var name) || name.ValueKind != JsonValueKind.String)
                return Done(RatingsOutcomes.Malformed, status);
            if (!TryNumber(item, "value", out var value) || !TryNumber(item, "score", out _) || !TryVotes(item, out var votes))
                return Done(RatingsOutcomes.Malformed, status);
            var source = name.GetString();
            if (RatingSources.FromMdbList(source, value, votes) is not { } mapped) continue;
            result.Add(new FetchedRating(mapped.Source, mapped.Scale, mapped.Value, votes is > 0 ? votes : null));
        }

        // One value per source; MDBList has been seen to repeat a source.
        return new MdbListResult(RatingsOutcomes.Ok, result.DistinctBy(rating => rating.Source).ToArray(), null, status);
    }

    /// <summary>Reads an optional number: absent, null, "" or "N/A" is no value; a number or numeric text is read; anything else is malformed.</summary>
    private static bool TryNumber(JsonElement item, string property, out double? value)
    {
        value = null;
        if (!item.TryGetProperty(property, out var raw)) return true;
        switch (raw.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Number:
                value = raw.GetDouble();
                return true;
            case JsonValueKind.String:
                var text = raw.GetString()?.Trim() ?? string.Empty;
                if (text.Length == 0 || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return true;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return false;
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryVotes(JsonElement item, out int? votes)
    {
        votes = null;
        if (!item.TryGetProperty("votes", out var raw) || raw.ValueKind == JsonValueKind.Null) return true;
        if (raw.ValueKind == JsonValueKind.String && (raw.GetString()?.Trim() ?? string.Empty) is "" or "N/A") return true;
        votes = Integer(raw);
        return votes is not null;
    }

    /// <summary>
    /// When the provider asked to be called again: the later of <c>Retry-After</c> and <c>X-RateLimit-Reset</c>, so a short
    /// Retry-After never hides a reset days away (review 2026-10-07, P2 6). Null when neither says. A long delay is kept as
    /// given, however far away, and one past the last representable moment saturates to it (round 2, P3 7).
    /// </summary>
    private DateTime? RetryAfter(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        DateTime? latest = null;
        void Consider(DateTime candidate) => latest = latest is { } known && known >= candidate ? known : candidate;
        // A delay in seconds is read from the header's own text, digit by digit, so a number too large for .NET's parser (which
        // then reports no header at all) still saturates to the last representable moment (review round 3, P3 6).
        if (response.Headers.NonValidated.TryGetValues("Retry-After", out var raw) && raw.Count == 1 &&
            raw.ToString().Trim() is { Length: > 0 } text && text.All(char.IsAsciiDigit))
        {
            var left = (DateTime.MaxValue - now).TotalSeconds;
            var delay = 0d;
            foreach (var digit in text)
            {
                delay = delay * 10 + (digit - '0');
                if (delay >= left - 1) break;
            }

            Consider(delay >= left - 1 ? DateTime.MaxValue : now.AddSeconds(delay));
        }
        else if (response.Headers.RetryAfter?.Date is { } date)
        {
            Consider(date.UtcDateTime);
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 && seconds < 253402300799)
            Consider(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
        return latest;
    }

    private static int? Integer(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var number) && number is >= 0 and <= int.MaxValue => (int)number,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= int.MaxValue => (int)parsed,
        _ => null
    };
}
