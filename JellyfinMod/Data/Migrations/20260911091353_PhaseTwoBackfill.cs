using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTwoBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TotalItems = table.Column<int>(type: "INTEGER", nullable: false),
                    ScannedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedEntries = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedBindings = table.Column<int>(type: "INTEGER", nullable: false),
                    UnchangedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    UnmatchedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    ConflictedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_StartedAt",
                table: "ReconciliationRuns",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationRuns");
        }
    }
}
