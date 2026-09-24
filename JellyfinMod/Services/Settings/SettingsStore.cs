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
/// <para>Idempotent: it only moves values that are still in the XML, so it runs at every startup and picks up a value
/// an older Dashboard page wrote through the host's plugin-configuration API. Secret values never pass through
/// here; only references move, and a reference that is no longer needed is removed from the store.</para>
/// <para>Durable in this order (REVIEW-2026-09-24 S7-R1): the XML values are read into locals, the database row is
/// written and saved, and only then are the XML fields cleared and saved; secrets are removed last, and only
/// references nothing holds any more (S7-R2). A failed database save leaves the XML, the in-memory configuration
/// and the secret store exactly as they were; a failed XML save puts the in-memory fields back, and the next start
/// finds the same references in both places and only clears the XML.</para>
/// </remarks>
public static class SettingsXmlImport
{
    /// <summary>Imports what is still in the XML configuration; returns true when anything was moved.</summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="xml">The live XML configuration; it is changed only after the database save succeeded.</param>
    /// <param name="saveXml">Persists <paramref name="xml"/>.</param>
    /// <param name="secrets">The secret store.</param>
    /// <param name="logger">The logger; it never receives a URL, a user name or a secret.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task<bool> RunAsync(ModDbContext database, PluginConfiguration xml, Action saveXml, AcquisitionSecretStore secrets,
        ILogger logger, CancellationToken cancellationToken)
    {
        // Read everything first: nothing below touches the live configuration until the database holds the values.
        var tokenRef = xml.TmdbReadAccessTokenRef;
        var rpcUrl = xml.TransmissionRpcUrl;
        var rpcUsername = xml.TransmissionUsername;
        var rpcPasswordRef = xml.TransmissionPasswordRef;
        if (tokenRef is null && rpcUrl.Length == 0) return false;

        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        var candidates = new List<string?>();
        var added = new List<string>();

        if (tokenRef is not null && settings.TmdbReadAccessTokenRef != tokenRef)
        {
            // The XML reference is newer: nothing but an older page writes it after the first import.
            candidates.Add(settings.TmdbReadAccessTokenRef);
            settings.TmdbReadAccessTokenRef = tokenRef;
            settings.DiscoveryRevision++;
            settings.DiscoveryVerifiedRevision = null;
            logger.LogInformation("JellyfinMod moved the TMDB token reference from the plugin XML into its settings database");
        }

        if (rpcUrl.Length > 0)
        {
            var cleaned = CleanEndpoint(rpcUrl, out var userInfoUser, out var userInfoPassword, out var hadUserInfo);
            if (hadUserInfo)
            {
                // S7-R3: the pre-S7 page accepted credentials inside the address; they never reach the row or a DTO.
                if (rpcUsername.Length == 0 && userInfoUser.Length > 0) rpcUsername = userInfoUser;
                if (rpcPasswordRef is null && userInfoPassword.Length > 0)
                {
                    rpcPasswordRef = await secrets.AddAsync(userInfoPassword, cancellationToken).ConfigureAwait(false);
                    added.Add(rpcPasswordRef);
                }

                logger.LogWarning("JellyfinMod removed credentials from the imported seed-protection address; the user name and password are kept apart from it");
            }

            if (cleaned is null)
                logger.LogWarning("JellyfinMod could not use the seed-protection address in the plugin XML (not an http or https address); seed protection needs a new address and retention stays blocked until then");

            var client = settings.DownloadClientId is { } clientId
                ? await database.AcquisitionDownloadClients.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == clientId, cancellationToken).ConfigureAwait(false)
                : null;
            var sameAsClient = cleaned is not null && client is { Kind: TransmissionDriver.DriverKind } &&
                AcquisitionConfiguration.NormalizeEndpoint(client.BaseUrl) == AcquisitionConfiguration.NormalizeEndpoint(cleaned) &&
                client.Username == rpcUsername;
            var (source, url, username, passwordRef) = sameAsClient
                ? (SeedProtectionSources.AcquisitionClient, (string?)null, (string?)null, (string?)null)
                : (SeedProtectionSources.Separate, cleaned, (string?)rpcUsername, rpcPasswordRef);
            if (settings.SeedProtectionSource != source || settings.SeedProtectionRpcUrl != url ||
                settings.SeedProtectionUsername != username || settings.SeedProtectionPasswordRef != passwordRef)
            {
                candidates.Add(settings.SeedProtectionPasswordRef);
                settings.SeedProtectionSource = source;
                settings.SeedProtectionRpcUrl = url;
                settings.SeedProtectionUsername = username;
                settings.SeedProtectionPasswordRef = passwordRef;
                settings.SeedProtectionRevision++;
                logger.LogInformation("JellyfinMod moved the seed-protection connection from the plugin XML into its settings database as {Source}",
                    source);
            }

            // The acquisition client's own credentials reach the same daemon; the XML's separate password is spare.
            if (sameAsClient) candidates.Add(rpcPasswordRef);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Nothing else was changed; a password lifted out of the address has no holder, so it goes.
            foreach (var reference in added)
                await secrets.RemoveAsync(reference, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // The database holds every value now; the XML may be emptied. A field another writer changed meanwhile stays.
        var clearToken = xml.TmdbReadAccessTokenRef == tokenRef && tokenRef is not null;
        var clearSeed = xml.TransmissionRpcUrl == rpcUrl && rpcUrl.Length > 0;
        var keptUsername = xml.TransmissionUsername;
        var keptPasswordRef = xml.TransmissionPasswordRef;
        if (clearToken) xml.TmdbReadAccessTokenRef = null;
        if (clearSeed)
        {
            xml.TransmissionRpcUrl = string.Empty;
            xml.TransmissionUsername = string.Empty;
            xml.TransmissionPasswordRef = null;
        }

        try
        {
            saveXml();
        }
        catch
        {
            // Put the live object back as it is on disk: the next start sees the same references in both places.
            if (clearToken) xml.TmdbReadAccessTokenRef = tokenRef;
            if (clearSeed)
            {
                xml.TransmissionRpcUrl = rpcUrl;
                xml.TransmissionUsername = keptUsername;
                xml.TransmissionPasswordRef = keptPasswordRef;
            }

            throw;
        }

        // Last, and only for references nothing holds any more (S7-R2).
        var inUse = await ReferencesInUseAsync(database, xml, cancellationToken).ConfigureAwait(false);
        foreach (var reference in candidates.OfType<string>().Distinct().Where(reference => !inUse.Contains(reference)))
            await secrets.RemoveAsync(reference, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns the address without user information, query or fragment, and what the user information held;
    /// <c>null</c> when it is not an absolute http or https address.
    /// </summary>
    private static string? CleanEndpoint(string value, out string user, out string password, out bool hadUserInfo)
    {
        user = string.Empty;
        password = string.Empty;
        hadUserInfo = false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        if (uri.UserInfo.Length > 0)
        {
            hadUserInfo = true;
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            user = Uri.UnescapeDataString(separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]);
            password = separator < 0 ? string.Empty : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
        }

        if (!hadUserInfo && uri.Query.Length == 0 && uri.Fragment.Length == 0) return value.Trim();
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }
            .Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    private static async Task<HashSet<string>> ReferencesInUseAsync(ModDbContext database, PluginConfiguration xml,
        CancellationToken cancellationToken)
    {
        var settings = await database.AcquisitionSettings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == AcquisitionSettings.SingletonId, cancellationToken).ConfigureAwait(false);
        var references = new List<string?>
        {
            settings?.TmdbReadAccessTokenRef, settings?.SeedProtectionPasswordRef,
            xml.TmdbReadAccessTokenRef, xml.TmdbApiKeyRef, xml.TransmissionPasswordRef
        };
        references.AddRange(await database.AcquisitionDownloadClients.AsNoTracking().Select(row => row.PasswordSecretRef)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        references.AddRange(await database.AcquisitionIndexers.AsNoTracking().Select(row => row.ApiKeySecretRef)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        references.AddRange(await database.ProwlarrSources.AsNoTracking().Select(row => row.ApiKeySecretRef)
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        return references.OfType<string>().ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Marks that startup should run <see cref="SettingsXmlImport"/> against this XML configuration. Registered by the
/// plugin itself; an integration host registers it only when it exercises the import.
/// </summary>
/// <param name="Configuration">The XML configuration and how to save it.</param>
public sealed record SettingsXmlImportSource(RetentionConfigurationSource Configuration);
