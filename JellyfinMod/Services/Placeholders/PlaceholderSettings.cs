namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// The per-library switch "Show missing episodes as Jellyfin items" (P1.FU24), off by default: the TV libraries listed in
/// <see cref="PluginConfiguration.PlaceholderLibraryIds"/>. Kept in the XML configuration (a list of ids, no new table).
/// </summary>
public static class PlaceholderSettings
{
    private static readonly object Gate = new();

    /// <summary>The libraries whose switch is on.</summary>
    public static IReadOnlySet<Guid> EnabledLibraries() =>
        (Plugin.Instance?.Configuration.PlaceholderLibraryIds ?? []).ToHashSet();

    /// <summary>Whether a library's switch is on.</summary>
    public static bool IsEnabled(Guid libraryId) => EnabledLibraries().Contains(libraryId);

    /// <summary>Turns a library's switch on or off and saves the configuration.</summary>
    public static void Set(Guid libraryId, bool enabled)
    {
        lock (Gate)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded.");
            var configuration = plugin.Configuration;
            var ids = configuration.PlaceholderLibraryIds.Where(id => id != libraryId).ToList();
            if (enabled) ids.Add(libraryId);
            configuration.PlaceholderLibraryIds = ids.ToArray();
            plugin.SaveConfiguration();
        }
    }
}
