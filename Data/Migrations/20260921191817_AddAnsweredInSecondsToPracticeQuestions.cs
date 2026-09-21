using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds <c>AnsweredInSeconds</c>: how long an answer took, first keystroke to submit.
    /// </summary>
    /// <remarks>
    /// <b>No CLAUDE.md §8 provider branch needed</b>, and that is why this shape was chosen: a nullable
    /// <c>int</c> scaffolds as <c>INTEGER</c>, which is <c>integer</c> on PostgreSQL — none of the three
    /// things that have actually broken here (DateTime, bool, identity). Adding a nullable column also
    /// takes no table rewrite and no backfill. Same reasoning as <c>PriorAttemptsJson</c>.
    /// </remarks>
    public partial class AddAnsweredInSecondsToPracticeQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnsweredInSeconds",
                table: "PracticeQuestions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnsweredInSeconds",
                table: "PracticeQuestions");
        }
    }
}
