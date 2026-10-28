using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// The files a movie's or an episode's Replace grab may remove, fixed when the grab is admitted (Codex review of the movie
    /// Replace, P1 1). One nullable column added; nothing existing is rebuilt, so the GrabClaimRelease trigger stays.
    /// </summary>
    public partial class GrabReplaceTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplaceTargets",
                table: "GrabOperations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "GrabReplaceTargets is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
