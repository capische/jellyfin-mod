using System.Text.Json;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Ratings;

/// <summary>
/// Reads and projects ratings (P9.R5): SQLite and the host's in-memory items only, never a provider call. One value per
/// source, by the precedence of plan decision 5; unknown raw sources are never exposed.
/// </summary>
public sealed class RatingsStore(ModDbContext database, HostRatingsReader host, TimeProvider clock)
{
    /// <summary>Gets the settings row, creating it with the defaults on first use.</summary>
    public static async Task<RatingsSettings> GetSettingsAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var settings = await database.RatingsSettings.SingleOrDefaultAsync(row => row.Id == RatingsSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (settings is not null) return settings;
        database.RatingsSettings.Add(new RatingsSettings());
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent first read created it.
            database.ChangeTracker.Clear();
        }

        return await database.RatingsSettings.SingleAsync(row => row.Id == RatingsSettings.SingletonId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the provider state row, creating it on first use.</summary>
    public static async Task<RatingsProviderState> GetStateAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var state = await database.RatingsProviderStates.SingleOrDefaultAsync(row => row.Id == RatingsProviderState.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (state is not null) return state;
        database.RatingsProviderStates.Add(new RatingsProviderState());
        try
        {
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
        }

        return await database.RatingsProviderStates.SingleAsync(row => row.Id == RatingsProviderState.SingletonId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the settings without creating or tracking anything; defaults when the row does not exist yet.</summary>
    public async Task<RatingsSettings> ReadSettingsAsync(CancellationToken cancellationToken) =>
        await database.RatingsSettings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == RatingsSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false) ?? new RatingsSettings();

    /// <summary>Every known source's value for each entry (and its bound or given native item).</summary>
    public async Task<Dictionary<Guid, IReadOnlyList<RatingDto>>> ForEntriesAsync(IReadOnlyCollection<Entry> entries, RatingsSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<RatingDto>>();
        if (!settings.Enabled || entries.Count == 0) return result;
        var ids = entries.Select(entry => entry.Id).ToArray();
        var stored = (await database.TitleRatings.AsNoTracking().Where(row => ids.Contains(row.EntryId)).ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(row => row.EntryId);
        foreach (var entry in entries)
            result[entry.Id] = Project(entry, stored[entry.Id], host.Read(entry.JellyfinItemId), settings);
        return result;
    }

    /// <summary>One entry's ratings.</summary>
    public async Task<IReadOnlyList<RatingDto>> ForEntryAsync(Entry entry, RatingsSettings settings, CancellationToken cancellationToken) =>
        (await ForEntriesAsync([entry], settings, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(entry.Id) ?? [];

    /// <summary>A native item's ratings: its entry's when it has one, plus what the host stored on the item itself.</summary>
    public async Task<IReadOnlyList<RatingDto>> ForNativeAsync(Entry? entry, BaseItem item, RatingsSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return [];
        var stored = entry is null ? [] : await database.TitleRatings.AsNoTracking().Where(row => row.EntryId == entry.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Project(entry, stored, host.Read(item), settings);
    }

    /// <summary>Merges the snapshot, stored and host values into one value per known source, in the complete display order.</summary>
    public IReadOnlyList<RatingDto> Project(Entry? entry, IEnumerable<TitleRating> stored, IReadOnlyList<HostRating> hosted, RatingsSettings settings)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var window = TimeSpan.FromDays(Math.Max(1, settings.RefreshDays));
        var candidates = new List<RatingDto>();
        if (Snapshot(entry) is { } tmdb)
            candidates.Add(new RatingDto(RatingSources.Tmdb, RatingSources.Round(tmdb.Score, RatingSources.Ten), RatingSources.Ten, tmdb.Votes,
                RatingSources.ProviderTmdb, null, null, false));
        foreach (var row in stored.Where(row => RatingSources.IsKnown(row.Source) && row.Scale != RatingSources.Unknown))
        {
            var at = DateTime.SpecifyKind(row.FetchedAt, DateTimeKind.Utc);
            candidates.Add(new RatingDto(row.Source, row.Value, row.Scale, row.Votes, row.Provider, at, row.Url, now - at > window));
        }

        foreach (var row in hosted)
            candidates.Add(new RatingDto(row.Source, row.Value, row.Scale, null, row.Provider, row.FetchedAt, null, false));
        var result = new List<RatingDto>();
        foreach (var source in RatingSources.Known)
        {
            foreach (var provider in RatingSources.Precedence(source))
            {
                if (candidates.FirstOrDefault(rating => rating.Source == source && rating.Provider == provider) is not { } chosen) continue;
                result.Add(chosen);
                break;
            }
        }

        return result;
    }

    /// <summary>The TMDB score and vote count of the entry's own snapshot, when it has a score (plan decision 4).</summary>
    private static (double Score, int? Votes)? Snapshot(Entry? entry)
    {
        if (entry?.MetadataJson is not { } json) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<TmdbMetadata>(json);
            return metadata?.CommunityRating is { } score && score is > 0 and <= 10 ? (score, metadata.VoteCount) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
