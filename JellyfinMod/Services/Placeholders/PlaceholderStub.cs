using System.Collections.Concurrent;
using System.Security;
using System.Text;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// The plugin's placeholder for a missing episode (P1.FU24): a Jellyfin media stub, a small <c>.disc</c> file that Jellyfin 12
/// resolves to a real episode with <c>IsPlaceHolder</c>, never probes and gives full TMDB metadata. Its body is Kodi's media-stub
/// message (<c>&lt;discstub&gt;</c>, which Kodi shows instead of playing) after one marker line, so the plugin recognises its
/// own stubs by content and never touches another <c>.disc</c> file (a user's own disc stub, a Blu-ray marker).
/// </summary>
public static class PlaceholderStub
{
    /// <summary>The stub's extension, one of Jellyfin's stub types (<c>NamingOptions.StubFileExtensions</c>).</summary>
    public const string Extension = "disc";

    /// <summary>The first line of every stub the plugin writes; a file without it is never the plugin's.</summary>
    public const string Marker = "<!-- JellyfinMod placeholder v1 -->";

    /// <summary>A stub is far below this; a larger <c>.disc</c> file is not read and is never the plugin's.</summary>
    private const int MaxBytes = 4096;

    private static readonly ConcurrentDictionary<string, (DateTime Modified, long Length, bool IsStub)> Seen = new(StringComparer.Ordinal);

    /// <summary>The stub's body: the marker, then Kodi's stub message naming the episode.</summary>
    public static byte[] Body(string seriesTitle, int season, int episode, string? episodeTitle)
    {
        var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seriesTitle} S{season:00}E{episode:00}") +
            (string.IsNullOrWhiteSpace(episodeTitle) ? string.Empty : " - " + episodeTitle);
        var text = Marker + "\n<discstub>\n  <title>" + SecurityElement.Escape(name) + "</title>\n" +
            "  <message>Not downloaded yet. Open JellyfinMod to get it.</message>\n</discstub>\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        // A very long title is cut rather than letting the stub grow past what the reader accepts.
        return bytes.Length < MaxBytes ? bytes : Encoding.UTF8.GetBytes(Marker + "\n<discstub><message>Not downloaded yet.</message></discstub>\n");
    }

    /// <summary>A movie's stub body (P1.FU26): the marker, then Kodi's stub message naming the movie.</summary>
    public static byte[] MovieBody(string title, int? year)
    {
        var name = title + (year is { } value ? " (" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : string.Empty);
        var bytes = Encoding.UTF8.GetBytes(Marker + "\n<discstub>\n  <title>" + SecurityElement.Escape(name) + "</title>\n" +
            "  <message>Not downloaded yet. Open JellyfinMod to get it.</message>\n</discstub>\n");
        return bytes.Length < MaxBytes ? bytes : Encoding.UTF8.GetBytes(Marker + "\n<discstub><message>Not downloaded yet.</message></discstub>\n");
    }

    /// <summary>Whether a path names a <c>.disc</c> file at all (cheap; no I/O).</summary>
    public static bool HasStubExtension(string? path) =>
        path is not null && path.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the file at <paramref name="path"/> is one of the plugin's stubs: a regular <c>.disc</c> file (not a link) under
    /// 4 KB whose first line is the marker. An unreadable file is not a stub. Answers are kept per path, size and modification
    /// time, so the response filter reads each stub once.
    /// </summary>
    public static bool IsStubFile(string? path)
    {
        if (!HasStubExtension(path)) return false;
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaxBytes) return false;
            if (Seen.TryGetValue(path!, out var known) && known.Modified == info.LastWriteTimeUtc && known.Length == info.Length)
                return known.IsStub;
            var isStub = StartsWithMarker(path!);
            if (Seen.Count > 50_000) Seen.Clear();
            Seen[path!] = (info.LastWriteTimeUtc, info.Length, isStub);
            return isStub;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool StartsWithMarker(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
        var expected = Encoding.UTF8.GetBytes(Marker);
        var buffer = new byte[expected.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) return false;
            read += count;
        }

        return buffer.AsSpan().SequenceEqual(expected);
    }

    /// <summary>Forgets what is known about a path (it was written or removed).</summary>
    public static void Forget(string path) => Seen.TryRemove(path, out _);
}
