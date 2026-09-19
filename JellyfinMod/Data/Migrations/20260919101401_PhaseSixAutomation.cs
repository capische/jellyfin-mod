using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseSixAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "RetentionOperations",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "retention");

            migrationBuilder.AddColumn<Guid>(
                name: "UpgradeOperationId",
                table: "RetentionOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Automatic",
                table: "GrabOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Intent",
                table: "GrabOperations",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "acquire");

            migrationBuilder.AddColumn<Guid>(
                name: "UpgradeOperationId",
                table: "GrabOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutomationBatchSize",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.AddColumn<bool>(
                name: "AutomationEnabled",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutomationIntervalHours",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "AutomationRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DailyAutoGrabBudget",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "DecisionLogCap",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2000);

            migrationBuilder.AddColumn<bool>(
                name: "EpisodeUpgradesEnabled",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "FreeSpaceFloorBytes",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 25000000000L);

            migrationBuilder.AddColumn<int>(
                name: "FreeSpaceFloorPercent",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentImports",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "NewEpisodeDelayMinutes",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<bool>(
                name: "ReacquireReclaimed",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Cutoff",
                table: "AcquisitionQualityProfiles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumAutoScore",
                table: "AcquisitionQualityProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumSeeders",
                table: "AcquisitionQualityProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpgradeAllowed",
                table: "AcquisitionQualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UpgradeMode",
                table: "AcquisitionQualityProfiles",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "replace");

            migrationBuilder.AddColumn<int>(
                name: "DailyQueryBudget",
                table: "AcquisitionIndexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "MinIntervalSeconds",
                table: "AcquisitionIndexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "AutomationDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    IndexerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GrabId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetsConsidered = table.Column<int>(type: "INTEGER", nullable: false),
                    Searched = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Grabbed = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradesPlanned = table.Column<int>(type: "INTEGER", nullable: false),
                    QueriesByIndexerJson = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTargets",
                columns: table => new
                {
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NextSearchAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSearchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsecutiveEmpty = table.Column<int>(type: "INTEGER", nullable: false),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    LastGrabId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastAutoGrabAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProfileRevisionSeen = table.Column<int>(type: "INTEGER", nullable: false),
                    AirDateSeen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SearchNowRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MetadataRefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTargets", x => x.TargetId);
                    table.ForeignKey(
                        name: "FK_AutomationTargets_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndexerBudgets",
                columns: table => new
                {
                    IndexerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QueriesUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    LastQueryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    BreakerOpenUntil = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexerBudgets", x => x.IndexerId);
                    table.ForeignKey(
                        name: "FK_IndexerBudgets_AcquisitionIndexers_IndexerId",
                        column: x => x.IndexerId,
                        principalTable: "AcquisitionIndexers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UpgradeOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenTargetKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SupersededBindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupersededQuality = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NewQuality = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NewGrabId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NewImportOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReplacementRetentionOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpgradeOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpgradeOperations_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_Automatic_CreatedAt",
                table: "GrabOperations",
                columns: new[] { "Automatic", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationDecisions_CreatedAt",
                table: "AutomationDecisions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationDecisions_EntryId",
                table: "AutomationDecisions",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationDecisions_RunId",
                table: "AutomationDecisions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuns_StartedAt",
                table: "AutomationRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTargets_EntryId",
                table: "AutomationTargets",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTargets_NextSearchAt",
                table: "AutomationTargets",
                column: "NextSearchAt");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeOperations_EntryId",
                table: "UpgradeOperations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeOperations_NewGrabId",
                table: "UpgradeOperations",
                column: "NewGrabId");

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeOperations_OpenTargetKey",
                table: "UpgradeOperations",
                column: "OpenTargetKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpgradeOperations_State",
                table: "UpgradeOperations",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationDecisions");

            migrationBuilder.DropTable(
                name: "AutomationRuns");

            migrationBuilder.DropTable(
                name: "AutomationTargets");

            migrationBuilder.DropTable(
                name: "IndexerBudgets");

            migrationBuilder.DropTable(
                name: "UpgradeOperations");

            migrationBuilder.DropIndex(
                name: "IX_GrabOperations_Automatic_CreatedAt",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "RetentionOperations");

            migrationBuilder.DropColumn(
                name: "UpgradeOperationId",
                table: "RetentionOperations");

            migrationBuilder.DropColumn(
                name: "Automatic",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "Intent",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "UpgradeOperationId",
                table: "GrabOperations");

            migrationBuilder.DropColumn(
                name: "AutomationBatchSize",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "AutomationEnabled",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "AutomationIntervalHours",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "AutomationRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "DailyAutoGrabBudget",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "DecisionLogCap",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "EpisodeUpgradesEnabled",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "FreeSpaceFloorBytes",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "FreeSpaceFloorPercent",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentImports",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "NewEpisodeDelayMinutes",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "ReacquireReclaimed",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "Cutoff",
                table: "AcquisitionQualityProfiles");

            migrationBuilder.DropColumn(
                name: "MinimumAutoScore",
                table: "AcquisitionQualityProfiles");

            migrationBuilder.DropColumn(
                name: "MinimumSeeders",
                table: "AcquisitionQualityProfiles");

            migrationBuilder.DropColumn(
                name: "UpgradeAllowed",
                table: "AcquisitionQualityProfiles");

            migrationBuilder.DropColumn(
                name: "UpgradeMode",
                table: "AcquisitionQualityProfiles");

            migrationBuilder.DropColumn(
                name: "DailyQueryBudget",
                table: "AcquisitionIndexers");

            migrationBuilder.DropColumn(
                name: "MinIntervalSeconds",
                table: "AcquisitionIndexers");
        }
    }
}
