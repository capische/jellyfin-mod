using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinMod.Data;
using MediaBrowser.Common.Net;

namespace JellyfinMod.Services.Ratings;

/// <summary>An integration host's HTTP boundary; production always uses the public query service.</summary>
public sealed record WikidataRatingsEndpoint(Uri Address);

/// <summary>One best-effort lookup, without request URLs or raw response data.</summary>
public sealed record WikidataRatingsResult(string Outcome, IReadOnlyDictionary<string, string> Links, DateTime? RetryAfter = null);

/// <summary>One bounded SPARQL lookup by IMDb id, shared by all missing source links of a title.</summary>
public sealed partial class WikidataRatingsClient(IHttpClientFactory clients, TimeProvider clock, WikidataRatingsEndpoint? endpoint = null)
{
    /// <summary>The extra time a background refresh may spend looking up links.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Only title identifiers may be inserted into the query.</summary>
    public static bool IsImdbId(string? value) => value is not null && Imdb().IsMatch(value);

    /// <summary>Reads identifiers, validates each path, and rebuilds each source URL.</summary>
    public async Task<WikidataRatingsResult> FetchAsync(string imdbId, CancellationToken cancellationToken)
    {
        if (!IsImdbId(imdbId)) return new(RatingsOutcomes.Ok, new Dictionary<string, string>());
        var query = $$"""
            SELECT ?item ?rt ?mc ?lb WHERE {
              ?item <http://www.wikidata.org/prop/direct/P345> "{{imdbId}}" .
              OPTIONAL { ?item <http://www.wikidata.org/prop/direct/P1258> ?rt . }
              OPTIONAL { ?item <http://www.wikidata.org/prop/direct/P1712> ?mc . }
              OPTIONAL { ?item <http://www.wikidata.org/prop/direct/P6127> ?lb . }
            } LIMIT 2
            """;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var address = endpoint?.Address ?? new Uri("https://query.wikidata.org/sparql");
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "?format=json&query=" + Uri.EscapeDataString(query));
            request.Headers.Accept.ParseAdd("application/sparql-results+json");
            request.Headers.UserAgent.ParseAdd("JellyfinMod/0.1.0 (+https://github.com/capische/jellyfin-mod)");
            using var response = await clients.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new(RatingsOutcomes.RateLimited, new Dictionary<string, string>(),
                    MdbListClient.RetryAfter(response, clock.GetUtcNow().UtcDateTime));
            }
            if (!response.IsSuccessStatusCode) return new(RatingsOutcomes.Failed, new Dictionary<string, string>());
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var bindings = document.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.ValueKind != JsonValueKind.Array) return new(RatingsOutcomes.Malformed, new Dictionary<string, string>());
            // More than one answer is ambiguous: leave the web's search fallback in place.
            var links = new Dictionary<string, string>();
            if (bindings.GetArrayLength() == 1)
            {
                var row = bindings[0];
                Add("rt", "https://www.rottentomatoes.com/", RatingSources.TomatoesCritic, RatingSources.TomatoesAudience);
                Add("mc", "https://www.metacritic.com/", RatingSources.Metacritic, RatingSources.MetacriticUser);
                Add("lb", "https://letterboxd.com/film/", RatingSources.Letterboxd);
                void Add(string field, string root, params string[] sources)
                {
                    if (!row.TryGetProperty(field, out var binding) || binding.GetProperty("type").GetString() != "literal" ||
                        binding.GetProperty("value").ValueKind != JsonValueKind.String) return;
                    foreach (var source in sources)
                        if (RatingLinks.Rebuild(source, root + binding.GetProperty("value").GetString()) is { } url) links[source] = url;
                }
            }
            return new(RatingsOutcomes.Ok, links);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(RatingsOutcomes.Timeout, new Dictionary<string, string>());
        }
        catch (HttpRequestException)
        {
            return new(RatingsOutcomes.Unreachable, new Dictionary<string, string>());
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(RatingsOutcomes.Malformed, new Dictionary<string, string>());
        }
    }

    [GeneratedRegex(@"\Att[0-9]{7,10}\z")]
    private static partial Regex Imdb();
}
