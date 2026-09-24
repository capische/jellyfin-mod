using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenRetentionFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VersionKeeps_EntryId",
                table: "VersionKeeps");

            migrationBuilder.DropIndex(
                name: "IX_VersionKeeps_MediaPath",
                table: "VersionKeeps");

            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedDeadline",
                table: "RetentionEvaluations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IdentityUnverified",
                table: "EpisodeBindings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_VersionKeeps_EntryId_MediaPath",
                table: "VersionKeeps",
                columns: new[] { "EntryId", "MediaPath" },
                unique: true);

            // RET2-R3: a file on a TMDB-identified episode may have been bound by its number only. Until reconciliation
            // proves otherwise (a native TMDB id, or an agreeing title or air date) every such binding counts as unverified,
            // so no upgrade can replace it in the meantime; the next reconciliation clears the verified ones.
            migrationBuilder.Sql(
                "UPDATE \"EpisodeBindings\" SET \"IdentityUnverified\" = 1 WHERE \"EpisodeId\" IN " +
                "(SELECT \"Id\" FROM \"Episodes\" WHERE \"TmdbId\" <> 0);");

            // RET2-R5: a window already announced by a retention_started event is not announced again after the deploy.
            migrationBuilder.Sql(
                "UPDATE \"RetentionEvaluations\" SET \"AnnouncedDeadline\" = (" +
                "SELECT replace(replace(json_extract(h.\"Data\", '$.deadline'), 'T', ' '), 'Z', '') FROM \"History\" h " +
                "WHERE h.\"EventType\" = 'retention_started' AND h.\"EntryId\" = \"RetentionEvaluations\".\"EntryId\" AND " +
                "json_valid(h.\"Data\") AND upper(ifnull(json_extract(h.\"Data\", '$.episodeId'), '')) = " +
                "upper(ifnull(\"RetentionEvaluations\".\"EpisodeId\", '')) ORDER BY h.\"CreatedAt\" DESC LIMIT 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other Phase 10 migrations (RET-R3): the rollback is the pre-deploy database backup.
            throw new NotSupportedException(
                "PhaseTenRetentionFixes is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
