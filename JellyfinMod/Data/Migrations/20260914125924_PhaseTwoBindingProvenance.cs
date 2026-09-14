using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTwoBindingProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SeriesItemId",
                table: "EpisodeBindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TargetLibraryId",
                table: "EpisodeBindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TargetLibraryId",
                table: "EntryBindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql(
                "UPDATE EntryBindings SET VersionGroupId = JellyfinItemId WHERE VersionGroupId IS NULL;");
            migrationBuilder.Sql(
                "UPDATE EntryBindings SET TargetLibraryId = COALESCE(" +
                "(SELECT TargetLibraryId FROM Entries WHERE Entries.Id = EntryBindings.EntryId), " +
                "'00000000-0000-0000-0000-000000000000');");
            migrationBuilder.Sql(
                "UPDATE EpisodeBindings SET SeriesItemId = COALESCE(" +
                "(SELECT owner.JellyfinItemId FROM Episodes episode JOIN Entries owner ON owner.Id = episode.EntryId " +
                "WHERE episode.Id = EpisodeBindings.EpisodeId), '00000000-0000-0000-0000-000000000000'), " +
                "TargetLibraryId = COALESCE((SELECT owner.TargetLibraryId FROM Episodes episode " +
                "JOIN Entries owner ON owner.Id = episode.EntryId WHERE episode.Id = EpisodeBindings.EpisodeId), " +
                "'00000000-0000-0000-0000-000000000000');");

            migrationBuilder.AlterColumn<Guid>(
                name: "VersionGroupId",
                table: "EntryBindings",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesItemId",
                table: "EpisodeBindings");

            migrationBuilder.DropColumn(
                name: "TargetLibraryId",
                table: "EpisodeBindings");

            migrationBuilder.DropColumn(
                name: "TargetLibraryId",
                table: "EntryBindings");

            migrationBuilder.AlterColumn<Guid>(
                name: "VersionGroupId",
                table: "EntryBindings",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");
        }
    }
}
