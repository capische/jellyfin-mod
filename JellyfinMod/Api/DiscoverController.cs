using System.Collections.Concurrent;
using System.Net;
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
        var entries = await database.Entries.AsNoTracking().Where(e => e.MediaType == type && (!targetLibraryId.HasValue || e.TargetLibraryId == targetLibraryId)).ToListAsync(cancellationToken);
        var held = entries.Where(e => access.CanRead(user, e)).Select(e => e.TmdbId).ToHashSet();
        var heldTvdb = new HashSet<int>();
        foreach (var native in access.GetNativeItems(user, type, targetLibraryId))
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
            var candidates = remote.Items.DistinctBy(item => item.TmdbId).Where(item => !held.Contains(item.TmdbId)).ToArray();
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (candidate, token) =>
            {
                try
                {
                    var metadata = await tmdb.GetDetailsAsync(type, candidate.TmdbId, token);
                    if (!(metadata.TvdbId is { } tvdbId && heldTvdb.Contains(tvdbId)) && access.CanReadMetadata(user, metadata))
                        visible.TryAdd(metadata.TmdbId, metadata);
                }
                catch (TmdbException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    // Search indexes may retain a removed title; it must not discard other suggestions.
                }
            });

            return new DiscoveryResult(candidates.Where(item => visible.ContainsKey(item.TmdbId)).Select(item => visible[item.TmdbId]).ToArray(), remote.HasMore ? page + 1 : null);
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
