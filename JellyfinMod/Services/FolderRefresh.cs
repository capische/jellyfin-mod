using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services;

/// <summary>
/// Makes Jellyfin hold files and folders the plugin has just written or removed, at once (P4.FU12), instead of after its library
/// monitor delay (60 s on the hosts). Shared by the placeholder writer, the delete repair and the import.
/// </summary>
public interface IMediaDiscovery
{
    /// <summary>
    /// Asks Jellyfin to pick up the changes at <paramref name="paths"/> (files or folders, some of which may be gone) in a library.
    /// Requests that arrive together are one discovery. The task completes when Jellyfin holds the changes, or when the attempt
    /// gave up (never faulted: the callers also report the change to the library monitor, which still applies).
    /// </summary>
    Task DiscoverAsync(Guid libraryId, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes once every discovery of the library asked for before this call is over, so a removal that follows cannot be
    /// overtaken by a discovery still making items for what it removes (P4.FU12: Remove Placeholders during the first sync).
    /// A discovery asked for before this call and put off until a library scan ends is dropped (the library monitor still
    /// picks its paths up).
    /// </summary>
    Task IdleAsync(Guid libraryId);
}

/// <summary>
/// The discovery (P4.FU12), done by Jellyfin's own folder validation, awaited and bounded to what changed, one batch at a time.
/// Never <c>ChangedExternally</c> on a library folder, which refreshes every title in it, and never an item built by hand.
/// For each changed path the nearest folder Jellyfin holds an item for decides:
/// <list type="bullet">
/// <item>a season (an episode or its stub swapped or written): that season, validated once for all the paths in it;</item>
/// <item>a series (a new season folder): the series is validated for the season, then the season for its files;</item>
/// <item>the library's folder (a series or movie folder written or recreated), or no item at all (the folder item itself is
/// missing: Jellyfin's top library folders are validated once, which is shallow): the library folder is validated for the new
/// title folder, and a new series then validated in full, since all of it is new.</item>
/// </list>
/// A validation refreshes only the metadata of the paths named (<c>RefreshPaths</c>) in everything that was already there. The
/// callers report the change to the library monitor as well, so a path this cannot place is found by Jellyfin in the end.
/// </summary>
public sealed class FolderRefresh(ILibraryManager library, IFileSystem fileSystem, ILogger<FolderRefresh> logger) : IMediaDiscovery, IHostedService
{
    private sealed record Request(Guid LibraryId, IReadOnlyCollection<string> Paths, TaskCompletionSource Done, int Generation);

    /// <summary>How long after a request the batch is taken, so the files of one moment (a pack's) are one look.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(150);

    private readonly Channel<Request> queue = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Request, byte> open = new();
    // Each library's removal generation, raised by IdleAsync: a discovery put off past a scan is asked for again only while the
    // generation it was asked in still holds, under this lock, so it is either open before a removal's wait or never asked.
    private readonly Dictionary<Guid, int> generations = [];
    private CancellationTokenSource? stopping;
    private Task? worker;

