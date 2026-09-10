using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Entities;

namespace JellyfinMod.Services;

/// <summary>Enforces the signed-in user's native library and content visibility.</summary>
public sealed class LibraryAccess(IUserManager users, ILibraryManager library, ILocalizationManager localization)
{
    private readonly Dictionary<(Guid UserId, string MediaType), IReadOnlyList<CollectionFolder>> _libraries = new();

    /// <summary>Resolves only the authenticated Jellyfin user claim; request IDs are never trusted.</summary>
    public User? GetUser(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var id) ? users.GetUserById(id) : null;

    /// <summary>Gets accessible real movie/TV libraries compatible with the requested media type.</summary>
    public IReadOnlyList<CollectionFolder> GetLibraries(User user, string mediaType)
    {
        var key = (user.Id, mediaType);
        if (_libraries.TryGetValue(key, out var cached)) return cached;
        var folders = library.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>()
            .Where(folder => folder.CollectionType == (mediaType == "movie" ? CollectionType.movies : CollectionType.tvshows)).ToArray();
        _libraries[key] = folders;
        return folders;
    }

    /// <summary>Checks access to a supplied destination, including type and native library visibility.</summary>
    public bool CanUseLibrary(User user, string mediaType, Guid? id) =>
        id.HasValue && GetLibraries(user, mediaType).Any(folder => folder.Id == id);

    /// <summary>Checks access to a supplied destination when the caller did not filter by media type.</summary>
    public bool CanUseLibrary(User user, Guid id) =>
        GetLibraries(user, "movie").Concat(GetLibraries(user, "series")).Any(folder => folder.Id == id);

    /// <summary>Checks access to a durable entry without leaking its native binding or metadata.</summary>
    public bool CanRead(User user, Entry entry)
    {
        if (!CanUseLibrary(user, entry.MediaType, entry.TargetLibraryId)) return false;
        if (entry.JellyfinItemId is { } nativeId)
            return GetNativeItems(user, entry.MediaType, entry.TargetLibraryId).Any(native => native.Id == nativeId);
        return entry.MetadataJson is { } json && CanReadMetadata(user, JsonSerializer.Deserialize<TmdbMetadata>(json)!);
    }

    /// <summary>Checks ratings and tag allowlists for metadata with no native item.</summary>
    public bool CanReadMetadata(User user, TmdbMetadata metadata)
    {
        // TMDB discovery excludes adult titles; direct-create requests must apply the same boundary.
        if (metadata.Adult) return false;
        // A wanted title has no administrator-assigned allowlist tags. Never assume it qualifies.
        if (user.GetPreference(PreferenceKind.AllowedTags).Length > 0) return false;
        var scores = metadata.Certifications.Select(cert => localization.GetRatingScore(cert.Rating, cert.Country))
            .OfType<ParentalRatingScore>().ToArray();
        var blockUnrated = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems)
            .Contains(metadata.MediaType == "movie" ? UnratedItem.Movie : UnratedItem.Series);
        if (scores.Length == 0) return !blockUnrated && !metadata.Adult && !user.MaxParentalRatingScore.HasValue;
        if (!user.MaxParentalRatingScore.HasValue) return true;
        return !metadata.Adult && scores.All(score => score.Score < user.MaxParentalRatingScore.Value ||
            (score.Score == user.MaxParentalRatingScore.Value && (!user.MaxParentalRatingSubScore.HasValue ||
                (score.SubScore ?? 0) <= user.MaxParentalRatingSubScore.Value)));
    }

    /// <summary>Gets accessible native items in a validated library using Jellyfin's own restrictions.</summary>
    public IReadOnlyList<BaseItem> GetNativeItems(User user, string mediaType, Guid? libraryId = null, Action<InternalItemsQuery>? configure = null)
    {
        return GetLibraries(user, mediaType).Where(folder => !libraryId.HasValue || folder.Id == libraryId)
            .SelectMany(folder =>
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [mediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
                    Recursive = true,
                    IsVirtualItem = false
                };
                configure?.Invoke(query);
                return folder.GetItemList(query);
            }).DistinctBy(item => item.Id).ToArray();
    }

    /// <summary>Gets the latest played descendant date for a native series, using the pinned presentation-key relationship.</summary>
    public DateTime? GetSeriesDatePlayed(User user, BaseItem series, IUserDataManager userData)
    {
        if (string.IsNullOrEmpty(series.PresentationUniqueKey)) return null;
        return library.GetItemList(new InternalItemsQuery(user)
        {
            SeriesPresentationUniqueKey = series.PresentationUniqueKey,
            IsPlayed = true,
            GroupByPresentationUniqueKey = false
        }).Select(item => userData.GetUserData(user, item)).OfType<UserItemData>().Where(data => data.Played).Select(data => data.LastPlayedDate).DefaultIfEmpty().Max();
    }

    /// <summary>Gets playable descendant episodes visible to the requesting user.</summary>
    public IReadOnlyList<BaseItem> GetEpisodes(User user, BaseItem series) => series is Folder folder
        ? folder.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsVirtualItem = false
        })
        : [];

    /// <summary>Binds individual episode availability within a verified native series.</summary>
    public void BindEpisodes(User user, BaseItem? series, IReadOnlyList<Episode> episodes)
    {
        if (series is not Folder) return;
        var nativeEpisodes = GetEpisodes(user, series);
        foreach (var episode in episodes)
        {
            var tmdbId = episode.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var matches = nativeEpisodes.Where(item => ProviderId(item, "Tmdb") == tmdbId).ToArray();
            if (matches.Length == 0)
                matches = nativeEpisodes.Where(item => item.ParentIndexNumber == episode.SeasonNumber && item.IndexNumber == episode.EpisodeNumber
                    && ProviderId(item, "Tmdb") is null).ToArray();
            if (matches.Length != 1) continue;
            episode.JellyfinItemId = matches[0].Id;
            episode.State = FileState.OnDisk;
        }
    }

    /// <summary>Checks a bound episode's own restrictions before returning its metadata.</summary>
    public bool CanReadEpisode(User user, Episode episode) => episode.JellyfinItemId is not { } id ||
        GetNativeItems(user, "series").SelectMany(series => GetEpisodes(user, series)).Any(native => native.Id == id);

    /// <summary>Finds an owned match only within the authorized destination library.</summary>
    public BaseItem? FindOwned(User user, TmdbMetadata metadata, Guid libraryId)
    {
        var native = GetNativeItems(user, metadata.MediaType, libraryId);
        var tmdbId = metadata.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return native.FirstOrDefault(item => ProviderId(item, "Tmdb") == tmdbId) ??
            (metadata.MediaType == "series" && metadata.TvdbId is { } tvdbId
                ? native.FirstOrDefault(item => ProviderId(item, "Tvdb") == tvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : null);
    }

    private static string? ProviderId(BaseItem item, string provider) => item.ProviderIds
        .FirstOrDefault(value => value.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
}
