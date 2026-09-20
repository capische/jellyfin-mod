using System.Text.Json;
using System.Text.Json.Serialization;

namespace JellyfinMod.Services.Web;

/// <summary>
/// What a built JellyfinMod web bundle says about itself (P7.S3).
/// </summary>
/// <remarks>
/// Written by the fork's build into <c>jellyfinmod-web.json</c>. The plugin never guesses any of it: a bundle
/// that does not carry a readable manifest is not served at all.
/// </remarks>
public sealed class WebBundleManifest
{
    /// <summary>Gets or sets the bundle's identity: twelve hex characters over its own file list and contents.</summary>
    [JsonPropertyName("bundleId")]
    public string BundleId { get; set; } = string.Empty;

    /// <summary>Gets or sets the fork revision this bundle was built from.</summary>
    [JsonPropertyName("webCommit")]
    public string WebCommit { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was built.</summary>
    [JsonPropertyName("builtAt")]
    public string BuiltAt { get; set; } = string.Empty;

    /// <summary>Gets or sets how many files went into the identity.</summary>
    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    /// <summary>Gets the Health capability names this bundle expects; absent in an older build.</summary>
    /// <remarks>
    /// A private setter with <see cref="JsonIncludeAttribute"/> rather than a get-only property: the serializer
    /// leaves a get-only collection alone, which silently produced an empty list here.
    /// </remarks>
    [JsonInclude]
    [JsonPropertyName("expectsCapabilities")]
    public IList<string> ExpectsCapabilities { get; private set; } = [];

    /// <summary>Gets or sets the server versions this bundle is built for.</summary>
    [JsonPropertyName("supportedServer")]
    public SupportedServer? SupportedServer { get; set; }

    /// <summary>Reads a manifest, or null when it is absent or unreadable.</summary>
    public static WebBundleManifest? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<WebBundleManifest>(File.ReadAllText(path));
            return string.IsNullOrEmpty(manifest?.BundleId) ? null : manifest;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// How a bundle expresses which servers it runs on (PHASE7 §3.5, corrected 2026-09-20).
/// </summary>
/// <remarks>
/// A minimum and a list of versions someone actually ran, never a guessed upper bound. A host above the minimum
/// but outside the tested list runs and says so; refusing every version nobody has tried yet would turn each
/// host release into an outage.
/// </remarks>
public sealed class SupportedServer
{
    /// <summary>Gets or sets the lowest server version this bundle supports.</summary>
    [JsonPropertyName("minimum")]
    public string Minimum { get; set; } = string.Empty;

    /// <summary>Gets the server versions this bundle has actually been run against.</summary>
    [JsonInclude]
    [JsonPropertyName("testedOn")]
    public IList<string> TestedOn { get; private set; } = [];

    /// <summary>
    /// Whether this host counts as one the bundle has been run against.
    /// </summary>
    /// <remarks>
    /// Compared as versions, not as strings: a host reporting <c>12.0.0.0</c> is the <c>12.0.0</c> someone tested,
    /// and reporting it as untested because of a trailing zero would be noise an administrator learns to ignore.
    /// </remarks>
    public bool IsTested(string hostVersion)
    {
        if (TestedOn.Contains(hostVersion, StringComparer.OrdinalIgnoreCase)) return true;
        var host = Normalize(hostVersion);
        return host is not null && TestedOn.Any(tested => Normalize(tested) == host);
    }

    /// <summary>
    /// A version with every component present.
    /// </summary>
    /// <remarks>
    /// <see cref="Version"/> records components nobody wrote as -1, so a host reporting <c>12.0.0.0</c> and a
    /// manifest saying <c>12.0.0</c> compare as different versions. They are the same release, and telling an
    /// administrator their server is untested because of a trailing zero would be noise.
    /// </remarks>
    private static Version? Normalize(string value) => Version.TryParse(value, out var parsed)
        ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0))
        : null;
}
