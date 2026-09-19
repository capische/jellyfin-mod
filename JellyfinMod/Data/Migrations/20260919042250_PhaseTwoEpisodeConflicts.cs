using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTwoEpisodeConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpisodeConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservedTmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedSeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedEpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpisodeConflicts_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EpisodeConflicts_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeConflicts_EntryId",
                table: "EpisodeConflicts",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeConflicts_EpisodeId",
                table: "EpisodeConflicts",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeConflicts_JellyfinItemId",
                table: "EpisodeConflicts",
                column: "JellyfinItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpisodeConflicts");
        }
    }
}
