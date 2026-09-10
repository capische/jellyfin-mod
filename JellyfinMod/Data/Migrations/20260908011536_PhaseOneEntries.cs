using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseOneEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Entries_MediaType_TmdbId",
                table: "Entries");

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "Entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    StillPath = table.Column<string>(type: "TEXT", nullable: true),
                    AirDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RuntimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_MediaType_TmdbId_TargetLibraryId",
                table: "Entries",
                columns: new[] { "MediaType", "TmdbId", "TargetLibraryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes",
                columns: new[] { "EntryId", "TmdbId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Episodes");

            migrationBuilder.DropIndex(
                name: "IX_Entries_MediaType_TmdbId_TargetLibraryId",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "Entries");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_MediaType_TmdbId",
                table: "Entries",
                columns: new[] { "MediaType", "TmdbId" },
                unique: true);
        }
    }
}
