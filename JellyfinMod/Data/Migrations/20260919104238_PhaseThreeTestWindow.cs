using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeTestWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TestWindowMinutes",
                table: "RetentionPolicySnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestWindowMinutes",
                table: "RetentionPolicySnapshots");
        }
    }
}
