using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
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
    IReadOnlyList<(TraktLink Link, string Name)> Users,
    IReadOnlyDictionary<Guid, TraktProfile> Profiles,
    IReadOnlyList<TraktExportRun> Activity,
    string? RunningWhy);

/// <summary>Why JellyfinMod started an export run (Phase 13, D73); null for a run started elsewhere (the Dashboard).</summary>
public static class TraktExportWhy
{
    /// <summary>A linked user finished an episode after the last run (the quiet period has passed).</summary>
    public const string Watch = "watch";

    /// <summary>An account was linked again after its sign-in could not be renewed.</summary>
    public const string Reconnect = "reconnect";

    /// <summary>The daily safety net: no run for a day.</summary>
    public const string Daily = "daily";

    /// <summary>The last run was not clean and its retry wait has passed.</summary>
    public const string Retry = "retry";

    /// <summary>Sync Missing Watches Now.</summary>
    public const string SyncNow = "sync_now";

    /// <summary>The export had never run.</summary>
    public const string First = "first";
}

/// <summary>
/// One export run as Settings › Trakt's Activity lists it (Phase 13, D50, D73): when it ran, why, its result, and the stock
/// plugin's own counts from that run's lines in the host's log (null when it logged none).
/// </summary>
/// <param name="StartedAt">The host's start of the run, in UTC.</param>
/// <param name="EndedAt">The host's end of the run, in UTC.</param>
/// <param name="Status">The host's result: Completed, Failed, Cancelled or Aborted.</param>
/// <param name="Clean">Completed with no Trakt error logged while it ran.</param>
/// <param name="Why">A <see cref="TraktExportWhy"/> value, or null for a run JellyfinMod did not start.</param>
/// <param name="EpisodesWatched">Episodes the plugin sent as watched.</param>
/// <param name="MoviesWatched">Movies the plugin sent as watched.</param>
/// <param name="EpisodesCollected">Episodes the plugin added to the Trakt collection.</param>
/// <param name="MoviesCollected">Movies the plugin added to the Trakt collection.</param>
/// <param name="Error">The Trakt error in a clause ("Trakt answered 503 Service Unavailable"), or null.</param>
public sealed record TraktExportRun(DateTime StartedAt, DateTime EndedAt, string Status, bool Clean, string? Why,
    int? EpisodesWatched, int? MoviesWatched, int? EpisodesCollected, int? MoviesCollected, string? Error);

