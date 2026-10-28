using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// Phase 12 M30: a quality profile's Prefer (null, No Preference, for every stored profile), the quality profile for phones,
    /// the ticked TVs and the switch that fetches a missing phone copy (off). Columns are added; nothing is rebuilt. An existing
    /// install's quality profile for phones becomes its profile copied from the 1080p preset, when it has one; a profile the
    /// administrator made, a "Mobile" one included, is never chosen or changed.
    /// </summary>
    public partial class PhoneCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FetchPhoneCopies",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PhoneProfileId",
                table: "AcquisitionSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TvModels",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TvModelsRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Prefer",
                table: "AcquisitionQualityProfiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            // The 1080p preset's id (Assets/quality-presets.json). Guids are stored as upper-case text by EF Core on SQLite;
            // the comparison ignores case so a row written either way matches.
            migrationBuilder.Sql("""
                UPDATE AcquisitionSettings SET PhoneProfileId = (
                    SELECT Id FROM AcquisitionQualityProfiles WHERE upper(Id) = '49375E78-8599-44FA-9D7E-59E12A8B87DA')
                WHERE PhoneProfileId IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "PhoneCopies is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
