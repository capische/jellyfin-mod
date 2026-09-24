using System.Net;
using System.Text.Json;
using JellyfinMod.Data;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>What one sync did (P7.S9).</summary>
public sealed record ProwlarrSyncOutcome(string Code, int Seen, int Created, int Updated, int Disabled, int Removed, int Verified,
    IReadOnlyList<string> Failed);

/// <summary>A refused Prowlarr call, carrying a stable code.</summary>
public sealed class ProwlarrException(string code, string message) : Exception(message)
{
    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Imports Prowlarr's torrent indexers as ordinary JellyfinMod indexers and keeps them in step (PHASE7 §6).
/// </summary>
/// <remarks>
/// A pull, never Prowlarr's application push, so the key stays on this side. Fail-closed: an error changes nothing;
/// an answer with no torrent indexer disables the synced ones only after two such answers at least an hour apart; a
/// body missing a required field aborts; a sync never enables what an administrator disabled.
/// </remarks>
public sealed class ProwlarrSync(
    ModDbContext database,
    AcquisitionSecretStore secrets,
    IHttpClientFactory http,
    ReleaseSearchService search,
    ReleaseSearchCache cache,
    TimeProvider clock,
    ILogger<ProwlarrSync> logger)
{
    /// <summary>How long a removed indexer is kept disabled before it is deleted (PHASE7 default 13).</summary>
    public static readonly TimeSpan RemovalWindow = TimeSpan.FromDays(30);

    /// <summary>How far apart two empty answers must be before they count as two.</summary>
    public static readonly TimeSpan EmptySpacing = TimeSpan.FromHours(1);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Checks the source answers, accepts the key and has enabled torrent indexers. Changes nothing.</summary>
    public async Task<(string Code, string Message, string? Version)> TestAsync(ProwlarrSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var status = await GetAsync(source, "api/v1/system/status", cancellationToken).ConfigureAwait(false);
            var version = status.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
            using var _ = await GetAsync(source, "api/v1/health", cancellationToken).ConfigureAwait(false);
            var indexers = await ReadIndexersAsync(source, cancellationToken).ConfigureAwait(false);
            var torrents = indexers.Count(indexer => indexer.Protocol == "torrent" && indexer.Enable);
            return torrents == 0
                ? ("no_torrent_indexers", "Prowlarr answered, but has no enabled torrent indexer to import.", version)
                : ("ok", $"Prowlarr answered and has {torrents} enabled torrent indexer(s).", version);
        }
        catch (ProwlarrException failure)
        {
            return (failure.Code, failure.Message, null);
        }
    }

