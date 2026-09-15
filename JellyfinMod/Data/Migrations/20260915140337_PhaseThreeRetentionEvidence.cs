using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeRetentionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetentionPolicy",
                table: "Entries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Phase 1 stored null as inherit and a positive value as an explicit duration.
            migrationBuilder.Sql("UPDATE Entries SET RetentionPolicy = 1 WHERE ReclaimAfterDays IS NOT NULL");

            migrationBuilder.CreateTable(
                name: "CompletionObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JellyfinItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvidenceAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Played = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlaybackPositionTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ObservedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompletionObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompletionObservations_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RetentionPolicySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchedUserMode = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReclaimAfterDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ExemptFavourites = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnabledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionPolicySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompletionObservations_EntryId",
                table: "CompletionObservations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CompletionObservations_JellyfinItemId",
                table: "CompletionObservations",
                column: "JellyfinItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CompletionObservations_TargetId_UserId",
                table: "CompletionObservations",
                columns: new[] { "TargetId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompletionObservations");

            migrationBuilder.DropTable(
                name: "RetentionPolicySnapshots");

            migrationBuilder.DropColumn(
                name: "RetentionPolicy",
                table: "Entries");
        }
    }
}
