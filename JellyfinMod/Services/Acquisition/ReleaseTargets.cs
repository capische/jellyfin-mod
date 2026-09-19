using System.Text.Json;
using JellyfinMod.Data;

namespace JellyfinMod.Services.Acquisition;

/// <summary>Builds the search target of an entry or episode, shared by the picker and automation.</summary>
public static class ReleaseTargets
{
    /// <summary>Returns the target of one movie entry or one episode.</summary>
    public static ReleaseTarget For(Entry entry, Episode? episode)
    {
        var metadata = entry.MetadataJson is { } json ? JsonSerializer.Deserialize<TmdbMetadata>(json) : null;
        return new ReleaseTarget(entry.Id, episode?.Id, entry.MediaType, entry.Title, metadata?.OriginalTitle, entry.Year,
            episode?.SeasonNumber, episode?.EpisodeNumber,
            // An episode's own runtime only: a series average would be a guessed duration (P4.A4).
            entry.MediaType == "series" ? episode?.RuntimeMinutes : metadata?.RuntimeMinutes,
            entry.ImdbId ?? metadata?.ImdbId, entry.TmdbId, metadata?.TvdbId);
    }
}
