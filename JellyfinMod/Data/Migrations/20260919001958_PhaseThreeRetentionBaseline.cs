using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeRetentionBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BaselineAt",
                table: "RetentionEvaluations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "RequiresFreshCompletion",
                table: "RetentionEvaluations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing targets get a conservative floor at upgrade time, so no deadline moves earlier.
            migrationBuilder.Sql("UPDATE \"RetentionEvaluations\" SET \"BaselineAt\" = strftime('%Y-%m-%d %H:%M:%f', 'now');");

            // Targets with no current representation (reclaimed or externally removed before this
            // upgrade) must not keep a schedule that re-acquired media would inherit.
            migrationBuilder.Sql(
                """
                UPDATE "RetentionEvaluations"
                SET "State" = 'waiting', "Reason" = 'representation_reset', "CompletionBasisAt" = NULL,
                    "EligibleAt" = NULL, "Deadline" = NULL, "RequiresFreshCompletion" = 1
                WHERE ("EpisodeId" IS NULL AND "TargetId" NOT IN (SELECT "EntryId" FROM "EntryBindings"))
                   OR ("EpisodeId" IS NOT NULL AND "TargetId" NOT IN (SELECT "EpisodeId" FROM "EpisodeBindings"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineAt",
                table: "RetentionEvaluations");

            migrationBuilder.DropColumn(
                name: "RequiresFreshCompletion",
                table: "RetentionEvaluations");
        }
    }
}
