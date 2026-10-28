using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinMod.Data;

namespace JellyfinMod.Services.Acquisition;

/// <summary>One TV model of the TVs catalogue (Phase 12 M30), read from <c>Assets/tv-models.json</c>.</summary>
public sealed record TvModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("webOS")] string WebOs,
    [property: JsonPropertyName("dolbyVision")] bool DolbyVision,
    [property: JsonPropertyName("hdr10Plus")] bool Hdr10Plus,
    [property: JsonPropertyName("hlg")] bool Hlg,
    [property: JsonPropertyName("ddPlusAtmos")] bool DdPlusAtmos,
    [property: JsonPropertyName("dts")] bool Dts,
    [property: JsonPropertyName("trueHd")] bool TrueHd);

/// <summary>What every ticked TV plays: a capability counts only when all of them have it, since one best copy serves them all.</summary>
public sealed record TvCapabilities(bool DolbyVision, bool Hdr10Plus, bool DdPlusAtmos, bool Dts, bool TrueHd);

/// <summary>The TVs catalogue shipped with the plugin (Phase 12 M30; the first slice of PHASE8 E7's device catalogue).</summary>
public static class TvCatalog
{
    /// <summary>Gets every model, in the shipped order.</summary>
    public static IReadOnlyList<TvModel> All { get; } = Load();

    /// <summary>Whether an id names a catalogue model.</summary>
    public static bool IsKnown(string id) => All.Any(model => model.Id == id);

