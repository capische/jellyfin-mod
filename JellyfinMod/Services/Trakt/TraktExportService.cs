using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Trakt;

/// <summary>Why JellyfinMod will not start the Trakt plugin's export (Phase 13).</summary>
public static class TraktExportRefusal
{
    /// <summary>The stock Trakt plugin is not installed and active.</summary>
    public const string NotInstalled = "not_installed";

    /// <summary>The installed Trakt plugin is older than <see cref="TraktPluginState.MinimumExportVersion"/> (Q6).</summary>
    public const string OldPlugin = "old_plugin";

    /// <summary>The plugin's export task or its settings could not be read with the expected shape.</summary>
    public const string Unknown = "unknown";

    /// <summary>No Jellyfin user is linked to Trakt with a working sign-in.</summary>
    public const string NoUsers = "no_users";

    /// <summary>A linked user has "post unwatched history" on, so the export would remove Trakt plays.</summary>
    public const string UnwatchedHistory = "unwatched_history";
}

/// <summary>What the Settings section and the Home notice show (Phase 13, P13.T3).</summary>
public sealed record TraktExportStatus(
    bool Installed,
    string? Version,
    bool AutoExport,
    bool Running,
    string? Refusal,
    TaskResult? LastRun,
    bool? LastRunClean,
    DateTime? NextAttemptAt,
    int Failures,
    IReadOnlyList<(TraktLink Link, string Name)> Users);

