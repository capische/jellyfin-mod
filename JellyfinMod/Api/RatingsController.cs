using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Ratings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Api;

/// <summary>
/// Ratings (Phase 9): the administrator's settings, Test and status, a manual refresh, and the reads every signed-in user's
/// detail pages make. Reads are SQLite and the host's in-memory items only; nothing here calls a provider except Test.
/// </summary>
/// <remarks>The MDBList key is write-only: no response, log line or history record carries it or its reference.</remarks>
[ApiController, Authorize, Route("JellyfinMod")]
public sealed class RatingsController(
    ModDbContext database,
    DatabaseInitializer readiness,
    LibraryAccess access,
    ILibraryManager library,
    AcquisitionSecretStore secrets,
    RatingsStore ratings,
    RatingsRefreshRunner runner,
    RatingsRefreshQueue queue,
    MdbListClient client,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Gets the ratings settings.</summary>
    [HttpGet("Settings/Ratings"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RatingsSettingsDto>> Settings(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        return await DtoAsync(await RatingsStore.GetSettingsAsync(database, cancellationToken), cancellationToken);
    }

    /// <summary>Changes the ratings settings; a field left out stays as it is. Replacing the key clears an <c>unauthorized</c> block.</summary>
    [HttpPatch("Settings/Ratings"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RatingsSettingsDto>> PatchSettings(RatingsSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        await using var settingsMutation = await SettingsMutationGate.AcquireAsync(cancellationToken);
        if (request.Revision is null) return Invalid("revision_required");
        if (request.ApiKey is null) return Invalid("invalid_secret_change");
        if (ValidateSecret(request.ApiKey) is { } error) return Invalid(error);
        if (request.RefreshDays is < 1 or > 365) return Invalid("invalid_refresh_days", "Refresh every 1 to 365 days.");
        if (request.DailyBudget is < 1 or > 250000) return Invalid("invalid_daily_budget", "The daily budget is 1 to 250,000 calls.");
        if (request.DefaultSources is { } sources && (sources.Any(source => !RatingSources.IsKnown(source)) ||
                sources.Distinct(StringComparer.Ordinal).Count() != sources.Length))
            return Invalid("invalid_sources", "Choose each source once, from the available sources.");
        var settings = await RatingsStore.GetSettingsAsync(database, cancellationToken);
        if (request.Revision != settings.Revision) return Conflict(new ProblemDetails
        {
            Status = 409, Type = "revision_conflict", Title = "This configuration changed since it was loaded. Reload and try again."
        });
        var previous = settings.ApiKeyRef;
        if (request.Enabled is { } enabled) settings.Enabled = enabled;
        if (request.RefreshDays is { } days) settings.RefreshDays = days;
        if (request.DailyBudget is { } budget) settings.DailyBudget = budget;
        if (request.DefaultSources is { } chosen) settings.DefaultSources = JsonSerializer.Serialize(chosen);
        if (request.ApiKey.Action != "unchanged")
        {
            settings.ApiKeyRef = request.ApiKey.Action == "replace" ? await secrets.AddAsync(request.ApiKey.Value!.Trim(), cancellationToken) : null;
            settings.VerifiedRevision = null;
            settings.VerifiedAt = null;
            // A new key gets a fresh start: the old one's refusal says nothing about it, and MDBList's daily quota is the key's,
            // so a breaker opened by a 429 closes too. A breaker opened by provider errors stays: those are the provider's.
            var state = await RatingsStore.GetStateAsync(database, cancellationToken);
            if (state.Blocker == RatingsOutcomes.Unauthorized) state.Blocker = null;
            if (state.BreakerReason == RatingsOutcomes.RateLimited) (state.BreakerUntil, state.BreakerReason) = (null, null);
        }

        settings.Revision++;
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId);
        database.History.Add(new HistoryRecord
        {
            EntryId = Guid.Empty, EventType = "settings_changed", Summary = "Settings changed: ratings",
            Data = JsonSerializer.Serialize(new { area = "ratings", revision = settings.Revision, userId })
        });
        await database.SaveChangesAsync(cancellationToken);
        if (previous != settings.ApiKeyRef) await secrets.RemoveAsync(previous, CancellationToken.None);
        return await DtoAsync(settings, cancellationToken);
    }

    /// <summary>Makes one real MDBList call for a fixed well-known title and answers a code and a sentence.</summary>
    [HttpPost("Settings/Ratings/Test"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RatingsTestDto>> Test(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var result = await runner.TestAsync(cancellationToken);
        var sources = result.Ratings.Select(rating => rating.Source).Where(RatingSources.IsKnown).ToArray();
        return new RatingsTestDto(result.Outcome == RatingsOutcomes.Ok, result.Outcome, result.Outcome switch
        {
            RatingsOutcomes.Ok => sources.Length > 0 ? "MDBList accepted the key and returned ratings." : "MDBList accepted the key but returned no ratings for the test title.",
            RatingsOutcomes.NotConfigured => "No MDBList key is saved yet.",
            RatingsOutcomes.Unauthorized => "MDBList refused the key. Check it was copied completely.",
            RatingsOutcomes.RateLimited => "MDBList says today's request limit is spent. Fetching pauses until the limit resets.",
            RatingsOutcomes.Timeout => "MDBList did not answer in time.",
            RatingsOutcomes.Unreachable => "MDBList could not be reached from this server.",
            RatingsOutcomes.Malformed => "MDBList answered, but not in the shape JellyfinMod reads.",
            RatingsOutcomes.NotFound => "MDBList accepted the request but did not know the test title.",
            "budget_spent" => "Today's ratings budget is spent, so no call was made; try again tomorrow or raise the budget.",
            "breaker_open" => "Fetching is paused after provider errors or the provider's daily limit, so no call was made; try again later.",
            _ => "MDBList answered with an error."
        }, sources);
    }

    /// <summary>The fetcher's budget, breaker and last run, for administrators.</summary>
    [HttpGet("Ratings/Status"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RatingsStatusDto>> Status(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var settings = await ratings.ReadSettingsAsync(cancellationToken);
        var state = await database.RatingsProviderStates.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == RatingsProviderState.SingletonId, cancellationToken) ?? new RatingsProviderState();
        var now = clock.GetUtcNow().UtcDateTime;
        var total = await database.Entries.CountAsync(cancellationToken);
        var rated = await database.TitleRatings.Select(row => row.EntryId).Distinct().CountAsync(cancellationToken);
        return new RatingsStatusDto(settings.Enabled, await secrets.HasAsync(settings.ApiKeyRef, cancellationToken), state.Blocker,
            new RatingsBreakerDto(state.BreakerUntil > now, Utc(state.BreakerUntil > now ? state.BreakerUntil : null),
                state.BreakerUntil > now ? state.BreakerReason : null, state.ConsecutiveFailures),
            new RatingsBudgetDto(now.Date, RatingsRefreshRunner.BudgetUsed(state, now), settings.DailyBudget),
            state.LastRunStartedAt is { } started
                ? new RatingsRunDto(Utc(started)!.Value, Utc(state.LastRunFinishedAt), state.LastRunFetched, state.LastRunFailed, state.LastRunStopReason)
                : null,
            total, total - rated, queue.Count);
    }

    /// <summary>Whether ratings are on and the administrator's default order, for every signed-in user.</summary>
    [HttpGet("Ratings/Defaults")]
    public async Task<ActionResult<RatingsDefaultsDto>> Defaults(CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        if (access.GetUser(User) is null) return Unauthorized();
        var settings = await ratings.ReadSettingsAsync(cancellationToken);
        return new RatingsDefaultsDto(settings.Enabled, RatingSources.ParseList(settings.DefaultSources), RatingSources.Known, settings.RefreshDays);
    }

    /// <summary>A native movie's or series' ratings, with or without a catalog entry; 404 for an item the user cannot see.</summary>
    [HttpGet("Ratings/Items/{itemId:guid}")]
    public async Task<ActionResult<ItemRatingsDto>> Item([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        // The host's own visibility check: a hidden item answers exactly like a missing one.
        var item = library.GetItemById<BaseItem>(itemId, user);
        if (item is null) return NotFound();
        if (item is not (Movie or Series)) return new ItemRatingsDto(null, []);
        var ids = new[] { item.Id, access.MainVersionOf(item.Id) ?? item.Id }.Distinct().ToArray();
        var candidates = await database.Entries.AsNoTracking()
            .Where(entry => entry.JellyfinItemId != null && ids.Contains(entry.JellyfinItemId.Value) ||
                database.EntryBindings.Any(binding => binding.EntryId == entry.Id && ids.Contains(binding.JellyfinItemId)))
            .OrderBy(entry => entry.Id).ToListAsync(cancellationToken);
        var entry = candidates.FirstOrDefault(candidate => access.CanRead(user, candidate));
        var settings = await ratings.ReadSettingsAsync(cancellationToken);
        return new ItemRatingsDto(entry?.Id, await ratings.ForNativeAsync(entry, item, settings, cancellationToken));
    }

    /// <summary>Queues one title's refresh inside the budget (202); 409 with a code when fetching cannot happen now.</summary>
    [HttpPost("Entries/{id:guid}/Ratings/Refresh"), Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<RatingsRefreshQueuedDto>> Refresh(Guid id, CancellationToken cancellationToken)
    {
        if (!readiness.IsReady) return StatusCode(503);
        var user = access.GetUser(User);
        if (user is null) return Unauthorized();
        var entry = await database.Entries.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (entry is null || !access.CanRead(user, entry)) return NotFound();
        var settings = await ratings.ReadSettingsAsync(cancellationToken);
        var state = await database.RatingsProviderStates.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == RatingsProviderState.SingletonId, cancellationToken) ?? new RatingsProviderState();
        var refusal = RatingsRefreshRunner.Refusal(settings, state, await secrets.HasAsync(settings.ApiKeyRef, cancellationToken),
            clock.GetUtcNow().UtcDateTime);
        if (refusal is null && !queue.TryEnqueue(id)) refusal = "queue_full";
        if (refusal is not null) return Conflict(new ProblemDetails { Status = 409, Type = refusal, Title = RefusalSentence(refusal) });
        return Accepted(new RatingsRefreshQueuedDto(true));
    }

    private async Task<RatingsSettingsDto> DtoAsync(RatingsSettings settings, CancellationToken cancellationToken)
    {
        var verified = settings.VerifiedRevision == settings.Revision && settings.VerifiedAt is not null;
        return new RatingsSettingsDto(settings.Enabled, await secrets.HasAsync(settings.ApiKeyRef, cancellationToken), settings.RefreshDays,
            settings.DailyBudget, RatingSources.ParseList(settings.DefaultSources), RatingSources.Known, verified,
            verified ? Utc(settings.VerifiedAt) : null, client.BaseAddress != RatingsEndpoint.Default, settings.Revision);
    }

    /// <summary>The sentence for each refusal code a manual refresh can meet.</summary>
    public static string RefusalSentence(string code) => code switch
    {
        "ratings_disabled" => "Ratings are turned off in the settings.",
        RatingsOutcomes.NotConfigured => "No MDBList key is saved yet.",
        RatingsOutcomes.Unauthorized => "MDBList refused the saved key; replace it in the settings first.",
        "breaker_open" => "Fetching is paused after provider errors or the provider's daily limit; try again later.",
        "budget_spent" => "Today's ratings budget is spent; try again tomorrow.",
        "queue_full" => "Too many refreshes are already waiting; try again shortly.",
        _ => "Ratings cannot be refreshed now."
    };

    private static DateTime? Utc(DateTime? value) => value is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;

    private static string? ValidateSecret(SecretChangeRequest change) => change.Action switch
    {
        "unchanged" when change.Value is null => null,
        "replace" when !string.IsNullOrWhiteSpace(change.Value) && change.Value.Length <= 1024 => null,
        "clear" when change.Value is null => null,
        _ => "invalid_secret_change"
    };

    private BadRequestObjectResult Invalid(string code, string? message = null) =>
        BadRequest(new ProblemDetails { Status = 400, Type = code, Title = message ?? "The configuration is not valid." });
}