    /// <summary>Loads the source through this service's own context and syncs it.</summary>
    public async Task<ProwlarrSyncOutcome?> SyncByIdAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await database.ProwlarrSources.SingleOrDefaultAsync(value => value.Id == sourceId, cancellationToken).ConfigureAwait(false);
        return source is null ? null : await SyncAsync(source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one sync for <paramref name="source"/>, which must be tracked by this service's context.</summary>
    public async Task<ProwlarrSyncOutcome> SyncAsync(ProwlarrSource source, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        source.LastSyncAt = now;
        IReadOnlyList<RemoteIndexer> remote;
        try
        {
            remote = await ReadIndexersAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (ProwlarrException failure)
        {
            // Fail closed: nothing about the indexers changes on an error.
            source.LastSyncOutcome = failure.Code;
            source.LastError = failure.Code;
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("JellyfinMod Prowlarr sync of {Source} failed: {Code}", source.Name, failure.Code);
            return new ProwlarrSyncOutcome(failure.Code, 0, 0, 0, 0, 0, 0, []);
        }

        var torrents = remote.Where(indexer => indexer.Protocol == "torrent").ToList();
        if (torrents.Count == 0)
        {
            if (source.LastEmptySyncAt is not { } last || now - last >= EmptySpacing)
            {
                source.ConsecutiveEmptySyncs++;
                source.LastEmptySyncAt = now;
            }

            if (source.ConsecutiveEmptySyncs < 2)
            {
                source.LastSyncOutcome = "empty_waiting";
                source.LastError = null;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new ProwlarrSyncOutcome("empty_waiting", 0, 0, 0, 0, 0, 0, []);
            }
        }
        else
        {
            source.ConsecutiveEmptySyncs = 0;
            source.LastEmptySyncAt = null;
        }

        var rows = await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId == source.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var otherNames = (await database.AcquisitionIndexers.Where(indexer => indexer.ProwlarrSourceId != source.Id)
            .Select(indexer => indexer.Name).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseUri = new Uri(source.BaseUrl.TrimEnd('/') + "/");
        int created = 0, updated = 0, disabled = 0, removed = 0;
        var toVerify = new List<AcquisitionIndexer>();
        foreach (var indexer in torrents)
        {
            var row = rows.SingleOrDefault(candidate => candidate.ProwlarrIndexerId == indexer.Id);
            var name = otherNames.Contains(indexer.Name) ? $"{indexer.Name} ({source.Name})" : indexer.Name;
            var baseUrl = new Uri(baseUri, $"{indexer.Id}/api").ToString();
            var categories = string.Join(',', indexer.Categories);
            var hosts = string.Join(',', indexer.DownloadHosts);
            var wanted = indexer.Enable && !AdminDisabled(row);
            if (row is null)
            {
                row = new AcquisitionIndexer
                {
                    ManagedBy = IndexerOwners.Prowlarr, ProwlarrSourceId = source.Id, ProwlarrIndexerId = indexer.Id, Name = name, BaseUrl = baseUrl,
                    Categories = categories, Priority = indexer.Priority, DownloadHosts = hosts, Enabled = wanted
                };
                database.AcquisitionIndexers.Add(row);
                rows.Add(row);
                created++;
                if (wanted) toVerify.Add(row);
                continue;
            }

            var changed = row.Name != name || row.BaseUrl != baseUrl || row.Categories != categories || row.Priority != indexer.Priority ||
                row.DownloadHosts != hosts;
            if (changed)
            {
                (row.Name, row.BaseUrl, row.Categories, row.Priority, row.DownloadHosts) = (name, baseUrl, categories, indexer.Priority, hosts);
                row.Revision++;
                row.VerifiedRevision = null;
                row.CapabilitiesJson = null;
                updated++;
            }

            if (row.ProwlarrRemovedAt is not null)
            {
                // Back in Prowlarr: the removal no longer applies.
                row.ProwlarrRemovedAt = null;
                if (row.LastError == "prowlarr_removed") row.LastError = null;
            }

            if (row.Enabled && !wanted) disabled++;
            row.Enabled = wanted;
            if (wanted && (changed || row.VerifiedRevision != row.Revision)) toVerify.Add(row);
        }

        var seen = torrents.Select(indexer => indexer.Id).ToHashSet();
        foreach (var row in rows.Where(row => row.ProwlarrIndexerId is not { } id || !seen.Contains(id)).ToList())
        {
            if (row.ProwlarrRemovedAt is null)
            {
                row.ProwlarrRemovedAt = now;
                if (row.Enabled) disabled++;
                row.Enabled = false;
                row.LastError = "prowlarr_removed";
            }
            else if (now - row.ProwlarrRemovedAt.Value >= RemovalWindow)
            {
                database.AcquisitionIndexers.Remove(row);
                removed++;
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ApplyStatusAsync(source, rows, cancellationToken).ConfigureAwait(false);

        // Every new or changed synced indexer proves itself through Prowlarr with t=caps; a failure leaves it off.
        var verified = 0;
        var failed = new List<string>();
        foreach (var row in toVerify)
        {
            try
            {
                await search.VerifyAsync(row, cancellationToken).ConfigureAwait(false);
                verified++;
            }
            catch (TorznabException failure)
            {
                row.Enabled = false;
                row.LastError = failure.Code;
                failed.Add(row.Name);
            }
        }

        source.LastSyncOutcome = "ok";
        source.LastError = null;
        database.History.Add(new HistoryRecord
        {
            EntryId = Guid.Empty, EventType = "prowlarr_synced", Summary = $"Prowlarr sync: {source.Name}",
            Data = JsonSerializer.Serialize(new { sourceId = source.Id, seen = torrents.Count, created, updated, disabled, removed, verified, failed = failed.Count })
        });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        cache.Invalidate();
        logger.LogInformation("JellyfinMod Prowlarr sync of {Source}: {Seen} seen, {Created} created, {Updated} updated, {Disabled} disabled, {Removed} removed, {Verified} verified, {Failed} failed",
            source.Name, torrents.Count, created, updated, disabled, removed, verified, failed.Count);
        return new ProwlarrSyncOutcome("ok", torrents.Count, created, updated, disabled, removed, verified, failed);
    }

    /// <summary>Prowlarr's own back-off (<c>disabledTill</c>) opens the Phase 6 breaker until then.</summary>
    private async Task ApplyStatusAsync(ProwlarrSource source, List<AcquisitionIndexer> rows, CancellationToken cancellationToken)
    {
        JsonDocument status;
        try
        {
            status = await GetAsync(source, "api/v1/indexerstatus", cancellationToken).ConfigureAwait(false);
        }
        catch (ProwlarrException)
        {
            return;
        }

        using (status)
        {
            if (status.RootElement.ValueKind != JsonValueKind.Array) return;
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var entry in status.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("indexerId", out var idValue) || !idValue.TryGetInt32(out var id) ||
                    !entry.TryGetProperty("disabledTill", out var till) || till.ValueKind != JsonValueKind.String ||
                    !DateTime.TryParse(till.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until) ||
                    until <= now)
                    continue;
                if (rows.SingleOrDefault(row => row.ProwlarrIndexerId == id) is not { } row) continue;
                var state = await database.IndexerBudgets.SingleOrDefaultAsync(value => value.IndexerId == row.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (state is null)
                {
                    state = new IndexerBudgetState { IndexerId = row.Id, Day = now.Date };
                    database.IndexerBudgets.Add(state);
                }

                if (state.BreakerOpenUntil is null || state.BreakerOpenUntil < until) state.BreakerOpenUntil = until;
            }
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool AdminDisabled(AcquisitionIndexer? row)
    {
        if (row?.AdminOverridesJson is not { } json) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<RemoteIndexer>> ReadIndexersAsync(ProwlarrSource source, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(source, "api/v1/indexer", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ProwlarrException("prowlarr_schema", "Prowlarr's indexer list was not a list.");
        var host = new Uri(source.BaseUrl).IdnHost.ToLowerInvariant();
        var result = new List<RemoteIndexer>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var idValue) ||
                !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("enable", out var enable) || enable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ProwlarrException("prowlarr_schema", "Prowlarr's indexer list is missing a required field.");
            var priority = item.TryGetProperty("priority", out var priorityValue) && priorityValue.TryGetInt32(out var p) ? p : 25;
            var hosts = new List<string> { host };
            if (item.TryGetProperty("supportsRedirect", out var redirect) && redirect.ValueKind == JsonValueKind.True &&
                item.TryGetProperty("indexerUrls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                hosts.AddRange(urls.EnumerateArray().Select(url => Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null)
                    .OfType<string>());
            result.Add(new RemoteIndexer(idValue, name.GetString()!.Trim(), protocol.GetString()!, enable.GetBoolean(), priority,
                Categories(item), hosts.Distinct(StringComparer.Ordinal).ToArray()));
        }

        return result;
    }

    /// <summary>The movie and TV trees the indexer advertises; both trees when it advertises none.</summary>
    private static int[] Categories(JsonElement item)
    {
        var advertised = new List<int>();
        if (item.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categories.EnumerateArray())
            {
                if (category.TryGetProperty("id", out var id) && id.TryGetInt32(out var value)) advertised.Add(value);
                if (category.TryGetProperty("subCategories", out var subs) && subs.ValueKind == JsonValueKind.Array)
                    advertised.AddRange(subs.EnumerateArray().Select(sub => sub.TryGetProperty("id", out var subId) && subId.TryGetInt32(out var v) ? v : 0));
            }
        }

        var wanted = advertised.Where(id => id is >= 2000 and < 3000 or >= 5000 and < 6000).Distinct().Order().ToArray();
        return wanted.Length > 0 ? wanted : [2000, 5000];
    }

    private async Task<JsonDocument> GetAsync(ProwlarrSource source, string path, CancellationToken cancellationToken)
    {
        var key = await secrets.GetAsync(source.ApiKeySecretRef, cancellationToken).ConfigureAwait(false);
        if (key is null) throw new ProwlarrException("unauthorized", "No Prowlarr API key is saved.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(source.BaseUrl.TrimEnd('/') + "/"), path));
            request.Headers.Add("X-Api-Key", key);
            using var response = await http.CreateClient(NamedClient.Default).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProwlarrException("unauthorized", "Prowlarr refused the API key.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ProwlarrException("rate_limited", "Prowlarr asked to slow down.");
            if (!response.IsSuccessStatusCode)
                throw new ProwlarrException("unreachable", $"Prowlarr answered HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProwlarrException("timeout", "Prowlarr did not answer in time.");
        }
        catch (HttpRequestException)
        {
            throw new ProwlarrException("unreachable", "Prowlarr could not be reached from this server.");
        }
        catch (JsonException)
        {
            throw new ProwlarrException("prowlarr_schema", "Prowlarr answered with something that is not JSON.");
        }
    }

    private sealed record RemoteIndexer(int Id, string Name, string Protocol, bool Enable, int Priority, int[] Categories, string[] DownloadHosts);
}

/// <summary>Scheduled Prowlarr sync, every six hours by default (PHASE7 §6).</summary>
public sealed class ProwlarrSyncTask(IServiceScopeFactory scopeFactory) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Sync JellyfinMod indexers from Prowlarr";

    /// <inheritdoc />
    public string Key => "JellyfinModProwlarrSync";

    /// <inheritdoc />
    public string Description => "Imports Prowlarr's torrent indexers and keeps them in step. Changes nothing when Prowlarr fails.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<ProwlarrSync>();
        var ids = await scope.ServiceProvider.GetRequiredService<ModDbContext>().ProwlarrSources.AsNoTracking().Where(source => source.Enabled)
            .Select(source => source.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < ids.Count; index++)
        {
            await sync.SyncByIdAsync(ids[index], cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (index + 1) / ids.Count);
        }
    }
}
