using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JellyfinMod.Data.Migrations
{
    /// <summary>
    /// User decision 9 (2026-10-08): the default sources become IMDb, Rotten Tomatoes critics and audience, and Trakt. A server
    /// whose administrator never changed the default still holds decision 6's list exactly, so that list is replaced; any other
    /// list is an administrator's own choice and stays. (An administrator who saved exactly decision 6's list cannot be told
    /// apart from one who never changed it; nothing records which fields a save changed.)
    /// </summary>
    public partial class PhaseNineRatingsDisplayDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE RatingsSettings SET DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"trakt\"]' " +
                "WHERE DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"tmdb\",\"trakt\"]';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE RatingsSettings SET DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"tmdb\",\"trakt\"]' " +
                "WHERE DefaultSources = '[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"trakt\"]';");
        }
    }
}
