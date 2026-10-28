using System.Threading.Channels;
using JellyfinMod.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Ratings;

/// <summary>What started a ratings pass, as Status reports it.</summary>
public static class RatingsPassKinds
{
    /// <summary>The daily task at 04:00.</summary>
    public const string Daily = "daily";

    /// <summary>Titles that just arrived: an entry created, a file bound, an import completed.</summary>
    public const string Arrivals = "arrivals";

    /// <summary>A key verified by Test, or ratings switched on with a key saved.</summary>
    public const string Setup = "setup";

    /// <summary>The first start with a key saved and no run ever completed.</summary>
    public const string Startup = "startup";
}

/// <summary>
/// Fetches ratings without waiting for the daily task (user decision 8, 2026-10-08): soon after titles arrive, and in full
/// when ratings are set up. Every trigger only records what it asks for and wakes one background worker, so no request or
/// import waits for MDBList, and a burst of arrivals — a library scan, a bulk add — becomes one pass once it has been quiet
/// for <see cref="RatingsOptions.ArrivalQuiet"/>. Exactly one pass runs at a time; a trigger during a pass is folded into the
/// next one. Each pass goes through <see cref="RatingsRefreshRunner.RunAutomaticAsync"/>, so the claim, the budget, the
/// breaker, the blocker and the credential gate apply exactly as they do to the daily run.
/// </summary>
public sealed class RatingsAutoFetch(IServiceScopeFactory scopes, DatabaseInitializer readiness, RatingsOptions options, ILogger<RatingsAutoFetch> logger)
    : BackgroundService
{
    /// <summary>Arrived entries kept by id at most; beyond this the pass still takes every title never attempted.</summary>
    private const int ArrivalLimit = 10000;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object _lock = new();
    private readonly HashSet<Guid> _arrived = [];
    private bool _arrivals;
    private string? _full;
    private long _firstArrival;
    private long _lastArrival;

    /// <summary>Asks for the titles of these entries to be fetched soon (they were created, or a file of theirs arrived).</summary>
    public void Arrived(params Guid[] entryIds)
    {
        if (!options.Automatic || entryIds.Length == 0) return;
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (!_arrivals) _firstArrival = now;
            _arrivals = true;
            _lastArrival = now;
            foreach (var id in entryIds)
                if (_arrived.Count < ArrivalLimit) _arrived.Add(id);
        }

        _wake.Writer.TryWrite(true);
    }

    /// <summary>Asks for a full run at once (<see cref="RatingsPassKinds.Setup"/> or <see cref="RatingsPassKinds.Startup"/>).</summary>
    public void RunAll(string kind)
    {
        if (!options.Automatic) return;
        lock (_lock) _full ??= kind;
        _wake.Writer.TryWrite(true);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Automatic) return;
        try
        {
            while (!readiness.IsReady) await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            await CheckFirstStartAsync(stoppingToken).ConfigureAwait(false);
            while (await _wake.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                await SettleAsync(stoppingToken).ConfigureAwait(false);
                string? kind;
                Guid[]? arrived;
                lock (_lock)
                {
                    // A full run fetches everything an arrivals pass would, so it takes the arrivals with it.
                    kind = _full ?? (_arrivals ? RatingsPassKinds.Arrivals : null);
                    arrived = _full is null && _arrivals ? _arrived.ToArray() : null;
                    (_full, _arrivals) = (null, false);
                    _arrived.Clear();
                }

                if (kind is null) continue;
                await PassAsync(kind, arrived, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits until arrivals have been quiet for a moment (or for the longest wait); a full run starts at once.</summary>
    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_full is not null || !_arrivals) return;
                var now = Environment.TickCount64;
                var quiet = options.ArrivalQuiet - TimeSpan.FromMilliseconds(now - _lastArrival);
                var longest = options.ArrivalMaxWait - TimeSpan.FromMilliseconds(now - _firstArrival);
                wait = quiet < longest ? quiet : longest;
            }

            if (wait <= TimeSpan.Zero) return;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PassAsync(string kind, Guid[]? arrived, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var refusal = await scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>()
                .RunAutomaticAsync(kind, arrived, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
                logger.LogInformation("Ratings {Kind} pass skipped ({Refusal}); the daily run takes these titles", kind, refusal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The titles stay due; the next trigger or the daily run takes them.
            logger.LogWarning("Ratings {Kind} pass failed: {ErrorType}", kind, error.GetType().Name);
        }
    }

    /// <summary>A key saved and no run ever completed (the first start after setup, or an upgrade): run in full now.</summary>
    private async Task CheckFirstStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<AcquisitionSecretStore>();
            var settings = await database.RatingsSettings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == RatingsSettings.SingletonId,
                cancellationToken).ConfigureAwait(false);
            var state = await database.RatingsProviderStates.AsNoTracking().SingleOrDefaultAsync(row => row.Id == RatingsProviderState.SingletonId,
                cancellationToken).ConfigureAwait(false);
            if (settings is { Enabled: true } && state?.LastRunFinishedAt is null &&
                await secrets.HasAsync(settings.ApiKeyRef, cancellationToken).ConfigureAwait(false))
                RunAll(RatingsPassKinds.Startup);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Ratings: the first-start check failed ({ErrorType}); the daily run still runs", error.GetType().Name);
        }
    }
}
