using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// TMDB's guest stars and crew of each episode (P1.FU15): one nullable column, so SQLite adds it without rebuilding the
    /// table. Null until the series' list is loaded again, which the next opening of the series does once.
    /// </summary>
    public partial class EpisodeCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreditsJson",
                table: "Episodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "EpisodeCredits is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
