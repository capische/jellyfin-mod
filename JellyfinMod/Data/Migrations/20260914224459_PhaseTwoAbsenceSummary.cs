using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTwoAbsenceSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IncompleteLibraries",
                table: "ReconciliationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MissingItems",
                table: "ReconciliationRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MediaPath",
                table: "EpisodeBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageIdentity",
                table: "EpisodeBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaPath",
                table: "EntryBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorageIdentity",
                table: "EntryBindings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncompleteLibraries",
                table: "ReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "MissingItems",
                table: "ReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "MediaPath",
                table: "EpisodeBindings");

            migrationBuilder.DropColumn(
                name: "StorageIdentity",
                table: "EpisodeBindings");

            migrationBuilder.DropColumn(
                name: "MediaPath",
                table: "EntryBindings");

            migrationBuilder.DropColumn(
                name: "StorageIdentity",
                table: "EntryBindings");
        }
    }
}
