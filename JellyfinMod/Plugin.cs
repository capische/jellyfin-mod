using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

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
