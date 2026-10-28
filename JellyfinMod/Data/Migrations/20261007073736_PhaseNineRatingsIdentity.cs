using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseNineRatingsIdentity : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Review 2026-10-07 round 2: provider links are no longer kept (P1), and a fetch attempt belongs to the title identity
        /// rather than to one entry (P2 5), without a per-title retry deadline (P2 4). Each title keeps its latest attempt; an
        /// attempt whose entry no longer exists has no identity to keep and goes.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "ef_ratings_attempts" AS
                SELECT f."Id", e."MediaType", e."TmdbId", f."AttemptedAt", f."Outcome", f."Error", f."Manual"
                FROM "RatingsFetches" AS f JOIN "Entries" AS e ON e."Id" = f."EntryId"
                WHERE f."Id" = (
                    SELECT f2."Id" FROM "RatingsFetches" AS f2 JOIN "Entries" AS e2 ON e2."Id" = f2."EntryId"
                    WHERE e2."MediaType" = e."MediaType" AND e2."TmdbId" = e."TmdbId"
                    ORDER BY f2."AttemptedAt" DESC, f2."Id" LIMIT 1);
                """);

            migrationBuilder.DropTable(name: "RatingsFetches");

            migrationBuilder.CreateTable(
                name: "RatingsFetches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Manual = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingsFetches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_AttemptedAt",
                table: "RatingsFetches",
                column: "AttemptedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_MediaType_TmdbId",
                table: "RatingsFetches",
                columns: new[] { "MediaType", "TmdbId" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "RatingsFetches" ("Id", "MediaType", "TmdbId", "AttemptedAt", "Outcome", "Error", "Manual")
                SELECT "Id", "MediaType", "TmdbId", "AttemptedAt", "Outcome", "Error", "Manual" FROM "ef_ratings_attempts";
                DROP TABLE "ef_ratings_attempts";
                """);

            migrationBuilder.DropColumn(
                name: "Url",
                table: "TitleRatings");
        }

        /// <inheritdoc />
        /// <remarks>Forward-only in practice: going back restores the shapes, empty of attempts and links.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RatingsFetches");

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

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_AttemptedAt",
                table: "RatingsFetches",
                column: "AttemptedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RatingsFetches_EntryId",
                table: "RatingsFetches",
                column: "EntryId",
                unique: true);

            migrationBuilder.AddColumn<string>(
                name: "Url",
                table: "TitleRatings",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }
    }
}
