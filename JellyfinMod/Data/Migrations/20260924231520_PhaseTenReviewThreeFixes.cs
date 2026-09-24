using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenReviewThreeFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileFingerprint",
                table: "EpisodeBindings",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileFingerprint",
                table: "EntryBindings",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other Phase 10 migrations (RET-R3): the rollback is the pre-deploy database backup.
            throw new NotSupportedException(
                "PhaseTenReviewThreeFixes is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
