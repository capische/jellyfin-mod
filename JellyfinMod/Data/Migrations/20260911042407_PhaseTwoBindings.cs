using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTwoBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes");

            migrationBuilder.CreateTable(
                name: "EntryBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VersionGroupId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EntryBindings_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EpisodeBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpisodeBindings_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryBindings_EntryId",
                table: "EntryBindings",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryBindings_JellyfinItemId",
                table: "EntryBindings",
                column: "JellyfinItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeBindings_EpisodeId",
                table: "EpisodeBindings",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeBindings_JellyfinItemId",
                table: "EpisodeBindings",
                column: "JellyfinItemId",
                unique: true);

            migrationBuilder.Sql(
                "INSERT INTO EntryBindings (Id, EntryId, JellyfinItemId, VersionGroupId) " +
                "SELECT JellyfinItemId, Id, JellyfinItemId, JellyfinItemId FROM Entries " +
                "WHERE JellyfinItemId IS NOT NULL;");
            migrationBuilder.Sql(
                "INSERT INTO EpisodeBindings (Id, EpisodeId, JellyfinItemId) " +
                "SELECT JellyfinItemId, Id, JellyfinItemId FROM Episodes WHERE JellyfinItemId IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntryBindings");

            migrationBuilder.DropTable(
                name: "EpisodeBindings");

            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" },
                unique: true);
        }
    }
}
