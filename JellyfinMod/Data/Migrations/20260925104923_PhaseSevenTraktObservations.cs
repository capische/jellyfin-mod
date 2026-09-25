using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseSevenTraktObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraktObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraktObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraktObservations_UserId_JellyfinItemId",
                table: "TraktObservations",
                columns: new[] { "UserId", "JellyfinItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraktObservations_UserId_SeasonId",
                table: "TraktObservations",
                columns: new[] { "UserId", "SeasonId" });

            migrationBuilder.CreateIndex(
                name: "IX_TraktObservations_UserId_SeriesId",
                table: "TraktObservations",
                columns: new[] { "UserId", "SeriesId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraktObservations");
        }
    }
}
