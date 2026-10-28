using System.Threading.Channels;
using JellyfinMod.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Ratings;

/// <summary>Registers the ratings services (P9).</summary>
public static class RatingsServices
{
    /// <summary>Registers the store, client, runner and queue. <see cref="TimeProvider"/> and the secret store come from the host's registrations.</summary>
    public static void Add(IServiceCollection services, Func<PluginConfiguration> configuration)
    {
        services.AddSingleton<RatingsRunGate>();
        services.AddSingleton<RatingsCredentialGate>();
        services.AddSingleton<RatingsRefreshQueue>();
        services.AddSingleton<RatingsAutoFetch>();
        services.AddSingleton(RatingsOptions.Default);
        services.AddTransient<HostRatingsReader>();
        services.AddTransient<RatingsStore>();
        services.AddTransient(provider => new MdbListClient(provider.GetRequiredService<IHttpClientFactory>(), configuration,
            provider.GetRequiredService<ILogger<MdbListClient>>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<RatingsEndpoint>()));
        services.AddTransient<RatingsRefreshRunner>();
    }

    /// <summary>Registers the manual-refresh worker and the automatic fetcher (user decision 8).</summary>
    public static void AddHostedServices(IServiceCollection services)
    {
        services.AddSingleton<IHostedService, RatingsRefreshWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<RatingsAutoFetch>());
    }
}

/// <summary>Manual per-title refreshes waiting for the fetcher (P9.R3); bounded, and one entry is queued once.</summary>
public sealed class RatingsRefreshQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait });
    private readonly HashSet<Guid> _queued = [];
    private readonly object _lock = new();

    /// <summary>Gets how many refreshes are waiting.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _queued.Count;
        }
    }

    /// <summary>Queues one entry; false when the queue is full. An entry already waiting is not queued twice.</summary>
    public bool TryEnqueue(Guid entryId)
    {
        lock (_lock)
        {
            if (_queued.Contains(entryId)) return true;
            if (!_channel.Writer.TryWrite(entryId)) return false;
            _queued.Add(entryId);
            return true;
        }
    }

    internal ChannelReader<Guid> Reader => _channel.Reader;

    internal void Done(Guid entryId)
    {
        lock (_lock) _queued.Remove(entryId);
    }
}

/// <summary>Runs queued manual refreshes one at a time, through the same gate, claim and budget as the daily task.</summary>
public sealed class RatingsRefreshWorker(RatingsRefreshQueue queue, IServiceScopeFactory scopes, DatabaseInitializer readiness,
    ILogger<RatingsRefreshWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entryId in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                for (var wait = 0; !readiness.IsReady && wait < 600; wait++) await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                using var scope = scopes.CreateScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>().FetchOneAsync(entryId, stoppingToken)
                    .ConfigureAwait(false);
                logger.LogInformation("Manual ratings refresh for entry {EntryId}: {Outcome}", entryId, outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogWarning("Manual ratings refresh for entry {EntryId} failed: {ErrorType}", entryId, error.GetType().Name);
            }
            finally
            {
                queue.Done(entryId);
            }
        }
    }
}

/// <summary>The native daily task <c>JellyfinModRatingsRefresh</c> (P9.R3).</summary>
public sealed class RatingsRefreshTask(IServiceScopeFactory scopeFactory) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Refresh JellyfinMod ratings";

    /// <inheritdoc />
    public string Key => "JellyfinModRatingsRefresh";

    /// <inheritdoc />
    public string Description => "Fetches title ratings from MDBList within the daily budget: titles without ratings first, newest first, then titles whose ratings are older than the refresh window.";

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
    [
        new()
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RatingsRefreshRunner>().RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
