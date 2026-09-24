using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenMovieBacklog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Decision 12 (2026-09-24): movies follow the episode backlog rule. Only a watch at or after the later of a movie's
            // first tracking (its evaluation baseline) and the first-ever switch-on counts. A movie scheduled by the Phase 3
            // rule from a watch before that floor stops being due now, before any run can act on the old schedule, and its
            // History says why. A movie scheduled from a watch after the floor is left as it is.
            const string floor =
                "max(e.\"BaselineAt\", coalesce((SELECT coalesce(p.\"FirstEnabledAt\", p.\"EnabledAt\") FROM \"RetentionPolicySnapshots\" p LIMIT 1), e.\"BaselineAt\"))";
            const string backlog =
                "e.\"EpisodeId\" IS NULL AND e.\"State\" = 'scheduled' AND e.\"CompletionBasisAt\" IS NOT NULL AND " +
                "e.\"CompletionBasisAt\" < " + floor;
            migrationBuilder.Sql(
                "INSERT INTO \"History\" (\"Id\", \"EntryId\", \"EventType\", \"Summary\", \"Data\", \"CreatedAt\") " +
                "SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || " +
                "hex(randomblob(2)) || '-' || hex(randomblob(6))), e.\"EntryId\", 'retention_rule_changed', " +
                "'No longer due: movies now count only a watch after the movie was tracked and retention was first switched on " +
                "(decision 12); this one was watched before that', " +
                "json_object('reason', 'decision_12_movie_backlog', 'previousDeadline', e.\"Deadline\", " +
                "'completionBasis', e.\"CompletionBasisAt\", 'floor', " + floor + "), " +
                "strftime('%Y-%m-%d %H:%M:%f', 'now') FROM \"RetentionEvaluations\" e WHERE " + backlog + ";");
            migrationBuilder.Sql(
                "UPDATE \"RetentionEvaluations\" AS e SET \"State\" = 'waiting', \"Reason\" = 'waiting_for_completion', " +
                "\"CompletionBasisAt\" = NULL, \"EligibleAt\" = NULL, \"Deadline\" = NULL WHERE " + backlog + ";");
            // Every movie now needs a new watch, like every episode.
            migrationBuilder.Sql(
                "UPDATE \"RetentionEvaluations\" SET \"RequiresFreshCompletion\" = 1 WHERE \"EpisodeId\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other Phase 10 migrations (RET-R3): the rollback is the pre-deploy database backup.
            throw new NotSupportedException(
                "PhaseTenMovieBacklog is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
