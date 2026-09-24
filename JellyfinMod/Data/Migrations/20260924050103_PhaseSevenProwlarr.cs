using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// P7.S9: Prowlarr sources and the columns that let an indexer belong to one. Existing indexers are manual.
    /// </summary>
    public partial class PhaseSevenProwlarr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminOverridesJson",
                table: "AcquisitionIndexers",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManagedBy",
                table: "AcquisitionIndexers",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<int>(
                name: "ProwlarrIndexerId",
                table: "AcquisitionIndexers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProwlarrRemovedAt",
                table: "AcquisitionIndexers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProwlarrSourceId",
                table: "AcquisitionIndexers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProwlarrSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ApiKeySecretRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncOutcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ConsecutiveEmptySyncs = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEmptySyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProwlarrSources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionIndexers_ProwlarrSourceId",
                table: "AcquisitionIndexers",
                column: "ProwlarrSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProwlarrSources_Name",
                table: "ProwlarrSources",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AcquisitionIndexers_ProwlarrSources_ProwlarrSourceId",
                table: "AcquisitionIndexers",
                column: "ProwlarrSourceId",
                principalTable: "ProwlarrSources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AcquisitionIndexers_ProwlarrSources_ProwlarrSourceId",
                table: "AcquisitionIndexers");

            migrationBuilder.DropTable(
                name: "ProwlarrSources");

            migrationBuilder.DropIndex(
                name: "IX_AcquisitionIndexers_ProwlarrSourceId",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "AdminOverridesJson",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "ManagedBy",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "ProwlarrIndexerId",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "ProwlarrRemovedAt",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "ProwlarrSourceId",
                table: "AcquisitionIndexers");
        }
    }
}
