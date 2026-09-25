using System.Text.Json.Serialization;

namespace JellyfinMod.Api.Contracts;

/// <summary>Whether watch history for one title arrived from Trakt for the requesting user (P7.Q16).</summary>
/// <param name="Installed">True while the stock Trakt plugin is installed and active.</param>
/// <param name="HasHistory">
/// True when JellyfinMod saw Trakt's history import for this movie or episode, or for any visible episode of this
/// series or season, for the requesting user. Always false while the plugin is absent.
/// </param>
/// <param name="LastSyncedAt">When that history last arrived, in UTC, or null.</param>
public sealed record TraktItemStatusDto(
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("hasHistory")] bool HasHistory,
    [property: JsonPropertyName("lastSyncedAt")] DateTime? LastSyncedAt);
