using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// Each indexer's colour in the release picker (P4.FU25): one integer column, so SQLite adds it without rebuilding the
    /// table. The indexers already there get the palette in the plugin's indexer order (priority, then name), the ninth
    /// wrapping to the first; an indexer added later gets the least used colour when it is saved (<c>ModDbContext</c>).
    /// </summary>
    public partial class IndexerColours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ColorIndex",
                table: "AcquisitionIndexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE AcquisitionIndexers SET ColorIndex = (
                    SELECT (ranked.Position - 1) % 8 FROM (
                        SELECT Id, ROW_NUMBER() OVER (ORDER BY Priority, Name) AS Position FROM AcquisitionIndexers) AS ranked
                    WHERE ranked.Id = AcquisitionIndexers.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            // Forward-only like the other migrations (RET-R3): the rollback is the database backup taken before the deploy.
            throw new NotSupportedException(
                "IndexerColours is forward-only. Roll back by restoring the database backup taken before the deploy.");
    }
}
