using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace JellyfinMod.Data;

/// <summary>
/// Reconciles databases written by earlier 0.1.0.0 builds whose schema differs from what today's migration files
/// reconstruct, before EF applies the pending migrations. Every repair is a no-op on a database that already matches.
/// </summary>
internal static class HistoricalSchemaRepair
{
    private const string Bindings = "20260910233343_PhaseTwoBindings";
    /// <summary>The id build 7009757 recorded for the same migration, before c2f8c25 restored the deployed one.</summary>
    private const string EarlyBindings = "20260911042407_PhaseTwoBindings";
    private const string BindingProvenance = "20260914125924_PhaseTwoBindingProvenance";
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";
    private const string RetentionOperations = "20260916120000_PhaseThreeRetentionOperations";
    private const string SeasonPacks = "20261008043448_SeasonPacks";
    /// <summary>
    /// Migrations of the manual download cleanup, withdrawn before release (user decision 2026-10-02: 0.1.0.0 never deletes
    /// downloads; the cleanup tool comes in a later version). Only test instances ran them.
    /// </summary>
    private static readonly string[] WithdrawnCleanupRuns = ["20261002015135_WholeReviewDownloadCleanupRuns", "20261002031344_WholeReviewCleanupRunState"];

    /// <summary>Repairs every known historical variant; the caller then migrates.</summary>
    public static async Task RepairAsync(ModDbContext database, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (SqliteConnection)database.Database.GetDbConnection();
            if (!await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken).ConfigureAwait(false)) return;
            var applied = await AppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            await RepairBindingsIdAsync(connection, applied, cancellationToken).ConfigureAwait(false);
            await RepairBindingProvenanceAsync(connection, applied, cancellationToken).ConfigureAwait(false);
            await RepairRetentionOperationsAsync(connection, applied, cancellationToken).ConfigureAwait(false);
            await RemoveWithdrawnCleanupRunsAsync(connection, applied, cancellationToken).ConfigureAwait(false);
            await RepairReplaceTargetsAsync(connection, applied, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await database.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Build 7009757 recorded PhaseTwoBindings as <c>20260911042407_PhaseTwoBindings</c>; c2f8c25 went back to the id deployed
    /// databases carried. EF would take today's id as pending and create the binding tables again, so a database from that
    /// build never became ready (Codex re-review P1-c). When both binding tables exist with the columns that build created,
    /// the recorded id is renamed to today's in one transaction; the provenance repair below then sees that build's shape.
    /// A database whose tables do not match is left alone, so nothing unknown is declared applied.
    /// </summary>
    private static async Task RepairBindingsIdAsync(SqliteConnection connection, HashSet<string> applied, CancellationToken cancellationToken)
    {
        if (!applied.Contains(EarlyBindings) || applied.Contains(Bindings)) return;
        var entryColumns = await ColumnsAsync(connection, "EntryBindings", cancellationToken).ConfigureAwait(false);
        var episodeColumns = await ColumnsAsync(connection, "EpisodeBindings", cancellationToken).ConfigureAwait(false);
        if (!new[] { "Id", "EntryId", "JellyfinItemId", "VersionGroupId" }.All(entryColumns.Contains) ||
            !new[] { "Id", "EpisodeId", "JellyfinItemId" }.All(episodeColumns.Contains))
            return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var rename = connection.CreateCommand())
        {
            rename.Transaction = transaction;
            rename.CommandText = "UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = $current WHERE \"MigrationId\" = $early;";
            rename.Parameters.AddWithValue("$current", Bindings);
            rename.Parameters.AddWithValue("$early", EarlyBindings);
            await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        applied.Remove(EarlyBindings);
        applied.Add(Bindings);
    }

    /// <summary>
    /// Build c2f8c25 created <c>EntryBindings.TargetLibraryId</c> and <c>EpisodeBindings.SeriesItemId</c> and
    /// <c>TargetLibraryId</c> in <c>PhaseTwoBindings</c> itself; the later <c>PhaseTwoBindingProvenance</c> adds them again
    /// and fails with a duplicate column, leaving the plugin not ready (whole-review P1 10). Such a database already holds
    /// the provenance the migration would derive, observed rather than derived: keep it, add any column still missing, fill
    /// only empty values exactly as the migration would, and record the migration as applied.
    /// </summary>
    private static async Task RepairBindingProvenanceAsync(SqliteConnection connection, IReadOnlySet<string> applied,
        CancellationToken cancellationToken)
    {
        if (!applied.Contains(Bindings) || applied.Contains(BindingProvenance)) return;
        var entryColumns = await ColumnsAsync(connection, "EntryBindings", cancellationToken).ConfigureAwait(false);
        var episodeColumns = await ColumnsAsync(connection, "EpisodeBindings", cancellationToken).ConfigureAwait(false);
        var present = new[]
        {
            entryColumns.Contains("TargetLibraryId"), episodeColumns.Contains("SeriesItemId"), episodeColumns.Contains("TargetLibraryId")
        };
        // Today's schema lacks all three: the migration itself applies.
        if (!present.Any(value => value)) return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!present[0]) await ExecuteAsync(connection, transaction, AddGuid("EntryBindings", "TargetLibraryId"), cancellationToken).ConfigureAwait(false);
        if (!present[1]) await ExecuteAsync(connection, transaction, AddGuid("EpisodeBindings", "SeriesItemId"), cancellationToken).ConfigureAwait(false);
        if (!present[2]) await ExecuteAsync(connection, transaction, AddGuid("EpisodeBindings", "TargetLibraryId"), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE EntryBindings SET VersionGroupId = JellyfinItemId WHERE VersionGroupId IS NULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"UPDATE EntryBindings SET TargetLibraryId = COALESCE((SELECT TargetLibraryId FROM Entries WHERE Entries.Id = EntryBindings.EntryId), '{EmptyGuid}') " +
            $"WHERE TargetLibraryId IS NULL OR TargetLibraryId = '{EmptyGuid}';", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE EpisodeBindings SET SeriesItemId = COALESCE((SELECT owner.JellyfinItemId FROM Episodes episode JOIN Entries owner " +
            $"ON owner.Id = episode.EntryId WHERE episode.Id = EpisodeBindings.EpisodeId), '{EmptyGuid}') " +
            $"WHERE SeriesItemId IS NULL OR SeriesItemId = '{EmptyGuid}';", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE EpisodeBindings SET TargetLibraryId = COALESCE((SELECT owner.TargetLibraryId FROM Episodes episode JOIN Entries owner " +
            $"ON owner.Id = episode.EntryId WHERE episode.Id = EpisodeBindings.EpisodeId), '{EmptyGuid}') " +
            $"WHERE TargetLibraryId IS NULL OR TargetLibraryId = '{EmptyGuid}';", cancellationToken).ConfigureAwait(false);
        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($id, $version);";
            record.Parameters.AddWithValue("$id", BindingProvenance);
            record.Parameters.AddWithValue("$version", "10.0.11");
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>TargetLibraryId</c> and <c>StorageIdentity</c> (36ec0ac) and <c>ActionId</c> (698f1d9) were added by editing the
    /// already-applied <c>PhaseThreeRetentionOperations</c>, so a database that applied an earlier form of it lacks them, and
    /// the later audit rebuild copied each missing column's name as text into the rebuilt table (EF quotes identifiers, and
    /// SQLite reads an unknown quoted identifier as a string). Missing columns are added; every action identity is made a
    /// valid one of its own; a library is taken from the operation's entry; and an open operation whose storage identity
    /// was never recorded is failed, so recovery never acts on a file whose provenance cannot be established
    /// (whole-review chunk 3c, P2 2). A no-op on a database that already matches.
    /// </summary>
    private static async Task RepairRetentionOperationsAsync(SqliteConnection connection, IReadOnlySet<string> applied,
        CancellationToken cancellationToken)
    {
        if (!applied.Contains(RetentionOperations)) return;
        var columns = await ColumnsAsync(connection, "RetentionOperations", cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("ActionId"))
        {
            await ExecuteAsync(connection, transaction, AddGuid("RetentionOperations", "ActionId"), cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction,
                "CREATE INDEX IF NOT EXISTS \"IX_RetentionOperations_ActionId\" ON \"RetentionOperations\" (\"ActionId\");",
                cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("TargetLibraryId"))
            await ExecuteAsync(connection, transaction, AddGuid("RetentionOperations", "TargetLibraryId"), cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("StorageIdentity"))
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE \"RetentionOperations\" ADD COLUMN \"StorageIdentity\" TEXT NOT NULL DEFAULT '';", cancellationToken)
                .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE RetentionOperations SET ActionId = Id " +
            $"WHERE ActionId IS NULL OR ActionId IN ('', 'ActionId', '{EmptyGuid}');", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE RetentionOperations SET TargetLibraryId = COALESCE((SELECT TargetLibraryId FROM Entries " +
            $"WHERE Entries.Id = RetentionOperations.EntryId AND Entries.TargetLibraryId IS NOT NULL), '{EmptyGuid}') " +
            $"WHERE TargetLibraryId IS NULL OR TargetLibraryId IN ('', 'TargetLibraryId', '{EmptyGuid}');", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "UPDATE RetentionOperations SET StorageIdentity = '' WHERE StorageIdentity IS NULL OR StorageIdentity = 'StorageIdentity';",
            cancellationToken).ConfigureAwait(false);
        await using (var fail = connection.CreateCommand())
        {
            fail.Transaction = transaction;
            fail.CommandText = "UPDATE RetentionOperations SET State = 'failed', Reason = 'legacy_provenance_unknown', CompletedAt = $now " +
                "WHERE State IN ('prepared', 'unlinked') AND StorageIdentity = '';";
            fail.Parameters.AddWithValue("$now", DateTime.UtcNow);
            await fail.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>ImportOperations.ReplaceTargets</c> was added by editing the already-applied <c>SeasonPacks</c> migration (Codex pack
    /// re-review 2, P1 1), so a database that applied its earlier form lacks the column and every import read fails. The column
    /// is added empty: a replace import without recorded targets removes nothing and finishes done, so recovery never invents
    /// a file to delete. <c>ImportOperations.DestinationFingerprint</c> joined the same migration later (Codex review of the
    /// user fixes, 2026-10-09) and is added empty the same way: an import without it never lends its label's resolution to a
    /// file. A no-op on a database that already has them, or that has not applied <c>SeasonPacks</c> yet (the migration itself
    /// then creates them).
    /// </summary>
    private static async Task RepairReplaceTargetsAsync(SqliteConnection connection, IReadOnlySet<string> applied,
        CancellationToken cancellationToken)
    {
        if (!applied.Contains(SeasonPacks)) return;
        var columns = await ColumnsAsync(connection, "ImportOperations", cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0 || columns.Contains("ReplaceTargets") && columns.Contains("DestinationFingerprint")) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("ReplaceTargets"))
            await ExecuteAsync(connection, transaction, "ALTER TABLE \"ImportOperations\" ADD COLUMN \"ReplaceTargets\" TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("DestinationFingerprint"))
            await ExecuteAsync(connection, transaction, "ALTER TABLE \"ImportOperations\" ADD COLUMN \"DestinationFingerprint\" TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string AddGuid(string table, string column) =>
        $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT NOT NULL DEFAULT '{EmptyGuid}';";

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A database that ran the withdrawn cleanup-run migrations has their table and history rows, which this build's model
    /// does not know. Both go in one transaction, so the database matches this build's migrations exactly and the later
    /// version can add the table again from its own migration. The table only held test runs of the withdrawn tool.
    /// </summary>
    private static async Task RemoveWithdrawnCleanupRunsAsync(SqliteConnection connection, HashSet<string> applied,
        CancellationToken cancellationToken)
    {
        // Only by the withdrawn ids: a later version that adds the table again under its own migration keeps it.
        if (!WithdrawnCleanupRuns.Any(applied.Contains)) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE IF EXISTS \"DownloadCleanupRuns\";";
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var migration in WithdrawnCleanupRuns)
        {
            await using var forget = connection.CreateCommand();
            forget.Transaction = transaction;
            forget.CommandText = "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $id;";
            forget.Parameters.AddWithValue("$id", migration);
            await forget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var migration in WithdrawnCleanupRuns) applied.Remove(migration);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<HashSet<string>> AppliedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\";";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>The table's column names; empty when the table does not exist.</summary>
    internal static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''", StringComparison.Ordinal)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }
}
