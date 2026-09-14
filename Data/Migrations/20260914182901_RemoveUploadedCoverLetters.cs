using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUploadedCoverLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Plain DropTable works on both SQLite and PostgreSQL. Files under uploads/coverletters/
            // are not touched here; they are orphaned on disk and can be removed from the volume by hand.
            migrationBuilder.DropTable(
                name: "CoverLetterVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirrors the shape the table had after the earlier PostgreSQL type fixes: timestamptz
            // for the upload time, an identity key, and no explicit type on the bool/long columns so
            // each provider picks its native mapping.
            var dateTime = migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "timestamp with time zone"
                : "TEXT";

            migrationBuilder.CreateTable(
                name: "CoverLetterVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(nullable: false),
                    IsActive = table.Column<bool>(nullable: false),
                    UploadedAt = table.Column<DateTime>(type: dateTime, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverLetterVersions", x => x.Id);
                });
        }
    }
}
