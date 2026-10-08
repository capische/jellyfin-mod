using Emby.Naming.Common;
using Emby.Naming.Video;

namespace JellyfinMod.Services.Import;

/// <summary>
/// How Jellyfin 12 would group a folder's videos into versions with one more file in it, through the host's own naming code
/// (<c>VideoListResolver</c>, the one its TV resolver uses), so a new version's name is checked before anything is linked
/// (Codex review of the user fixes, 2026-10-09).
/// </summary>
public static class JellyfinGrouping
{
    private static readonly NamingOptions Options = new();

    /// <summary>
    /// The main file and every member of the version group <paramref name="candidate"/> would join in
    /// <paramref name="folder"/>, or null when the folder cannot be read or the candidate would stand alone. Files the host's
    /// own scan leaves out (<paramref name="ignored"/>: its ignore patterns and configured rules, such as macOS <c>._</c>
    /// sidecars) never take part, as they never reach its resolver (Codex re-review 5 of the user fixes).
    /// </summary>
    public static (string Main, IReadOnlyList<string> Members)? MainOf(string folder, string candidate, string libraryRoot,
        Func<string, bool> ignored)
    {
        try
        {
            var paths = Directory.EnumerateFiles(folder).Where(path => !string.Equals(path, candidate, StringComparison.Ordinal) && !ignored(path))
                .Append(candidate);
            var videos = paths.Select(path => VideoResolver.Resolve(path, false, Options, true, libraryRoot)).OfType<VideoFileInfo>().ToList();
            var groups = new VideoListResolver(Options).Resolve(videos, true, true, libraryRoot, Jellyfin.Data.Enums.CollectionType.tvshows);
            foreach (var group in groups)
            {
                var members = group.Files.Select(file => file.Path)
                    .Concat(group.AlternateVersions.SelectMany(version => version.Files.Select(file => file.Path))).ToArray();
                if (members.Contains(candidate, StringComparer.Ordinal)) return (group.Files[0].Path, members);
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
