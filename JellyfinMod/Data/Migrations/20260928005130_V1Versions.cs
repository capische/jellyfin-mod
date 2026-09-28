using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class V1Versions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupersededPath",
                table: "UpgradeOperations",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerItemId",
                table: "EpisodeBindings",
                type: "TEXT",
                nullable: true);

            // Episode upgrades are on by default now that V1 tracks an episode's versions (user, 2026-09-28). The old
            // default off was a safety block nobody chose, so existing databases turn it on too; the revision moves so a
            // settings form opened before the upgrade cannot silently turn it back off.
            migrationBuilder.Sql(
                "UPDATE \"AcquisitionSettings\" SET \"EpisodeUpgradesEnabled\" = 1, " +
                "\"AutomationRevision\" = \"AutomationRevision\" + 1 WHERE \"EpisodeUpgradesEnabled\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupersededPath",
                table: "UpgradeOperations");

            migrationBuilder.DropColumn(
                name: "OwnerItemId",
                table: "EpisodeBindings");
        }
    }
}
