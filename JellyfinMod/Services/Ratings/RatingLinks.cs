using System.Text.RegularExpressions;

namespace JellyfinMod.Services.Ratings;

/// <summary>D277: accept only title-page paths, and build a new URL on a fixed source host.</summary>
public static partial class RatingLinks
{
    /// <summary>Rebuilds a source URL; credentials, query, fragment, escapes and all other paths are rejected.</summary>
    public static string? Rebuild(string source, string? value)
    {
        if (value is null || value.Length > 512) return null;
        var match = source switch
        {
            RatingSources.TomatoesCritic or RatingSources.TomatoesAudience => Tomatoes().Match(value),
            RatingSources.Metacritic or RatingSources.MetacriticUser => Metacritic().Match(value),
            RatingSources.Letterboxd => Letterboxd().Match(value),
            RatingSources.RogerEbert => Ebert().Match(value),
            _ => null
        };
        if (match is not { Success: true }) return null;
        var path = match.Groups[1].Value;
        return source switch
        {
            RatingSources.TomatoesCritic or RatingSources.TomatoesAudience => "https://www.rottentomatoes.com/" + path,
            RatingSources.Metacritic or RatingSources.MetacriticUser => "https://www.metacritic.com/" + path + "/",
            RatingSources.Letterboxd => "https://letterboxd.com/" + path + "/",
            _ => "https://www.rogerebert.com/" + path
        };
    }

    /// <summary>Whether one of the sources with a Wikidata identifier needs a link.</summary>
    public static bool NeedsLookup(FetchedRating rating) => rating.Url is null && rating.Source is
        RatingSources.TomatoesCritic or RatingSources.TomatoesAudience or RatingSources.Metacritic or
        RatingSources.MetacriticUser or RatingSources.Letterboxd;

    [GeneratedRegex(@"\Ahttps://(?:www\.)?rottentomatoes\.com/((?:m|tv)/[a-z0-9]+(?:[_-][a-z0-9]+)*)/?\z")]
    private static partial Regex Tomatoes();

    [GeneratedRegex(@"\Ahttps://(?:www\.)?metacritic\.com/((?:movie|tv)/[a-z0-9]+(?:-[a-z0-9]+)*)/?\z")]
    private static partial Regex Metacritic();

    [GeneratedRegex(@"\Ahttps://(?:www\.)?letterboxd\.com/(film/[a-z0-9]+(?:-[a-z0-9]+)*)/?\z")]
    private static partial Regex Letterboxd();

    [GeneratedRegex(@"\Ahttps://(?:www\.)?rogerebert\.com/(reviews/[a-z0-9]+(?:-[a-z0-9]+)*)/?\z")]
    private static partial Regex Ebert();
}
