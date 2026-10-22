using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Placeholders;

/// <summary>
/// The one-time clean-up of the stubs line's placeholder files (P1.FU30; review P1-1, 2026-10-22). A server that ran the stubs
/// line holds the plugin's <c>.disc</c> stubs and Jellyfin's items for them; left there, a movie stub would read as the title's
/// file and every stubbed episode would get a second placeholder. So at start, before this line makes any item
/// (<see cref="WaitAsync"/>), every <c>.disc</c> file in a TV or movie library whose first line is the stubs' marker is removed,
/// with the season and title folders that left empty (the shapes the stubs line made and removed), and Jellyfin's items of the
/// removed stubs and folders. Nothing else is ever touched: a <c>.disc</c> without the marker, a link, a file over 4 KB, a
/// folder with anything left in it, a library folder. Done once: recorded in the configuration; a run that could not remove
/// every stub runs again at the next start, and until then the title source still never takes a marked stub for media.
/// </summary>
public sealed class PlaceholderStubCleanup(ILibraryManager library, ILogger<PlaceholderStubCleanup> logger) : IHostedService
{
    /// <summary>The first line of every stub the stubs line wrote; a file without it is never the plugin's.</summary>
    public const string Marker = "<!-- JellyfinMod placeholder v1 -->";

    /// <summary>A stub is far below this; a larger <c>.disc</c> file is never the plugin's.</summary>
    private const int MaxBytes = 4096;

    /// <summary>How long a sync waits for the clean-up before it defers its items to the next run.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the clean-up has run (or had nothing to do); until then no placeholder item is made.</summary>
    public static bool IsDone => Finished.Task.IsCompleted;

    /// <summary>Waits for the clean-up, at most a minute; false when it has not finished (the caller makes no item now).</summary>
    public static async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Finished.Task.WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> is one of the stubs line's stubs: a regular <c>.disc</c> file (not a link) of at
    /// most 4 KB whose first line is the marker. Read only; an unreadable file is not a stub.
    /// </summary>
    public static bool IsStubFile(string? path)
    {
        if (path is null || !path.EndsWith(".disc", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaxBytes) return false;
            var expected = Encoding.UTF8.GetBytes(Marker);
            var buffer = new byte[expected.Length];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) return false;
                read += count;
            }

            return buffer.AsSpan().SequenceEqual(expected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(Run, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs the clean-up now unless it was done before; returns the stubs removed and those that could not be.</summary>
    public (int Removed, int Failed) Run()
    {
        try
        {
            if (PlaceholderSettings.StubsRemoved()) return (0, 0);
            var (removed, failed) = RemoveAll();
            if (failed == 0) PlaceholderSettings.MarkStubsRemoved();
            if (removed + failed > 0)
                logger.LogInformation("JellyfinMod removed {Removed} placeholder stub file(s) of its earlier stubs line, {Failed} could not be removed{Again}",
                    removed, failed, failed > 0 ? " (tried again at the next start)" : string.Empty);
            return (removed, failed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not clean up the stub files of its earlier stubs line; it tries again at the next start: {Error}",
                error.Message);
            return (0, 1);
        }
        finally
        {
            Finished.TrySetResult();
        }
    }

    private (int Removed, int Failed) RemoveAll()
    {
        int removed = 0, failed = 0;
        foreach (var folder in library.GetVirtualFolders() ?? [])
        {
            if (folder.CollectionType is not (CollectionTypeOptions.tvshows or CollectionTypeOptions.movies)) continue;
            foreach (var root in folder.Locations ?? [])
            {
                if (!Directory.Exists(root)) continue;
                // A stub lies in a title's folder (a movie, or a series beside its seasons) or in a season folder: never deeper,
                // never loose in the library folder.
                foreach (var title in Folders(root))
                {
                    foreach (var season in Folders(title))
                    {
                        Clean(season, ref removed, ref failed);
                        if (IsSeasonFolderName(Path.GetFileName(season))) DropFolder(season);
                    }

                    Clean(title, ref removed, ref failed);
                    if (Path.GetFileName(title).Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase)) DropFolder(title);
                }
            }
        }

        return (removed, failed);
    }

    /// <summary>The real folders inside a folder (no links out of it).</summary>
    private IEnumerable<string> Folders(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent).Where(path => new DirectoryInfo(path).LinkTarget is null).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("JellyfinMod could not list {Folder} for stub files: {Error}", parent, error.Message);
            return [];
        }
    }

    /// <summary>Removes the marked stubs in one folder, and Jellyfin's items for them.</summary>
    private void Clean(string folder, ref int removed, ref int failed)
    {
        string[] stubs;
        try
        {
            stubs = Directory.EnumerateFiles(folder, "*.disc").Where(IsStubFile).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var stub in stubs)
        {
            var item = Find(stub, false);
            try
            {
                // Checked again just before: only a marked stub is ever deleted.
                if (!IsStubFile(stub)) continue;
                File.Delete(stub);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogWarning("JellyfinMod could not remove its old placeholder stub {Path}: {Error}", stub, error.Message);
                continue;
            }

            if (item is Video { IsPlaceHolder: true }) DeleteItem(item);
        }
    }

    /// <summary>Removes a season or title folder the removal left empty, and Jellyfin's item for it.</summary>
    private void DropFolder(string folder)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
            var item = Find(folder, true);
            Directory.Delete(folder, false);
            if (item is Season or Series or Video) DeleteItem(item);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("JellyfinMod left the folder {Folder} of its old stubs: {Error}", folder, error.Message);
        }
    }

    private BaseItem? Find(string path, bool isFolder)
    {
        try
        {
            return library.FindByPath(path, isFolder);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _ = error;
            return null;
        }
    }

    private void DeleteItem(BaseItem item)
    {
        try
        {
            library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogDebug("JellyfinMod could not delete the Jellyfin item of an old stub; its library scan will: {Error}", error.Message);
        }
    }

    private static bool IsSeasonFolderName(string name) =>
        name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase);
}
