using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Services.Acquisition;

/// <summary>One quality profile preset shipped with the plugin, read from <c>Assets/quality-presets.json</c>.</summary>
public sealed record QualityPreset(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("default")] bool Default,
    [property: JsonPropertyName("qualities")] IReadOnlyList<string> Qualities,
    [property: JsonPropertyName("minimumBytesPerHour")] long? MinimumBytesPerHour,
    [property: JsonPropertyName("maximumBytesPerHour")] long? MaximumBytesPerHour,
    [property: JsonPropertyName("cutoff")] string? Cutoff,
    [property: JsonPropertyName("upgradeAllowed")] bool UpgradeAllowed,
    [property: JsonPropertyName("upgradeMode")] string UpgradeMode,
    [property: JsonPropertyName("minimumSeeders")] int? MinimumSeeders = null,
    [property: JsonPropertyName("prefer")] string? Prefer = null,
    [property: JsonPropertyName("phones")] bool Phones = false);

/// <summary>What adding the presets did: the profiles created and the presets left out, with why.</summary>
public sealed record QualityPresetsAdded(
    IReadOnlyList<QualityPreset> Added,
    IReadOnlyList<(QualityPreset Preset, string Reason)> Skipped,
    Guid? DefaultSet);

/// <summary>
/// The quality profile presets (P4.FU9, user 2026-10-15: "predefine 4K, 2K, 1080p and other quality profiles ... and set up
/// by default 4K downloaded with fallback to lower quality profiles"). A preset becomes an ordinary profile whose id is the
/// preset's own, so it can be edited or deleted like any other, and adding the presets again only adds the missing ones.
/// </summary>
/// <remarks>
/// Fallback needs no mechanism of its own: a profile lists its qualities best first, the evaluator scores each allowed
/// quality <c>(count - index) * 100</c>, which no other contribution (at most 45 points) can outweigh, and the picker and
/// automation take the first eligible release in score order. So the highest allowed quality on offer wins, and the next
/// one down wins when it is missing or every copy of it is rejected.
/// </remarks>
public static class QualityPresets
{
    /// <summary>Gets every preset, in the shipped order.</summary>
    public static IReadOnlyList<QualityPreset> All { get; } = Load();

    /// <summary>Gets the preset a fresh install uses as its default profile.</summary>
    public static QualityPreset Default => All.Single(preset => preset.Default);

    /// <summary>Gets the preset a fresh install uses as its quality profile for phones (Phase 12: the 1080p preset).</summary>
    public static QualityPreset Phones => All.Single(preset => preset.Phones);

