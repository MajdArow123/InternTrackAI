using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeZoneToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A string column is TEXT on SQLite and text on PostgreSQL alike, so no provider branch is
            // needed here (unlike the DateTime columns in earlier migrations). Existing users get the
            // documented default zone rather than an empty string.
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "UserProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "America/Toronto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "UserProfiles");
        }
    }
}
