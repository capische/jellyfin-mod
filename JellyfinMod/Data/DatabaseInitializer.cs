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
            // Tracked updates. They were required while the plugin compiled against EF Core 9 and the host ran
            // EF Core 10 (the bulk-update setter type differs, and the type-load failure stopped the server); the
            // plugin now compiles against the host's own EF Core 10.0.11, so ExecuteUpdate would bind as well.
            var stranded = await database.ReconciliationRuns.Where(run => run.Status == "running")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var run in stranded)
            {
                run.Status = "interrupted";
                run.CompletedAt = DateTime.UtcNow;
            }

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var interrupted = stranded.Count;
            if (interrupted > 0)
                logger.LogWarning("JellyfinMod marked {Count} unfinished reconciliation runs as interrupted", interrupted);
            await ReconciliationRunHistory.PruneAsync(database, cancellationToken).ConfigureAwait(false);
            var quarantined = await database.Entries.CountAsync(entry => entry.TargetLibraryId == null ||
                (entry.MetadataJson == null && entry.JellyfinItemId == null), cancellationToken).ConfigureAwait(false);
            if (quarantined > 0)
                logger.LogWarning("JellyfinMod preserved {Count} legacy entries without library scope or metadata. They are quarantined from browsing; an administrator must repair these records before they can be shown. No destination library was guessed.", quarantined);
            await ImportXmlSettingsAsync(scope.ServiceProvider, database, cancellationToken).ConfigureAwait(false);
            IsReady = true;
            logger.LogInformation("JellyfinMod database migrations applied");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A plugin database failure must not prevent Jellyfin and its library from starting.
            logger.LogError(exception, "JellyfinMod database initialization failed; plugin health is unavailable");
        }
    }

    /// <summary>
    /// Moves discovery and seed-protection settings out of the XML configuration (P7.S7). A failure is logged;
    /// the import writes the database first and empties the XML only after that save succeeded, so readers keep
    /// finding every value in one store or the other, and the next start tries again.
    /// </summary>
    private async Task ImportXmlSettingsAsync(IServiceProvider services, ModDbContext database, CancellationToken cancellationToken)
    {
        if (services.GetService<JellyfinMod.Services.SettingsXmlImportSource>() is not { } source ||
            services.GetService<JellyfinMod.Services.AcquisitionSecretStore>() is not { } secrets)
            return;
        try
        {
            await JellyfinMod.Services.SettingsXmlImport.RunAsync(database, source.Configuration.Current, source.Configuration.Save,
                secrets, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Log the type only: a provider message may quote a value it was writing.
            logger.LogError("JellyfinMod could not move the TMDB token and seed-protection settings out of the plugin XML ({Error}); " +
                "they stay in the XML, where discovery and seed protection keep reading them, and the move is tried again at the next start",
                exception.GetType().Name);
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
        if (cutoff is not { } oldestKept) return;
        database.ReconciliationRuns.RemoveRange(await database.ReconciliationRuns
            .Where(run => run.StartedAt < oldestKept && run.Status != "running")
            .ToListAsync(cancellationToken).ConfigureAwait(false));
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
