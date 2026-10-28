using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMod.Services.Ratings;

/// <summary>A rating read from the host's own item, never written back (P9.R4).</summary>
public sealed record HostRating(string Source, string Provider, string Scale, double Value, DateTime FetchedAt);

/// <summary>
/// Reads what the host's metadata providers stored on a native item (plan decision 3, R1 evidence). Jellyfin 12's OMDb
/// provider writes <c>CriticRating</c> (Rotten Tomatoes critics) and <c>CommunityRating</c> (IMDb) and no vote count; its TMDb
/// provider writes <c>CommunityRating</c> too, and the first remote provider in the library's fetcher order wins it. So
/// <c>CommunityRating</c> is labelled by whichever of the two is enabled first for the item's type, and
/// <c>CriticRating</c> only while OMDb is enabled. A library without explicit type options says nothing; nothing is guessed.
/// </summary>
public sealed class HostRatingsReader(ILibraryManager library)
{
    /// <summary>The fetcher name Jellyfin gives its OMDb provider.</summary>
    public const string OmdbFetcher = "The Open Movie Database";

    /// <summary>The fetcher name Jellyfin gives its TMDb provider.</summary>
    public const string TmdbFetcher = "TheMovieDb";

    /// <summary>Reads the bound item's ratings, or none when it is gone or unreadable.</summary>
    public IReadOnlyList<HostRating> Read(Guid? itemId)
    {
        if (itemId is not { } id || id == Guid.Empty) return [];
        try
        {
            return Read(library.GetItemById(id));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Reads one native movie or series.</summary>
    public IReadOnlyList<HostRating> Read(BaseItem? item)
    {
        if (item is null || item.CommunityRating is null && item.CriticRating is null) return [];
        var type = item switch
        {
            MediaBrowser.Controller.Entities.Movies.Movie => "Movie",
            MediaBrowser.Controller.Entities.TV.Series => "Series",
            _ => null
        };
        if (type is null) return [];
        var options = library.GetLibraryOptions(item)?.TypeOptions?.FirstOrDefault(option =>
            string.Equals(option.Type, type, StringComparison.OrdinalIgnoreCase));
        if (options is null) return [];
        var enabled = options.MetadataFetchers ?? [];
        var order = (options.MetadataFetcherOrder ?? []).Concat(enabled).Distinct(StringComparer.Ordinal)
            .Where(name => enabled.Contains(name, StringComparer.Ordinal)).ToArray();
        var omdb = Array.IndexOf(order, OmdbFetcher);
        var tmdb = Array.IndexOf(order, TmdbFetcher);
        var at = item.DateLastRefreshed > DateTime.MinValue.AddDays(1) ? item.DateLastRefreshed : item.DateCreated;
        at = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var result = new List<HostRating>();
        if (item.CommunityRating is { } community && float.IsFinite(community) && community is > 0 and <= 10)
        {
            var first = omdb >= 0 && (tmdb < 0 || omdb < tmdb) ? OmdbFetcher : tmdb >= 0 ? TmdbFetcher : null;
            if (first == OmdbFetcher)
                result.Add(new(RatingSources.Imdb, RatingSources.ProviderHostOmdb, RatingSources.Ten, RatingSources.Round(community, RatingSources.Ten), at));
            else if (first == TmdbFetcher)
                result.Add(new(RatingSources.Tmdb, RatingSources.ProviderHostTmdb, RatingSources.Ten, RatingSources.Round(community, RatingSources.Ten), at));
        }

        if (item.CriticRating is { } critic && float.IsFinite(critic) && critic is >= 0 and <= 100 && omdb >= 0)
            result.Add(new(RatingSources.TomatoesCritic, RatingSources.ProviderHostOmdb, RatingSources.Percent,
                RatingSources.Round(critic, RatingSources.Percent), at));
        return result;
    }
}
