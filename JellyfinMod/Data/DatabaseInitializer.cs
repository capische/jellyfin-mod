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
