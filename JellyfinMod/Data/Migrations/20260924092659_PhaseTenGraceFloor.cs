using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseTenGraceFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GraceNotBefore",
                table: "RetentionEvaluations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other Phase 10 migrations (RET-R3): the rollback is the pre-deploy database backup.
            throw new NotSupportedException(
                "PhaseTenGraceFloor is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
