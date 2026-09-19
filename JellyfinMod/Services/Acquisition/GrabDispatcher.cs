using JellyfinMod.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Services.Acquisition;

/// <summary>
/// Submits held grabs when their hold ends and recovers unresolved operations after a restart (P4.A5).
/// </summary>
public sealed class GrabDispatcher(
    IServiceScopeFactory scopes,
    DatabaseInitializer readiness,
    TimeProvider time,
    ILogger<GrabDispatcher> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _recovery = Task.CompletedTask;

    /// <summary>Gets a task that completes when startup recovery has finished.</summary>
    public Task Recovery => _recovery;

    /// <summary>Submits the operation once its hold has ended, unless it was cancelled meanwhile.</summary>
    public void Schedule(Guid operationId, DateTime holdUntil)
    {
        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var delay = holdUntil - time.GetUtcNow().UtcDateTime;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token).ConfigureAwait(false);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GrabService>().DispatchAsync(operationId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Shutdown during the hold: the next start fails the held operation, nothing was sent.
            }
            catch (Exception error)
            {
                logger.LogError(error, "Dispatching grab {Operation} failed", operationId);
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _stopping.Token;
        _recovery = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; !readiness.IsReady && attempt < 600; attempt++)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                if (!readiness.IsReady) return;
                using var scope = scopes.CreateScope();
                var grabs = scope.ServiceProvider.GetRequiredService<GrabService>();
                foreach (var id in await grabs.UnresolvedAsync(token).ConfigureAwait(false))
                {
                    var operation = await grabs.RecheckAsync(id, startup: true, token).ConfigureAwait(false);
                    logger.LogInformation("Recovered grab {Operation}: {State}", id, operation?.State);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                logger.LogError(error, "Grab recovery failed; unresolved grabs stay blocked until rechecked");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
