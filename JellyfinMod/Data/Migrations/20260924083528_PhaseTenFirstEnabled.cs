using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenFirstEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstEnabledAt",
                table: "RetentionPolicySnapshots",
                type: "TEXT",
                nullable: true);

            // A database that has retention switched on now keeps that switch-on as its first; one that has it off learns
            // the first switch-on at the next enable, which only moves the floor later (PHASE10 Q9).
            migrationBuilder.Sql(
                "UPDATE \"RetentionPolicySnapshots\" SET \"FirstEnabledAt\" = \"EnabledAt\" WHERE \"EnabledAt\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other Phase 10 migrations (RET-R3): the rollback is the pre-deploy database backup.
            throw new NotSupportedException(
                "PhaseTenFirstEnabled is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
