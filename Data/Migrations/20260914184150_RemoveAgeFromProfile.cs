using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAgeFromProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plain DropColumn works on both providers: SQLite rebuilds the table behind the scenes,
            // PostgreSQL issues ALTER TABLE ... DROP COLUMN.
            migrationBuilder.DropColumn(
                name: "Age",
                table: "UserProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No explicit column type so each provider picks its native int mapping.
            migrationBuilder.AddColumn<int>(
                name: "Age",
                table: "UserProfiles",
                nullable: true);
        }
    }
}
