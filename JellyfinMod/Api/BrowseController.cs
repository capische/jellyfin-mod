using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>Interleaves authorized native and catalog candidates before any pagination.</summary>
[ApiController, Authorize, Route("JellyfinMod/Browse")]
public sealed class BrowseController(ModDbContext database, DatabaseInitializer readiness, LibraryAccess access,
    CatalogSortName sortNames, IDtoService dto, IUserDataManager userData, IMediaSourceManager mediaSources,
    IAuthorizationService? authorization = null, JellyfinMod.Services.Import.ClientSnapshotCache? snapshots = null) : ControllerBase
{
    private static readonly HashSet<string> SupportedSorts = ["SortName", "DateCreated", "ProductionYear", "PremiereDate", "CommunityRating", "CriticRating", "Runtime", "DateLastContentAdded", "OfficialRating", "DatePlayed", "PlayCount", "Random", "SeriesDatePlayed"];

    /// <summary>Returns one combined page. Unsupported sort/filter contracts fail explicitly.</summary>
    [HttpPost]
    public async Task<ActionResult<BrowseResult>> Browse(BrowseRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (request.TargetLibraryId.HasValue && !access.CanUseLibrary(user, request.MediaType, request.TargetLibraryId)) return NotFound();
        if (request.SortBy is null || request.State is null || request.SortBy.Length is < 1 or > 3 || request.SortBy.Any(field => !SupportedSorts.Contains(field)) ||
            request.State.Any(value => !Enum.GetValues<FileState>().Any(state => FileStates.ToWire(state) == value)) ||
            (request.SortBy.Contains("Random") && string.IsNullOrWhiteSpace(request.RandomSeed))) return BadRequest();
        var filters = request.Filters;
        if (filters is null || new object?[] { filters.Genres, filters.Years, filters.OfficialRatings, filters.Tags, filters.StudioIds, filters.Status,
            filters.SeriesStatus, filters.Features, filters.VideoBasicFilter, filters.VideoTypes, filters.AudioLanguages, filters.SubtitleLanguages }.Any(value => value is null)) return BadRequest();
        if (filters.Status.Any(value => value is not ("IsPlayed" or "IsUnplayed" or "IsFavorite" or "IsResumable")) ||
            filters.SeriesStatus.Any(value => !Enum.TryParse<SeriesStatus>(value, out _)) || filters.VideoTypes.Any(value => !Enum.TryParse<VideoType>(value, out _)) ||
            filters.Features.Any(value => value is not ("HasSubtitles" or "HasTrailer" or "HasSpecialFeature" or "HasThemeSong" or "HasThemeVideo")) ||
            filters.VideoBasicFilter.Any(value => value is not ("IsSD" or "IsHD" or "Is4K" or "Is3D"))) return BadRequest();
        var query = database.Entries.AsNoTracking().Where(entry => entry.MediaType == request.MediaType);
        if (request.TargetLibraryId.HasValue) query = query.Where(entry => entry.TargetLibraryId == request.TargetLibraryId);
        var native = access.GetNativeItems(user, request.MediaType, request.TargetLibraryId);
        var nativeIds = native.Select(item => item.Id).ToHashSet();
        var entries = (await query.ToListAsync(cancellationToken)).Where(entry => access.CanRead(user, entry, nativeIds)).ToArray();
        var entryIds = entries.Select(entry => entry.Id).ToArray();
        var evaluations = await database.RetentionEvaluations.AsNoTracking()
            .Where(evaluation => entryIds.Contains(evaluation.EntryId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var retentionPolicy = await database.RetentionPolicySnapshots.AsNoTracking().SingleOrDefaultAsync(
            policy => policy.Id == RetentionPolicyService.PolicyId, cancellationToken).ConfigureAwait(false);
        // A series is played when all its episodes are; only the native query applies Jellyfin's episode-based
        // semantics, so series Played/Unplayed go there instead of to the series row's own user data (P1.P10).
        var seriesPlayed = request.MediaType == "series"
            ? filters.Status.Where(value => value is "IsPlayed" or "IsUnplayed").ToArray()
            : [];
        var hasNativeQueryFilters = new Array[] { filters.Genres, filters.Years, filters.OfficialRatings, filters.Tags,
            filters.StudioIds, filters.SeriesStatus, filters.VideoTypes, filters.Features, filters.VideoBasicFilter,
            seriesPlayed }.Any(values => values.Length > 0);
        var nativeMatches = (hasNativeQueryFilters ? access.GetNativeItems(user, request.MediaType, request.TargetLibraryId, query =>
        {
            query.Genres = filters.Genres;
            query.Years = filters.Years;
            query.OfficialRatings = filters.OfficialRatings;
            query.Tags = filters.Tags;
            query.StudioIds = filters.StudioIds;
            query.SeriesStatuses = filters.SeriesStatus.Select(Enum.Parse<SeriesStatus>).ToArray();
            query.VideoTypes = filters.VideoTypes.Select(Enum.Parse<VideoType>).ToArray();
            foreach (var value in filters.Features.Concat(filters.VideoBasicFilter.Where(value => value != "IsHD" || !filters.VideoBasicFilter.Contains("IsSD")))) ApplyFeature(query, value);
            if (seriesPlayed.Length == 1) query.IsPlayed = seriesPlayed[0] == "IsPlayed";
        }) : native).Select(item => item.Id).ToHashSet();
        // Played and Unplayed together match nothing, as in the native filter.
        if (seriesPlayed.Length > 1) nativeMatches.Clear();
        var rowStatus = filters.Status.Except(seriesPlayed).ToArray();
        if (filters.AudioLanguages.Length > 0 || filters.SubtitleLanguages.Length > 0)
        {
            var nativeById = native.ToDictionary(item => item.Id);
            nativeMatches.RemoveWhere(id =>
            {
                var mediaItems = request.MediaType == "series" ? access.GetEpisodes(user, nativeById[id]) : [nativeById[id]];
                var streams = mediaItems.SelectMany(item => mediaSources.GetMediaStreams(item.Id));
                return filters.AudioLanguages.Length > 0 && !streams.Any(stream => stream.Type == MediaStreamType.Audio && filters.AudioLanguages.Contains(stream.Language, StringComparer.OrdinalIgnoreCase)) ||
                    filters.SubtitleLanguages.Length > 0 && !streams.Any(stream => stream.Type == MediaStreamType.Subtitle && filters.SubtitleLanguages.Contains(stream.Language, StringComparer.OrdinalIgnoreCase));
            });
        }
        var boundEntries = entries.Where(entry => entry.JellyfinItemId.HasValue && nativeIds.Contains(entry.JellyfinItemId.Value))
            .GroupBy(entry => entry.JellyfinItemId!.Value).ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Id).First());
        var candidates = native.Select(item => new Candidate(item, boundEntries.GetValueOrDefault(item.Id), null, item.SortName, userData.GetUserData(user, item),
                request.MediaType == "series" && request.SortBy.Contains("SeriesDatePlayed") ? access.GetSeriesDatePlayed(user, item, userData) : null))
            .Concat(entries.Where(entry => !entry.JellyfinItemId.HasValue || !nativeIds.Contains(entry.JellyfinItemId.Value))
                .Select(entry => new Candidate(null, entry, entry.MetadataJson is null ? null : JsonSerializer.Deserialize<TmdbMetadata>(entry.MetadataJson), sortNames.GetKey(entry.Title), null, null)));
        candidates = candidates.Where(row => row.Native is null ? MatchesMetadata(row, filters) : nativeMatches.Contains(row.Native.Id) && MatchesStatus(row.UserData, rowStatus));
        if (!string.IsNullOrEmpty(request.Alphabet)) candidates = candidates.Where(row => request.Alphabet == "#"
            ? StringComparer.Ordinal.Compare(row.SortName, "a") < 0 : row.SortName.StartsWith(request.Alphabet, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var search = sortNames.GetSearchKey(request.Query);
            candidates = candidates.Where(row => row.SearchValues.Any(value => sortNames.GetSearchKey(value).Contains(search, StringComparison.Ordinal)));
        }
        var projections = await JellyfinMod.Services.Import.QueueReadModel.ProjectAsync(database, snapshots, entryIds, cancellationToken);
        if (request.State.Length > 0) candidates = candidates.Where(row => request.State.Contains(row.Native is null
            ? new EntryDto(row.Entry!, projections.GetValueOrDefault(row.Entry!.Id)).State : "onDisk"));
        if (request.DueWithinDays.HasValue)
        {
            var deadline = DateTime.UtcNow.AddDays(request.DueWithinDays.Value);
            var dueEntryIds = evaluations.Where(evaluation => evaluation.State == RetentionEvaluationStates.Scheduled &&
                    evaluation.Deadline.HasValue && evaluation.Deadline.Value <= deadline)
                .Select(evaluation => evaluation.EntryId).ToHashSet();
            candidates = candidates.Where(row => row.Entry is { State: FileState.OnDisk } &&
                dueEntryIds.Contains(row.Entry.Id));
        }
        // Across libraries, the native copy that carries the bound entry (and its retention summary) wins (P1.P10).
        if (!request.TargetLibraryId.HasValue) candidates = candidates.GroupBy(row => row.TitleIdentity).Select(group => group
            .OrderBy(row => row.Native is null).ThenBy(row => row.Entry is null)
            .ThenBy(row => row.Identity, StringComparer.Ordinal).First());
        var filtered = candidates.ToArray();
        Array.Sort(filtered, (left, right) => Compare(left, right, request));
        var page = filtered.Skip(request.StartIndex);
        if (request.Limit.HasValue) page = page.Take(request.Limit.Value);
        var isAdmin = authorization is not null &&
            (await authorization.AuthorizeAsync(User, MediaBrowser.Common.Api.Policies.RequiresElevation)).Succeeded;
        var options = new DtoOptions { Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSourceCount, ItemFields.DateCreated] };
        var rows = page.Select(row => new BrowseRow(row.Native is null ? "entry" : "native",
            row.Native is null ? null : dto.GetBaseItemDto(row.Native, options, user), row.Entry is null ? null : new EntryDto(row.Entry, projections.GetValueOrDefault(row.Entry.Id)),
            row.Entry is null ? null : RetentionSummaries.ForViewer(
                RetentionSummaries.ForEntry(row.Entry, retentionPolicy, evaluations), isAdmin))).ToArray();
        return new BrowseResult(rows, filtered.Length, entries.Length > 0);
    }

    private static bool MatchesMetadata(Candidate row, BrowseFilters filters) =>
        filters.Status.Length == 0 && filters.Tags.Length == 0 && filters.StudioIds.Length == 0 && filters.OfficialRatings.Length == 0 &&
        filters.Features.Length == 0 && filters.VideoBasicFilter.Length == 0 && filters.VideoTypes.Length == 0 && filters.SeriesStatus.Length == 0 &&
        filters.AudioLanguages.Length == 0 && filters.SubtitleLanguages.Length == 0 &&
        (filters.Genres.Length == 0 || row.Metadata?.Genres.Any(genre => filters.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)) == true) &&
        (filters.Years.Length == 0 || row.Entry!.Year.HasValue && filters.Years.Contains(row.Entry.Year.Value));

    private static bool MatchesStatus(UserItemData? data, string[] values) => values.Length == 0 || data is not null && values.All(value => value switch
    {
        "IsPlayed" => data.Played,
        "IsUnplayed" => !data.Played,
        "IsFavorite" => data.IsFavorite,
        "IsResumable" => data.PlaybackPositionTicks > 0,
        _ => false
    });

    private static void ApplyFeature(InternalItemsQuery query, string value)
    {
        switch (value)
        {
            case "HasSubtitles": query.HasSubtitles = true; break;
            case "HasTrailer": query.HasTrailer = true; break;
            case "HasSpecialFeature": query.HasSpecialFeature = true; break;
            case "HasThemeSong": query.HasThemeSong = true; break;
            case "HasThemeVideo": query.HasThemeVideo = true; break;
            case "IsSD": query.IsHD = false; break;
            case "IsHD": query.IsHD = true; break;
            case "Is4K": query.Is4K = true; break;
            case "Is3D": query.Is3D = true; break;
        }
    }

    private static int Compare(Candidate left, Candidate right, BrowseRequest request)
    {
        foreach (var field in request.SortBy)
        {
            var result = CompareValue(left.Key(field, request.RandomSeed), right.Key(field, request.RandomSeed));
            if (result == 0 && field == "SortName") result = StringComparer.Ordinal.Compare(left.Title, right.Title);
            if (result != 0) return request.SortOrder == "Descending" ? -result : result;
        }
        // Stabilize equal keys so titles cannot repeat or disappear between pages.
        var providerOrder = StringComparer.Ordinal.Compare(left.TitleIdentity, right.TitleIdentity);
        return providerOrder == 0 ? StringComparer.Ordinal.Compare(left.Identity, right.Identity) : providerOrder;
    }

    private static int CompareValue(IComparable? left, IComparable? right) => left is null ? (right is null ? 0 : -1) :
        right is null ? 1 : left is string text ? StringComparer.Ordinal.Compare(text, (string)right) : left.CompareTo(right);

    private sealed record Candidate(BaseItem? Native, Entry? Entry, TmdbMetadata? Metadata, string SortName, UserItemData? UserData, DateTime? SeriesDatePlayed)
    {
        public string Title => Native?.Name ?? Entry!.Title;
        public IEnumerable<string> SearchValues => new[] { Title, Native?.OriginalTitle, Native?.SortName, Metadata?.OriginalTitle }.OfType<string>();
        public string Identity => Native is null ? "entry:" + Entry!.Id : "native:" + Native.Id;
        public string TitleIdentity => Entry is not null ? "tmdb:" + Entry.TmdbId :
            Native!.ProviderIds.TryGetValue("Tmdb", out var id) ? "tmdb:" + id : Identity;
        public IComparable? Key(string field, string? seed) => field switch
        {
            "SortName" => SortName,
            "DateCreated" => Native?.DateCreated ?? Entry!.AddedAt,
            "ProductionYear" => Native?.ProductionYear ?? (Native is null ? Entry!.Year : null),
            "PremiereDate" => Native is null ? Metadata?.PremiereDate ?? YearDate(Entry!.Year) : Native.PremiereDate ?? YearDate(Native.ProductionYear),
            "CommunityRating" => Native is null ? Metadata?.CommunityRating : (double?)Native.CommunityRating,
            "CriticRating" => (double?)Native?.CriticRating,
            "Runtime" => Native is null ? (long?)Metadata?.RuntimeMinutes * TimeSpan.TicksPerMinute : Native.RunTimeTicks,
            "DateLastContentAdded" => (Native as Folder)?.DateLastMediaAdded,
            "OfficialRating" => Native?.InheritedParentalRatingValue,
            "DatePlayed" => UserData?.LastPlayedDate,
            "SeriesDatePlayed" => SeriesDatePlayed,
            "PlayCount" => UserData?.PlayCount,
            "Random" => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + TitleIdentity))),
            _ => null
        };

        private static DateTime? YearDate(int? year) => year is >= 1 and <= 9999 ? new DateTime(year.Value, 1, 1) : null;
    }
}
