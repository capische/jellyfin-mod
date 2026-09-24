using System.Text.Json;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Acquisition;

/// <summary>How long a persisted grab is held before submission (user decision 2).</summary>
public sealed record GrabHoldOptions(TimeSpan Hold)
{
    /// <summary>The documented production hold.</summary>
    public static GrabHoldOptions Default { get; } = new(TimeSpan.FromSeconds(5));
}

/// <summary>Resolved acquisition settings with readiness blockers.</summary>
public sealed record AcquisitionState(
    AcquisitionSettings Settings,
    AcquisitionDownloadClient? Client,
    AcquisitionQualityProfile? DefaultProfile,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Gets a value indicating whether grabs can be enabled.</summary>
    public bool Ready => Blockers.Count == 0;
}

/// <summary>Reads acquisition configuration and resolves secrets for one operation (P4.A2).</summary>
/// <remarks>
/// The database context is transient, so callers pass their own: the settings row returned here must be tracked by
/// the context that later saves it.
/// </remarks>
public sealed class AcquisitionConfiguration(AcquisitionSecretStore secrets, DownloadClientDrivers drivers, ModDbContext? database = null)
{
    /// <summary>Loads the singleton settings row, tracked by <paramref name="database"/>, creating it on first use.</summary>
    public static async Task<AcquisitionSettings> GetSettingsAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var settings = await database.AcquisitionSettings.SingleOrDefaultAsync(
            value => value.Id == AcquisitionSettings.SingletonId, cancellationToken).ConfigureAwait(false);
        if (settings is not null) return settings;
        settings = new AcquisitionSettings();
        database.AcquisitionSettings.Add(settings);
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent first read created it.
            database.ChangeTracker.Clear();
            settings = await database.AcquisitionSettings.SingleAsync(value => value.Id == AcquisitionSettings.SingletonId,
                cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    /// <summary>Resolves settings and lists everything that prevents enabling grabs.</summary>
    public async Task<AcquisitionState> GetStateAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        if (!await database.AcquisitionIndexers.AnyAsync(indexer => indexer.Enabled && indexer.VerifiedRevision == indexer.Revision,
                cancellationToken).ConfigureAwait(false))
            blockers.Add("no_verified_indexer");
        var client = settings.DownloadClientId is { } clientId
            ? await database.AcquisitionDownloadClients.AsNoTracking().SingleOrDefaultAsync(value => value.Id == clientId,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (client is null) blockers.Add("no_download_client");
        else
        {
            if (!client.Enabled) blockers.Add("download_client_disabled");
            if (client.VerifiedRevision != client.Revision) blockers.Add("download_client_unverified");
            if (drivers.Get(client.Kind) is null) blockers.Add("download_client_driver_missing");
        }

        var profile = settings.DefaultQualityProfileId is { } profileId
            ? await database.AcquisitionQualityProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.Id == profileId,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (profile is null) blockers.Add("no_default_profile");
        return new AcquisitionState(settings, client, profile, blockers);
    }

    /// <summary>Resolves a client's connection, including its password, for one call.</summary>
    public async Task<DownloadClientConnection> ConnectAsync(AcquisitionDownloadClient client, CancellationToken cancellationToken)
    {
        var password = await secrets.GetAsync(client.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        if (client.PasswordSecretRef is not null && password is null)
            throw new DownloadClientRejectedException("secret_unavailable", "The saved client password is no longer available; enter it again.");
        return new DownloadClientConnection(new Uri(client.BaseUrl), client.Username, password, client.Label, client.DownloadDirectory);
    }

    /// <summary>Resolves an indexer's endpoint, including its API key, for one call.</summary>
    /// <remarks>
    /// A Prowlarr-synced indexer holds no key: it uses its source's, so rotating that one key rotates every feed and
    /// the key is only ever sent to the source's own host (P7.S9).
    /// </remarks>
    public async Task<TorznabEndpoint> EndpointAsync(AcquisitionIndexer indexer, CancellationToken cancellationToken)
    {
        var reference = indexer.ApiKeySecretRef;
        if (indexer.ProwlarrSourceId is { } sourceId && database is not null)
            reference = await database.ProwlarrSources.AsNoTracking().Where(source => source.Id == sourceId)
                .Select(source => source.ApiKeySecretRef).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var key = await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        if (reference is not null && key is null)
            throw new TorznabException("secret_unavailable", "The saved API key is no longer available; enter it again.");
        return new TorznabEndpoint(new Uri(indexer.BaseUrl), key);
    }

    /// <summary>Returns the hosts an indexer may download torrents from: its own host plus configured extras.</summary>
    public static IReadOnlySet<string> AllowedHosts(AcquisitionIndexer indexer) =>
        indexer.DownloadHosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => host.ToLowerInvariant()).Append(new Uri(indexer.BaseUrl).IdnHost.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Reads a profile's ordered qualities.</summary>
    public static IReadOnlyList<string> Qualities(AcquisitionQualityProfile profile) =>
        JsonSerializer.Deserialize<string[]>(profile.QualitiesJson) ?? [];

    /// <summary>Reads a verified capability snapshot.</summary>
    public static TorznabCapabilities? Capabilities(AcquisitionIndexer indexer) =>
        indexer.CapabilitiesJson is { } json ? JsonSerializer.Deserialize<TorznabCapabilities>(json) : null;

    /// <summary>Normalizes an RPC endpoint for comparison with the seed-protection endpoint.</summary>
    public static string NormalizeEndpoint(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant()
            : string.Empty;
}
