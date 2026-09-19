using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseFourAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "QualityProfileId",
                table: "Entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AcquisitionDownloadClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PasswordSecretRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DownloadDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LocalDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    VerifiedLibraryIds = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    OpenUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ApiVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionDownloadClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcquisitionIndexers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ApiKeySecretRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Categories = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadHosts = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MinimumSeedRatio = table.Column<double>(type: "REAL", nullable: true),
                    MinimumSeedMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CapabilitiesFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerifiedRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionIndexers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcquisitionQualityProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    QualitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MinimumBytesPerHour = table.Column<long>(type: "INTEGER", nullable: true),
                    MaximumBytesPerHour = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionQualityProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GrabOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActiveTarget = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ActiveHash = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    SearchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IndexerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IndexerName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceGuid = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RawTitle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ParsedJson = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: true),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    ScoringVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadClientRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DownloadDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    HoldUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    SeedRatio = table.Column<double>(type: "REAL", nullable: true),
                    SeedMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrabOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrabOperations_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GrabOperations_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AcquisitionSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DownloadClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DefaultQualityProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcquisitionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcquisitionSettings_AcquisitionDownloadClients_DownloadClientId",
                        column: x => x.DownloadClientId,
                        principalTable: "AcquisitionDownloadClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AcquisitionSettings_AcquisitionQualityProfiles_DefaultQualityProfileId",
                        column: x => x.DefaultQualityProfileId,
                        principalTable: "AcquisitionQualityProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_QualityProfileId",
                table: "Entries",
                column: "QualityProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionDownloadClients_Name",
                table: "AcquisitionDownloadClients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionIndexers_Name",
                table: "AcquisitionIndexers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionQualityProfiles_Name",
                table: "AcquisitionQualityProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionSettings_DefaultQualityProfileId",
                table: "AcquisitionSettings",
                column: "DefaultQualityProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_AcquisitionSettings_DownloadClientId",
                table: "AcquisitionSettings",
                column: "DownloadClientId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_ActiveHash",
                table: "GrabOperations",
                column: "ActiveHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_ActiveTarget",
                table: "GrabOperations",
                column: "ActiveTarget",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_EntryId",
                table: "GrabOperations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_EpisodeId",
                table: "GrabOperations",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_RequestedBy_IdempotencyKey",
                table: "GrabOperations",
                columns: new[] { "RequestedBy", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrabOperations_State",
                table: "GrabOperations",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcquisitionIndexers");

            migrationBuilder.DropTable(
                name: "AcquisitionSettings");

            migrationBuilder.DropTable(
                name: "GrabOperations");

            migrationBuilder.DropTable(
                name: "AcquisitionDownloadClients");

            migrationBuilder.DropTable(
                name: "AcquisitionQualityProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Entries_QualityProfileId",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "QualityProfileId",
                table: "Entries");
        }
    }
}
