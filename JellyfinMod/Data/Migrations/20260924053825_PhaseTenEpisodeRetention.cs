using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenEpisodeRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes");

            migrationBuilder.AddColumn<int>(
                name: "ReclaimAfterDays",
                table: "Episodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionPolicy",
                table: "Episodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_Position",
                table: "Episodes",
                columns: new[] { "EntryId", "SeasonNumber", "EpisodeNumber" },
                unique: true,
                filter: "\"TmdbId\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes",
                columns: new[] { "EntryId", "TmdbId" },
                unique: true,
                filter: "\"TmdbId\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_Position",
                table: "Episodes");

            migrationBuilder.DropIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "ReclaimAfterDays",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "RetentionPolicy",
                table: "Episodes");

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_EntryId_TmdbId",
                table: "Episodes",
                columns: new[] { "EntryId", "TmdbId" },
                unique: true);
        }
    }
}