    /// <summary>
    /// On a fresh install, one that has no quality profile and has never saved its acquisition settings, creates every preset
    /// and makes the default one the default profile. Anything else is left exactly as it is. Returns whether it seeded.
    /// </summary>
    public static async Task<bool> SeedFreshInstallAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        if (await database.AcquisitionQualityProfiles.AnyAsync(cancellationToken).ConfigureAwait(false)) return false;
        var settings = await database.AcquisitionSettings.SingleOrDefaultAsync(row => row.Id == AcquisitionSettings.SingletonId,
            cancellationToken).ConfigureAwait(false);
        if (settings is not null && (settings.DefaultQualityProfileId is not null || settings.Revision != 1)) return false;
        settings ??= database.AcquisitionSettings.Add(new AcquisitionSettings()).Entity;
        foreach (var preset in All) database.AcquisitionQualityProfiles.Add(ToProfile(preset));
        settings.DefaultQualityProfileId = Default.Id;
        settings.PhoneProfileId = Phones.Id;
        settings.Revision++;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Adds every preset that has no profile yet (by id), leaving every existing profile untouched. A preset whose name another
    /// profile already uses is skipped. When no default profile is set, the default preset (or, when it is skipped, nothing)
    /// becomes the default. The caller holds the settings gate.
    /// </summary>
    public static async Task<QualityPresetsAdded> AddMissingAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var existing = await database.AcquisitionQualityProfiles.AsNoTracking().Select(profile => new { profile.Id, profile.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = existing.Select(profile => profile.Id).ToHashSet();
        var names = existing.Select(profile => profile.Name).ToHashSet(StringComparer.Ordinal);
        var added = new List<QualityPreset>();
        var skipped = new List<(QualityPreset, string)>();
        foreach (var preset in All)
        {
            if (ids.Contains(preset.Id)) skipped.Add((preset, "already_added"));
            else if (names.Contains(preset.Name)) skipped.Add((preset, "name_in_use"));
            else
            {
                database.AcquisitionQualityProfiles.Add(ToProfile(preset));
                added.Add(preset);
            }
        }

        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, cancellationToken).ConfigureAwait(false);
        Guid? defaultSet = null;
        if (settings.DefaultQualityProfileId is null && (ids.Contains(Default.Id) || added.Any(preset => preset.Id == Default.Id)))
        {
            settings.DefaultQualityProfileId = Default.Id;
            settings.Revision++;
            defaultSet = Default.Id;
        }

        // With no quality profile for phones chosen, the 1080p preset becomes it, as the 4K one becomes the default.
        if (settings.PhoneProfileId is null && (ids.Contains(Phones.Id) || added.Any(preset => preset.Id == Phones.Id)))
        {
            settings.PhoneProfileId = Phones.Id;
            settings.Revision++;
        }

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new QualityPresetsAdded(added, skipped, defaultSet);
    }

    private static AcquisitionQualityProfile ToProfile(QualityPreset preset) => new()
    {
        Id = preset.Id,
        Name = preset.Name,
        QualitiesJson = JsonSerializer.Serialize(preset.Qualities),
        MinimumBytesPerHour = preset.MinimumBytesPerHour,
        MaximumBytesPerHour = preset.MaximumBytesPerHour,
        Cutoff = preset.Cutoff,
        UpgradeAllowed = preset.UpgradeAllowed,
        UpgradeMode = preset.UpgradeMode,
        MinimumSeeders = preset.MinimumSeeders,
        Prefer = preset.Prefer
    };

    /// <summary>Returns the preset a profile was created from, by id, or null.</summary>
    public static Guid? PresetIdOf(Guid profileId) => All.Any(preset => preset.Id == profileId) ? profileId : null;

    private static List<QualityPreset> Load()
    {
        using var stream = typeof(QualityPresets).Assembly.GetManifestResourceStream("JellyfinMod.quality-presets.json")
            ?? throw new InvalidOperationException("The quality presets resource is missing.");
        using var document = JsonDocument.Parse(stream);
        var presets = document.RootElement.GetProperty("presets").Deserialize<List<QualityPreset>>() ?? [];
        // The shipped data obeys the same rules as a profile an administrator saves.
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Qualities.Count == 0 ||
                preset.Qualities.Distinct(StringComparer.Ordinal).Count() != preset.Qualities.Count ||
                preset.Qualities.Any(quality => !QualityCatalog.IsKnown(quality)) ||
                preset.Cutoff is { } cutoff && !preset.Qualities.Contains(cutoff, StringComparer.Ordinal) ||
                preset.UpgradeAllowed && preset.Cutoff is null || preset.UpgradeMode is not ("replace" or "add") ||
                preset.MinimumBytesPerHour > preset.MaximumBytesPerHour || !ProfilePreferences.IsKnown(preset.Prefer))
                throw new InvalidOperationException($"The quality preset {preset.Name} is not valid.");
        }

        if (presets.Count(preset => preset.Default) != 1 || presets.Count(preset => preset.Phones) != 1 || presets.Select(preset => preset.Id).Distinct().Count() != presets.Count ||
            presets.Select(preset => preset.Name).Distinct(StringComparer.Ordinal).Count() != presets.Count)
            throw new InvalidOperationException("The quality presets need one default and unique ids and names.");
        return presets;
    }
}
