using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Import;

/// <summary>
/// The long-running download monitor (P5.I3): starts after the database is migrated, runs recovery once, then ticks every
/// <c>ImportPollSeconds</c> while any import or seed release is open. It never reads the client when nothing is open.
/// </summary>
public sealed class ImportMonitor(
    IServiceScopeFactory scopes,
    DatabaseInitializer readiness,
    ImportTickGate gate,
    ILogger<ImportMonitor> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private Task _loop = Task.CompletedTask;
    private long _ticks;

    /// <summary>Gets how many ticks ran, for log evidence.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Asks for a tick now, for example after a grab was accepted or a queue action.</summary>
    public void Wake() => _wake.Release();

    /// <summary>Runs one tick under the import gate. Used by the loop, the repair task and the queue API.</summary>
    public async Task<ImportTickResult> TickOnceAsync(CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ImportService>().TickAsync(cancellationToken).ConfigureAwait(false);
        var tick = Interlocked.Increment(ref _ticks);
        if (result.OpenImports + result.OpenSeedReleases > 0)
            logger.LogDebug("Import tick {Tick}: {Imports} imports, {Seeds} seed releases, {Reads} client reads, reachable {Reachable}",
                tick, result.OpenImports, result.OpenSeedReleases, result.ClientReads, result.ClientReachable);
        return result;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _stopping.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            for (var attempt = 0; !readiness.IsReady && attempt < 600; attempt++)
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            if (!readiness.IsReady) return;
            while (!token.IsCancellationRequested)
            {
                var poll = TimeSpan.FromSeconds(15);
                try
                {
                    using var scope = scopes.CreateScope();
                    var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
                    var settings = await AcquisitionConfiguration.GetSettingsAsync(database, token).ConfigureAwait(false);
                    poll = TimeSpan.FromSeconds(Math.Clamp(settings.ImportPollSeconds, 1, 3600));
                    if (await scope.ServiceProvider.GetRequiredService<ImportService>().HasOpenWorkAsync(token).ConfigureAwait(false))
                        await TickOnceAsync(token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "The import monitor tick failed; it runs again after the poll interval");
                }

                // Idle or not, the monitor only wakes early when asked; an idle tick costs one database query.
                await _wake.WaitAsync(poll, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// The native repair task (P5.I3): runs the same tick as the monitor, which also recovers operations interrupted in
/// <c>linking</c>, <c>linked</c>, <c>scanning</c> or <c>removing</c>, and re-requests scans that timed out.
/// </summary>
public sealed class ImportRepairTask(ImportMonitor monitor) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Repair JellyfinMod imports";

    /// <inheritdoc />
    public string Key => "JellyfinModImportRepair";

    /// <inheritdoc />
    public string Description => "Checks downloads, finishes interrupted imports and releases seeding copies whose goals are met.";

    /// <inheritdoc />
    public string Category => "JellyfinMod";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await monitor.TickOnceAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
