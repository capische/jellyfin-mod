using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// The Prowlarr definition each synced indexer runs on (P4.FU27), so Settings and the release picker can mark Prowlarr's slow
    /// default 1337x definition: one nullable text column, which SQLite adds without rebuilding the table. It stays empty until
    /// the next Prowlarr sync fills it; manual indexers keep it empty.
    /// </summary>
    public partial class IndexerDefinitionNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProwlarrDefinitionName",
                table: "AcquisitionIndexers",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "IndexerDefinitionNames is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
