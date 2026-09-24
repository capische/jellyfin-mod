using Microsoft.Data.Sqlite;

namespace JellyfinMod.Data;

/// <summary>
/// Retries a database step that failed only because another writer held SQLite's write lock past the busy timeout
/// (<c>database is locked</c>). Microsoft.Data.Sqlite waits by polling every 150 ms, which is not fair: on a slow disk a
/// writer that commits back to back can keep a second writer out for longer than the timeout. The step must be safe
/// to run again from the start; anything else is rethrown at once.
/// </summary>
public static class SqliteBusy
{
    /// <summary>Attempts after the first, and the pause before each.</summary>
    private static readonly TimeSpan[] Pauses = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)];

    /// <summary>Whether an exception is SQLite's busy or locked error, directly or inside an EF Core update error.</summary>
    public static bool IsBusy(Exception error) => error switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        { InnerException: { } inner } => IsBusy(inner),
        _ => false
    };

    /// <summary>Runs <paramref name="step"/>, running it again after a pause when it fails with a busy database.</summary>
    public static async Task RetryAsync(Func<Task> step, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await step().ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (attempt < Pauses.Length && IsBusy(error))
            {
                await Task.Delay(Pauses[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs <paramref name="step"/> with the same retries and returns its result.</summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> step, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await step().ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < Pauses.Length && IsBusy(error))
            {
                await Task.Delay(Pauses[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
