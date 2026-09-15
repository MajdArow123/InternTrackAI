using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResumeExtractedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No ActiveProvider branch here, unlike the DateTime/double/identity migrations: the scaffolded
            // "TEXT" is only wrong on Npgsql for those types. An unbounded nullable string is TEXT on SQLite
            // and text on PostgreSQL either way, so the column type is left to the provider.
            migrationBuilder.AddColumn<string>(
                name: "ExtractedText",
                table: "ResumeVersions",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtractedText",
                table: "ResumeVersions");
        }
    }
}
