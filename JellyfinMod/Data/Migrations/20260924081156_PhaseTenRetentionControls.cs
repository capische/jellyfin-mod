using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenRetentionControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RET-R6: databases created before Phase 2 still carry the Phase 1 unique position index, while the migration
            // chain and the model say non-unique. Recreate it non-unique everywhere, so every database has one shape:
            // position rows (TmdbId = 0) stay unique through IX_Episodes_EntryId_Position; a TMDB row and a position row
            // never share a position because Refresh, Add and reconciliation refuse to create that pair.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Episodes_EntryId_SeasonNumber_EpisodeNumber\";");
            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" });

            migrationBuilder.CreateTable(
                name: "VersionKeeps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MediaPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PhysicalIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionKeeps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VersionKeeps_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VersionKeeps_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VersionKeeps_EntryId",
                table: "VersionKeeps",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_VersionKeeps_EpisodeId",
                table: "VersionKeeps",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_VersionKeeps_MediaPath",
                table: "VersionKeeps",
                column: "MediaPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VersionKeeps_PhysicalIdentity",
                table: "VersionKeeps",
                column: "PhysicalIdentity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only (RET-R3): the rollback is the pre-deploy database backup, documented in PHASE10.md.
            throw new NotSupportedException(
                "PhaseTenRetentionControls is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
