using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeasonPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Pack",
                table: "SeedReleaseOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReplaceDetail",
                table: "ImportOperations",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplaceState",
                table: "ImportOperations",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplaceTargets",
                table: "ImportOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationFingerprint",
                table: "ImportOperations",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "GrabOperations",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "fill");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "GrabOperations",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "episode");

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "GrabOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GrabClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrabId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActiveKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Held = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrabClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrabClaims_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrabClaims_GrabOperations_GrabId",
                        column: x => x.GrabId,
                        principalTable: "GrabOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrabClaims_ActiveKey",
                table: "GrabClaims",
                column: "ActiveKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrabClaims_EpisodeId",
                table: "GrabClaims",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabClaims_GrabId_EpisodeId",
                table: "GrabClaims",
                columns: new[] { "GrabId", "EpisodeId" },
                unique: true);

            // Existing grabs: a movie's is the title scope, a series' the episode scope; another version adds, everything else
            // fills (season and series packs, 2026-10-08).
            migrationBuilder.Sql(
                "UPDATE GrabOperations SET Scope = CASE WHEN EpisodeId IS NOT NULL OR EXISTS (SELECT 1 FROM Entries " +
                "WHERE Entries.Id = GrabOperations.EntryId AND Entries.MediaType = 'series') THEN 'episode' ELSE 'title' END;");
            migrationBuilder.Sql("UPDATE GrabOperations SET Mode = CASE WHEN Intent = 'addVersion' THEN 'add' ELSE 'fill' END;");
            // Every active single-episode grab claims its episode under the key it already owns (one claim per grab, so the
            // grab's own id is a unique claim id), so a pack cannot take an episode a running grab is downloading.
            migrationBuilder.Sql(
                "INSERT INTO GrabClaims (Id, GrabId, EpisodeId, ActiveKey, Held, CreatedAt) " +
                "SELECT Id, Id, EpisodeId, ActiveTarget, CASE WHEN Intent = 'addVersion' OR UpgradeOperationId IS NOT NULL " +
                "THEN 1 ELSE 0 END, CreatedAt FROM GrabOperations WHERE ActiveTarget IS NOT NULL AND EpisodeId IS NOT NULL " +
                "AND EXISTS (SELECT 1 FROM Episodes WHERE Episodes.Id = GrabOperations.EpisodeId);");
            // Releasing a grab's target releases its claims, whichever code path releases it. A later migration that makes EF
            // rebuild GrabOperations must create this trigger again.
            migrationBuilder.Sql(
                "CREATE TRIGGER GrabClaimRelease AFTER UPDATE OF ActiveTarget ON GrabOperations " +
                "WHEN NEW.ActiveTarget IS NULL AND OLD.ActiveTarget IS NOT NULL " +
                "BEGIN UPDATE GrabClaims SET ActiveKey = NULL WHERE GrabId = NEW.Id AND ActiveKey IS NOT NULL; END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS GrabClaimRelease;");
            migrationBuilder.DropTable(
                name: "GrabClaims");

            migrationBuilder.DropColumn(
                name: "Pack",
                table: "SeedReleaseOperations");

            migrationBuilder.DropColumn(
                name: "ReplaceDetail",
                table: "ImportOperations");

            migrationBuilder.DropColumn(
                name: "ReplaceState",
                table: "ImportOperations");

            migrationBuilder.DropColumn(
                name: "ReplaceTargets",
                table: "ImportOperations");

            migrationBuilder.DropColumn(
                name: "DestinationFingerprint",
                table: "ImportOperations");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "GrabOperations");
        }
    }
}
