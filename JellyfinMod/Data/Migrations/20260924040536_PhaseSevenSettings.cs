using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// P7.S7: discovery and seed protection move into the settings row (the XML import runs at startup), plus the
    /// retention revision and the first-run setup timestamps. Existing rows start at revision 1 and read seed
    /// state through the selected acquisition client unless the import finds a separate endpoint.
    /// </summary>
    public partial class PhaseSevenSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscoveryRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscoveryVerifiedAt",
                table: "AcquisitionSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryVerifiedRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SeedProtectionPasswordRef",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeedProtectionRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SeedProtectionRpcUrl",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeedProtectionSource",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "acquisitionClient");

            migrationBuilder.AddColumn<string>(
                name: "SeedProtectionUsername",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupCompletedAt",
                table: "AcquisitionSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupDismissedAt",
                table: "AcquisitionSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TmdbReadAccessTokenRef",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveryRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "DiscoveryVerifiedAt",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "DiscoveryVerifiedRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "RetentionRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedProtectionPasswordRef",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedProtectionRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedProtectionRpcUrl",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedProtectionSource",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedProtectionUsername",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SetupCompletedAt",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SetupDismissedAt",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "TmdbReadAccessTokenRef",
                table: "AcquisitionSettings");
        }
    }
}
