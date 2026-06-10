using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InternTrackAI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverLetterVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverLetterVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResumeVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResumeVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: true),
                    Age = table.Column<int>(type: "INTEGER", nullable: true),
                    Country = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhotoFileName = table.Column<string>(type: "TEXT", nullable: true),
                    PhotoVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillsJson = table.Column<string>(type: "TEXT", nullable: true),
                    TargetRolesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverLetterVersions");

            migrationBuilder.DropTable(
                name: "ResumeVersions");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
