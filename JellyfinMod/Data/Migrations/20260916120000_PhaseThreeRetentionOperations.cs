using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations;

/// <inheritdoc />
public partial class PhaseThreeRetentionOperations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RetentionOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetLibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                MediaPath = table.Column<string>(type: "TEXT", nullable: false),
                StorageIdentity = table.Column<string>(type: "TEXT", nullable: false),
                PhysicalIdentity = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                LogicalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                HardlinkCountBefore = table.Column<long>(type: "INTEGER", nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                PreparedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UnlinkedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                PhysicalBytesReleased = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RetentionOperations", x => x.Id);
                table.ForeignKey(
                    name: "FK_RetentionOperations_Entries_EntryId",
                    column: x => x.EntryId,
                    principalTable: "Entries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RetentionOperations_Episodes_EpisodeId",
                    column: x => x.EpisodeId,
                    principalTable: "Episodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RetentionOperations_BindingId_State",
            table: "RetentionOperations",
            columns: new[] { "BindingId", "State" });

        migrationBuilder.CreateIndex(
            name: "IX_RetentionOperations_EntryId",
            table: "RetentionOperations",
            column: "EntryId");

        migrationBuilder.CreateIndex(
            name: "IX_RetentionOperations_EpisodeId",
            table: "RetentionOperations",
            column: "EpisodeId");

        migrationBuilder.CreateIndex(
            name: "IX_RetentionOperations_PhysicalIdentity",
            table: "RetentionOperations",
            column: "PhysicalIdentity");

        migrationBuilder.CreateIndex(
            name: "IX_RetentionOperations_PreparedAt",
            table: "RetentionOperations",
            column: "PreparedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "RetentionOperations");
}
