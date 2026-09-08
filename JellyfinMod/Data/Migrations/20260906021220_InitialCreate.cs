using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetLibraryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WatchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReclaimAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReclaimAfterDays = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_JellyfinItemId",
                table: "Entries",
                column: "JellyfinItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_MediaType_TmdbId",
                table: "Entries",
                columns: new[] { "MediaType", "TmdbId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_State",
                table: "Entries",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_History_EntryId",
                table: "History",
                column: "EntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "History");
        }
    }
}
