using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeRetentionEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetentionEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    AccessFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CompletionBasisAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EligibleAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Deadline = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EvaluatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RetentionEvaluations_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RetentionEvaluations_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionEvaluations_Deadline",
                table: "RetentionEvaluations",
                column: "Deadline");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionEvaluations_EntryId",
                table: "RetentionEvaluations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionEvaluations_EpisodeId",
                table: "RetentionEvaluations",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionEvaluations_TargetId",
                table: "RetentionEvaluations",
                column: "TargetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RetentionEvaluations");
        }
    }
}
