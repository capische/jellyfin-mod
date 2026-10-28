using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseNineRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RatingsFetches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RetryAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Manual = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingsFetches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RatingsFetches_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RatingsProviderStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Blocker = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BreakerUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BreakerReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    BudgetDay = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BudgetUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunFinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunFetched = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunStopReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingsProviderStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RatingsSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiKeyRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RefreshDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DailyBudget = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultSources = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingsSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TitleRatings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Scale = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Votes = table.Column<int>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleRatings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TitleRatings_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_AttemptedAt",
                table: "RatingsFetches",
                column: "AttemptedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_EntryId",
                table: "RatingsFetches",
                column: "EntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleRatings_EntryId_Source_Provider",
                table: "TitleRatings",
                columns: new[] { "EntryId", "Source", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RatingsFetches");

            migrationBuilder.DropTable(
                name: "RatingsProviderStates");

            migrationBuilder.DropTable(
                name: "RatingsSettings");

            migrationBuilder.DropTable(
                name: "TitleRatings");
        }
    }
}
