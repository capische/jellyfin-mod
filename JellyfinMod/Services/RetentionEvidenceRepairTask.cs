using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinMod.Services;

/// <summary>Scheduled repair for retention evidence missed while the plugin was offline.</summary>
public sealed class RetentionEvidenceRepairTask(IServiceScopeFactory scopeFactory) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Refresh JellyfinMod retention evidence";

    /// <inheritdoc />
    public string Key => "JellyfinModRetentionEvidence";

    /// <inheritdoc />
    public string Description => "Re-reads per-user watched, favourite and resume state without deleting media.";

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
        // Repairs missed events after downtime shortly before the 03:00 reclamation task (P3.T8).
        new()
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromMinutes(150).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RetentionCompletionService>()
            .RefreshAllAsync(progress, cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<RetentionEvaluator>()
            .EvaluateAllAsync(cancellationToken).ConfigureAwait(false);
    }
}
