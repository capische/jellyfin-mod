using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Administrator-only acquisition configuration (P4.A2). Secrets are write-only: reads report whether one is
/// configured, never its value, and every change invalidates cached searches.
/// </summary>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod/Settings")]
public sealed partial class AcquisitionSettingsController(
    ModDbContext database,
    DatabaseInitializer readiness,
    AcquisitionConfiguration configuration,
    AcquisitionSecretStore secrets,
    DownloadClientDrivers drivers,
    DownloadDestinationValidator destinations,
    ReleaseSearchService search,
    ReleaseSearchCache cache,
    GrabHoldOptions hold,
    RetentionConfigurationSource pluginConfiguration) : ControllerBase
{
    /// <summary>Lists indexers.</summary>
    [HttpGet("Indexers")]
    public async Task<ActionResult<IReadOnlyList<IndexerSettingsDto>>> Indexers(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var indexers = await database.AcquisitionIndexers.AsNoTracking().OrderBy(value => value.Priority).ThenBy(value => value.Name)
            .ToListAsync(cancellationToken);
        return indexers.Select(ToDto).ToArray();
    }

    /// <summary>Creates an indexer.</summary>
    [HttpPost("Indexers")]
    public async Task<ActionResult<IndexerSettingsDto>> CreateIndexer(IndexerSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateIndexer(request, creating: true) is { } error) return Invalid(error);
        var indexer = new AcquisitionIndexer();
        Apply(indexer, request);
        indexer.ApiKeySecretRef = request.ApiKey.Action == "replace"
            ? await secrets.AddAsync(request.ApiKey.Value!, cancellationToken) : null;
        database.AcquisitionIndexers.Add(indexer);
        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            await secrets.RemoveAsync(indexer.ApiKeySecretRef, CancellationToken.None);
            return conflict;
        }

        return StatusCode(201, ToDto(indexer));
    }

    /// <summary>Replaces an indexer; its capabilities must be verified again.</summary>
    [HttpPatch("Indexers/{id:guid}")]
    public async Task<ActionResult<IndexerSettingsDto>> PatchIndexer(Guid id, IndexerSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateIndexer(request, creating: false) is { } error) return Invalid(error);
        var indexer = await database.AcquisitionIndexers.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (indexer is null) return NotFound();
        if (request.Revision != indexer.Revision) return RevisionConflict();
        var previous = indexer.ApiKeySecretRef;
        Apply(indexer, request);
        indexer.ApiKeySecretRef = await ApplySecretAsync(previous, request.ApiKey, cancellationToken);
        indexer.Revision++;
        indexer.VerifiedRevision = null;
        indexer.CapabilitiesJson = null;
        indexer.CapabilitiesFetchedAt = null;
        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        if (previous != indexer.ApiKeySecretRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        return ToDto(indexer);
    }

    /// <summary>Deletes an indexer. Grabs keep their recorded source name.</summary>
    [HttpDelete("Indexers/{id:guid}")]
    public async Task<IActionResult> DeleteIndexer(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var indexer = await database.AcquisitionIndexers.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (indexer is null) return NotFound();
        database.AcquisitionIndexers.Remove(indexer);
        await database.SaveChangesAsync(cancellationToken);
        await secrets.RemoveAsync(indexer.ApiKeySecretRef, CancellationToken.None);
        cache.Invalidate();
        return NoContent();
    }

    /// <summary>Fetches and records the indexer's capabilities without searching.</summary>
    [HttpPost("Indexers/{id:guid}/Test")]
    public async Task<ActionResult<ConnectionTestDto>> TestIndexer(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var indexer = await database.AcquisitionIndexers.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (indexer is null) return NotFound();
        try
        {
            var capabilities = await search.VerifyAsync(indexer, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            cache.Invalidate();
            return new ConnectionTestDto(true, "ok",
                $"Capabilities verified: {capabilities.MovieSearch.Count} movie and {capabilities.TvSearch.Count} TV search parameters.",
                null, null);
        }
        catch (TorznabException failure)
        {
            await database.SaveChangesAsync(cancellationToken);
            return new ConnectionTestDto(false, failure.Code, failure.Message, null, null);
        }
    }

    /// <summary>Lists download clients.</summary>
    [HttpGet("DownloadClients")]
    public async Task<ActionResult<IReadOnlyList<DownloadClientSettingsDto>>> DownloadClients(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var clients = await database.AcquisitionDownloadClients.AsNoTracking().OrderBy(value => value.Name).ToListAsync(cancellationToken);
        return clients.Select(ToDto).ToArray();
    }

    /// <summary>Creates a download client after checking its destination filesystem.</summary>
    [HttpPost("DownloadClients")]
    public async Task<ActionResult<DownloadClientSettingsDto>> CreateDownloadClient(DownloadClientSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateClient(request, creating: true) is { } error) return Invalid(error);
        var destination = destinations.Check(request.LocalDirectory.Trim());
        if (!destination.Ok) return Invalid(destination.Code, destination.Message);
        var client = new AcquisitionDownloadClient();
        Apply(client, request, destination);
        client.PasswordSecretRef = request.Password.Action == "replace"
            ? await secrets.AddAsync(request.Password.Value!, cancellationToken) : null;
        database.AcquisitionDownloadClients.Add(client);
        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            await secrets.RemoveAsync(client.PasswordSecretRef, CancellationToken.None);
            return conflict;
        }

        return StatusCode(201, ToDto(client));
    }

    /// <summary>Replaces a download client; it must be tested again before grabs use it.</summary>
    [HttpPatch("DownloadClients/{id:guid}")]
    public async Task<ActionResult<DownloadClientSettingsDto>> PatchDownloadClient(Guid id, DownloadClientSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateClient(request, creating: false) is { } error) return Invalid(error);
        var client = await database.AcquisitionDownloadClients.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (client is null) return NotFound();
        if (request.Revision != client.Revision) return RevisionConflict();
        var destination = destinations.Check(request.LocalDirectory.Trim());
        if (!destination.Ok) return Invalid(destination.Code, destination.Message);
        var previous = client.PasswordSecretRef;
        Apply(client, request, destination);
        client.PasswordSecretRef = await ApplySecretAsync(previous, request.Password, cancellationToken);
        client.Revision++;
        client.VerifiedRevision = null;
        client.VerifiedAt = null;
        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        if (previous != client.PasswordSecretRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        return ToDto(client);
    }

    /// <summary>Deletes a download client that is neither selected nor owning an active grab.</summary>
    [HttpDelete("DownloadClients/{id:guid}")]
    public async Task<IActionResult> DeleteDownloadClient(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var client = await database.AcquisitionDownloadClients.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (client is null) return NotFound();
        if ((await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken)).DownloadClientId == id)
            return Conflict(ProblemBody("download_client_selected", "Select another download client first."));
        if (await database.GrabOperations.AnyAsync(operation => operation.DownloadClientId == id && operation.ActiveHash != null, cancellationToken))
            return Conflict(ProblemBody("download_client_in_use", "Grabs handed to this client are still active; resolve them first."));
        database.AcquisitionDownloadClients.Remove(client);
        await database.SaveChangesAsync(cancellationToken);
        await secrets.RemoveAsync(client.PasswordSecretRef, CancellationToken.None);
        cache.Invalidate();
        return NoContent();
    }

    /// <summary>Checks the destination filesystem and the client's version without adding a torrent.</summary>
    [HttpPost("DownloadClients/{id:guid}/Test")]
    public async Task<ActionResult<ConnectionTestDto>> TestDownloadClient(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var client = await database.AcquisitionDownloadClients.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (client is null) return NotFound();
        client.VerifiedRevision = null;
        var destination = destinations.Check(client.LocalDirectory);
        client.VerifiedLibraryIds = string.Join(',', destination.SameFilesystemLibraryIds);
        if (!destination.Ok)
        {
            client.LastError = destination.Code;
            await database.SaveChangesAsync(cancellationToken);
            return new ConnectionTestDto(false, destination.Code, destination.Message, null, null);
        }

        if (drivers.Get(client.Kind) is not { } driver)
        {
            client.LastError = "download_client_driver_missing";
            await database.SaveChangesAsync(cancellationToken);
            return new ConnectionTestDto(false, "download_client_driver_missing", "This client kind is not supported.", null, null);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var probe = await driver.ProbeAsync(await configuration.ConnectAsync(client, timeout.Token), timeout.Token);
            client.ClientVersion = probe.Version;
            client.ApiVersion = probe.ApiVersion;
            client.VerifiedAt = DateTime.UtcNow;
            client.VerifiedRevision = client.Revision;
            client.LastError = null;
            await database.SaveChangesAsync(cancellationToken);
            cache.Invalidate();
            return new ConnectionTestDto(true, "ok", "Connection and download folder verified.", probe.Version, probe.ApiVersion);
        }
        catch (Exception failure) when (failure is DownloadClientRejectedException or DownloadClientUnavailableException or
            OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var code = failure switch
            {
                DownloadClientRejectedException rejected => rejected.Code,
                DownloadClientUnavailableException unavailable => unavailable.Code,
                _ => "timeout"
            };
            client.LastError = code;
            await database.SaveChangesAsync(cancellationToken);
            return new ConnectionTestDto(false, code, failure is OperationCanceledException
                ? "The download client did not answer in time." : failure.Message, null, null);
        }
    }

    /// <summary>Lists quality profiles.</summary>
    [HttpGet("QualityProfiles")]
    public async Task<ActionResult<IReadOnlyList<QualityProfileDto>>> QualityProfiles(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var defaultId = (await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken)).DefaultQualityProfileId;
        var profiles = await database.AcquisitionQualityProfiles.AsNoTracking().OrderBy(value => value.Name).ToListAsync(cancellationToken);
        return profiles.Select(profile => ToDto(profile, defaultId)).ToArray();
    }

    /// <summary>Creates a quality profile.</summary>
    [HttpPost("QualityProfiles")]
    public async Task<ActionResult<QualityProfileDto>> CreateQualityProfile(QualityProfileRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateProfile(request) is { } error) return Invalid(error);
        var profile = new AcquisitionQualityProfile();
        Apply(profile, request);
        database.AcquisitionQualityProfiles.Add(profile);
        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        return StatusCode(201, ToDto(profile, null));
    }

    /// <summary>Replaces a quality profile and advances its revision.</summary>
    [HttpPatch("QualityProfiles/{id:guid}")]
    public async Task<ActionResult<QualityProfileDto>> PatchQualityProfile(Guid id, QualityProfileRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (ValidateProfile(request) is { } error) return Invalid(error);
        var profile = await database.AcquisitionQualityProfiles.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (profile is null) return NotFound();
        if (request.Revision != profile.Revision) return RevisionConflict();
        Apply(profile, request);
        profile.Revision++;
        if (await SaveAsync(cancellationToken) is { } conflict) return conflict;
        return ToDto(profile, (await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken)).DefaultQualityProfileId);
    }

    /// <summary>Deletes a profile that is neither the default nor assigned to an entry.</summary>
    [HttpDelete("QualityProfiles/{id:guid}")]
    public async Task<IActionResult> DeleteQualityProfile(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var profile = await database.AcquisitionQualityProfiles.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (profile is null) return NotFound();
        if ((await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken)).DefaultQualityProfileId == id)
            return Conflict(ProblemBody("profile_is_default", "Choose another default profile first."));
        if (await database.Entries.AnyAsync(entry => entry.QualityProfileId == id, cancellationToken))
            return Conflict(ProblemBody("profile_assigned", "Titles still use this profile; reassign them first."));
        database.AcquisitionQualityProfiles.Remove(profile);
        await database.SaveChangesAsync(cancellationToken);
        cache.Invalidate();
        return NoContent();
    }

    /// <summary>Gets enablement, defaults and readiness.</summary>
    [HttpGet("Acquisition")]
    public async Task<ActionResult<AcquisitionSettingsDto>> Acquisition(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await ToDtoAsync(cancellationToken);
    }

    /// <summary>Updates the selected client, default profile and enablement.</summary>
    [HttpPatch("Acquisition")]
    public async Task<ActionResult<AcquisitionSettingsDto>> PatchAcquisition(AcquisitionSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.Revision) return RevisionConflict();
        if (request.DownloadClientId is { } clientId &&
            !await database.AcquisitionDownloadClients.AnyAsync(value => value.Id == clientId, cancellationToken))
            return Invalid("invalid_download_client");
        if (request.DefaultQualityProfileId is { } profileId &&
            !await database.AcquisitionQualityProfiles.AnyAsync(value => value.Id == profileId, cancellationToken))
            return Invalid("invalid_quality_profile");
        var previous = (settings.DownloadClientId, settings.DefaultQualityProfileId, settings.Enabled);
        settings.DownloadClientId = request.DownloadClientId;
        settings.DefaultQualityProfileId = request.DefaultQualityProfileId;
        settings.Enabled = false;
        await database.SaveChangesAsync(cancellationToken);
        if (request.Enabled)
        {
            var state = await configuration.GetStateAsync(database, cancellationToken);
            if (!state.Ready)
            {
                // A refused enable leaves the previous configuration exactly as it was.
                (settings.DownloadClientId, settings.DefaultQualityProfileId, settings.Enabled) = previous;
                await database.SaveChangesAsync(cancellationToken);
                return Conflict(new ProblemDetails
                {
                    Status = 409, Type = "acquisition_not_ready", Title = "Acquisition is not ready.",
                    Extensions = { ["blockers"] = state.Blockers }
                });
            }
        }

        settings.Enabled = request.Enabled;
        settings.Revision++;
        await database.SaveChangesAsync(cancellationToken);
        cache.Invalidate();
        return await ToDtoAsync(cancellationToken);
    }

    private async Task<AcquisitionSettingsDto> ToDtoAsync(CancellationToken cancellationToken)
    {
        var state = await configuration.GetStateAsync(database, cancellationToken);
        var seedMatches = state.Client is { Kind: TransmissionDriver.DriverKind } client &&
            AcquisitionConfiguration.NormalizeEndpoint(client.BaseUrl) ==
            AcquisitionConfiguration.NormalizeEndpoint(pluginConfiguration.Current.TransmissionRpcUrl);
        return new AcquisitionSettingsDto(state.Settings.Enabled, state.Settings.DownloadClientId, state.Settings.DefaultQualityProfileId,
            state.Settings.Revision, state.Ready, state.Blockers, (int)Math.Ceiling(hold.Hold.TotalSeconds), seedMatches,
            QualityCatalog.All.Select(quality => new QualityDefinitionDto(quality.Id, quality.Source, quality.Resolution)).ToArray());
    }

    private async Task<string?> ApplySecretAsync(string? current, SecretChangeRequest change, CancellationToken cancellationToken) =>
        change.Action switch
        {
            "replace" => await secrets.AddAsync(change.Value!, cancellationToken),
            "clear" => null,
            _ => current
        };

    /// <summary>Saves and maps a unique-name violation to 409; any change invalidates cached searches.</summary>
    private async Task<ActionResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            cache.Invalidate();
            return null;
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            database.ChangeTracker.Clear();
            return Conflict(ProblemBody("name_in_use", "Another configuration already uses this name."));
        }
    }

    private static string? ValidateSecret(SecretChangeRequest change, bool creating) => change.Action switch
    {
        "unchanged" when change.Value is null => null,
        "replace" when !string.IsNullOrEmpty(change.Value) && change.Value.Length <= 1024 => null,
        "clear" when change.Value is null && !creating => null,
        _ => "invalid_secret_change"
    };

    private static string? ValidateIndexer(IndexerSettingsRequest request, bool creating)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "invalid_name";
        if (!ValidEndpoint(request.BaseUrl)) return "invalid_indexer_url";
        if (request.Categories.Length > 64 || request.Categories.Any(category => category < 1)) return "invalid_indexer_categories";
        if (request.DownloadHosts.Length > 16 ||
            request.DownloadHosts.Any(host => Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown))
            return "invalid_download_hosts";
        if (!creating && request.Revision is null) return "revision_required";
        return ValidateSecret(request.ApiKey, creating);
    }

    private string? ValidateClient(DownloadClientSettingsRequest request, bool creating)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "invalid_name";
        if (drivers.Get(request.Kind) is null) return "unsupported_client_kind";
        if (!ValidEndpoint(request.BaseUrl)) return "invalid_client_url";
        if (!LabelPattern().IsMatch(request.Label) || request.Label.StartsWith("jfmod-", StringComparison.OrdinalIgnoreCase))
            return "invalid_label";
        if (!AbsolutePath(request.DownloadDirectory) || !AbsolutePath(request.LocalDirectory)) return "invalid_download_directory";
        if (request.OpenUrl is { Length: > 0 } open && !ValidEndpoint(open)) return "invalid_open_url";
        if (!creating && request.Revision is null) return "revision_required";
        return ValidateSecret(request.Password, creating);
    }

    private static string? ValidateProfile(QualityProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "invalid_name";
        if (request.Qualities.Length == 0 || request.Qualities.Distinct(StringComparer.Ordinal).Count() != request.Qualities.Length ||
            request.Qualities.Any(quality => !QualityCatalog.IsKnown(quality)))
            return "invalid_qualities";
        if (request.MinimumBytesPerHour > request.MaximumBytesPerHour) return "invalid_size_range";
        return null;
    }

    private static bool ValidEndpoint(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool AbsolutePath(string value) => value.StartsWith('/') && !value.Split('/').Contains("..") &&
        !value.Contains('\0', StringComparison.Ordinal);

    private static void Apply(AcquisitionIndexer indexer, IndexerSettingsRequest request)
    {
        indexer.Name = request.Name.Trim();
        indexer.BaseUrl = request.BaseUrl.Trim();
        indexer.Enabled = request.Enabled;
        indexer.Categories = string.Join(',', request.Categories.Distinct().Order());
        indexer.Priority = request.Priority;
        indexer.DownloadHosts = string.Join(',', request.DownloadHosts.Select(host => host.Trim().ToLowerInvariant()).Distinct());
        indexer.MinimumSeedRatio = request.MinimumSeedRatio;
        indexer.MinimumSeedMinutes = request.MinimumSeedMinutes;
        indexer.LastError = null;
    }

    private static void Apply(AcquisitionDownloadClient client, DownloadClientSettingsRequest request, DestinationCheck destination)
    {
        client.Name = request.Name.Trim();
        client.Kind = request.Kind;
        client.BaseUrl = request.BaseUrl.Trim();
        client.Username = request.Username;
        client.Enabled = request.Enabled;
        client.Label = request.Label;
        client.DownloadDirectory = request.DownloadDirectory.TrimEnd('/');
        client.LocalDirectory = request.LocalDirectory.Trim().TrimEnd('/');
        client.VerifiedLibraryIds = string.Join(',', destination.SameFilesystemLibraryIds);
        client.OpenUrl = string.IsNullOrWhiteSpace(request.OpenUrl) ? null : request.OpenUrl.Trim();
        client.LastError = null;
    }

    private static void Apply(AcquisitionQualityProfile profile, QualityProfileRequest request)
    {
        profile.Name = request.Name.Trim();
        profile.QualitiesJson = JsonSerializer.Serialize(request.Qualities);
        profile.MinimumBytesPerHour = request.MinimumBytesPerHour;
        profile.MaximumBytesPerHour = request.MaximumBytesPerHour;
    }

    private static IndexerSettingsDto ToDto(AcquisitionIndexer indexer)
    {
        var capabilities = indexer.VerifiedRevision == indexer.Revision ? AcquisitionConfiguration.Capabilities(indexer) : null;
        return new IndexerSettingsDto(indexer.Id, indexer.Name, indexer.BaseUrl, indexer.ApiKeySecretRef is not null, indexer.Enabled,
            indexer.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray(), indexer.Priority,
            indexer.DownloadHosts.Split(',', StringSplitOptions.RemoveEmptyEntries), indexer.MinimumSeedRatio, indexer.MinimumSeedMinutes,
            indexer.Revision, capabilities is not null,
            capabilities is null ? null : new IndexerCapabilitiesDto(capabilities.MovieSearch, capabilities.TvSearch, capabilities.Search,
                capabilities.Categories, capabilities.LimitMax, capabilities.LimitDefault),
            indexer.CapabilitiesFetchedAt is { } fetched ? DateTime.SpecifyKind(fetched, DateTimeKind.Utc) : null, indexer.LastError);
    }

    private static DownloadClientSettingsDto ToDto(AcquisitionDownloadClient client) => new(client.Id, client.Name, client.Kind,
        client.BaseUrl, client.Username, client.PasswordSecretRef is not null, client.Enabled, client.Label, client.DownloadDirectory,
        client.LocalDirectory, client.VerifiedLibraryIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToArray(),
        client.OpenUrl, client.Revision, client.VerifiedRevision == client.Revision, client.ClientVersion, client.ApiVersion,
        client.VerifiedAt is { } verified ? DateTime.SpecifyKind(verified, DateTimeKind.Utc) : null, client.LastError);

    private static QualityProfileDto ToDto(AcquisitionQualityProfile profile, Guid? defaultId) => new(profile.Id, profile.Name,
        AcquisitionConfiguration.Qualities(profile), profile.MinimumBytesPerHour, profile.MaximumBytesPerHour, profile.Revision,
        profile.Id == defaultId);

    private BadRequestObjectResult Invalid(string code, string? message = null) =>
        BadRequest(new ProblemDetails { Status = 400, Type = code, Title = message ?? "The configuration is not valid." });

    private ConflictObjectResult RevisionConflict() =>
        Conflict(ProblemBody("revision_conflict", "This configuration changed since it was loaded. Reload and try again."));

    private static ProblemDetails ProblemBody(string code, string title) => new() { Status = 409, Type = code, Title = title };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex LabelPattern();
}
