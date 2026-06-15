using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchScoreToJobApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchRecommendation",
                table: "JobApplications",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchScore",
                table: "JobApplications",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchRecommendation",
                table: "JobApplications");

            migrationBuilder.DropColumn(
                name: "MatchScore",
                table: "JobApplications");
        }
    }
}
