using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// The grab requests answered with an earlier grab (P4.FU29: Add on a release still seeding imports it again from the
    /// seeding copy): each request's requester, idempotency key and fingerprint, so a replay gets the same grab back and starts
    /// no second import. One new table; nothing existing is rebuilt, so the GrabClaimRelease trigger stays.
    /// </summary>
    public partial class GrabRequestKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrabRequestKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GrabId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrabRequestKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrabRequestKeys_GrabOperations_GrabId",
                        column: x => x.GrabId,
                        principalTable: "GrabOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrabRequestKeys_GrabId",
                table: "GrabRequestKeys",
                column: "GrabId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabRequestKeys_RequestedBy_IdempotencyKey",
                table: "GrabRequestKeys",
                columns: new[] { "RequestedBy", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "GrabRequestKeys is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
