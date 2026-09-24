using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JellyfinMod.Data;

/// <summary>
/// Records who holds the plugin database's single write lock and for how long, so a <c>database is locked</c> failure
/// names the writer it waited for instead of leaving it to a guess. SQLite in WAL mode lets any number of readers run
/// beside one writer; a second writer waits (Microsoft.Data.Sqlite retries for the command timeout) and then fails with
/// error 5. Nothing here changes what is written: it measures the wait for and the hold of the write lock, per operation.
/// </summary>
public static class SqliteWriteDiagnostics
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();
    private static readonly ConcurrentDictionary<object, Hold> Holders = new();
    private static readonly ConcurrentDictionary<DbConnection, long> Waiting = new();
    private static long totalSlowHolds;
    private static readonly ConcurrentDictionary<string, WriteStats> Statistics = new(StringComparer.Ordinal);

    /// <summary>Holds and waits at or above this are logged.</summary>
    public static TimeSpan SlowThreshold { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets where slow holds, long waits and lock failures are reported; null reports nothing.</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>Gets the interceptors to register on every plugin database context.</summary>
    public static IInterceptor[] Interceptors { get; } = [new TransactionProbe(), new CommandProbe()];

    /// <summary>Gets the number of write-lock holds at or above <see cref="SlowThreshold"/> since start.</summary>
    public static long SlowHolds => Interlocked.Read(ref totalSlowHolds);

    /// <summary>
    /// Per operation: how many write statements or transactions ran, and the total and longest time each took from
    /// asking for the write lock to releasing it (the wait for the lock included).
    /// </summary>
    public static IReadOnlyDictionary<string, (long Count, double TotalSeconds, double MaxSeconds)> Snapshot() =>
        Statistics.ToDictionary(pair => pair.Key, pair => pair.Value.Read(), StringComparer.Ordinal);

    /// <summary>Forgets the statistics (a test run reads them per phase).</summary>
    public static void ResetStatistics() => Statistics.Clear();

    private static void Measure(string operation, TimeSpan elapsed) =>
        Statistics.GetOrAdd(operation, static _ => new WriteStats()).Add(elapsed);

    /// <summary>Names the plugin operation whose database writes follow on this async flow, until the scope ends.</summary>
    public static IDisposable Operation(string name)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = previous is null ? name : previous + " > " + name;
        return new Restore(previous);
    }

    private static string OperationName => CurrentOperation.Value ?? "unnamed";

    private static void Acquired(object holder, long waitStarted)
    {
        var now = Stopwatch.GetTimestamp();
        Holders[holder] = new Hold(OperationName, now, waitStarted);
        var waited = Stopwatch.GetElapsedTime(waitStarted, now);
        if (waited >= SlowThreshold)
            Logger?.LogWarning("JellyfinMod database: {Operation} waited {Waited:F1} s for the write lock", OperationName,
                waited.TotalSeconds);
    }

    private static void Released(object holder)
    {
        if (!Holders.TryRemove(holder, out var hold)) return;
        var held = Stopwatch.GetElapsedTime(hold.Since);
        Measure(hold.Operation, Stopwatch.GetElapsedTime(hold.AskedAt));
        if (held < SlowThreshold) return;
        Interlocked.Increment(ref totalSlowHolds);
        Logger?.LogWarning("JellyfinMod database: {Operation} held the write lock for {Held:F1} s", hold.Operation, held.TotalSeconds);
    }

    private static void Failed(object? self, Exception exception, long waitStarted)
    {
        if (exception is not SqliteException { SqliteErrorCode: 5 or 6 } sqlite) return;
        var waited = Stopwatch.GetElapsedTime(waitStarted);
        var now = Stopwatch.GetTimestamp();
        var holders = Holders.Where(pair => !ReferenceEquals(pair.Key, self))
            .Select(pair => $"{pair.Value.Operation} for {Stopwatch.GetElapsedTime(pair.Value.Since, now).TotalSeconds:F1} s")
            .ToArray();
        Logger?.LogWarning(
            "JellyfinMod database: {Operation} gave up after {Waited:F1} s ({Error}); write lock now held by: {Holders}; " +
            "{Waiters} other writers waiting",
            OperationName, waited.TotalSeconds, sqlite.SqliteExtendedErrorCode,
            holders.Length == 0 ? "no plugin writer (released just now, or a process outside the plugin)" : string.Join(", ", holders),
            Math.Max(0, Waiting.Count - 1));
    }

    private static bool IsWrite(DbCommand command)
    {
        var text = command.CommandText.AsSpan().TrimStart();
        return text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Hold(string Operation, long Since, long AskedAt);

    private sealed class WriteStats
    {
        private long count;
        private long totalTicks;
        private long maxTicks;

        public void Add(TimeSpan elapsed)
        {
            Interlocked.Increment(ref count);
            Interlocked.Add(ref totalTicks, elapsed.Ticks);
            long seen;
            while (elapsed.Ticks > (seen = Interlocked.Read(ref maxTicks)) &&
                   Interlocked.CompareExchange(ref maxTicks, elapsed.Ticks, seen) != seen)
            {
            }
        }

        public (long, double, double) Read() => (Interlocked.Read(ref count), TimeSpan.FromTicks(Interlocked.Read(ref totalTicks)).TotalSeconds,
            TimeSpan.FromTicks(Interlocked.Read(ref maxTicks)).TotalSeconds);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentOperation.Value = previous;
    }

    /// <summary>Explicit and SaveChanges transactions: BEGIN IMMEDIATE takes the write lock, commit or rollback frees it.</summary>
    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            Waiting[connection] = Stopwatch.GetTimestamp();
            return result;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Waiting[connection] = Stopwatch.GetTimestamp();
            return ValueTask.FromResult(result);
        }

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result)
        {
            Acquired(result, Waiting.TryRemove(connection, out var started) ? started : Stopwatch.GetTimestamp());
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Acquired(result, Waiting.TryRemove(connection, out var started) ? started : Stopwatch.GetTimestamp());
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            Released(transaction);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Released(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
            Released(transaction);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Released(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
        {
            var started = transaction.Connection is { } connection && Waiting.TryRemove(connection, out var value)
                ? value
                : Stopwatch.GetTimestamp();
            Failed(transaction, eventData.Exception, started);
            Released(transaction);
        }

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            TransactionFailed(transaction, eventData);
            return Task.CompletedTask;
        }
    }

    /// <summary>A write outside a transaction holds the write lock for the statement itself.</summary>
    private sealed class CommandProbe : DbCommandInterceptor
    {
        private static readonly ConditionalWeakTable<DbCommand, StrongBox<long>> Started = new();

        private static void Start(DbCommand command)
        {
            var now = Stopwatch.GetTimestamp();
            Started.AddOrUpdate(command, new StrongBox<long>(now));
            if (command.Transaction is null && IsWrite(command) && command.Connection is { } connection)
                Waiting[connection] = now;
        }

        private static void Done(DbCommand command)
        {
            if (command.Transaction is not null || !IsWrite(command) || command.Connection is not { } connection) return;
            if (!Waiting.TryRemove(connection, out var started)) return;
            // The statement's own wait and hold cannot be told apart; report it as a hold only when it was slow.
            Holders[connection] = new Hold(OperationName, started, started);
            Released(connection);
        }

        private static void Fail(DbCommand command, Exception exception)
        {
            var started = Started.TryGetValue(command, out var box) ? box.Value : Stopwatch.GetTimestamp();
            Failed(command.Connection, exception, started);
            if (command.Connection is { } connection) Waiting.TryRemove(connection, out _);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Start(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Start(command);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Done(command);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Done(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Start(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Start(command);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Done(command);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Done(command);
            return ValueTask.FromResult(result);
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Fail(command, eventData.Exception);

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Fail(command, eventData.Exception);
            return Task.CompletedTask;
        }
    }
}
