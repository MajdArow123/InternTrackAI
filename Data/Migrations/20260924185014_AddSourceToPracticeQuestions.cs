using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds <c>Source</c>: which generator wrote a practice question (<see cref="Models.Enums.QuestionSource"/>),
    /// so practice topic dedupe can skip interview-prep topics that were measured to be too broad to
    /// compare against.
    /// </summary>
    /// <remarks>
    /// <b>No CLAUDE.md §8 provider branch needed</b> — the same shape as
    /// <c>AddAnsweredInSecondsToPracticeQuestions</c>: a nullable <c>int</c> scaffolds as <c>INTEGER</c>,
    /// which is <c>integer</c> on PostgreSQL, with no table rewrite and no backfill. Existing rows stay
    /// null, which reads as "practice"; that is exact, because every existing prep row has an empty topic.
    /// </remarks>
    public partial class AddSourceToPracticeQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "PracticeQuestions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "PracticeQuestions");
        }
    }
}
