using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <summary>
    /// The resume-parse draft table, plus <c>UserProfiles.ProfileLastEnrichedAt</c>. A draft is what
    /// the review screen reads; nothing here is ever written to a profile without confirmation.
    /// </summary>
    /// <remarks>
    /// Needs the CLAUDE.md §8 provider branch, unlike AddFieldAwarenessToProfile: scaffolding against
    /// SQLite gives DateTime columns <c>type: "TEXT"</c> (a text column on Postgres — inserts coerce and
    /// every read throws InvalidCastException) and gives a new identity key only <c>Sqlite:Autoincrement</c>
    /// (Postgres inserts then fail with 23502). It also gives <c>bool</c> columns <c>INTEGER</c>, which is
    /// int4 on Postgres and throws on every read — the trap FixPostgreSqlTypesAndAddDataProtection had to go back and undo
    /// for eight existing columns. Template: 20260913230212_AddStatusSuggestions (which has no bool).
    /// </remarks>
    public partial class AddParsedResumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";
            var dateTime = isPostgres ? "timestamp with time zone" : "TEXT";
            var boolean  = isPostgres ? "boolean" : "INTEGER";

            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileLastEnrichedAt",
                table: "UserProfiles",
                type: dateTime,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ParsedResumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ResumeVersionId = table.Column<int>(type: "INTEGER", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: dateTime, nullable: false),
                    Applied = table.Column<bool>(type: boolean, nullable: false),
                    CharactersExtracted = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParsedResumes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParsedResumes_UserId_CreatedAt",
                table: "ParsedResumes",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParsedResumes");

            migrationBuilder.DropColumn(
                name: "ProfileLastEnrichedAt",
                table: "UserProfiles");
        }
    }
}
