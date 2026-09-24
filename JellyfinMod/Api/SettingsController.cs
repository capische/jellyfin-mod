using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Acquisition;
using JellyfinMod.Services.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Api;

/// <summary>
/// The settings contract every form shares (P7.S7): typed, revisioned, administrator-only endpoints for the
/// settings that used to be edited only as XML, plus the readiness Overview and the first-run setup state.
/// </summary>
/// <remarks>
/// Secrets are write-only everywhere: a read reports whether one is configured, never its value or reference.
/// Every PATCH carries the revision it was made against and is refused with 409 when it is stale; unknown fields
/// are refused with 400 by the request types.
/// </remarks>
[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("JellyfinMod")]
public sealed class SettingsController(
    ModDbContext database,
    DatabaseInitializer readiness,
    AcquisitionConfiguration acquisition,
    AcquisitionSecretStore secrets,
    RetentionConfigurationSource xml,
    TmdbClient tmdb,
    IHttpClientFactory httpClients,
    IUserManager users,
    IServiceProvider services) : ControllerBase
{
    private static readonly string[] WatchedUserModes = ["allUsers", "selectedUser", "anyUser"];

    /// <summary>Gets the discovery settings.</summary>
    [HttpGet("Settings/Discovery")]
    public async Task<ActionResult<DiscoverySettingsDto>> Discovery(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await DiscoveryDtoAsync(await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken), cancellationToken);
    }

    /// <summary>Replaces or clears the TMDB token; the next test must pass again.</summary>
    [HttpPatch("Settings/Discovery")]
    public async Task<ActionResult<DiscoverySettingsDto>> PatchDiscovery(DiscoverySettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (request.Revision is null) return Invalid("revision_required");
        if (ValidateSecret(request.Token) is { } error) return Invalid(error);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.DiscoveryRevision) return RevisionConflict();
        var previous = settings.TmdbReadAccessTokenRef;
        if (request.Token.Action != "unchanged")
        {
            settings.TmdbReadAccessTokenRef = request.Token.Action == "replace"
                ? await secrets.AddAsync(request.Token.Value!.Trim(), cancellationToken) : null;
            // A token saved before P7.S7 may still sit in the XML, and it would win over this one.
            await ClearXmlTokenAsync();
        }

        settings.DiscoveryRevision++;
        settings.DiscoveryVerifiedRevision = null;
        settings.DiscoveryVerifiedAt = null;
        Record("discovery", settings.DiscoveryRevision);
        await database.SaveChangesAsync(cancellationToken);
        if (previous != settings.TmdbReadAccessTokenRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        return await DiscoveryDtoAsync(settings, cancellationToken);
    }

    /// <summary>Checks the saved TMDB token against TMDB and records a pass against the current revision.</summary>
    [HttpPost("Settings/Discovery/Test")]
    public async Task<ActionResult<ConnectionTestDto>> TestDiscovery(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var revision = settings.DiscoveryRevision;
        var (code, message) = await tmdb.TestAsync(cancellationToken);
        if (code == "ok")
        {
            settings.DiscoveryVerifiedRevision = revision;
            settings.DiscoveryVerifiedAt = DateTime.UtcNow;
        }
        else
        {
            settings.DiscoveryVerifiedRevision = null;
        }

        await database.SaveChangesAsync(cancellationToken);
        return new ConnectionTestDto(code == "ok", code, message, null, null);
    }

    /// <summary>Gets where seed protection reads Transmission from.</summary>
    [HttpGet("Settings/SeedProtection")]
    public async Task<ActionResult<SeedProtectionSettingsDto>> SeedProtection(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await SeedDtoAsync(await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken), cancellationToken);
    }

    /// <summary>Changes the seed-protection source.</summary>
    [HttpPatch("Settings/SeedProtection")]
    public async Task<ActionResult<SeedProtectionSettingsDto>> PatchSeedProtection(SeedProtectionSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        using var operation = SqliteWriteDiagnostics.Operation("seed-protection settings");
        if (request.Revision is null) return Invalid("revision_required");
        if (request.Source is not (SeedProtectionSources.AcquisitionClient or SeedProtectionSources.Separate))
            return Invalid("invalid_seed_protection_source");
        if (ValidateSecret(request.Password) is { } error) return Invalid(error);
        if (request.Source == SeedProtectionSources.Separate && !ValidEndpoint(request.RpcUrl))
            return Invalid("invalid_rpc_url", "Enter the Transmission RPC address, for example http://host:9091/transmission/rpc, without credentials.");
        if (request.Username is { Length: > 256 }) return Invalid("invalid_username");
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.SeedProtectionRevision) return RevisionConflict();
        var previous = settings.SeedProtectionPasswordRef;
        settings.SeedProtectionSource = request.Source;
        if (request.Source == SeedProtectionSources.Separate)
        {
            settings.SeedProtectionRpcUrl = request.RpcUrl!.Trim();
            settings.SeedProtectionUsername = request.Username?.Trim() ?? string.Empty;
            settings.SeedProtectionPasswordRef = request.Password.Action switch
            {
                "replace" => await secrets.AddAsync(request.Password.Value!, cancellationToken),
                "clear" => null,
                _ => previous
            };
        }
        else
        {
            // The acquisition client's own credentials apply; a separate password has nothing left to protect.
            settings.SeedProtectionRpcUrl = null;
            settings.SeedProtectionUsername = null;
            settings.SeedProtectionPasswordRef = null;
        }

        await ClearXmlSeedEndpointAsync();
        settings.SeedProtectionRevision++;
        Record("seedProtection", settings.SeedProtectionRevision);
        await database.SaveChangesAsync(cancellationToken);
        if (previous != settings.SeedProtectionPasswordRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        return await SeedDtoAsync(settings, cancellationToken);
    }

    /// <summary>Reads the effective seed-protection endpoint's session without changing anything.</summary>
    [HttpPost("Settings/SeedProtection/Test")]
    public async Task<ActionResult<ConnectionTestDto>> TestSeedProtection(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var endpoint = await EffectiveSeedEndpointAsync(cancellationToken);
        if (endpoint is null || !Uri.TryCreate(endpoint.RpcUrl, UriKind.Absolute, out var uri))
            return new ConnectionTestDto(false, "not_configured",
                "No Transmission is configured for seed protection: select a download client or enter a separate endpoint.", null, null);
        var password = endpoint.Password.Length > 0 ? endpoint.Password : await secrets.GetAsync(endpoint.PasswordRef, cancellationToken) ?? string.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = httpClients.CreateClient(NamedClient.Default);
            using var response = await TransmissionRpc.SendAsync(client, uri, "session-get", new { fields = new[] { "version", "rpc-version" } },
                endpoint.Username, password, timeout.Token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new ConnectionTestDto(false, "unauthorized", "Transmission refused the username or password.", null, null);
            if (!response.IsSuccessStatusCode)
                return new ConnectionTestDto(false, "unreachable", $"Transmission answered HTTP {(int)response.StatusCode}.", null, null);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!TransmissionRpc.Success(document.RootElement, out var arguments))
                return new ConnectionTestDto(false, "invalid_response", "The address answered, but not as Transmission RPC.", null, null);
            var version = arguments.TryGetProperty("version", out var value) ? value.GetString() : null;
            var rpc = arguments.TryGetProperty("rpc-version", out var rpcValue) ? rpcValue.ToString() : null;
            return new ConnectionTestDto(true, "ok", "Transmission answered; seed state can be read.", version, rpc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestDto(false, "timeout", "Transmission did not answer in time.", null, null);
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or IOException)
        {
            return new ConnectionTestDto(false, "unreachable", "Transmission could not be reached from this server.", null, null);
        }
    }

    /// <summary>Gets the retention settings.</summary>
    [HttpGet("Settings/Retention")]
    public async Task<ActionResult<RetentionSettingsDto>> Retention(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return RetentionDto((await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken)).RetentionRevision);
    }

    /// <summary>Changes the retention settings with the Dashboard's validation; a missing selected user is refused (T13).</summary>
    [HttpPatch("Settings/Retention")]
    public async Task<ActionResult<RetentionSettingsDto>> PatchRetention(RetentionSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        using var operation = SqliteWriteDiagnostics.Operation("retention settings");
        if (request.Revision is null) return Invalid("revision_required");
        if (request.ReclaimAfterDays is < 1 or > 3650) return Invalid("invalid_reclaim_days", "Days must be between 1 and 3650.");
        var mode = Array.IndexOf(WatchedUserModes, request.WatchedUserMode);
        if (mode < 0) return Invalid("invalid_watched_user_mode");
        if (mode == (int)WatchedUserMode.SelectedUser &&
            (request.SelectedUserId is not { } selected || selected == Guid.Empty || users.GetUserById(selected) is null))
            return Invalid("invalid_selected_user", "Choose a user that exists on this server.");
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.RetentionRevision) return RevisionConflict();
        var configuration = xml.Current;
        configuration.RetentionEnabled = request.Enabled;
        configuration.ReclaimAfterDays = request.ReclaimAfterDays;
        configuration.RetentionWatchedUserMode = (WatchedUserMode)mode;
        configuration.RetentionSelectedUserId = mode == (int)WatchedUserMode.SelectedUser ? request.SelectedUserId : null;
        configuration.ExemptFavourites = request.ExemptFavourites;
        xml.Save();
        settings.RetentionRevision++;
        Record("retention", settings.RetentionRevision);
        await database.SaveChangesAsync(cancellationToken);
        return RetentionDto(settings.RetentionRevision);
    }

    /// <summary>Readiness per area, plugin and interface identity, and the setup summary.</summary>
    [HttpGet("Settings/Overview")]
    public async Task<ActionResult<object>> Overview(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var state = await acquisition.GetStateAsync(database, cancellationToken);
        var discovery = await DiscoveryDtoAsync(settings, cancellationToken);
        var seed = await EffectiveSeedEndpointAsync(cancellationToken);
        var retention = RetentionDto(settings.RetentionRevision);
        var takeover = services.GetService<WebRootTakeover>()?.State;
        var bundles = services.GetService<WebBundleStore>();
        var areas = new List<SettingsAreaDto>
        {
            new("discovery", discovery.TokenConfigured || discovery.ApiKeyConfigured, null,
                discovery.TokenConfigured || discovery.ApiKeyConfigured ? [] : ["discovery_token_missing"], discovery.Revision),
            new("acquisition", state.Ready, settings.Enabled, state.Blockers, settings.Revision),
            new("seedProtection", seed is not null, null, seed is null ? ["seed_protection_unconfigured"] : [], settings.SeedProtectionRevision),
            new("import", true, settings.ImportEnabled, [], settings.ImportRevision),
            new("retention", !retention.SelectedUserMissing, retention.Enabled,
                retention.SelectedUserMissing ? ["retention_selected_user_missing"] : [], retention.Revision),
            new("automation", true, settings.AutomationEnabled, [], settings.AutomationRevision),
            new("interface", takeover?.Blocker is null, xml.Current.UiTakeoverEnabled,
                takeover?.Blocker is { } blocker ? [blocker] : [], null)
        };
        return Ok(new
        {
            plugin = new { version = Plugin.Instance?.Version.ToString(), revision = typeof(Plugin).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion },
            web = bundles?.Current is { } current ? new
            {
                bundleId = current.BundleId, webCommit = current.Manifest.WebCommit, builtAt = current.Manifest.BuiltAt,
                retainedBundleIds = bundles.RetainedBundleIds, takeover = takeover?.Status, takeoverBlocker = takeover?.Blocker
            } : null,
            areas,
            setup = await SetupAsync(settings, state, discovery, cancellationToken)
        });
    }

    /// <summary>The first-run setup steps, derived from the readiness checks the product already enforces.</summary>
    [HttpGet("Setup/State")]
    public async Task<ActionResult<SetupStateDto>> SetupState(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        var result = await SetupAsync(settings, await acquisition.GetStateAsync(database, cancellationToken),
            await DiscoveryDtoAsync(settings, cancellationToken), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Hides the setup banner; the wizard stays reachable from Settings.</summary>
    [HttpPost("Setup/Dismiss")]
    public async Task<ActionResult<SetupStateDto>> DismissSetup(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken);
        settings.SetupDismissedAt ??= DateTime.UtcNow;
        Record("setup", 0);
        await database.SaveChangesAsync(cancellationToken);
        return await SetupAsync(settings, await acquisition.GetStateAsync(database, cancellationToken),
            await DiscoveryDtoAsync(settings, cancellationToken), cancellationToken);
    }

    private async Task<SetupStateDto> SetupAsync(AcquisitionSettings settings, AcquisitionState state, DiscoverySettingsDto discovery,
        CancellationToken cancellationToken)
    {
        var steps = new List<SetupStepDto>();
        var blocked = false;

        void Step(string id, IReadOnlyList<string> reasons)
        {
            steps.Add(new SetupStepDto(id, reasons.Count == 0 ? "done" : blocked ? "blocked" : "pending", false, reasons));
            if (reasons.Count > 0) blocked = true;
        }

        Step("discovery", !(discovery.TokenConfigured || discovery.ApiKeyConfigured) ? ["discovery_token_missing"]
            : discovery.Verified ? [] : ["discovery_unverified"]);

        var clientReasons = new List<string>();
        if (state.Client is not { } client) clientReasons.Add("no_download_client");
        else
        {
            if (!client.Enabled) clientReasons.Add("download_client_disabled");
            if (client.VerifiedRevision != client.Revision) clientReasons.Add("download_client_unverified");
            var mappings = await database.DownloadClientPathMappings.AsNoTracking()
                .Where(mapping => mapping.DownloadClientId == client.Id).ToListAsync(cancellationToken);
            if (mappings.Any(mapping => mapping.VerifiedAt is null)) clientReasons.Add("path_mapping_unverified");
        }

        Step("downloadClient", clientReasons);
        Step("indexers", state.Blockers.Contains("no_verified_indexer") ? ["no_verified_indexer"] : []);
        Step("qualityProfile", settings.DefaultQualityProfileId is null ? ["no_default_profile"] : []);
        Step("enable", settings.Enabled ? [] : state.Ready ? ["acquisition_disabled"] : ["acquisition_not_ready"]);
        steps.Add(new SetupStepDto("optional", "done", true, []));
        var complete = steps.All(step => step.Status == "done");
        if (complete) settings.SetupCompletedAt ??= DateTime.UtcNow;
        return new SetupStateDto(complete, settings.SetupCompletedAt, settings.SetupDismissedAt, steps);
    }

    private async Task<DiscoverySettingsDto> DiscoveryDtoAsync(AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        var configuration = xml.Current;
        var tokenRef = configuration.TmdbReadAccessTokenRef ?? settings.TmdbReadAccessTokenRef;
        var token = configuration.TmdbReadAccessToken.Length > 0 || await secrets.HasAsync(tokenRef, cancellationToken);
        var key = configuration.TmdbApiKey.Length > 0 || await secrets.HasAsync(configuration.TmdbApiKeyRef, cancellationToken);
        var verified = settings.DiscoveryVerifiedRevision == settings.DiscoveryRevision;
        return new DiscoverySettingsDto(token, key, verified,
            verified && settings.DiscoveryVerifiedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, settings.DiscoveryRevision);
    }

    private async Task<SeedProtectionSettingsDto> SeedDtoAsync(AcquisitionSettings settings, CancellationToken cancellationToken)
    {
        var configuration = xml.Current;
        var legacy = configuration.TransmissionRpcUrl.Length > 0;
        var effective = await EffectiveSeedEndpointAsync(cancellationToken);
        var client = settings.DownloadClientId is { } clientId
            ? await database.AcquisitionDownloadClients.AsNoTracking().SingleOrDefaultAsync(row => row.Id == clientId, cancellationToken)
            : null;
        var matches = effective is not null && client is { Kind: TransmissionDriver.DriverKind } &&
            AcquisitionConfiguration.NormalizeEndpoint(client.BaseUrl) == AcquisitionConfiguration.NormalizeEndpoint(effective.RpcUrl);
        var separate = settings.SeedProtectionSource == SeedProtectionSources.Separate;
        return new SeedProtectionSettingsDto(settings.SeedProtectionSource, separate ? WithoutCredentials(settings.SeedProtectionRpcUrl) : null,
            separate ? settings.SeedProtectionUsername : null,
            separate && await secrets.HasAsync(settings.SeedProtectionPasswordRef, cancellationToken),
            WithoutCredentials(effective?.RpcUrl), matches, legacy, settings.SeedProtectionRevision);
    }

    /// <summary>An address as an administrator may see it: never with user information (§5, S7-R3).</summary>
    private static string? WithoutCredentials(string? value) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString()
            : value;

    private async Task<SeedEndpoint?> EffectiveSeedEndpointAsync(CancellationToken cancellationToken)
    {
        var configuration = xml.Current;
        return configuration.TransmissionRpcUrl.Length > 0
            ? new SeedEndpoint(configuration.TransmissionRpcUrl, configuration.TransmissionUsername, configuration.TransmissionPassword,
                configuration.TransmissionPasswordRef)
            : await SeedEndpoint.ResolveAsync(database, cancellationToken);
    }

    private RetentionSettingsDto RetentionDto(int revision)
    {
        var configuration = xml.Current;
        var selected = configuration.RetentionSelectedUserId;
        var user = selected is { } id && id != Guid.Empty ? users.GetUserById(id) : null;
        var missing = configuration.RetentionWatchedUserMode == WatchedUserMode.SelectedUser && user is null;
        return new RetentionSettingsDto(configuration.RetentionEnabled, configuration.ReclaimAfterDays,
            WatchedUserModes[(int)configuration.RetentionWatchedUserMode], selected, user?.Username, missing,
            configuration.ExemptFavourites, configuration.RetentionTestWindowMinutes, revision);
    }

    private async Task ClearXmlTokenAsync()
    {
        var configuration = xml.Current;
        if (configuration.TmdbReadAccessTokenRef is null && configuration.TmdbReadAccessToken.Length == 0) return;
        var stale = configuration.TmdbReadAccessTokenRef;
        configuration.TmdbReadAccessTokenRef = null;
        configuration.TmdbReadAccessToken = string.Empty;
        xml.Save();
        await secrets.RemoveAsync(stale, CancellationToken.None);
    }

    private async Task ClearXmlSeedEndpointAsync()
    {
        var configuration = xml.Current;
        if (configuration.TransmissionRpcUrl.Length == 0 && configuration.TransmissionPasswordRef is null) return;
        var stale = configuration.TransmissionPasswordRef;
        configuration.TransmissionRpcUrl = string.Empty;
        configuration.TransmissionUsername = string.Empty;
        configuration.TransmissionPasswordRef = null;
        xml.Save();
        await secrets.RemoveAsync(stale, CancellationToken.None);
    }

    /// <summary>A settings change on the record: who, which area and which revision; never a value.</summary>
    private void Record(string area, int revision)
    {
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId);
        database.History.Add(new HistoryRecord
        {
            EntryId = Guid.Empty, EventType = "settings_changed", Summary = $"Settings changed: {area}",
            Data = JsonSerializer.Serialize(new { area, revision, userId })
        });
    }

    private static string? ValidateSecret(SecretChangeRequest change) => change.Action switch
    {
        "unchanged" when change.Value is null => null,
        "replace" when !string.IsNullOrWhiteSpace(change.Value) && change.Value.Length <= 1024 => null,
        "clear" when change.Value is null => null,
        _ => "invalid_secret_change"
    };

    private static bool ValidEndpoint(string? value) => value is not null && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private BadRequestObjectResult Invalid(string code, string? message = null) =>
        BadRequest(new ProblemDetails { Status = 400, Type = code, Title = message ?? "The configuration is not valid." });

    private ConflictObjectResult RevisionConflict() => Conflict(new ProblemDetails
    {
        Status = 409, Type = "revision_conflict", Title = "This configuration changed since it was loaded. Reload and try again."
    });
}
