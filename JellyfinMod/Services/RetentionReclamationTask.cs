using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Services;

/// <summary>Native daily task that runs a bounded automatic retention batch.</summary>
public sealed class RetentionReclamationTask(IServiceScopeFactory scopeFactory) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Reclaim expired JellyfinMod media";

    /// <inheritdoc />
    public string Key => "JellyfinModRetentionReclamation";

    /// <inheritdoc />
    public string Description => "Reclaims a bounded batch of media whose retention window has expired.";

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
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RetentionRunner>()
            .RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
