using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <summary>
    /// Adds <c>PriorAttemptsJson</c>: the last three superseded attempts at a practice question, so
    /// "Retry this question" can show what was said before instead of overwriting it silently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only migration in this repo that needs no CLAUDE.md §8 provider branch</b>, and that is why
    /// this shape was chosen over an attempts table: a nullable string is <c>text</c> on both providers,
    /// so it avoids all three things that have actually broken on PostgreSQL here — identity columns,
    /// DateTime, and bool. Adding a nullable column also takes no table rewrite and no default backfill,
    /// which matters because this ships in the same deploy as two earlier schema changes.
    /// </para>
    /// <para>
    /// The explicit <c>type: "TEXT"</c> is load-bearing even with no branch: it is what stops the column
    /// becoming a length-limited <c>varchar</c> on PostgreSQL, which
    /// <c>MigrationColumnTypeTests.No_app_column_becomes_a_length_limited_varchar_on_PostgreSQL</c> pins.
    /// </para>
    /// </remarks>
    public partial class AddPriorAttemptsToPracticeQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PriorAttemptsJson",
                table: "PracticeQuestions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriorAttemptsJson",
                table: "PracticeQuestions");
        }
    }
}