/// <summary>A linked user's Trakt username and avatar (Phase 13, D49), read by the server; never a token.</summary>
/// <param name="Username">The Trakt username, or null.</param>
/// <param name="AvatarUrl">An http(s) address of the Trakt avatar, or null.</param>
public sealed record TraktProfile(string? Username, string? AvatarUrl);

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
/// <para>Settings also shows each linked account's Trakt username and avatar (D49): the server reads Trakt's
/// <c>/users/settings</c> once a day or when the link changes, with the token the Trakt plugin holds, which is read here only
/// and never logged, stored or returned; and the last 20 runs (D50), with the plugin's own counts from each run's log
/// lines, kept in <c>trakt-activity.json</c> in the plugin's data folder so a restart keeps them.</para>
/// </remarks>
public sealed class TraktExportService(
    TraktPluginState trakt,
    ITaskManager taskManager,
    IUserManager users,
    ILibraryManager library,
    IUserDataManager userData,
    IApplicationPaths paths,
    IHttpClientFactory http,
    ILogger<TraktExportService> logger) : IHostedService, IDisposable
{
    private const int ActivityLength = 20;
    private static readonly TimeSpan ProfileAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan ProfileRetry = TimeSpan.FromHours(1);
    private static readonly Regex LogLine = new(@"^\[(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\] \[(?<level>[A-Z]{3})\] \[\d+\] (?<source>[^:\s]+): (?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex StatusAnswer = new(@"status code does not indicate success: (?<code>\d{3}) \((?<reason>[^)]*)\)", RegexOptions.Compiled);
    // The stock plugin's count lines at v33 (SyncLibraryTask): 'Episodes to set {State}: {Count}' and the collection ones.
    private static readonly Regex EpisodesWatchedLine = new(@"^Episodes to set ""?watched""?: (?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex MoviesWatchedLine = new(@"^Movies to set ""?watched""?: (?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex EpisodesCollectedLine = new(@"^Episodes to ""?add to""? collection:? (?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex MoviesCollectedLine = new(@"^Movies to ""?add to""? collection:? (?<n>\d+)", RegexOptions.Compiled);
    private readonly Dictionary<Guid, (DateTime? Fingerprint, DateTime FetchedAt, TraktProfile Profile)> _profiles = [];
    private List<TraktExportRun>? _activity;
    // The starts JellyfinMod asked for, oldest first: a run takes the latest one asked for before it began.
    private readonly List<(string Why, DateTime At)> _pending = [];
    // Every run's result as the host reports its end, so two runs close together are both listed; the background check
    // judges them in order. The host's last result covers a restart.
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskResult> _finished = new();
    private static readonly int[] RetrySteps = [1, 2, 4, 8, 16, 24];
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    // The plugin's log lines reach the file through an asynchronous sink: a run is judged once this has passed after its end.
    private static readonly TimeSpan LogSettle = TimeSpan.FromSeconds(5);
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
        taskManager.TaskCompleted += OnTaskCompleted;
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
                    await RefreshProfilesAsync(token).ConfigureAwait(false);
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
        taskManager.TaskCompleted -= OnTaskCompleted;
        if (_stopping is null) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _stopping?.Dispose();

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs args)
    {
        if (string.Equals(args.Task?.ScheduledTask?.Key, TraktPluginState.ExportTaskKey, StringComparison.Ordinal) && args.Result is { } result)
        {
            _finished.Enqueue(result);
        }
    }

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
            var running = worker?.State is TaskState.Running or TaskState.Cancelling;
            return new TraktExportStatus(info.Installed, info.Version, AutoExport, running,
                RefusalFor(info, worker, links), last, clean, AutoExport ? _nextAttemptAt : null, _failures, named,
                _profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Profile), [.. Activity()],
                running && _pending.Count > 0 ? _pending[^1].Why : null);
        }
    }

    /// <summary>Starts the export now (Sync Missing Watches Now), or joins the run already going. Returns a refusal or null.</summary>
    public string? RunNow()
    {
        var info = trakt.Describe();
        var worker = info.Installed ? trakt.FindExportTask() : null;
        var refusal = RefusalFor(info, worker, info.Installed ? trakt.ReadLinks() : null);
        if (refusal is not null) return refusal;
        Start(worker!, "requested by an administrator", TraktExportWhy.SyncNow);
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

    private void Start(IScheduledTaskWorker worker, string why, string code)
    {
        // The host runs one copy of a task at a time; a run that is already going is joined, not queued behind.
        if (worker.State != TaskState.Idle) return;
        lock (_gate)
        {
            _relinked = false;
            _pending.Add((code, DateTime.UtcNow));
            if (_pending.Count > 8) _pending.RemoveAt(0);
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
        // Runs that ended since the last check, oldest first, once their log has arrived; then the host's last result.
        while (_finished.TryPeek(out var done) && DateTime.UtcNow >= Utc(done.EndTimeUtc) + LogSettle && _finished.TryDequeue(out done)) Judge(done);
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

        (string Words, string Code)? why = relinked ? ("a Trakt account was linked again", TraktExportWhy.Reconnect)
            : last is null ? ("the export has not run yet", TraktExportWhy.First)
            : clean == false ? ("the last run was not clean", TraktExportWhy.Retry)
            : clean is null ? null // its log is still being written
            : latest is { } after && after > Utc(last.StartTimeUtc) ? ("episodes were watched since the last run", TraktExportWhy.Watch)
            : now - Utc(last.EndTimeUtc) >= Daily ? ("the daily safety-net export", TraktExportWhy.Daily)
            : null;
        if (why is { } chosen) Start(worker, chosen.Words, chosen.Code);
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
            // A run already listed, older than the one judged last (its end was queued, then it was the host's last result).
            if (_judgedStart is not null && start < _judgedStart && Activity().FirstOrDefault(known => known.StartedAt == start) is { } listed) return listed.Clean;
        }

        if (DateTime.UtcNow < end + LogSettle) return null;
        var scan = ScanRunLog(start, end, end + LogSettle);
        var clean = run.Status == TaskCompletionStatus.Completed && !scan.Error;
        lock (_gate)
        {
            if (_judgedStart == start) return _judgedClean;
            // The run's reason is the start JellyfinMod asked for just before it, if any (a Dashboard run has none).
            string? why = null;
            var asked = _pending.FindLastIndex(pending => pending.At <= start.AddMilliseconds(200));
            if (asked >= 0)
            {
                if (start - _pending[asked].At < TimeSpan.FromMinutes(30)) why = _pending[asked].Why;
                _pending.RemoveRange(0, asked + 1);
            }

            Record(new TraktExportRun(start, end, run.Status.ToString(), clean, why, scan.EpisodesWatched, scan.MoviesWatched,
                scan.EpisodesCollected, scan.MoviesCollected, scan.Error ? scan.Clause ?? "the Trakt plugin logged an error" : null));
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

            if (_judgedStart is null || start >= _judgedStart)
            {
                _judgedStart = start;
                _judgedClean = clean;
            }

            return clean;
        }
    }

    private sealed record RunScan(bool Error, string? Clause, int? EpisodesWatched, int? MoviesWatched, int? EpisodesCollected, int? MoviesCollected);

    /// <summary>
    /// The run's lines in the host's file log between <paramref name="from"/> and <paramref name="to"/>: whether the Trakt
    /// plugin logged an error (ERR or FTL from its export or its Trakt client), that error in a clause, and the plugin's own
    /// counts of what it sent (up to a second after the run's <paramref name="end"/>). Reads the log files written since the run started; when none can be read the host's status
    /// alone decides and no counts are known.
    /// </summary>
    private RunScan ScanRunLog(DateTime from, DateTime end, DateTime to)
    {
        // The count lines are the run's own, logged before its end; a run seconds later must not add its counts here.
        var countsTo = end.AddSeconds(1);
        var error = false;
        string? clause = null;
        int? episodes = null, movies = null, episodesCollected = null, moviesCollected = null;
        var counted = false;
        try
        {
            foreach (var file in Directory.GetFiles(paths.LogDirectoryPath, "log_*.log").Where(file => File.GetLastWriteTimeUtc(file) >= from))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var inTraktError = false;
                while (reader.ReadLine() is { } line)
                {
                    var match = LogLine.Match(line);
                    if (!match.Success)
                    {
                        // An exception's own lines follow the error line they belong to.
                        if (inTraktError && clause is null) clause = ClauseOf(line);
                        continue;
                    }

                    inTraktError = false;
                    if (!DateTimeOffset.TryParseExact(match.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var at) || at.UtcDateTime < from.AddSeconds(-1) || at.UtcDateTime > to) continue;
                    var source = match.Groups["source"].Value;
                    if (source != "Trakt.ScheduledTasks.SyncLibraryTask" && source != "Trakt.Api.TraktApi") continue;
                    var message = match.Groups["message"].Value;
                    if (match.Groups["level"].Value is "ERR" or "FTL")
                    {
                        error = inTraktError = true;
                        clause ??= message.Contains("token refresh", StringComparison.OrdinalIgnoreCase) ? "the Trakt sign-in could not be renewed" : null;
                        continue;
                    }

                    if (at.UtcDateTime > countsTo) continue;
                    if (Count(EpisodesWatchedLine, message) is { } e) { episodes = (episodes ?? 0) + e; counted = true; }
                    else if (Count(MoviesWatchedLine, message) is { } m) { movies = (movies ?? 0) + m; counted = true; }
                    else if (Count(EpisodesCollectedLine, message) is { } ec) { episodesCollected = (episodesCollected ?? 0) + ec; counted = true; }
                    else if (Count(MoviesCollectedLine, message) is { } mc) { moviesCollected = (moviesCollected ?? 0) + mc; counted = true; }
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug("JellyfinMod could not read the host's log to judge the Trakt export: {Error}", failure.Message);
        }

        return counted ? new RunScan(error, clause, episodes ?? 0, movies ?? 0, episodesCollected ?? 0, moviesCollected ?? 0)
            : new RunScan(error, clause, null, null, null, null);
    }

    private static int? Count(Regex pattern, string message) =>
        pattern.Match(message) is { Success: true } found && int.TryParse(found.Groups["n"].Value, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>A Trakt error in a clause, from one line of the exception the plugin logged.</summary>
    private static string? ClauseOf(string line)
    {
        if (StatusAnswer.Match(line) is { Success: true } answer) return $"Trakt answered {answer.Groups["code"].Value} {answer.Groups["reason"].Value}";
        return line.Contains("SSL connection could not be established", StringComparison.Ordinal) ||
            line.Contains("failed after", StringComparison.Ordinal) || line.Contains("TaskCanceledException", StringComparison.Ordinal) ||
            line.Contains("HttpRequestException: No such host", StringComparison.Ordinal)
            ? "Trakt could not be reached" : null;
    }

    /// <summary>The last runs, newest first, read once from the plugin's data folder. Call under the gate.</summary>
    private List<TraktExportRun> Activity()
    {
        if (_activity is not null) return _activity;
        _activity = [];
        if (ActivityFile is { } file && File.Exists(file))
        {
            try
            {
                _activity = JsonSerializer.Deserialize<List<TraktExportRun>>(File.ReadAllText(file)) ?? [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogDebug("JellyfinMod could not read the Trakt activity: {Error}", error.Message);
            }
        }

        return _activity;
    }

    private static string? ActivityFile => Plugin.Instance?.DataPath is { } folder ? Path.Combine(folder, "trakt-activity.json") : null;

    /// <summary>Adds a judged run to the activity (newest first, the last 20) and saves it. Call under the gate.</summary>
    private void Record(TraktExportRun run)
    {
        var activity = Activity();
        // A restart judges the last run again; it is listed once.
        if (activity.Any(known => known.StartedAt == run.StartedAt)) return;
        activity.Insert(0, run);
        if (activity.Count > ActivityLength) activity.RemoveRange(ActivityLength, activity.Count - ActivityLength);
        if (ActivityFile is not { } file) return;
        try
        {
            var written = file + ".tmp";
            File.WriteAllText(written, JsonSerializer.Serialize(activity));
            File.Move(written, file, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("JellyfinMod could not save the Trakt activity: {Error}", error.Message);
        }
    }

    /// <summary>
    /// Reads each linked account's Trakt username and avatar (D49) when it has none yet, its link changed, or a day has
    /// passed; an account that needs reconnecting keeps what was read last, and a removed one is forgotten. One read-only
    /// call to Trakt's <c>/users/settings</c> per account, with the token the Trakt plugin holds; the token never leaves
    /// this method and is never logged.
    /// </summary>
    private async Task RefreshProfilesAsync(CancellationToken cancellationToken)
    {
        var links = trakt.Describe().Installed ? trakt.ReadLinks() : null;
        if (links is null) return;
        List<TraktLink> due;
        lock (_gate)
        {
            foreach (var gone in _profiles.Keys.Where(id => links.All(link => link.UserId != id)).ToList()) _profiles.Remove(gone);
            var now = DateTime.UtcNow;
            due = [.. links.Where(link => link.Linked && (!_profiles.TryGetValue(link.UserId, out var known) ||
                known.Fingerprint != link.TokenExpiresAt || now - known.FetchedAt > ProfileAge))];
        }

        foreach (var link in due)
        {
            var profile = await ReadProfileAsync(link.UserId, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                var kept = _profiles.TryGetValue(link.UserId, out var known) ? known.Profile : new TraktProfile(null, null);
                // A failed read keeps what was known and tries again in an hour.
                _profiles[link.UserId] = profile is null
                    ? (link.TokenExpiresAt, DateTime.UtcNow - ProfileAge + ProfileRetry, kept)
                    : (link.TokenExpiresAt, DateTime.UtcNow, profile);
            }
        }
    }

    private async Task<TraktProfile?> ReadProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (trakt.ReadProfileAccess(userId) is not { } access) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/users/settings");
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", access.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("JellyfinMod could not read a Trakt profile: Trakt answered {Status}", (int)response.StatusCode);
                return null;
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!body.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return null;
            var username = user.TryGetProperty("username", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            string? avatar = null;
            if (user.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object &&
                images.TryGetProperty("avatar", out var picture) && picture.ValueKind == JsonValueKind.Object &&
                picture.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(full.GetString(), UriKind.Absolute, out var address) && address.Scheme is "https" or "http")
            {
                avatar = address.AbsoluteUri;
            }

            return new TraktProfile(username, avatar);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogDebug("JellyfinMod could not read a Trakt profile: {Error}", error.GetType().Name);
            return null;
        }
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
