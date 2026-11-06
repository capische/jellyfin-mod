using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <inheritdoc />
    public partial class RatingLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Url",
                table: "TitleRatings",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            // D274: only the exact old default changes. Custom lists and their order are preserved.
            migrationBuilder.Sql(
                "UPDATE RatingsSettings SET DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"tmdb\",\"trakt\"]' " +
                "WHERE DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"trakt\"]';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("RatingLinks is a forward-only migration.");
        }
    }
}
