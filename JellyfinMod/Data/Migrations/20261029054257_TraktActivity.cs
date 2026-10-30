using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// Settings › Trakt's per-title activity (Phase 13, D127): one row per title, direction and user with its latest state,
    /// kept 30 days; it replaces the 20 runs kept in trakt-activity.json. One new table; nothing existing is rebuilt.
    /// </summary>
    public partial class TraktActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraktActivity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Detail = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraktActivity", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraktActivity_At",
                table: "TraktActivity",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_TraktActivity_UserId_ItemId_Direction",
                table: "TraktActivity",
                columns: new[] { "UserId", "ItemId", "Direction" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraktActivity");
        }
    }
}