    /// <inheritdoc />
    public Task DiscoverAsync(Guid libraryId, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Request request;
        lock (generations) request = new Request(libraryId, paths.ToArray(), done, generations.GetValueOrDefault(libraryId));
        open[request] = 0;
        _ = done.Task.ContinueWith(_ => open.TryRemove(request, out var _), TaskScheduler.Default);
        if (!queue.Writer.TryWrite(request)) done.TrySetResult();
        return done.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task IdleAsync(Guid libraryId)
    {
        lock (generations) generations[libraryId] = generations.GetValueOrDefault(libraryId) + 1;
        return Task.WhenAll(open.Keys.Where(request => request.LibraryId == libraryId).Select(request => request.Done.Task));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(() => ProcessAsync(stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        var source = Interlocked.Exchange(ref stopping, null);
        if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null) await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Everything that arrived while the last batch ran, and within the window after the first, is one batch: a
                // pack's files, a season's stubs, a series.
                await Task.Delay(Window, cancellationToken).ConfigureAwait(false);
                var batch = new List<Request>();
                while (queue.Reader.TryRead(out var request)) batch.Add(request);
                foreach (var group in batch.GroupBy(request => request.LibraryId))
                {
                    var paths = group.SelectMany(request => request.Paths).Distinct(StringComparer.Ordinal).ToArray();
                    try
                    {
                        // A running native scan may have read these folders already, and a validation now would compete with it:
                        // the paths are discovered once it ends. Nobody waits for that, since a caller may hold the library
                        // lease the scan's own post-scan reconciliation needs.
                        if (library.IsScanRunning)
                            foreach (var request in group) DiscoverAfterScan(request, cancellationToken);
                        else await DiscoverLibraryAsync(group.Key, paths, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        logger.LogWarning("JellyfinMod could not make Jellyfin hold {Count} changed path(s) of library {Library} now; its library monitor still will: {Error}",
                            group.Sum(request => request.Paths.Count), group.Key, error.Message);
                    }
                    finally
                    {
                        foreach (var request in group) request.Done.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Asks for a request's paths again once the running scan has ended (polled every 2 s, for at most an hour), unless a removal
    /// waited for the library's discoveries since it was asked for.
    /// </summary>
    private void DiscoverAfterScan(Request deferred, CancellationToken cancellationToken) =>
        _ = Task.Run(async () =>
        {
            try
            {
                for (var waited = 0; waited < 1800 && library.IsScanRunning; waited++)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                Task? again = null;
                lock (generations)
                    if (generations.GetValueOrDefault(deferred.LibraryId) == deferred.Generation)
                        again = DiscoverAsync(deferred.LibraryId, deferred.Paths, cancellationToken);
                if (again is not null) await again.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Stopping, or the scan state unreadable: the library monitor still picks the paths up.
            }
        }, CancellationToken.None);

    private async Task DiscoverLibraryAsync(Guid libraryId, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var info = library.GetVirtualFolders().FirstOrDefault(folder => Guid.TryParse(folder.ItemId, out var id) && id == libraryId);
        var roots = (info?.Locations ?? []).Select(Path.TrimEndingDirectorySeparator).ToArray();
        var tv = info?.CollectionType == CollectionTypeOptions.tvshows;
        // What each folder Jellyfin holds is to be told about, gathered across the paths so a pack is one look.
        var seasons = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seriesList = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var newSeasons = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var tops = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var started = DateTime.UtcNow;
        foreach (var raw in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(raw);
            var root = roots.FirstOrDefault(candidate => MediaStorageIdentity.Contains(candidate, path));
            if (root is null || string.Equals(path, root, StringComparison.Ordinal)) continue;
            // The nearest folder of the path Jellyfin holds an item for, walking up from the deepest folder that exists.
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (folder is not null && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
            BaseItem? nearest = null;
            string? nearestPath = null;
            for (var candidate = folder; candidate is not null && MediaStorageIdentity.Contains(root, candidate); candidate = Path.GetDirectoryName(candidate))
            {
                if (library.FindByPath(candidate, true) is not { } item) continue;
                nearest = item;
                nearestPath = candidate;
                break;
            }

            if (nearest is Season && nearestPath is not null)
            {
                Add(seasons, nearestPath, path);
            }
            else if (nearest is Series && nearestPath is not null)
            {
                // A season folder Jellyfin holds no item for: the series is validated for it, then the season for its files.
                var seasonFolder = ChildUnder(nearestPath, path);
                Add(seriesList, nearestPath, seasonFolder ?? path);
                if (seasonFolder is not null && !string.Equals(seasonFolder, path, StringComparison.Ordinal)) Add(newSeasons, seasonFolder, path);
            }
            else
            {
                Add(tops, root, path);
            }
        }

        foreach (var (seasonFolder, changed) in seasons)
            await ValidateAsync(seasonFolder, changed, cancellationToken).ConfigureAwait(false);
        foreach (var (seriesFolder, changed) in seriesList)
            await ValidateAsync(seriesFolder, changed, cancellationToken).ConfigureAwait(false);
        foreach (var (seasonFolder, changed) in newSeasons)
            await ValidateAsync(seasonFolder, changed, cancellationToken).ConfigureAwait(false);
        foreach (var (root, changed) in tops)
            await DiscoverUnderLibraryFolderAsync(root, changed, tv, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("JellyfinMod made Jellyfin hold {Paths} changed path(s) of library {Library}: {Seasons} season(s), {Series} series, {Tops} library folder look(s) in {Seconds:F1} s",
            paths.Count, libraryId, seasons.Count + newSeasons.Count, seriesList.Count, tops.Count, (DateTime.UtcNow - started).TotalSeconds);
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string path)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(path);
    }

    /// <summary>The first folder below <paramref name="parent"/> on the way to <paramref name="path"/>, or null.</summary>
    private static string? ChildUnder(string parent, string path)
    {
        if (!path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var next = path[(parent.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
        return Path.Combine(parent, next);
    }

    private async Task ValidateAsync(string folderPath, IReadOnlyCollection<string> refreshPaths, CancellationToken cancellationToken)
    {
        if (library.FindByPath(folderPath, true) is not Folder folder) return;
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
        {
            // Only what changed is refreshed in what was already there; a path not yet an item is refreshed when it is created.
            RefreshPaths = refreshPaths.ToArray()
        };
        await folder.ValidateChildren(new Progress<double>(), options, false, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A title folder written in a library folder: the library folder is validated for it (shallow), then a new series in full. Without
    /// an item for the library folder itself (it was empty at the last scan), Jellyfin's top folders are validated once to make it.
    /// </summary>
    private async Task DiscoverUnderLibraryFolderAsync(string root, IReadOnlyCollection<string> changed, bool tv, CancellationToken cancellationToken)
    {
        if (library.FindByPath(root, true) is not Folder)
        {
            await library.ValidateTopLibraryFolders(cancellationToken, false).ConfigureAwait(false);
            if (library.FindByPath(root, true) is not Folder)
            {
                logger.LogInformation("JellyfinMod found no Jellyfin item for the library folder {Folder} after validating the top folders; the library monitor will find the new title", root);
                library.QueueLibraryScan();
                return;
            }
        }

        var titleFolders = changed.Select(path => ChildUnder(root, path)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        await ValidateAsync(root, tv ? titleFolders : changed, cancellationToken).ConfigureAwait(false);
        if (!tv) return;
        // A series that was not there before is new down to its episodes: validated in full, and refreshed in full.
        foreach (var titleFolder in titleFolders)
        {
            if (library.FindByPath(titleFolder, true) is not Series created) continue;
            var options = new MetadataRefreshOptions(new DirectoryService(fileSystem));
            await created.ValidateChildren(new Progress<double>(), options, true, false, cancellationToken).ConfigureAwait(false);
        }
    }
}