/// <summary>
/// Decides when the stock Trakt plugin's own export runs (Phase 13). JellyfinMod never talks to Trakt: the plugin stays the
/// only Trakt client and writer, and its export sends only episodes played here that have no play on Trakt, with
/// Jellyfin's <c>LastPlayedDate</c>.
/// </summary>
/// <remarks>
/// <para>Correctness comes from running the export again, not from proving a run delivered: the export compares Jellyfin's
/// plays with Trakt's every time, so any later successful run sends whatever an earlier one missed. It is started (with the
/// switch on) a quiet period after a linked user's latest episode play, when an account is linked again after a failed
/// sign-in, once a day as a safety net, and after a run that was not clean; Sync Missing Watches Now starts it any time.</para>
/// <para>A run's result only paces the retries and tells the administrator: clean when the host says Completed and the
/// plugin logged no Trakt error (<c>Trakt.ScheduledTasks.SyncLibraryTask</c>, <c>Trakt.Api.TraktApi</c>, ERR or FTL) in the
/// host's file log between the run's start and a few seconds after its end; the plugin completes its task even when Trakt
/// refuses a write. Anything else is retried after 15 minutes, then 30, 60, 120, 240 and at most 360. What that loses: a
/// missed or misread run (an unrelated error in the same moments, a run across a log rotation) delays the missing watches
/// until the next export, at most a day, and never loses them. The pace is in memory; a restart judges the last run again
/// from the log.</para>
/// </remarks>
public sealed class TraktExportService(
    TraktPluginState trakt,
    ITaskManager taskManager,
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    IApplicationPaths paths,
    ILogger<TraktExportService> logger) : IHostedService, IDisposable
{
    private static readonly int[] RetrySteps = [1, 2, 4, 8, 16, 24];
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    // The plugin's log lines reach the file through an asynchronous sink: a run is judged once this has passed after its end.
    private static readonly TimeSpan LogSettle = TimeSpan.FromSeconds(5);
    private static readonly Regex ErrorLine = new(@"^\[(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\] \[(?:ERR|FTL)\] \[\d+\] (?:Trakt\.ScheduledTasks\.SyncLibraryTask|Trakt\.Api\.TraktApi):", RegexOptions.Compiled);
    private readonly object _gate = new();
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private int _failures;
    private DateTime? _nextAttemptAt;
    private DateTime? _judgedStart;
    private bool _judgedClean;
    private bool _relinked;
    private readonly HashSet<Guid> _reconnecting = [];

    private static int TestMinutes => Plugin.Instance?.Configuration.TraktExportTestMinutes ?? 0;

    private static TimeSpan Quiet => TestMinutes > 0 ? TimeSpan.FromMinutes(TestMinutes) : TimeSpan.FromMinutes(10);

    private static TimeSpan RetryBase => TestMinutes > 0 ? TimeSpan.FromMinutes(TestMinutes) : TimeSpan.FromMinutes(15);

    /// <summary>Gets a value indicating whether the administrator turned the automatic export on.</summary>
    public static bool AutoExport => Plugin.Instance?.Configuration.TraktAutoExport == true;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TestMinutes > 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    Tick();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    logger.LogWarning("JellyfinMod's Trakt export check failed; it tries again: {Error}", error.Message);
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is null) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    /// <summary>Describes the export for an administrator.</summary>
    public TraktExportStatus Describe()
    {
        var info = trakt.Describe();
        var worker = info.Installed ? trakt.FindExportTask() : null;
        var links = info.Installed ? trakt.ReadLinks() : null;
        var named = (links ?? [])
            .Select(link => (Link: link, Name: users.GetUserById(link.UserId)?.Username))
            .Where(pair => pair.Name is not null)
            .Select(pair => (pair.Link, pair.Name!))
            .OrderBy(pair => pair.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var last = worker?.LastExecutionResult;
        lock (_gate)
        {
            // Only the background check judges a run (it reads the log); a status read answers what it found, or null.
            bool? clean = last is not null && _judgedStart == Utc(last.StartTimeUtc) ? _judgedClean : null;
            return new TraktExportStatus(info.Installed, info.Version, AutoExport, worker?.State is TaskState.Running or TaskState.Cancelling,
                RefusalFor(info, worker, links), last, clean, AutoExport ? _nextAttemptAt : null, _failures, named);
        }
    }

    /// <summary>Starts the export now (Sync Missing Watches Now), or joins the run already going. Returns a refusal or null.</summary>
    public string? RunNow()
    {
        var info = trakt.Describe();
        var worker = info.Installed ? trakt.FindExportTask() : null;
        var refusal = RefusalFor(info, worker, info.Installed ? trakt.ReadLinks() : null);
        if (refusal is not null) return refusal;
        Start(worker!, "requested by an administrator");
        return null;
    }

    private static string? RefusalFor(TraktPluginInfo info, IScheduledTaskWorker? worker, IReadOnlyList<TraktLink>? links)
    {
        if (!info.Installed) return TraktExportRefusal.NotInstalled;
        if (!Version.TryParse(info.Version, out var version) || version < TraktPluginState.MinimumExportVersion) return TraktExportRefusal.OldPlugin;
        if (worker is null || links is null) return TraktExportRefusal.Unknown;
        if (links.Any(link => link.PostUnwatchedHistory)) return TraktExportRefusal.UnwatchedHistory;
        // Accounts whose sign-in could not be renewed are skipped by the plugin: with none linked, a run would only
        // complete without exporting anything (Codex P2-2).
        if (!links.Any(link => link.Linked)) return TraktExportRefusal.NoUsers;
        return null;
    }

    private void Start(IScheduledTaskWorker worker, string why)
    {
        // The host runs one copy of a task at a time; a run that is already going is joined, not queued behind.
        if (worker.State != TaskState.Idle) return;
        lock (_gate)
        {
            _relinked = false;
        }

        logger.LogInformation("JellyfinMod starts the Trakt plugin's export ({Why})", why);
        taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
    }

    private void Tick()
    {
        var info = trakt.Describe();
        if (!info.Installed) return;
        var worker = trakt.FindExportTask();
        var links = trakt.ReadLinks();
        if (links is not null) NoteReconnects(links);
        var last = worker?.LastExecutionResult;
        var clean = Judge(last);
        if (!AutoExport || RefusalFor(info, worker, links) is not null || worker!.State != TaskState.Idle) return;

        var now = DateTime.UtcNow;
        bool relinked;
        lock (_gate)
        {
            if (_nextAttemptAt is { } next && now < next) return;
            relinked = _relinked;
        }

        var latest = LatestEpisodePlay(links!.Where(link => link.Linked));
        if (latest is { } played && now - played < Quiet) return;

        var why = relinked ? "a Trakt account was linked again"
            : last is null ? "the export has not run yet"
            : clean == false ? "the last run was not clean"
            : clean is null ? null // its log is still being written
            : latest is { } after && after > Utc(last.StartTimeUtc) ? "episodes were watched since the last run"
            : now - Utc(last.EndTimeUtc) >= Daily ? "the daily safety-net export"
            : null;
        if (why is not null) Start(worker, why);
    }

    /// <summary>
    /// Whether the run was clean: Completed, with no Trakt error in the log while it ran. Null when there is no run or its
    /// log may still be arriving. Each run is judged once, which also sets the retry pace. Called only from the background
    /// check, so one judgment runs at a time and no request reads the log.
    /// </summary>
    private bool? Judge(TaskResult? run)
    {
        if (run is null) return null;
        var start = Utc(run.StartTimeUtc);
        var end = Utc(run.EndTimeUtc);
        lock (_gate)
        {
            if (_judgedStart == start) return _judgedClean;
        }

        if (DateTime.UtcNow < end + LogSettle) return null;
        var clean = run.Status == TaskCompletionStatus.Completed && !TraktErrorLogged(start, end + LogSettle);
        lock (_gate)
        {
            if (_judgedStart == start) return _judgedClean;
            // Only a newer run moves the pace; an older one (a host that went back in time) is shown, not counted.
            if (_judgedStart is null || start > _judgedStart)
            {
                if (clean)
                {
                    _failures = 0;
                    _nextAttemptAt = null;
                }
                else
                {
                    _failures++;
                    _nextAttemptAt = end + (RetryBase * RetrySteps[Math.Min(_failures, RetrySteps.Length) - 1]);
                    logger.LogInformation("The Trakt plugin's export was not clean ({Status}); JellyfinMod tries again at {Next:o}",
                        run.Status, _nextAttemptAt);
                }
            }

            _judgedStart = start;
            _judgedClean = clean;
            return clean;
        }
    }

    /// <summary>
    /// Whether the host's file log holds a Trakt error between <paramref name="from"/> and <paramref name="to"/>. Reads the
    /// log files written since the run started; when none can be read the host's status alone decides.
    /// </summary>
    private bool TraktErrorLogged(DateTime from, DateTime to)
    {
        try
        {
            foreach (var file in Directory.GetFiles(paths.LogDirectoryPath, "log_*.log").Where(file => File.GetLastWriteTimeUtc(file) >= from))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("] [ERR] ", StringComparison.Ordinal) && !line.Contains("] [FTL] ", StringComparison.Ordinal)) continue;
                    var match = ErrorLine.Match(line);
                    if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                    if (at.UtcDateTime >= from.AddSeconds(-1) && at.UtcDateTime <= to) return true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug("JellyfinMod could not read the host's log to judge the Trakt export: {Error}", error.Message);
        }

        return false;
    }

    /// <summary>Notes an account linked again after its sign-in could not be renewed, so its watches are sent soon (in memory).</summary>
    private void NoteReconnects(IReadOnlyList<TraktLink> links)
    {
        lock (_gate)
        {
            foreach (var link in links)
            {
                if (link.ReconnectNeeded) _reconnecting.Add(link.UserId);
                else if (link.Linked && _reconnecting.Remove(link.UserId))
                {
                    // A reconnect is a fresh start: the retry wait left from the failing sign-in does not delay it.
                    _relinked = true;
                    _failures = 0;
                    _nextAttemptAt = null;
                }
            }
        }
    }

    /// <summary>The latest time a linked user finished (or marked played) an episode, in UTC.</summary>
    private DateTime? LatestEpisodePlay(IEnumerable<TraktLink> links)
    {
        DateTime? latest = null;
        foreach (var link in links)
        {
            var user = users.GetUserById(link.UserId);
            if (user is null) continue;
            var item = library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsPlayed = true,
                IsVirtualItem = false,
                Recursive = true,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = 1
            }).FirstOrDefault();
            if (item is null || userData.GetUserData(user, item)?.LastPlayedDate is not { } played) continue;
            var utc = Utc(played);
            if (latest is null || utc > latest) latest = utc;
        }

        return latest;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
