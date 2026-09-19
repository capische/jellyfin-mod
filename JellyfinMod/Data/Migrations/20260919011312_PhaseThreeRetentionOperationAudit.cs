using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeRetentionOperationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RetentionOperations_Entries_EntryId",
                table: "RetentionOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_RetentionOperations_Episodes_EpisodeId",
                table: "RetentionOperations");

            migrationBuilder.AlterColumn<Guid>(
                name: "EntryId",
                table: "RetentionOperations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddForeignKey(
                name: "FK_RetentionOperations_Entries_EntryId",
                table: "RetentionOperations",
                column: "EntryId",
                principalTable: "Entries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_RetentionOperations_Episodes_EpisodeId",
                table: "RetentionOperations",
                column: "EpisodeId",
                principalTable: "Episodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RetentionOperations_Entries_EntryId",
                table: "RetentionOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_RetentionOperations_Episodes_EpisodeId",
                table: "RetentionOperations");

            migrationBuilder.AlterColumn<Guid>(
                name: "EntryId",
                table: "RetentionOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RetentionOperations_Entries_EntryId",
                table: "RetentionOperations",
                column: "EntryId",
                principalTable: "Entries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RetentionOperations_Episodes_EpisodeId",
                table: "RetentionOperations",
                column: "EpisodeId",
                principalTable: "Episodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
