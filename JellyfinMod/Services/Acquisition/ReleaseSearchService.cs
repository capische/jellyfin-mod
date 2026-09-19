using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>Bounds for one indexer during a search.</summary>
public sealed record TorznabOptions(TimeSpan Timeout, int MaxPages, int MaxParallel)
{
    /// <summary>The production bounds.</summary>
    public static TorznabOptions Default { get; } = new(TimeSpan.FromSeconds(20), 3, 4);
}

/// <summary>
/// Fans a target out to enabled indexers using only advertised capabilities, evaluates every row, and stores an
/// immutable snapshot (P4.A3/A4). A search never submits anything.
/// </summary>
public sealed class ReleaseSearchService(
    ModDbContext database,
    TorznabClient torznab,
    AcquisitionConfiguration configuration,
    ReleaseSearchCache cache,
    TorznabOptions options,
    ILogger<ReleaseSearchService> logger)
{
    private const int MaxLimit = 100;

    /// <summary>Verifies an indexer's capabilities and records them against its current revision.</summary>
    public async Task<TorznabCapabilities> VerifyAsync(AcquisitionIndexer indexer, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            var endpoint = await configuration.EndpointAsync(indexer, timeout.Token).ConfigureAwait(false);
            var capabilities = await torznab.GetCapabilitiesAsync(endpoint, timeout.Token).ConfigureAwait(false);
            indexer.CapabilitiesJson = JsonSerializer.Serialize(capabilities);
            indexer.CapabilitiesFetchedAt = DateTime.UtcNow;
            indexer.VerifiedRevision = indexer.Revision;
            indexer.LastError = null;
            return capabilities;
        }
        catch (TorznabException error)
        {
            indexer.VerifiedRevision = null;
            indexer.LastError = error.Code;
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            indexer.VerifiedRevision = null;
            indexer.LastError = "timeout";
            throw new TorznabException("timeout", "The indexer did not answer in time.");
        }
    }

    /// <summary>Searches every enabled indexer and stores the evaluated snapshot.</summary>
    public async Task<ReleaseSearchSnapshot> SearchAsync(Guid userId, ReleaseTarget target, EvaluationProfile profile,
        bool profileInherited, int settingsRevision, CancellationToken cancellationToken)
    {
        var indexers = await database.AcquisitionIndexers.Where(indexer => indexer.Enabled)
            .OrderBy(indexer => indexer.Priority).ThenBy(indexer => indexer.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Capabilities first: anything unverified at its current revision is checked before it is searched.
        var capabilities = new Dictionary<Guid, (TorznabCapabilities? Value, string? Error)>();
        foreach (var indexer in indexers)
        {
            if (indexer.VerifiedRevision == indexer.Revision && AcquisitionConfiguration.Capabilities(indexer) is { } known)
            {
                capabilities[indexer.Id] = (known, null);
                continue;
            }

            try
            {
                capabilities[indexer.Id] = (await VerifyAsync(indexer, cancellationToken).ConfigureAwait(false), null);
            }
            catch (TorznabException error)
            {
                capabilities[indexer.Id] = (null, error.Code);
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = new Dictionary<Guid, TorznabEndpoint?>();
        foreach (var indexer in indexers)
        {
            try
            {
                endpoints[indexer.Id] = await configuration.EndpointAsync(indexer, cancellationToken).ConfigureAwait(false);
            }
            catch (TorznabException)
            {
                endpoints[indexer.Id] = null;
            }
        }

        using var gate = new SemaphoreSlim(options.MaxParallel);
        var results = await Task.WhenAll(indexers.Select(async indexer =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SearchIndexerAsync(indexer, capabilities[indexer.Id], endpoints[indexer.Id], target, profile,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        // A release an administrator blocklisted from the queue is rejected with a visible reason (P5.I7).
        var blocklist = await database.ReleaseBlocklist.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var blockedHashes = blocklist.Where(entry => entry.InfoHash is not null).Select(entry => entry.InfoHash!).ToHashSet(StringComparer.Ordinal);
        var blockedGuids = blocklist.Where(entry => entry.IndexerId is not null && entry.SourceGuid is not null)
            .Select(entry => (entry.IndexerId!.Value, entry.SourceGuid!)).ToHashSet();
        var candidates = results.SelectMany(result => result.Candidates)
            .Select(candidate => candidate.InfoHash is { } hash && blockedHashes.Contains(hash) ||
                blockedGuids.Contains((candidate.IndexerId, candidate.SourceGuid))
                    ? candidate with
                    {
                        Evaluation = candidate.Evaluation with
                        {
                            Eligible = false,
                            Rejections = candidate.Evaluation.Rejections
                                .Append(new ReleaseRejection("blocklisted", "An administrator removed this release from the queue and blocked it."))
                                .ToArray()
                        }
                    }
                    : candidate)
            .OrderByDescending(candidate => candidate.Evaluation.Eligible)
            .ThenByDescending(candidate => candidate.Evaluation.Score)
            .ThenBy(candidate => candidate.Seeders is null)
            .ThenByDescending(candidate => candidate.Seeders ?? 0)
            .ThenBy(candidate => candidate.IndexerPriority)
            .ThenByDescending(candidate => candidate.PublishedAt ?? DateTime.MinValue)
            .ThenBy(candidate => candidate.ReleaseId, StringComparer.Ordinal)
            .ToArray();
        var now = cache.UtcNow;
        var snapshot = new ReleaseSearchSnapshot(Guid.NewGuid(), userId, target, profile, profileInherited, settingsRevision,
            now, now + ReleaseSearchCache.Lifetime, candidates, results.Select(result => result.Outcome).ToArray());
        cache.Add(snapshot);
        return snapshot;
    }

    private async Task<(IReadOnlyList<ReleaseCandidate> Candidates, IndexerOutcome Outcome)> SearchIndexerAsync(
        AcquisitionIndexer indexer, (TorznabCapabilities? Value, string? Error) capabilities, TorznabEndpoint? endpoint,
        ReleaseTarget target, EvaluationProfile profile, CancellationToken cancellationToken)
    {
        IndexerOutcome Outcome(string status, string? message, int count = 0, bool truncated = false, int? retry = null) =>
            new(indexer.Id, indexer.Name, status, message, count, truncated, retry);
        if (endpoint is null)
            return ([], Outcome("secret_unavailable", "The saved API key is no longer available; enter it again."));
        if (capabilities.Value is not { } caps)
            return ([], Outcome(capabilities.Error ?? "capabilities_unavailable", "The indexer's capabilities could not be verified."));
        if (cache.RemainingBackOff(indexer.Id) is { } wait)
            return ([], Outcome("rate_limited", "The indexer asked to wait before searching again.", retry: (int)Math.Ceiling(wait.TotalSeconds)));

        var (parameters, identity, method) = BuildQuery(caps, target);
        if (parameters is null)
            return ([], Outcome("unsupported_search", "The indexer does not advertise a search this title can use."));
        var configured = indexer.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var categories = caps.Categories.Count == 0 ? configured : configured.Where(caps.Categories.Contains).ToArray();
        if (configured.Length > 0 && categories.Length == 0)
            return ([], Outcome("unsupported_search", "None of the configured categories is advertised by the indexer."));
        if (categories.Length > 0) parameters.Add(("cat", string.Join(',', categories)));
        var limit = Math.Min(caps.LimitMax ?? MaxLimit, MaxLimit);
        parameters.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));

        var allowedHosts = AcquisitionConfiguration.AllowedHosts(indexer);
        var items = new List<TorznabItem>();
        var truncated = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            for (var page = 0; ; page++)
            {
                var offset = items.Count;
                var pageParameters = parameters.Append(("offset", offset.ToString(CultureInfo.InvariantCulture))).ToArray();
                var result = await torznab.SearchAsync(endpoint, pageParameters, timeout.Token).ConfigureAwait(false);
                items.AddRange(result.Items);
                // The feed's own offset/total decides; without a total, a full page means there may be more.
                var more = result.Items.Count > 0 && (result.Total is { } total
                    ? offset + result.Items.Count < total
                    : result.Items.Count >= limit);
                if (!more) break;
                if (page + 1 >= options.MaxPages)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (TorznabException error)
        {
            if (error.Code == "rate_limited") cache.BackOff(indexer.Id, error.RetryAfter ?? TimeSpan.FromMinutes(1));
            logger.LogInformation("Torznab search on {Indexer} failed: {Code}", indexer.Name, error.Code);
            // Rows from pages already read are kept, but the source is reported as failed, never as complete.
            return (Evaluate(indexer, items, target, profile, identity, method, allowedHosts),
                Outcome(error.Code, error.Message, items.Count, items.Count > 0,
                    error.RetryAfter is { } retryAfter ? (int)Math.Ceiling(retryAfter.TotalSeconds) : null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Evaluate(indexer, items, target, profile, identity, method, allowedHosts),
                Outcome("timeout", "The indexer did not answer in time.", items.Count, items.Count > 0));
        }

        var candidates = Evaluate(indexer, items, target, profile, identity, method, allowedHosts);
        return (candidates, Outcome(candidates.Count == 0 ? "no_results" : "ok", null, candidates.Count, truncated));
    }

    private static (List<(string, string)>? Parameters, SearchIdentity Identity, string Method) BuildQuery(
        TorznabCapabilities caps, ReleaseTarget target)
    {
        if (target.MediaType == "movie")
        {
            if (caps.MovieSearch.Contains("imdbid") && target.ImdbId is { Length: > 2 } imdb)
                return ([("t", "movie"), ("imdbid", imdb.TrimStart('t', 'T'))], SearchIdentity.ProviderId, "imdbid");
            if (caps.MovieSearch.Contains("tmdbid"))
                return ([("t", "movie"), ("tmdbid", target.TmdbId.ToString(CultureInfo.InvariantCulture))], SearchIdentity.ProviderId, "tmdbid");
            var text = target.Year is { } year ? $"{target.Title} {year}" : target.Title;
            if (caps.MovieSearch.Contains("q")) return ([("t", "movie"), ("q", text)], SearchIdentity.QueryOnly, "q");
            if (caps.Search.Contains("q")) return ([("t", "search"), ("q", text)], SearchIdentity.QueryOnly, "q");
            return (null, SearchIdentity.QueryOnly, "none");
        }

        var season = target.SeasonNumber!.Value.ToString(CultureInfo.InvariantCulture);
        var episode = target.EpisodeNumber!.Value.ToString(CultureInfo.InvariantCulture);
        if (caps.TvSearch.Contains("tvdbid") && caps.TvSearch.Contains("season") && caps.TvSearch.Contains("ep") &&
            target.TvdbId is { } tvdb)
            return ([("t", "tvsearch"), ("tvdbid", tvdb.ToString(CultureInfo.InvariantCulture)), ("season", season), ("ep", episode)],
                SearchIdentity.ProviderId, "tvdbid");
        var query = $"{target.Title} S{target.SeasonNumber:00}E{target.EpisodeNumber:00}";
        if (caps.TvSearch.Contains("q")) return ([("t", "tvsearch"), ("q", query)], SearchIdentity.QueryOnly, "q");
        if (caps.Search.Contains("q")) return ([("t", "search"), ("q", query)], SearchIdentity.QueryOnly, "q");
        return (null, SearchIdentity.QueryOnly, "none");
    }

    private static List<ReleaseCandidate> Evaluate(AcquisitionIndexer indexer, IEnumerable<TorznabItem> items, ReleaseTarget target,
        EvaluationProfile profile, SearchIdentity identity, string method, IReadOnlySet<string> allowedHosts)
    {
        var candidates = new List<ReleaseCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!seen.Add(item.Guid)) continue;
            string? unsupported = null;
            var hash = TorrentMetadata.NormalizeHash(item.InfoHash);
            if (item.InfoHash is not null && hash is null) unsupported = "The indexer reports an infohash format that is not supported.";
            if (item.MagnetUrl is not null)
            {
                try
                {
                    var magnetHash = TorrentMetadata.FromMagnet(item.MagnetUrl).InfoHash;
                    if (hash is not null && hash != magnetHash) unsupported = "The indexer reports two different infohashes.";
                    hash ??= magnetHash;
                }
                catch (TorznabException error) when (item.DownloadUrl is null)
                {
                    unsupported = error.Message;
                }
                catch (TorznabException)
                {
                    // A torrent link remains; its metadata decides the identity at grab time.
                }
            }

            var hostAllowed = item.DownloadUrl is null || Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var link) &&
                link.Scheme is "http" or "https" && allowedHosts.Contains(link.IdnHost.ToLowerInvariant());
            var parsed = ReleaseParser.Parse(item.Title);
            var freeleech = item.DownloadVolumeFactor is { } factor ? factor == 0 : (bool?)null;
            var facts = new ReleaseFacts(parsed, identity, item.Size, item.Seeders, freeleech, item.ImdbId, item.TmdbId,
                item.TvdbId, item.Season, item.Episode, item.DownloadUrl is not null || item.MagnetUrl is not null, hostAllowed,
                unsupported);
            var evaluation = ReleaseEvaluator.Evaluate(target, profile, facts);
            var ratio = Max(indexer.MinimumSeedRatio, item.MinimumRatio);
            int? itemMinutes = item.MinimumSeedSeconds is { } seconds ? (int)Math.Min(int.MaxValue, (seconds + 59) / 60) : null;
            var minutes = indexer.MinimumSeedMinutes is null && itemMinutes is null
                ? (int?)null
                : Math.Max(indexer.MinimumSeedMinutes ?? 0, itemMinutes ?? 0);
            candidates.Add(new ReleaseCandidate(ReleaseId(indexer.Id, item.Guid), indexer.Id, indexer.Name, indexer.Priority,
                indexer.Revision, item.Guid, item.Title, parsed, item.Size, item.Seeders, item.Peers, item.PublishedAt, freeleech,
                hash, evaluation, ratio, minutes, method, allowedHosts, item.DownloadUrl, item.MagnetUrl));
        }

        return candidates;
    }

    private static double? Max(double? left, double? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);

    /// <summary>An opaque, stable identity for a row: indexer-namespaced, never the GUID itself.</summary>
    private static string ReleaseId(Guid indexerId, string guid) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(indexerId.ToString("N") + "\n" + guid)).AsSpan(0, 16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
