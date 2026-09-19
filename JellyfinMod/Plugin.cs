using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using JellyfinMod.Services;

namespace JellyfinMod;

/// <summary>
/// The JellyfinMod catalog plugin: one entry per title, with or without a media file.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // BasePlugin.DataFolderPath can gain a _<Version> suffix across upgrades, which would
        // orphan the database on every plugin update. Pin an explicit path instead.
        DataPath = Path.Combine(applicationPaths.DataPath, "jellyfinmod");
        Directory.CreateDirectory(DataPath);

        // An older configuration may still hold credentials in plain XML; move them out once (user decision 3).
        var loaded = Configuration;
        if (HasPlaintextSecret(loaded))
        {
            ProtectSecrets(loaded, loaded);
            SaveConfiguration();
        }
    }

    /// <summary>A submitted secret field with this value removes the stored secret.</summary>
    public const string ClearSecret = "__clear__";

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Secrets are write-only: a submitted value is stored outside the XML file before anything is saved, an
        // empty field keeps the stored value, and references always come from the server-side configuration.
        var incoming = (PluginConfiguration)configuration;
        ProtectSecrets(incoming, Configuration);
        base.UpdateConfiguration(incoming);
    }

    private static bool HasPlaintextSecret(PluginConfiguration configuration) =>
        configuration.TmdbReadAccessToken.Length > 0 || configuration.TmdbApiKey.Length > 0 ||
        configuration.TransmissionPassword.Length > 0;

    private void ProtectSecrets(PluginConfiguration incoming, PluginConfiguration current)
    {
        var store = new AcquisitionSecretStore(DataPath);
        incoming.TmdbReadAccessTokenRef = Protect(store, incoming.TmdbReadAccessToken, current.TmdbReadAccessTokenRef);
        incoming.TmdbReadAccessToken = string.Empty;
        incoming.TmdbApiKeyRef = Protect(store, incoming.TmdbApiKey, current.TmdbApiKeyRef);
        incoming.TmdbApiKey = string.Empty;
        incoming.TransmissionPasswordRef = Protect(store, incoming.TransmissionPassword, current.TransmissionPasswordRef);
        incoming.TransmissionPassword = string.Empty;
    }

    private static string? Protect(AcquisitionSecretStore store, string submitted, string? currentReference)
    {
        if (string.IsNullOrEmpty(submitted)) return currentReference;
        store.RemoveAsync(currentReference, CancellationToken.None).GetAwaiter().GetResult();
        return submitted == ClearSecret ? null : store.AddAsync(submitted, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyfinMod";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6f1a2b3c-4d5e-4f60-9a71-8b2c3d4e5f60");

    /// <inheritdoc />
    public override string Description =>
        "Track what you want to watch whether or not the file exists, acquire it, and reclaim the disk when you are done.";

    /// <summary>Gets the version-independent folder holding this plugin's database.</summary>
    public string DataPath { get; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    ];
}