    /// <summary>The ticked ids stored in <see cref="AcquisitionSettings.TvModels"/>, in catalogue order, unknown ids dropped.</summary>
    public static IReadOnlyList<string> Ticked(string? stored)
    {
        var ids = (stored ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return All.Where(model => ids.Contains(model.Id)).Select(model => model.Id).ToArray();
    }

    /// <summary>What every ticked model plays, or null when none is ticked.</summary>
    public static TvCapabilities? Capabilities(string? stored)
    {
        var ticked = Ticked(stored).Select(id => All.Single(model => model.Id == id)).ToArray();
        return ticked.Length == 0 ? null : new TvCapabilities(ticked.All(model => model.DolbyVision), ticked.All(model => model.Hdr10Plus),
            ticked.All(model => model.DdPlusAtmos), ticked.All(model => model.Dts), ticked.All(model => model.TrueHd));
    }

    private static List<TvModel> Load()
    {
        using var stream = typeof(TvCatalog).Assembly.GetManifestResourceStream("JellyfinMod.tv-models.json")
            ?? throw new InvalidOperationException("The TVs catalogue resource is missing.");
        using var document = JsonDocument.Parse(stream);
        var models = document.RootElement.GetProperty("models").Deserialize<List<TvModel>>() ?? [];
        if (models.Count == 0 || models.Any(model => string.IsNullOrWhiteSpace(model.Id) || model.Id.Contains(',', StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(model.Name)) || models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != models.Count)
            throw new InvalidOperationException("The TVs catalogue needs models with unique ids and names.");
        return models;
    }
}

/// <summary>
/// The Prefer field's ordering of releases of the same quality (Phase 12 M30): two contributions, <c>prefer_picture</c> (a
/// picture tier times 8, at most 32) and <c>prefer_sound</c> (a sound tier, at most 7), read only from what the title parser
/// gives. The picture leads: one picture tier (8) outweighs any sound (7), so a Dolby Vision release with DTS stays above an
/// HDR10 one with Dolby Digital Plus, and sound decides between releases of the same picture. Together at most 39, they and
/// proper/repack, freeleech and seeders (at most 45) stay under one quality step (100), so a preference never lifts a release
/// over a better allowed quality, and nothing is rejected. A title that does not say adds nothing.
/// </summary>
public static class ReleasePreference
{
    /// <summary>The points of one picture tier.</summary>
    public const int PictureStep = 8;

    /// <summary>The picture and sound points of one parsed release under a profile's preference.</summary>
    public static (int Picture, int Sound) Points(string? prefer, TvCapabilities? tvs, ParsedRelease parsed) => prefer switch
    {
        ProfilePreferences.Tvs => (TvPicture(tvs, parsed.Hdr) * PictureStep, TvSound(tvs, parsed.Audio)),
        ProfilePreferences.Everywhere => (EverywherePicture(parsed.Hdr, parsed.Codec) * PictureStep, EverywhereSound(parsed.Audio)),
        _ => (0, 0)
    };

    private static bool Has(string? value, string word) =>
        value is not null && value.Split(' ').Contains(word, StringComparer.Ordinal);

    private static bool Dts(string? audio) => audio?.StartsWith("DTS", StringComparison.Ordinal) == true;

    /// <summary>
    /// Best on My TVs, picture tier: Dolby Vision 4, HDR10+ 3 (on TVs without it, 2 as its HDR10 layer), HDR10 2, HDR 1, SDR 0.
    /// A Dolby Vision release on TVs without Dolby Vision counts as its HDR10 layer, or as nothing when it has none.
    /// </summary>
    private static int TvPicture(TvCapabilities? tvs, string? hdr)
    {
        if (Has(hdr, "DV") && (tvs is null || tvs.DolbyVision)) return 4;
        if (Has(hdr, "HDR10+")) return tvs is null || tvs.Hdr10Plus ? 3 : 2;
        if (Has(hdr, "HDR10")) return 2;
        return Has(hdr, "HDR") ? 1 : 0;
    }

    /// <summary>
    /// Best on My TVs, sound tier: Dolby Digital Plus with Atmos 7, Dolby Digital Plus 6, Dolby Digital 5, AAC 4, FLAC or Opus 1,
    /// and TrueHD or DTS 0 unless every ticked TV decodes them (Q7). With no TV ticked, Atmos and lossless sound first: TrueHD
    /// Atmos 7, TrueHD and DTS:X 6, DTS-HD MA 5, FLAC and DD+ Atmos 4, DTS and DD+ 3, DD 2, AAC and Opus 1.
    /// </summary>
    private static int TvSound(TvCapabilities? tvs, string? audio)
    {
        var atmos = Has(audio, "Atmos");
        if (tvs is null)
        {
            if (Has(audio, "TrueHD")) return atmos ? 7 : 6;
            if (Has(audio, "DTS:X")) return 6;
            if (audio?.StartsWith("DTS-HD MA", StringComparison.Ordinal) == true) return 5;
            if (Has(audio, "DD+")) return atmos ? 4 : 3;
            if (Has(audio, "FLAC")) return 4;
            if (Dts(audio)) return 3;
            if (Has(audio, "DD")) return 2;
            return Has(audio, "AAC") || Has(audio, "Opus") ? 1 : 0;
        }

        if (Has(audio, "TrueHD")) return tvs.TrueHd ? (atmos ? 7 : 6) : 0;
        if (Dts(audio)) return tvs.Dts ? 5 : 0;
        if (Has(audio, "DD+")) return atmos && tvs.DdPlusAtmos ? 7 : 6;
        if (Has(audio, "DD")) return 5;
        if (Has(audio, "AAC")) return 4;
        return Has(audio, "FLAC") || Has(audio, "Opus") ? 1 : 0;
    }

    /// <summary>Plays Everywhere, picture tier: no HDR word and H.264 4, HEVC 3, an unnamed codec 2, another codec 1; HDR 0.</summary>
    private static int EverywherePicture(string? hdr, string? codec)
    {
        if (hdr is not null) return 0;
        return codec switch { "h264" => 4, "h265" => 3, null => 2, _ => 1 };
    }

    /// <summary>Plays Everywhere, sound tier: AAC, Dolby Digital and Dolby Digital Plus 7; FLAC or Opus 3; TrueHD and DTS 0.</summary>
    private static int EverywhereSound(string? audio)
    {
        if (Has(audio, "TrueHD") || Dts(audio)) return 0;
        if (Has(audio, "AAC") || Has(audio, "DD") || Has(audio, "DD+")) return 7;
        return Has(audio, "FLAC") || Has(audio, "Opus") ? 3 : 0;
    }
}
