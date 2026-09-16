using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeRetentionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetentionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Inspected = table.Column<int>(type: "INTEGER", nullable: false),
                    Eligible = table.Column<int>(type: "INTEGER", nullable: false),
                    Blocked = table.Column<int>(type: "INTEGER", nullable: false),
                    Reclaimed = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    Interrupted = table.Column<int>(type: "INTEGER", nullable: false),
                    LogicalBytesUnlinked = table.Column<long>(type: "INTEGER", nullable: false),
                    PhysicalBytesReleased = table.Column<long>(type: "INTEGER", nullable: false),
                    PhysicalBytesUnknown = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionRuns_StartedAt",
                table: "RetentionRuns",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RetentionRuns");
        }
    }
}
