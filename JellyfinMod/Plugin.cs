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
            var store = new AcquisitionSecretStore(DataPath);
            var plaintext = (loaded.TmdbReadAccessToken, loaded.TmdbApiKey, loaded.TransmissionPassword,
                loaded.TmdbReadAccessTokenRef, loaded.TmdbApiKeyRef, loaded.TransmissionPasswordRef);
            var change = ProtectSecrets(store, loaded, loaded);
            try
            {
                SaveConfiguration();
            }
            catch
            {
                // The XML still holds the plaintext and the old references: keep them, drop what was stored for nothing.
                (loaded.TmdbReadAccessToken, loaded.TmdbApiKey, loaded.TransmissionPassword, loaded.TmdbReadAccessTokenRef,
                    loaded.TmdbApiKeyRef, loaded.TransmissionPasswordRef) = plaintext;
                Retire(store, change.Added);
                throw;
            }

            Retire(store, change.Retired);
        }
    }

    /// <summary>A submitted secret field with this value removes the stored secret.</summary>
    public const string ClearSecret = "__clear__";

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Secrets are write-only: a submitted value is stored outside the XML file before anything is saved, an
        // empty field keeps the stored value, and references always come from the server-side configuration.
        // A replaced secret is retired only once the configuration naming its replacement is saved; a failed save restores
        // the previous configuration and keeps its secrets (whole-review chunk 3c, P2 3).
        // The whole read, protect, save and retire sequence is one step: two saves that read the same previous
        // configuration could otherwise save a reference the other has just retired (Codex round 2 P2).
        lock (_configurationGate)
        {
            var incoming = (PluginConfiguration)configuration;
            var previous = Configuration;
            var store = new AcquisitionSecretStore(DataPath);
            var change = ProtectSecrets(store, incoming, previous);
            try
            {
                base.UpdateConfiguration(incoming);
            }
            catch
            {
                Configuration = previous;
                Retire(store, change.Added);
                throw;
            }

            Retire(store, change.Retired);
        }
    }

    private readonly object _configurationGate = new();

    private static bool HasPlaintextSecret(PluginConfiguration configuration) =>
        configuration.TmdbReadAccessToken.Length > 0 || configuration.TmdbApiKey.Length > 0 ||
        configuration.TransmissionPassword.Length > 0;

    /// <summary>Secrets stored for a configuration, and the ones it replaces, retired only after it is saved.</summary>
    private sealed record SecretChange(List<string> Added, List<string> Retired);

    private static SecretChange ProtectSecrets(AcquisitionSecretStore store, PluginConfiguration incoming, PluginConfiguration current)
    {
        var change = new SecretChange([], []);
        incoming.TmdbReadAccessTokenRef = Protect(store, incoming.TmdbReadAccessToken, current.TmdbReadAccessTokenRef, change);
        incoming.TmdbReadAccessToken = string.Empty;
        incoming.TmdbApiKeyRef = Protect(store, incoming.TmdbApiKey, current.TmdbApiKeyRef, change);
        incoming.TmdbApiKey = string.Empty;
        incoming.TransmissionPasswordRef = Protect(store, incoming.TransmissionPassword, current.TransmissionPasswordRef, change);
        incoming.TransmissionPassword = string.Empty;
        return change;
    }

    /// <summary>Stores a submitted secret (or clears it) without touching the one it replaces, which the change retires later.</summary>
    private static string? Protect(AcquisitionSecretStore store, string submitted, string? currentReference, SecretChange change)
    {
        if (string.IsNullOrEmpty(submitted)) return currentReference;
        if (currentReference is not null) change.Retired.Add(currentReference);
        if (submitted == ClearSecret) return null;
        var added = store.AddAsync(submitted, CancellationToken.None).GetAwaiter().GetResult();
        change.Added.Add(added);
        return added;
    }

    private static void Retire(AcquisitionSecretStore store, IEnumerable<string> references)
    {
        foreach (var reference in references) store.RemoveAsync(reference, CancellationToken.None).GetAwaiter().GetResult();
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
