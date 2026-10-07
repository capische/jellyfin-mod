using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JellyfinMod.Services.Ratings;

/// <summary>
/// The closed set of rating sources and their scales (PHASE9 "One rating model", plan decision 2). Values are kept and
/// shown in their own scale; nothing here converts, averages or combines them.
/// </summary>
public static partial class RatingSources
{
    /// <summary>IMDb, 0–10.</summary>
    public const string Imdb = "imdb";

    /// <summary>TMDB, 0–10 (first-party) or 0–100 (MDBList).</summary>
    public const string Tmdb = "tmdb";

    /// <summary>Trakt, 0–100.</summary>
    public const string Trakt = "trakt";

    /// <summary>Rotten Tomatoes critics (Tomatometer), 0–100.</summary>
    public const string TomatoesCritic = "tomatoes_critic";

    /// <summary>Rotten Tomatoes audience (Popcornmeter), 0–100.</summary>
    public const string TomatoesAudience = "tomatoes_audience";

    /// <summary>Metacritic critics, 0–100.</summary>
    public const string Metacritic = "metacritic";

    /// <summary>Metacritic users, 0–10.</summary>
    public const string MetacriticUser = "metacritic_user";

    /// <summary>Letterboxd, 0–5.</summary>
    public const string Letterboxd = "letterboxd";

    /// <summary>Roger Ebert, 0–4 stars.</summary>
    public const string RogerEbert = "rogerebert";

    /// <summary>Scale 0–10, one decimal.</summary>
    public const string Ten = "ten";

    /// <summary>Scale 0–100, integer.</summary>
    public const string Percent = "percent";

    /// <summary>Scale 0–5, one decimal.</summary>
    public const string Five = "five";

    /// <summary>Scale 0–4, one decimal.</summary>
    public const string Four = "four";

    /// <summary>A raw source whose scale this release does not know; never exposed.</summary>
    public const string Unknown = "unknown";

    /// <summary>Provider: the entry's own TMDB snapshot.</summary>
    public const string ProviderTmdb = "tmdb";

    /// <summary>Provider: MDBList.</summary>
    public const string ProviderMdbList = "mdblist";

    /// <summary>Provider: the host's item, written by its OMDb metadata provider.</summary>
    public const string ProviderHostOmdb = "host_omdb";

    /// <summary>Provider: the host's item, written by its TMDb metadata provider.</summary>
    public const string ProviderHostTmdb = "host_tmdb";

    /// <summary>Every source this release shows, in the default display order of the complete set.</summary>
    public static IReadOnlyList<string> Known { get; } =
        [Imdb, TomatoesCritic, TomatoesAudience, Tmdb, Trakt, Metacritic, MetacriticUser, Letterboxd, RogerEbert];

    /// <summary>The default sources, in order (user decision 6, 2026-10-07).</summary>
    public static IReadOnlyList<string> Defaults { get; } = [Imdb, TomatoesCritic, TomatoesAudience, Tmdb, Trakt];

    /// <summary>Gets a value indicating whether a source is one this release shows.</summary>
    public static bool IsKnown(string? source) => source is not null && Known.Contains(source, StringComparer.Ordinal);

    /// <summary>Provider precedence per source: the first provider with a value wins (plan decision 5).</summary>
    public static IReadOnlyList<string> Precedence(string source) => source switch
    {
        Tmdb => [ProviderTmdb, ProviderMdbList, ProviderHostTmdb],
        Imdb or TomatoesCritic => [ProviderMdbList, ProviderHostOmdb],
        _ => [ProviderMdbList]
    };

    /// <summary>Maps one MDBList rating to the stored shape, or null when it carries no usable value.</summary>
    public static (string Source, string Scale, double Value)? FromMdbList(string? rawSource, double? value, int? votes)
    {
        if (rawSource is null || value is not { } number || double.IsNaN(number) || double.IsInfinity(number)) return null;
        var raw = rawSource.Trim().ToLowerInvariant();
        (string Source, string Scale)? mapped = raw switch
        {
            "imdb" => (Imdb, Ten),
            "tmdb" => (Tmdb, number > 10 ? Percent : Ten),
            "trakt" => (Trakt, Percent),
            "tomatoes" => (TomatoesCritic, Percent),
            "popcorn" or "audience" or "tomatoesaudience" => (TomatoesAudience, Percent),
            "metacritic" => (Metacritic, Percent),
            "metacriticuser" => (MetacriticUser, Ten),
            "letterboxd" => (Letterboxd, Five),
            "rogerebert" => (RogerEbert, Four),
            _ => null
        };
        // A zero without votes is MDBList's "no score yet", never a real zero (outline: absent, never 0).
        if (number == 0 && votes is not > 0) return null;
        if (mapped is { } known)
        {
            return InScale(number, known.Scale) ? (known.Source, known.Scale, Round(number, known.Scale)) : null;
        }

        var name = Unsafe().Replace(raw, string.Empty);
        if (name.Length is 0 or > 32 || number < 0 || number > 1000) return null;
        return (name, Unknown, number);
    }

    /// <summary>Whether a value fits its scale.</summary>
    public static bool InScale(double value, string scale) => scale switch
    {
        Ten => value is >= 0 and <= 10,
        Percent => value is >= 0 and <= 100,
        Five => value is >= 0 and <= 5,
        Four => value is >= 0 and <= 4,
        _ => false
    };

    /// <summary>Rounds a value as its scale is shown: one decimal, or an integer percentage.</summary>
    public static double Round(double value, string scale) =>
        scale == Percent ? Math.Round(value, MidpointRounding.AwayFromZero) : Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>Reads a stored source list; anything malformed or unknown falls back to the defaults.</summary>
    public static IReadOnlyList<string> ParseList(string? json)
    {
        try
        {
            var values = json is null ? null : JsonSerializer.Deserialize<string[]>(json);
            if (values is not null && values.All(IsKnown) && values.Distinct(StringComparer.Ordinal).Count() == values.Length) return values;
        }
        catch (JsonException)
        {
        }

        return Defaults;
    }

    /// <summary>The public sites each source's link may point at; nothing else is kept.</summary>
    private static readonly Dictionary<string, string[]> LinkHosts = new(StringComparer.Ordinal)
    {
        [Imdb] = ["imdb.com"],
        [Tmdb] = ["themoviedb.org"],
        [Trakt] = ["trakt.tv"],
        [TomatoesCritic] = ["rottentomatoes.com"],
        [TomatoesAudience] = ["rottentomatoes.com"],
        [Metacritic] = ["metacritic.com"],
        [MetacriticUser] = ["metacritic.com"],
        [Letterboxd] = ["letterboxd.com"],
        [RogerEbert] = ["rogerebert.com"]
    };

    /// <summary>
    /// A provider link as it may be stored and shown: an absolute http(s) address on that source's own public site, rebuilt
    /// as https with its path only — no credentials, query or fragment, so nothing the provider echoes back (a key in a query
    /// string, review 2026-10-07 P1) can reach a viewer. Anything else is dropped.
    /// </summary>
    public static string? SafeUrl(string source, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || !LinkHosts.TryGetValue(source, out var hosts) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 ||
            !uri.IsDefaultPort) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (!hosts.Any(allowed => host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))) return null;
        var path = uri.AbsolutePath;
        if (path.Length > 300 || path.Contains("apikey", StringComparison.OrdinalIgnoreCase)) return null;
        return "https://" + host + path;
    }

    /// <summary>Formats a value for logs and tests only; the interface formats its own.</summary>
    public static string Describe(string source, double value, string scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{source}={value}/{scale}");

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex Unsafe();
}
