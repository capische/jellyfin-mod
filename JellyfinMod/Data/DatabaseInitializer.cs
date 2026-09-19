using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Data;

/// <summary>Applies pending plugin migrations before the server starts accepting requests.</summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    /// <summary>Gets a value indicating whether the plugin database is ready.</summary>
    public bool IsReady { get; private set; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
            await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            // Only this process runs reconciliation, so a row still "running" belongs to a process that
            // stopped before finishing it (P2.R10).
            var interrupted = await database.ReconciliationRuns.Where(run => run.Status == "running")
                .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Status, "interrupted")
                    .SetProperty(run => run.CompletedAt, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
            if (interrupted > 0)
                logger.LogWarning("JellyfinMod marked {Count} unfinished reconciliation runs as interrupted", interrupted);
            await ReconciliationRunHistory.PruneAsync(database, cancellationToken).ConfigureAwait(false);
            var quarantined = await database.Entries.CountAsync(entry => entry.TargetLibraryId == null ||
                (entry.MetadataJson == null && entry.JellyfinItemId == null), cancellationToken).ConfigureAwait(false);
            if (quarantined > 0)
                logger.LogWarning("JellyfinMod preserved {Count} legacy entries without library scope or metadata. They are quarantined from browsing; an administrator must repair these records before they can be shown. No destination library was guessed.", quarantined);
            IsReady = true;
            logger.LogInformation("JellyfinMod database migrations applied");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A plugin database failure must not prevent Jellyfin and its library from starting.
            logger.LogError(exception, "JellyfinMod database initialization failed; plugin health is unavailable");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Keeps the reconciliation run history bounded (P2.R10).</summary>
internal static class ReconciliationRunHistory
{
    /// <summary>The number of most recent runs kept.</summary>
    public const int Keep = 50;

    /// <summary>Deletes all but the most recent runs.</summary>
    public static async Task PruneAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        var cutoff = await database.ReconciliationRuns.OrderByDescending(run => run.StartedAt)
            .Skip(Keep - 1).Select(run => (DateTime?)run.StartedAt).FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cutoff is { } oldestKept)
            await database.ReconciliationRuns.Where(run => run.StartedAt < oldestKept && run.Status != "running")
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
