namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// The per-library switch "Show missing episodes as Jellyfin items" (P1.FU24): on for every TV library unless an administrator
/// turned it off (<see cref="PluginConfiguration.PlaceholderDisabledLibraryIds"/>; user, 2026-10-15: "should be enabled by
/// default. That's the point."). Kept in the XML configuration (a list of ids, no new table).
/// </summary>
public static class PlaceholderSettings
{
    private static readonly object Gate = new();

    /// <summary>The libraries an administrator turned off.</summary>
    public static IReadOnlySet<Guid> DisabledLibraries() =>
        (Plugin.Instance?.Configuration.PlaceholderDisabledLibraryIds ?? []).ToHashSet();

    /// <summary>Whether a library's switch is on: every library not turned off.</summary>
    public static bool IsEnabled(Guid libraryId) => !DisabledLibraries().Contains(libraryId);

    /// <summary>Turns a library's switch on or off and saves the configuration.</summary>
    public static void Set(Guid libraryId, bool enabled)
    {
        lock (Gate)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("The plugin is not loaded.");
            var configuration = plugin.Configuration;
            var ids = configuration.PlaceholderDisabledLibraryIds.Where(id => id != libraryId).ToList();
            if (!enabled) ids.Add(libraryId);
            configuration.PlaceholderDisabledLibraryIds = ids.ToArray();
            plugin.SaveConfiguration();
        }
    }
}
