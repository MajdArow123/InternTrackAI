using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <summary>
    /// Field awareness on the profile (Field, FieldCategory, Seniority, YearsExperience, Location) —
    /// what Services/UserContextBuilder.cs turns into the context block every AI prompt carries.
    /// </summary>
    /// <remarks>
    /// No <c>ActiveProvider</c> branch here, deliberately: the CLAUDE.md §8 rule covers DateTime,
    /// floating-point and new identity columns, and this migration adds none of those. Five nullable
    /// TEXT/INTEGER columns on an existing table map straight onto Postgres text/integer.
    /// Both enums are stored as ints with explicit values (CLAUDE.md §7).
    /// </remarks>
    public partial class AddFieldAwarenessToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Field",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FieldCategory",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "UserProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Seniority",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YearsExperience",
                table: "UserProfiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Field",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "FieldCategory",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Seniority",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "YearsExperience",
                table: "UserProfiles");
        }
    }
}
