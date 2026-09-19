using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseFiveImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImportEnabled",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportPollSeconds",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<int>(
                name: "ImportRevision",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "QueueVisibleToUsers",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ScanTimeoutMinutes",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "SeedFloorHours",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: true,
                defaultValue: 168);

            migrationBuilder.AddColumn<double>(
                name: "SeedFloorRatio",
                table: "AcquisitionSettings",
                type: "REAL",
                nullable: true,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<bool>(
                name: "SeedReleaseEnabled",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "StalledAfterHours",
                table: "AcquisitionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<string>(
                name: "VideoExtensions",
                table: "AcquisitionSettings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "mkv,mp4,m4v,avi,mov,ts,m2ts,webm,wmv,mpg,mpeg");

            migrationBuilder.CreateTable(
                name: "DownloadClientPathMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientPathPrefix = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LocalPathPrefix = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerificationReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadClientPathMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadClientPathMappings_AcquisitionDownloadClients_DownloadClientId",
                        column: x => x.DownloadClientId,
                        principalTable: "AcquisitionDownloadClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrabId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpenGrabKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetLibraryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ReleaseTitle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Intent = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    HistoryReason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Progress = table.Column<double>(type: "REAL", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DownloadRateBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    EtaSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ClientStatus = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    ObservedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastProgressAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StalledSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SourceClientPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    SourceLocalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    SourcePhysicalIdentity = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    SourceMountIdentity = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourceLogicalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    DestinationRoot = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    DestinationPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    DestinationPhysicalIdentity = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    HardlinkCountAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    NativeItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BindingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    VersionLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ScanAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedDownloadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScanRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BoundAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    RetryOfId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportOperations_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImportOperations_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImportOperations_GrabOperations_GrabId",
                        column: x => x.GrabId,
                        principalTable: "GrabOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseBlocklist",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    IndexerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceGuid = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    RawTitle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseBlocklist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeedReleaseOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrabId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DownloadClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    GoalRatio = table.Column<double>(type: "REAL", nullable: true),
                    GoalRatioSource = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    GoalSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    GoalSecondsSource = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IndexerRatio = table.Column<double>(type: "REAL", nullable: true),
                    IndexerSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedRatio = table.Column<double>(type: "REAL", nullable: true),
                    ObservedSeedingSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    GoalMetAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SeedingPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SeedingPhysicalIdentity = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    LibraryPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceLogicalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    HardlinkCountBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    PhysicalBytesReleased = table.Column<long>(type: "INTEGER", nullable: true),
                    CreditedRetentionOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PreparedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RemovingAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RemovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedReleaseOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedReleaseOperations_ImportOperations_ImportOperationId",
                        column: x => x.ImportOperationId,
                        principalTable: "ImportOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadClientPathMappings_DownloadClientId_Order",
                table: "DownloadClientPathMappings",
                columns: new[] { "DownloadClientId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_EntryId",
                table: "ImportOperations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_EpisodeId",
                table: "ImportOperations",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_GrabId",
                table: "ImportOperations",
                column: "GrabId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_InfoHash",
                table: "ImportOperations",
                column: "InfoHash");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_OpenGrabKey",
                table: "ImportOperations",
                column: "OpenGrabKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_State",
                table: "ImportOperations",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_IndexerId_SourceGuid",
                table: "ReleaseBlocklist",
                columns: new[] { "IndexerId", "SourceGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseBlocklist_InfoHash",
                table: "ReleaseBlocklist",
                column: "InfoHash");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReleaseOperations_ImportOperationId",
                table: "SeedReleaseOperations",
                column: "ImportOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeedReleaseOperations_SeedingPhysicalIdentity",
                table: "SeedReleaseOperations",
                column: "SeedingPhysicalIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReleaseOperations_State",
                table: "SeedReleaseOperations",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadClientPathMappings");

            migrationBuilder.DropTable(
                name: "ReleaseBlocklist");

            migrationBuilder.DropTable(
                name: "SeedReleaseOperations");

            migrationBuilder.DropTable(
                name: "ImportOperations");

            migrationBuilder.DropColumn(
                name: "ImportEnabled",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "ImportPollSeconds",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "ImportRevision",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "QueueVisibleToUsers",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "ScanTimeoutMinutes",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedFloorHours",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedFloorRatio",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "SeedReleaseEnabled",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "StalledAfterHours",
                table: "AcquisitionSettings");

            migrationBuilder.DropColumn(
                name: "VideoExtensions",
                table: "AcquisitionSettings");
        }
    }
}
