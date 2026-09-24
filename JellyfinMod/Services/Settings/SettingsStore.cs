using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>The Transmission endpoint seed protection reads, wherever it is configured (P7.S7).</summary>
/// <param name="RpcUrl">The RPC endpoint.</param>
/// <param name="Username">The optional RPC username.</param>
/// <param name="Password">A password given as a value; empty when <paramref name="PasswordRef"/> applies.</param>
/// <param name="PasswordRef">The secret-store reference of the password.</param>
public sealed record SeedEndpoint(string RpcUrl, string Username, string Password, string? PasswordRef)
{
    /// <summary>
    /// Resolves the endpoint from the SQLite settings row: the selected acquisition client when the source is
    /// <c>acquisitionClient</c>, the row's own endpoint when it is <c>separate</c>; <c>null</c> when neither exists.
    /// </summary>
    public static async Task<SeedEndpoint?> ResolveAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var settings = await database.AcquisitionSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == AcquisitionSettings.SingletonId, cancellationToken).ConfigureAwait(false);
        if (settings is null) return null;
        if (settings.SeedProtectionSource == SeedProtectionSources.Separate)
            return string.IsNullOrWhiteSpace(settings.SeedProtectionRpcUrl) ? null
                : new SeedEndpoint(settings.SeedProtectionRpcUrl, settings.SeedProtectionUsername ?? string.Empty, string.Empty,
                    settings.SeedProtectionPasswordRef);
        if (settings.DownloadClientId is not { } clientId) return null;
        var client = await database.AcquisitionDownloadClients.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == clientId, cancellationToken).ConfigureAwait(false);
        return client is { Kind: TransmissionDriver.DriverKind }
            ? new SeedEndpoint(client.BaseUrl, client.Username, string.Empty, client.PasswordSecretRef)
            : null;
    }
}

/// <summary>
/// Moves the settings that lived in the XML plugin configuration before P7.S7 into the SQLite settings row, once
/// (PHASE7 default 11, open question 13): the TMDB Read Access Token reference and the seed-protection connection.
/// </summary>
/// <remarks>
/// Idempotent: it only moves values that are still in the XML, so it runs at every startup and picks up a value
/// an older Dashboard page wrote through the host's plugin-configuration API. Secret values never pass through
/// here; only references move, and a reference that is no longer needed is removed from the store.
/// </remarks>
public static class SettingsXmlImport
{
    /// <summary>Imports what is still in <paramref name="xml"/>; returns true when the XML must be saved.</summary>
    public static async Task<bool> RunAsync(ModDbContext database, PluginConfiguration xml, AcquisitionSecretStore secrets,
        ILogger logger, CancellationToken cancellationToken)
    {
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var xmlChanged = false;
        var obsolete = new List<string?>();

        if (xml.TmdbReadAccessTokenRef is { } tokenRef)
        {
            // The XML reference is newer: nothing but an older page writes it after the first import.
            if (settings.TmdbReadAccessTokenRef != tokenRef) obsolete.Add(settings.TmdbReadAccessTokenRef);
            settings.TmdbReadAccessTokenRef = tokenRef;
            settings.DiscoveryRevision++;
            settings.DiscoveryVerifiedRevision = null;
            xml.TmdbReadAccessTokenRef = null;
            xmlChanged = true;
            logger.LogInformation("JellyfinMod moved the TMDB token reference from the plugin XML into its settings database");
        }

        if (xml.TransmissionRpcUrl.Length > 0)
        {
            var client = settings.DownloadClientId is { } clientId
                ? await database.AcquisitionDownloadClients.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == clientId, cancellationToken).ConfigureAwait(false)
                : null;
            var sameAsClient = client is { Kind: TransmissionDriver.DriverKind } &&
                AcquisitionConfiguration.NormalizeEndpoint(client.BaseUrl) == AcquisitionConfiguration.NormalizeEndpoint(xml.TransmissionRpcUrl) &&
                client.Username == xml.TransmissionUsername;
            obsolete.Add(settings.SeedProtectionPasswordRef);
            if (sameAsClient)
            {
                // The same daemon with the same account: the acquisition client's own credentials already reach it.
                settings.SeedProtectionSource = SeedProtectionSources.AcquisitionClient;
                settings.SeedProtectionRpcUrl = null;
                settings.SeedProtectionUsername = null;
                settings.SeedProtectionPasswordRef = null;
                obsolete.Add(xml.TransmissionPasswordRef);
            }
            else
            {
                // A different endpoint keeps protecting exactly what it protected before the move.
                settings.SeedProtectionSource = SeedProtectionSources.Separate;
                settings.SeedProtectionRpcUrl = xml.TransmissionRpcUrl;
                settings.SeedProtectionUsername = xml.TransmissionUsername;
                settings.SeedProtectionPasswordRef = xml.TransmissionPasswordRef;
            }

            settings.SeedProtectionRevision++;
            xml.TransmissionRpcUrl = string.Empty;
            xml.TransmissionUsername = string.Empty;
            xml.TransmissionPasswordRef = null;
            xmlChanged = true;
            logger.LogInformation("JellyfinMod moved the seed-protection connection from the plugin XML into its settings database as {Source}",
                settings.SeedProtectionSource);
        }

        if (!xmlChanged) return false;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reference in obsolete.Where(reference => reference is not null).Distinct())
            await secrets.RemoveAsync(reference, CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// Marks that startup should run <see cref="SettingsXmlImport"/> against this XML configuration. Registered by the
/// plugin itself; an integration host registers it only when it exercises the import.
/// </summary>
/// <param name="Configuration">The XML configuration and how to save it.</param>
public sealed record SettingsXmlImportSource(RetentionConfigurationSource Configuration);
