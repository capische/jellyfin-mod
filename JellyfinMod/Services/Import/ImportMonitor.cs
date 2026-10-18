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

    private int _fullRequested;
    private int _localRequested;

    /// <summary>
    /// How often the client is read while an import awaits its download (P4.FU12): the file is imported within this of finishing.
    /// A configured faster poll is kept. Seed-only and idle work keeps the configured pace.
    /// </summary>
    public static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(2);

    /// <summary>How often a local pass looks at imports waiting for Jellyfin to bind and ready their file, between wakes.</summary>
    public static readonly TimeSpan LocalPoll = TimeSpan.FromSeconds(1);

    /// <summary>Asks for a tick now, for example after a grab was accepted or a queue action.</summary>
    public void Wake()
    {
        Volatile.Write(ref _fullRequested, 1);
        _wake.Release();
    }

    /// <summary>
    /// Asks for a local pass (P4.FU12): imports waiting for Jellyfin to bind and ready their file are looked at again, from the
    /// plugin's database and Jellyfin's library only. It never reads the download client or runs the rest of a tick, so a
    /// burst of bindings costs the client nothing. Calls that come together are one pass.
    /// </summary>
    public void WakeLocal()
    {
        Volatile.Write(ref _localRequested, 1);
        _wake.Release();
    }

    /// <summary>Runs one local pass under the import gate (see <see cref="WakeLocal"/>); returns whether any import still waits.</summary>
    public async Task<bool> LocalPassAsync(CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ImportService>();
        await service.ObserveBindingsAsync(cancellationToken).ConfigureAwait(false);
        // The old version goes as soon as the new one plays (a replacement or upgrade): these read the plugin's database and
        // Jellyfin only, never the download client.
        if (scope.ServiceProvider.GetService<Automation.UpgradeService>() is { } upgrades)
            await upgrades.AdvanceAllAsync(cancellationToken).ConfigureAwait(false);
        if (scope.ServiceProvider.GetService<PackReplaceService>() is { } replacements)
            await replacements.AdvanceAllAsync(cancellationToken).ConfigureAwait(false);
        return await service.HasBindingWorkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one tick under the import gate. Used by the loop, the repair task and the queue API.</summary>
    public async Task<ImportTickResult> TickOnceAsync(CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ImportService>().TickAsync(cancellationToken).ConfigureAwait(false);
        // The first missing and each viewer's next episode download first, re-read every tick (next-up priority, P4.FU3).
        if (scope.ServiceProvider.GetService<DownloadOrderService>() is { } order)
            await order.ApplyAsync(cancellationToken).ConfigureAwait(false);
        // Upgrades follow their imports on the same cadence (P6.M5); a host without automation has none.
        if (scope.ServiceProvider.GetService<Automation.UpgradeService>() is { } upgrades)
            await upgrades.AdvanceAllAsync(cancellationToken).ConfigureAwait(false);
        // Replace grabs remove what a pack's new files replace, once each is in the library (season and series packs).
        if (scope.ServiceProvider.GetService<PackReplaceService>() is { } replacements)
            await replacements.AdvanceAllAsync(cancellationToken).ConfigureAwait(false);
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
            var nextFull = DateTime.UtcNow;
            var observing = false;
            while (!token.IsCancellationRequested)
            {
                var full = Interlocked.Exchange(ref _fullRequested, 0) == 1 || DateTime.UtcNow >= nextFull;
                var local = Interlocked.Exchange(ref _localRequested, 0) == 1;
                if (full)
                {
                    var poll = TimeSpan.FromSeconds(15);
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
                        var settings = await AcquisitionConfiguration.GetSettingsAsync(database, token).ConfigureAwait(false);
                        poll = TimeSpan.FromSeconds(Math.Clamp(settings.ImportPollSeconds, 1, 3600));
                        var service = scope.ServiceProvider.GetRequiredService<ImportService>();
                        if (await service.HasOpenWorkAsync(token).ConfigureAwait(false))
                            await TickOnceAsync(token).ConfigureAwait(false);
                        // Imports awaiting a download are watched closely; everything else keeps the configured pace.
                        if (await service.HasWaitingDownloadAsync(token).ConfigureAwait(false)) poll = poll < ActivePoll ? poll : ActivePoll;
                        observing = await service.HasBindingWorkAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogError(error, "The import monitor tick failed; it runs again after the poll interval");
                    }

                    nextFull = DateTime.UtcNow + poll;
                }
                else if (local || observing)
                {
                    try
                    {
                        observing = await LocalPassAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogError(error, "The import monitor's local pass failed; it runs again shortly");
                    }
                }

                // Idle or not, the monitor only wakes early when asked; an idle tick costs one database query. Wakes that came
                // together (a scan binding many titles) are one pass. An import waiting for Jellyfin is looked at every LocalPoll.
                var wait = nextFull - DateTime.UtcNow;
                if (observing && wait > LocalPoll) wait = LocalPoll;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                await _wake.WaitAsync(wait, token).ConfigureAwait(false);
                while (_wake.Wait(0))
                {
                }
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
