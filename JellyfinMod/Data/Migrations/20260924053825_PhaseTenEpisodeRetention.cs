using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenEpisodeRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes");

            migrationBuilder.AddColumn<int>(
                name: "ReclaimAfterDays",
                table: "Episodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionPolicy",
                table: "Episodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_Position",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" },
                unique: true,
                filter: "\"TmdbId\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes",
                columns: new[] { "EntryId", "TmdbId" },
                unique: true,
                filter: "\"TmdbId\" <> 0");
        }

        /// <summary>
        /// Forward-only (RET-R3). Once reconciliation has created position rows, several rows of one series share TmdbId 0,
        /// so the old unique (EntryId, TmdbId) index cannot be recreated, and SQLite under the pinned tooling cannot drop
        /// the columns in a script. The rollback is the database backup taken before the deploy (PHASE10.md, Rollback).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException(
                "PhaseTenEpisodeRetention is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
