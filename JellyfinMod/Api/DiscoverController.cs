using System.Collections.Concurrent;
using System.Net;
using Jellyfin.Data;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>TMDB discovery with server-side library, identity and content filtering.</summary>
[ApiController, Authorize, Route("JellyfinMod/Discover")]
public sealed class DiscoverController(ModDbContext database, DatabaseInitializer readiness, LibraryAccess access, TmdbClient tmdb) : ControllerBase
{
    /// <summary>
    /// The documented per-request TMDB budget (P1.P8): one search call plus at most this many detail calls,
    /// so 21 calls for a full TMDB page.
    /// </summary>
    public const int MaxDetailCalls = 20;

    /// <summary>How long one candidate's detail call may take before it is skipped.</summary>
    private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Returns eligible suggestions and the next remote page, without unfiltered total counts.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<DiscoveryResult>> Search([FromQuery] string q, [FromQuery] string type,
        [FromQuery] Guid? targetLibraryId = null, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        if (type is not ("movie" or "series") || string.IsNullOrWhiteSpace(q) || q.Length > 200 || page is < 1 or > 500) return BadRequest();
        if (targetLibraryId.HasValue && !access.CanUseLibrary(user, type, targetLibraryId)) return NotFound();
        if (access.GetLibraries(user, type).Count == 0) return new DiscoveryResult([], null);
        // An allowlisted user can never read a title that has no administrator-assigned tags, so every page
        // would be empty; answer before any TMDB call (P1.P8).
        if (user.GetPreference(Jellyfin.Database.Implementations.Enums.PreferenceKind.AllowedTags).Length > 0)
            return new DiscoveryResult([], null);
        var entries = await database.Entries.AsNoTracking().Where(e => e.MediaType == type && (!targetLibraryId.HasValue || e.TargetLibraryId == targetLibraryId)).ToListAsync(cancellationToken);
        var nativeItems = access.GetNativeItems(user, type, targetLibraryId);
        var nativeIds = nativeItems.Select(item => item.Id).ToHashSet();
        var held = entries.Where(e => access.CanRead(user, e, nativeIds)).Select(e => e.TmdbId).ToHashSet();
        var heldTvdb = new HashSet<int>();
        foreach (var native in nativeItems)
        {
            if (native.ProviderIds.TryGetValue("Tmdb", out var value) && int.TryParse(value, out var id)) held.Add(id);
            if (type == "series" && native.ProviderIds.TryGetValue("Tvdb", out var tvdbValue) && int.TryParse(tvdbValue, out var tvdbId)) heldTvdb.Add(tvdbId);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // A bounded amount of work per request, with explicit continuation even for excluded pages.
            var remote = await tmdb.SearchAsync(q, type, page, timeout.Token);
            var visible = new ConcurrentDictionary<int, TmdbMetadata>();
            var candidates = remote.Items.DistinctBy(item => item.TmdbId).Where(item => !held.Contains(item.TmdbId))
                .Take(MaxDetailCalls).ToArray();
            var skipped = 0;
            // Detail calls get their own budget: one slow or failing candidate is skipped and counted, and
            // never fails the page once the search call itself succeeded (P1.P8).
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (candidate, token) =>
            {
                using var detailTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                detailTimeout.CancelAfter(DetailTimeout);
                try
                {
                    var metadata = await tmdb.GetDetailsAsync(type, candidate.TmdbId, detailTimeout.Token);
                    if (!(metadata.TvdbId is { } tvdbId && heldTvdb.Contains(tvdbId)) && access.CanReadMetadata(user, metadata))
                        visible.TryAdd(metadata.TmdbId, metadata);
                }
                catch (TmdbException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    // Search indexes may retain a removed title; it must not discard other suggestions.
                }
                catch (Exception error) when (error is TmdbException or HttpRequestException ||
                    error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref skipped);
                }
            });

            return new DiscoveryResult(candidates.Where(item => visible.ContainsKey(item.TmdbId)).Select(item => visible[item.TmdbId]).ToArray(),
                remote.HasMore ? page + 1 : null, skipped);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: 504, title: "TMDB discovery timed out. Please try again.");
        }
        catch (TmdbException error)
        {
            return Problem(statusCode: (int)error.StatusCode, title: error.Message);
        }
    }
}
