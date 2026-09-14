using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePublicProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plain DropColumn works on both providers: SQLite rebuilds the table behind the scenes,
            // PostgreSQL issues ALTER TABLE ... DROP COLUMN.
            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "PublicSlug",
                table: "UserProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No explicit column types: each provider picks its native mapping (bool -> boolean on
            // PostgreSQL, INTEGER on SQLite), avoiding the int4 column that
            // FixPublicProfileIsPublicTypeForPostgres had to repair.
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "UserProfiles",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicSlug",
                table: "UserProfiles",
                nullable: true);
        }
    }
}
